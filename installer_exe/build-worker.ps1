param(
    [Parameter(Mandatory=$true)][string]$ConfigFile,
    [Parameter(Mandatory=$true)][string]$StatusFile,
    [Parameter(Mandatory=$true)][string]$ScriptDir,
    [string]$InnoBinDir
)
$ErrorActionPreference = 'Stop'

# Force UTF-8 encoding for all I/O
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$config = [IO.File]::ReadAllText($ConfigFile, [Text.Encoding]::UTF8) | ConvertFrom-Json
$log = @(); $progress = 0; $output = ''

function Save-Status($state, $error='') {
    $json = @{ status = $state; progress = $progress; log = @($log); output = $output; error = $error } |
        ConvertTo-Json -Depth 10 -Compress
    [IO.File]::WriteAllText($StatusFile, $json, (New-Object Text.UTF8Encoding $false))
}
function Add-Log($text) {
    $script:log += @($text)
    Save-Status 'running'
}

# 启动后立即覆盖 UI 写入的 starting 状态，避免任务早期异常时长期停在 0%。
Save-Status 'running'

try {
    # --- Locate ISCC.exe ---
    $paths = @()
    if ($InnoBinDir) { $paths += Join-Path $InnoBinDir 'ISCC.exe' }
    $paths += 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files\Inno Setup 6\ISCC.exe'
    $iscc = $paths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup ISCC.exe not found.' }
    Add-Log "Inno Setup: $iscc"

    # --- Validate source ---
    if (-not (Test-Path -LiteralPath $config.sourceDir -PathType Container)) {
        throw 'Source directory not found: ' + $config.sourceDir
    }
    Add-Log "Source: $($config.sourceDir)"

    # --- Parse config ---
    $name = [string]$config.productName;    if (-not $name) { $name = 'My Application' }
    $version = [string]$config.version;      if (-not $version) { $version = '1.0.0' }
    $publisher = [string]$config.publisher;  if (-not $publisher) { $publisher = '' }
    $subtitle = [string]$config.subtitle;    if (-not $subtitle) { $subtitle = '安装程序' }
    $out = [string]$config.outputDir;        if (-not $out) { $out = $ScriptDir }
    $theme = [string]$config.theme;          if (-not $theme) { $theme = 'dark' }
    $installPath = [string]$config.installPath; if (-not $installPath) { $installPath = 'C:\Program Files\' + $name }
    $mainExe = [string]$config.mainExe
    $allowCustom = if ($null -eq $config.allowCustomInstall) { $true } else { [bool]$config.allowCustomInstall }
    $allowInstallPathSelection = [bool]$config.allowInstallPathSelection
    $addToSystemPath = [bool]$config.addToSystemPath
    $createDesktop = [bool]$config.createDesktopShortcut
    $createStartMenu = [bool]$config.createStartMenuShortcut
    $createStartup = [bool]$config.createStartupEntry
    $startupName = [string]$config.startupEntryName
    $startupArgs = [string]$config.startupArguments
    # systemPathValue supersedes legacy environmentValue and is only used for HKLM Path.
    $systemPathValue = [string]$config.systemPathValue
    if (-not $systemPathValue) { $systemPathValue = [string]$config.environmentValue }
    if (-not $systemPathValue) { $systemPathValue = '{app}' }
    $cleanupDesktop = if ($null -eq $config.cleanupDesktopShortcut) { $true } else { [bool]$config.cleanupDesktopShortcut }
    $cleanupStartMenu = if ($null -eq $config.cleanupStartMenuShortcut) { $true } else { [bool]$config.cleanupStartMenuShortcut }
    $cleanupStartup = if ($null -eq $config.cleanupStartupEntry) { $true } else { [bool]$config.cleanupStartupEntry }
    $cleanupInstallDir = [bool]$config.cleanupInstallDirectory

    # Calculate stub size
    $sourceFiles = Get-ChildItem -LiteralPath $config.sourceDir -Recurse -File -ErrorAction SilentlyContinue
    $sourceSizeBytes = ($sourceFiles | Measure-Object -Property Length -Sum).Sum
    if (-not $sourceSizeBytes) { $sourceSizeBytes = 0 }
    $stubMB = [math]::Round($sourceSizeBytes / 1MB, 1)

    # Optional components
    $components = @($config.optionalComponents | Where-Object { $_.name -and $_.downloadUrl })

    $base = ($name + '-Setup-' + $version) -replace '[\\/:*?"<>|]', '_'
    # ISCC may not handle non-ASCII in OutputBaseFilename; use ASCII-safe name and rename after
    $baseSafe = $base -replace '[^\x20-\x7E]', '_'
    $appId = ([string]$config.upgradeCode).Trim('{}')
    if (-not $appId) { $appId = [Guid]::NewGuid().ToString().ToUpper() }

    New-Item -ItemType Directory -Force -Path $out | Out-Null

    # --- Create build temp directory (unique name to avoid locked files) ---
    # Clean up old build temp directories
    Get-ChildItem -Path $ScriptDir -Directory -Filter '_build_*' -ErrorAction SilentlyContinue | ForEach-Object {
        try { Remove-Item -Recurse -Force $_.FullName -ErrorAction Stop }
        catch { Add-Log "Warning: could not clean old temp: $($_.Name)" }
    }
    $buildTemp = Join-Path $ScriptDir ('_build_' + [Guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Force -Path $buildTemp | Out-Null
    $sourceTemp = Join-Path $buildTemp 'source'
    New-Item -ItemType Directory -Force -Path $sourceTemp | Out-Null

    $progress = 10
    Add-Log "Copying source files to build temp..."
    Copy-Item -LiteralPath $config.sourceDir -Destination $sourceTemp -Recurse -Force
    Add-Log "Source files copied"

    # --- Build config JSON ---
    $configObj = @{
        productName = $name
        version = $version
        publisher = $publisher
        subtitle = $subtitle
        installPath = $installPath
        mainExe = $mainExe
        allowCustomInstall = $allowCustom
        allowInstallPathSelection = $allowInstallPathSelection
        addToSystemPath = $addToSystemPath
        createDesktopShortcut = $createDesktop
        createStartMenuShortcut = $createStartMenu
        createStartupEntry = $createStartup
        startupEntryName = $startupName
        startupArguments = $startupArgs
        systemPathValue = $systemPathValue
        cleanupDesktopShortcut = $cleanupDesktop
        cleanupStartMenuShortcut = $cleanupStartMenu
        cleanupStartupEntry = $cleanupStartup
        cleanupInstallDirectory = $cleanupInstallDir
        stubMB = $stubMB
        optionalComponents = @($components | ForEach-Object {
            @{ name=$_.name; downloadUrl=$_.downloadUrl; extractPath=[string]$_.extractPath; sha256=[string]$_.sha256; required=[bool]$_.required }
        })
    }
    $configJsonStr = $configObj | ConvertTo-Json -Depth 5 -Compress

    # --- Compile C# installer application ---
    $progress = 40
    Add-Log "Compiling native installer application..."
    $csTemplate = Join-Path $ScriptDir 'installer-app.cs'
    if (-not (Test-Path $csTemplate)) { throw 'installer-app.cs template not found' }
    $csContent = [IO.File]::ReadAllText($csTemplate, [Text.Encoding]::UTF8)

    # Replace the config placeholder
    # The config is inserted into a C# verbatim string (@"..."), so:
    # - Backslashes are literal (no escaping needed)
    # - Double quotes must be escaped as ""
    $csContent = $csContent.Replace('__CONFIG_JSON__', $configJsonStr.Replace('"', '""'))

    $csPath = Join-Path $buildTemp 'installer-app.cs'
    [IO.File]::WriteAllText($csPath, $csContent, (New-Object Text.UTF8Encoding $true))

    # Find csc.exe
    $csc = @(
        'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
        'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $csc) { throw 'C# compiler (csc.exe) not found' }

    $appExePath = Join-Path $buildTemp 'installer-app.exe'
    $cscArgs = '/nologo /target:winexe /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /out:"' + $appExePath + '" "' + $csPath + '"'
    Add-Log "Compiling: csc $cscArgs"

    $cscPsi = New-Object System.Diagnostics.ProcessStartInfo
    $cscPsi.FileName = $csc
    $cscPsi.Arguments = $cscArgs
    $cscPsi.UseShellExecute = $false
    $cscPsi.RedirectStandardOutput = $true
    $cscPsi.RedirectStandardError = $true
    $cscPsi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $cscPsi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $cscPsi.CreateNoWindow = $true
    $cscProc = [System.Diagnostics.Process]::Start($cscPsi)
    $cscOut = $cscProc.StandardOutput.ReadToEnd()
    $cscErr = $cscProc.StandardError.ReadToEnd()
    $cscProc.WaitForExit()
    if ($cscOut) { $cscOut -split "`n" | ForEach-Object { if ($_.Trim()) { Add-Log $_.Trim() } } }
    if ($cscErr) { $cscErr -split "`n" | ForEach-Object { if ($_.Trim()) { Add-Log $_.Trim() } } }
    if ($cscProc.ExitCode -ne 0) { throw "C# compilation failed (exit code: $($cscProc.ExitCode))" }
    if (-not (Test-Path $appExePath)) { throw "Compiled EXE not found: $appExePath" }
    Add-Log "Native installer compiled: $appExePath"

    # --- Generate Inno Setup .iss ---
    $progress = 55
    Add-Log "Generating Inno Setup wrapper..."

    # Handle icon
    $iconFile = ''
    $iconPath = [string]$config.iconPath
    if ($iconPath -and (Test-Path -LiteralPath $iconPath)) {
        $iconFile = Join-Path $buildTemp 'icon.ico'
        Copy-Item -LiteralPath $iconPath -Destination $iconFile -Force
        Add-Log "Icon: $iconPath"
    }

    $iconLine = if ($iconFile) { "SetupIconFile=$iconFile`r`n" } else { '' }

    # 可选的准备界面 Logo：先复制到独立构建目录，避免编译期间占用原始文件。
    $prepLogoFile = ''
    $prepLogoPath = [string]$config.prepLogoPath
    if ($prepLogoPath) {
        if (-not (Test-Path -LiteralPath $prepLogoPath -PathType Leaf)) { throw "Preparation logo not found: $prepLogoPath" }
        $extension = [IO.Path]::GetExtension($prepLogoPath).ToLowerInvariant()
        if ($extension -notin @('.png', '.jpg', '.jpeg', '.bmp')) { throw 'Preparation logo must be PNG, JPG or BMP.' }
        $prepLogoFile = Join-Path $buildTemp ('preparation-logo' + $extension)
        Copy-Item -LiteralPath $prepLogoPath -Destination $prepLogoFile -Force
        Add-Log "Preparation logo: $prepLogoPath"
    }

    $iss = @"
[Setup]
AppName=$name
AppVersion=$version
AppPublisher=$publisher
AppId={{}}$appId
DefaultDirName={tmp}\$base
CreateAppDir=no
Uninstallable=no
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
DisableStartupPrompt=yes
OutputDir=$out
OutputBaseFilename=setup-payload
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
$iconLine
[Files]
Source: "$appExePath"; DestDir: "{tmp}"; Flags: ignoreversion
Source: "$sourceTemp\*"; DestDir: "{tmp}\source"; Flags: recursesubdirs createallsubdirs ignoreversion

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    Exec(ExpandConstant('{tmp}\installer-app.exe'), '', ExpandConstant('{tmp}'), SW_SHOW, ewWaitUntilTerminated, ResultCode);
  end;
end;
"@

    $issPath = Join-Path $buildTemp 'build-wrapper.iss'
    [IO.File]::WriteAllText($issPath, $iss, (New-Object Text.UTF8Encoding $true))
    Add-Log "ISS wrapper generated"

    # --- Compile ---
    $progress = 70
    Add-Log 'Compiling with ISCC...'
    # Use System.Diagnostics.Process for reliable UTF-8 output capture
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $iscc
    $psi.Arguments = '"' + $issPath + '"'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    $exitCode = $proc.ExitCode
    if ($stdout) { $stdout -split "`n" | ForEach-Object { if ($_.Trim()) { Add-Log $_.Trim() } } }
    if ($stderr) { $stderr -split "`n" | ForEach-Object { if ($_.Trim()) { Add-Log $_.Trim() } } }

    if ($exitCode -ne 0) {
        throw "ISCC compilation failed (exit code: $exitCode)"
    }

    # --- Inno Setup produced setup-payload.exe, now compile the C# launcher ---
    $payloadExe = Join-Path $out 'setup-payload.exe'
    if (-not (Test-Path -LiteralPath $payloadExe)) {
        throw 'Inno Setup payload was not created: ' + $payloadExe
    }
    Add-Log "Payload built: $payloadExe ($([math]::Round((Get-Item $payloadExe).Length/1KB, 1)) KB)"

    $progress = 85
    Add-Log 'Compiling launcher with embedded payload...'

    # Read launcher template
    $launcherTemplate = Join-Path $ScriptDir 'launcher.cs'
    if (-not (Test-Path $launcherTemplate)) { throw 'launcher.cs template not found' }
    $launcherContent = [IO.File]::ReadAllText($launcherTemplate, [Text.Encoding]::UTF8)
    $launcherPath = Join-Path $buildTemp 'launcher.cs'
    [IO.File]::WriteAllText($launcherPath, $launcherContent, (New-Object Text.UTF8Encoding $true))

    # Compile launcher with embedded payload and manifest
    $manifestPath = Join-Path $ScriptDir 'app.manifest'
    $launcherExe = Join-Path $out ($base + '.exe')
    
    # Delete old output if exists
    if (Test-Path -LiteralPath $launcherExe) {
        try { Remove-Item -LiteralPath $launcherExe -Force -ErrorAction Stop }
        catch {
            # If can't delete, try alternative name
            $launcherExe = Join-Path $out ($baseSafe + '.exe')
            if (Test-Path -LiteralPath $launcherExe) { Remove-Item -LiteralPath $launcherExe -Force -ErrorAction Stop }
        }
    }

    $cscArgs = '/nologo /target:winexe /optimize+ '
    $cscArgs += '/reference:System.Windows.Forms.dll /reference:System.Drawing.dll '
    $cscArgs += "/resource:`"$payloadExe`",setup-payload.exe "
    if ($prepLogoFile) { $cscArgs += "/resource:`"$prepLogoFile`",preparation-logo " }
    if (Test-Path $manifestPath) { $cscArgs += "/win32manifest:`"$manifestPath`" " }
    if ($iconFile -and (Test-Path $iconFile)) { $cscArgs += "/win32icon:`"$iconFile`" " }
    $cscArgs += "/out:`"$launcherExe`" `"$launcherPath`""
    
    Add-Log "Compiling launcher: csc $cscArgs"

    $cscPsi = New-Object System.Diagnostics.ProcessStartInfo
    $cscPsi.FileName = $csc
    $cscPsi.Arguments = $cscArgs
    $cscPsi.UseShellExecute = $false
    $cscPsi.RedirectStandardOutput = $true
    $cscPsi.RedirectStandardError = $true
    $cscPsi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $cscPsi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $cscPsi.CreateNoWindow = $true
    $cscProc = [System.Diagnostics.Process]::Start($cscPsi)
    $cscOut = $cscProc.StandardOutput.ReadToEnd()
    $cscErr = $cscProc.StandardError.ReadToEnd()
    $cscProc.WaitForExit()
    if ($cscOut) { $cscOut -split "`n" | ForEach-Object { if ($_.Trim()) { Add-Log $_.Trim() } } }
    if ($cscErr) { $cscErr -split "`n" | ForEach-Object { if ($_.Trim()) { Add-Log $_.Trim() } } }
    if ($cscProc.ExitCode -ne 0) { throw "Launcher compilation failed (exit code: $($cscProc.ExitCode))" }
    if (-not (Test-Path -LiteralPath $launcherExe)) { throw "Launcher EXE not found: $launcherExe" }
    
    $payloadKB = [math]::Round((Get-Item $payloadExe).Length/1KB, 1)
    $launcherKB = [math]::Round((Get-Item $launcherExe).Length/1KB, 1)
    Add-Log "Launcher compiled: $launcherExe ($launcherKB KB, payload embedded: $payloadKB KB)"

    # Clean up payload (it's now embedded in the launcher)
    Remove-Item -LiteralPath $payloadExe -Force -ErrorAction SilentlyContinue

    $output = $launcherExe
    $progress = 100
    Add-Log 'BUILD COMPLETE'
    Add-Log "Output: $launcherExe"

    # Clean up temp
    Remove-Item -Recurse -Force $buildTemp -ErrorAction SilentlyContinue

    Save-Status 'done'

} catch {
    Add-Log ('[ERROR] ' + $_.Exception.Message)
    Save-Status 'error' $_.Exception.Message
    exit 1
}
