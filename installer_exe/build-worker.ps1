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
# 本次无覆盖/删除构建不创建或改写锁文件；调用方负责避免并发使用同一工作目录。

function Save-Status($state, $error='') {
    # 写入同目录临时文件后原子替换，UI 永远不会读到只写了一半的 JSON。
    $json = @{ status = $state; progress = $progress; log = @($log); output = $output; error = $error } |
        ConvertTo-Json -Depth 10 -Compress
    $tmp = $StatusFile + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($tmp, $json, (New-Object Text.UTF8Encoding $false))
    if (Test-Path -LiteralPath $StatusFile) {
        try { [IO.File]::Replace($tmp, $StatusFile, $null) }
        catch { Move-Item -LiteralPath $tmp -Destination $StatusFile -Force }
    } else { Move-Item -LiteralPath $tmp -Destination $StatusFile -Force }
}
function Add-Log($text) {
    $script:log += @($text)
    Save-Status 'running'
}
function Test-SafeRelativePath($value, $label, $requireFileName) {
    $path = ([string]$value).Trim().Replace('/', [string][char]92)
    if (-not $path) { throw ($label + ' is required.') }
    if ([IO.Path]::IsPathRooted($path) -or $path[0] -eq [char]92) { throw ($label + ' must be relative to the install root.') }
    $parts = $path.Split([char]92)
    if ($parts | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -eq '.' -or $_ -eq '..' }) { throw ($label + ' contains an unsafe path segment.') }
    if ($requireFileName -and [string]::IsNullOrWhiteSpace([IO.Path]::GetFileName($path))) { throw ($label + ' must include a file name.') }
    return ($parts -join [string][char]92)
}
function Get-BaseFileManifest($root, $files) {
    $rootPath = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'; $manifest = @()
    foreach ($file in $files | Sort-Object FullName) { $fullPath = [IO.Path]::GetFullPath($file.FullName); if (-not $fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) { throw "基础文件超出源目录：$fullPath" }; $manifest += [PSCustomObject]@{ relativePath = $fullPath.Substring($rootPath.Length).Replace('\', '/'); length = [Int64]$file.Length } }
    return @($manifest)
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
    $installPath = [string]$config.installPath; if (-not $installPath) { $installPath = 'C:\Program Files\' + $name }
    $mainExe = [string]$config.mainExe
    $allowInstallDirSelection = if ($null -eq $config.allowInstallDirSelection) { $true } else { [bool]$config.allowInstallDirSelection }
    $createDesktop = [bool]$config.createDesktopShortcut
    $createStartMenu = [bool]$config.createStartMenuShortcut
    $createStartup = [bool]$config.createStartupEntry
    $startupName = [string]$config.startupEntryName
    $startupArgs = [string]$config.startupArguments
    $desktopArgs = [string]$config.desktopArguments
    $startMenuArgs = [string]$config.startMenuArguments
    $runtimePathEntries = @($config.runtimePathEntries | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    # 仅允许产品私有运行时目录；绝不从旧全局 PATH 配置迁移值。
    $runtimePathEntries = @($runtimePathEntries | ForEach-Object {
        $entry = ([string]$_).Trim() -replace '/', '\'
        if ($entry.Equals('{app}', [StringComparison]::OrdinalIgnoreCase)) { return '{app}' }
        if (-not $entry.StartsWith('{app}\', [StringComparison]::OrdinalIgnoreCase)) { throw "运行时依赖目录必须以 {app} 为安装根：$($_)" }
        $relative = $entry.Substring(6).Trim('\')
        if ($relative.Contains('..')) { throw "运行时依赖目录必须是 {app} 下的相对路径：$($_)" }
        if ($relative) { '{app}\' + $relative } else { '{app}' }
    })
    $cleanupDesktop = if ($null -eq $config.cleanupDesktopShortcut) { $true } else { [bool]$config.cleanupDesktopShortcut }
    $cleanupStartMenu = if ($null -eq $config.cleanupStartMenuShortcut) { $true } else { [bool]$config.cleanupStartMenuShortcut }
    $cleanupStartup = if ($null -eq $config.cleanupStartupEntry) { $true } else { [bool]$config.cleanupStartupEntry }
    $cleanupInstallDir = [bool]$config.cleanupInstallDirectory
    $buildLogRefreshPercent = 10
    try { $requestedRefresh = [int]$config.buildLogRefreshPercent; if ($requestedRefresh -in 5, 10, 20) { $buildLogRefreshPercent = $requestedRefresh } } catch { }

    # Calculate stub size
    $sourceFiles = Get-ChildItem -LiteralPath $config.sourceDir -Recurse -File -ErrorAction SilentlyContinue
    $sourceSizeBytes = ($sourceFiles | Measure-Object -Property Length -Sum).Sum
    if (-not $sourceSizeBytes) { $sourceSizeBytes = 0 }
    $stubMB = [math]::Round($sourceSizeBytes / 1MB, 1)

    # 在线组件仅支持受控的 file、zip、rar 或 tar.gz，且只会原样保存，不解压或执行。
    $components = @($config.optionalComponents | Where-Object { $_.enabled -eq $true -and $_.name -and $_.downloadUrl })
    $componentUrls = @{}
    foreach ($component in $components) {
        $componentType = if ([string]$component.type) { ([string]$component.type).Trim().ToLowerInvariant() } else { 'file' }
        if ($componentType -notin @('file','zip','rar','tar.gz')) { throw "组件类型仅支持 file、zip、rar 或 tar.gz：$($component.name)" }
        $component.savePath = Test-SafeRelativePath $component.savePath "组件 savePath（$($component.name)）" $true
        try { $componentUri = [Uri]([string]$component.downloadUrl); if (-not $componentUri.IsAbsoluteUri -or $componentUri.Scheme -ne 'https') { throw 'not https' } } catch { throw "组件下载 URL 必须是合法 HTTPS 地址：$($component.name)" }
        if ($componentUrls.ContainsKey([string]$component.downloadUrl)) { throw "不同组件不能使用相同下载地址：$($component.name) 与 $($componentUrls[[string]$component.downloadUrl])" }
        $componentUrls[[string]$component.downloadUrl] = $component.name
        $component.type = $componentType
        $component | Add-Member -NotePropertyName sizeBytes -NotePropertyValue 0 -Force
        try {
            $request = [System.Net.HttpWebRequest]::Create([string]$component.downloadUrl)
            $request.Method = 'HEAD'
            $request.Timeout = 10000
            $request.ReadWriteTimeout = 10000
            $response = $request.GetResponse()
            try { if ($response.ContentLength -gt 0) { $component.sizeBytes = [Int64]$response.ContentLength } }
            finally { $response.Close() }
        }
        catch { Add-Log "Component size HEAD probe unavailable: $($component.name)" }
        if ($component.sizeBytes -gt 0) { Add-Log "Component size fallback written: $($component.name) = $([math]::Round($component.sizeBytes / 1MB, 1)) MB" }
        else { Add-Log "Component size unavailable: $($component.name)" }
    }

    # 构建名称只替换 Windows 禁止字符；将双引号作为 char 追加，避免正则字符串中的引号歧义。
    $invalidFileNamePattern = '[\\/:*?<>|]' + [char]34
    $base = ($name + '-Setup-' + $version) -replace $invalidFileNamePattern, '_'
    # ISCC may not handle non-ASCII in OutputBaseFilename; use ASCII-safe name and rename after
    $baseSafe = $base -replace '[^\x20-\x7E]', '_'
    $appId = ([string]$config.productId).Trim('{}')
    if (-not $appId) { $appId = ([string]$config.upgradeCode).Trim('{}') }
    $parsedProductId = [Guid]::Empty
    if (-not [Guid]::TryParse($appId, [ref]$parsedProductId)) { throw '产品唯一 ID 无效。每个产品模板必须配置独立 GUID，构建已停止。' }
    $appId = $parsedProductId.ToString().ToUpper()

    New-Item -ItemType Directory -Force -Path $out | Out-Null
    # 预检输出盘与构建临时盘空间。压缩率不可预测，保守要求源文件大小加 1GB 可用空间。
    $requiredBytes = [Int64]$sourceSizeBytes + 1GB
    $outRoot = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($out))
    $tempRoot = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($ScriptDir))
    $outFree = (Get-PSDrive -Name $outRoot.TrimEnd([char]92).TrimEnd(':') -ErrorAction Stop).Free
    $tempFree = (Get-PSDrive -Name $tempRoot.TrimEnd([char]92).TrimEnd(':') -ErrorAction Stop).Free
    if ($outFree -lt $requiredBytes) { throw "输出盘可用空间不足。需要至少 $([math]::Round($requiredBytes/1GB,2)) GB。" }
    if ($tempFree -lt $requiredBytes) { throw "构建盘可用空间不足。需要至少 $([math]::Round($requiredBytes/1GB,2)) GB。" }
    Add-Log "磁盘预检通过：输出盘 $([math]::Round($outFree/1GB,2)) GB，构建盘 $([math]::Round($tempFree/1GB,2)) GB 可用。"

    # 允许调用方已明确授权的正式覆盖构建；同名 .partial 仍视为未完成构建证据，不能触碰。
    $plannedLauncherExe = Join-Path $out ($base + '.exe')
    $plannedPartialExe = $plannedLauncherExe + '.partial'
    if (Test-Path -LiteralPath $plannedLauncherExe) { Add-Log "Existing output will be replaced after final validation: $plannedLauncherExe" }
    if (Test-Path -LiteralPath $plannedPartialExe) { throw "临时输出文件已存在，构建已停止以保护现有内容：$plannedPartialExe" }

    # --- Create isolated build workspace ---
    # 不清理其他 _build_* 目录：它们可能属于仍在运行的大包构建，删除会导致输出冲突或构建损坏。
    $buildTemp = Join-Path $ScriptDir ('_build_' + [Guid]::NewGuid().ToString('N').Substring(0,8))
    $innoOutput = Join-Path $buildTemp 'inno-output'
    New-Item -ItemType Directory -Force -Path $buildTemp | Out-Null
    New-Item -ItemType Directory -Force -Path $innoOutput | Out-Null
    # --- Index source files without copying them ---
    # Inno Setup 直接从源目录构建，避免构建阶段额外产生一份完整副本。
    $progress = 15
    Add-Log "Indexing source files for direct packaging..."
    $sourceFiles = @(Get-ChildItem -LiteralPath $config.sourceDir -Recurse -File -ErrorAction SilentlyContinue)
    $sourceSizeBytes = ($sourceFiles | Measure-Object -Property Length -Sum).Sum
    if (-not $sourceSizeBytes) { $sourceSizeBytes = 0 }
    $stubMB = [math]::Round($sourceSizeBytes / 1MB, 1)
    Add-Log "Source: $($sourceFiles.Count) files, $stubMB MB; no build-time source copy will be created."
    Add-Log "Generating verified base file manifest..."
    $baseFileManifest = @(Get-BaseFileManifest $config.sourceDir $sourceFiles)
    Add-Log "Base file manifest: $($baseFileManifest.Count) files."
    $sourceFingerprint = "{0}:{1}" -f $sourceFiles.Count, $sourceSizeBytes
    if ($mainExe) {
        $mainExeCheck = Join-Path $config.sourceDir $mainExe
        if (-not (Test-Path -LiteralPath $mainExeCheck -PathType Leaf)) { throw "主程序未在源目录根级找到：$mainExe。请填写相对于基础程序目录的正确路径。" }
        Add-Log "Main executable verified: $mainExe"
    }
    $progress = 50
    Save-Status 'running'

    # --- Build config JSON ---
    $configObj = @{
        productName = $name
        productId = $appId
        upgradeCode = $appId
        version = $version
        publisher = $publisher
        subtitle = $subtitle
        componentTitle = [string]$config.componentTitle
        installPath = $installPath
        mainExe = $mainExe
        controlPanelIcon = $(if ([string]$config.iconPath) { 'installer-product-icon.ico' } else { '' })
        allowInstallDirSelection = $allowInstallDirSelection
        createDesktopShortcut = $createDesktop
        createStartMenuShortcut = $createStartMenu
        createStartupEntry = $createStartup
        startupEntryName = $startupName
        startupArguments = $startupArgs
        desktopArguments = $desktopArgs
        startMenuArguments = $startMenuArgs
        runtimePathEntries = @($runtimePathEntries)
        cleanupDesktopShortcut = $cleanupDesktop
        cleanupStartMenuShortcut = $cleanupStartMenu
        cleanupStartupEntry = $cleanupStartup
        cleanupInstallDirectory = $cleanupInstallDir
        stubMB = $stubMB
        baseFileManifest = @($baseFileManifest)
        optionalComponents = @($components | ForEach-Object {
            @{ enabled=$true; name=$_.name; type=$_.type; downloadUrl=$_.downloadUrl; savePath=[string]$_.savePath; required=[bool]$_.required; sizeBytes=[Int64]$_.sizeBytes }
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

    # 同一份品牌 Logo 同时嵌入准备窗口和客户端安装窗口。
    $prepLogoFile = ''
    $prepLogoPath = [string]$config.prepLogoPath
    if ($prepLogoPath) {
        if (-not (Test-Path -LiteralPath $prepLogoPath -PathType Leaf)) { throw "Preparation logo not found: $prepLogoPath" }
        $extension = [IO.Path]::GetExtension($prepLogoPath).ToLowerInvariant()
        if ($extension -notin @('.png', '.jpg', '.jpeg', '.bmp')) { throw 'Preparation logo must be PNG, JPG or BMP.' }
        $prepLogoFile = Join-Path $buildTemp ('preparation-logo' + $extension)
        Copy-Item -LiteralPath $prepLogoPath -Destination $prepLogoFile -Force
        # 裁去接近白色的无效画布边距，让Logo主体与统一左对齐栅格真正对齐。
        try {
            Add-Type -AssemblyName System.Drawing
            $image = [Drawing.Bitmap]::FromFile($prepLogoFile)
            $left=$image.Width; $top=$image.Height; $right=-1; $bottom=-1
            for($y=0; $y -lt $image.Height; $y++){ for($x=0; $x -lt $image.Width; $x++){ $p=$image.GetPixel($x,$y); if($p.A -gt 10 -and ($p.R -lt 245 -or $p.G -lt 245 -or $p.B -lt 245)){ if($x -lt $left){$left=$x}; if($x -gt $right){$right=$x}; if($y -lt $top){$top=$y}; if($y -gt $bottom){$bottom=$y} } } }
            if($right -ge $left -and $bottom -ge $top){ $pad=8; $rect=New-Object Drawing.Rectangle([math]::Max(0,$left-$pad),[math]::Max(0,$top-$pad),[math]::Min($image.Width,$right-$left+1+$pad*2),[math]::Min($image.Height,$bottom-$top+1+$pad*2)); $cropped=New-Object Drawing.Bitmap($rect.Width,$rect.Height); $graphics=[Drawing.Graphics]::FromImage($cropped); $graphics.DrawImage($image,(New-Object Drawing.Rectangle(0,0,$cropped.Width,$cropped.Height)),$rect,[Drawing.GraphicsUnit]::Pixel); $graphics.Dispose(); $image.Dispose(); $cropped.Save($prepLogoFile,[Drawing.Imaging.ImageFormat]::Png); $cropped.Dispose(); Add-Log 'Preparation logo cropped to visible content' } else { $image.Dispose() }
        } catch { Add-Log 'Preparation logo crop skipped' }
        Add-Log "Preparation logo: $prepLogoPath"
    }

    $appExePath = Join-Path $buildTemp 'installer-app.exe'
    $cscArgs = '/nologo /target:winexe /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll '
    if ($prepLogoFile) { $cscArgs += "/resource:`"$prepLogoFile`",preparation-logo " }
    $cscArgs += '/out:"' + $appExePath + '" "' + $csPath + '"'
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

    # --- Compile runtime PATH launcher ---
    $runtimeLauncherTemplate = Join-Path $ScriptDir 'runtime-launcher.cs'
    if (-not (Test-Path -LiteralPath $runtimeLauncherTemplate -PathType Leaf)) { throw 'runtime-launcher.cs template not found' }
    $runtimeLauncherExe = Join-Path $buildTemp 'runtime-launcher.exe'
    $runtimeLauncherArgs = '/nologo /target:winexe /optimize+ /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /out:"' + $runtimeLauncherExe + '" "' + $runtimeLauncherTemplate + '"'
    $runtimeProc = Start-Process -FilePath $csc -ArgumentList $runtimeLauncherArgs -Wait -PassThru -NoNewWindow
    if ($runtimeProc.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $runtimeLauncherExe -PathType Leaf)) { throw 'Runtime launcher compilation failed.' }
    Add-Log "Runtime PATH launcher compiled: $runtimeLauncherExe"

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
    # 产品图标随主程序直接部署到用户选择的 {app} 目录，供后置配置器登记控制面板图标。
    $productIconSourceLine = if ($iconFile) { "Source: `"$iconFile`"; DestDir: `"{app}`"; DestName: `"installer-product-icon.ico`"; Flags: ignoreversion`r`n" } else { '' }

    $iss = @"
[Setup]
AppName=$name
AppVersion=$version
AppPublisher=$publisher
AppId={{}}$appId
DefaultDirName={param:InstallDir|$installPath}
CreateAppDir=yes
Uninstallable=no
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
DisableStartupPrompt=yes
OutputDir=$innoOutput
OutputBaseFilename=setup-payload
; normal 压缩档位减少启动时的解压 CPU 开销，优先缩短首次打开安装界面的等待时间。
Compression=lzma2/normal
SolidCompression=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
$iconLine
[Files]
; 基础程序由 Inno 直接释放到 {app}，即 Launcher 传入的 /InstallDir。
; 不再创建 LocalAppData Bootstrap\source，也不执行第二次文件复制。
Source: "$($config.sourceDir)\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; 运行时启动器在启动子进程时注入产品私有 PATH，不修改系统或用户 PATH。
Source: "$runtimeLauncherExe"; DestDir: "{app}"; DestName: "runtime-launcher.exe"; Flags: ignoreversion
; 后置配置器仅作为临时进程运行，负责后续快捷方式、组件和卸载登记。
Source: "$appExePath"; DestDir: "{tmp}"; DestName: "installer-configurator.exe"; Flags: deleteafterinstall ignoreversion
$productIconSourceLine

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  ReadyFile: String;
  SessionDir: String;
begin
  if CurStep = ssPostInstall then
  begin
    ReadyFile := ExpandConstant('{param:ReadyFile}');
    if ReadyFile = '' then ReadyFile := ExpandConstant('{%TEMP}\installer-ready.flag');
    SessionDir := ExpandConstant('{param:Session}');
    if SessionDir = '' then SessionDir := ExpandConstant('{tmp}');
    Exec(ExpandConstant('{tmp}\installer-configurator.exe'), '--silent --app "' + ExpandConstant('{app}') + '" --session "' + SessionDir + '"', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
"@

    $issPath = Join-Path $buildTemp 'build-wrapper.iss'
    [IO.File]::WriteAllText($issPath, $iss, (New-Object Text.UTF8Encoding $true))
    Add-Log "ISS wrapper generated"

    # --- Compile ---
    $progress = 70
    $largestFiles = @($sourceFiles | Sort-Object Length -Descending | Select-Object -First 5 | ForEach-Object { "$($_.FullName.Substring($config.sourceDir.Length).TrimStart([char]92)) ($([math]::Round($_.Length/1MB,1)) MB)" })
    Add-Log "开始压缩：$($sourceFiles.Count) 个文件，总计 $stubMB MB。"
    if ($largestFiles.Count) { Add-Log ("最大文件：" + ($largestFiles -join '；')) }
    Add-Log 'Inno Setup 正在压缩数据；将按文件批次更新进度。'
    # 不重定向 ISCC 的输出管道：超大包的高频输出可能堵塞管道并使编译器停在70%。
    # Inno Setup 不提供逐文件回调，按输出体积占源总量的比例估算文件批次；仅跨批次或产物增长足够时刷新。
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $iscc
    $psi.Arguments = '"' + $issPath + '"'
    $psi.WorkingDirectory = $buildTemp
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $false
    $psi.RedirectStandardError = $false
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $started = Get-Date; $payloadPath = Join-Path $innoOutput 'setup-payload.exe'
    # 以配置的文件比例（5%、10% 或 20%）作为唯一日志刷新阈值，默认 10%。
    $batchSize = [Math]::Max(1, [int][Math]::Ceiling($sourceFiles.Count * $buildLogRefreshPercent / 100.0))
    $lastBatch = -1
    while (-not $proc.HasExited) {
        $builtBytes = if (Test-Path -LiteralPath $payloadPath) { (Get-Item -LiteralPath $payloadPath).Length } else { [Int64]0 }
        # 压缩后产物通常小于源文件；以字节比例估算已进入压缩器的文件批次，只用于观察进度。
        $estimatedFiles = if ($sourceSizeBytes -gt 0) { [Math]::Min($sourceFiles.Count, [int][Math]::Floor($builtBytes / [double]$sourceSizeBytes * $sourceFiles.Count)) } else { 0 }
        $batch = [int][Math]::Floor($estimatedFiles / [double]$batchSize)
        if ($batch -gt $lastBatch) {
            $elapsed = [int]((Get-Date) - $started).TotalSeconds
            $progress = [Math]::Min(94, 70 + [int]($estimatedFiles / [double][Math]::Max(1,$sourceFiles.Count) * 24))
            Add-Log "压缩进度：约 $estimatedFiles / $($sourceFiles.Count) 个文件（每 $buildLogRefreshPercent% 刷新）；已生成 $([math]::Round($builtBytes/1MB,1)) MB；已用时 $elapsed 秒。"
            $lastBatch = $batch
        }
        Start-Sleep -Milliseconds 500
    }
    $exitCode = $proc.ExitCode

    if ($exitCode -ne 0) {
        throw "ISCC compilation failed (exit code: $exitCode)"
    }

    # --- Inno Setup produced setup-payload.exe, now compile the C# launcher ---
    $payloadExe = Join-Path $innoOutput 'setup-payload.exe'
    if (-not (Test-Path -LiteralPath $payloadExe)) {
        throw 'Inno Setup payload was not created: ' + $payloadExe
    }
    $sourceFilesAfter = @(Get-ChildItem -LiteralPath $config.sourceDir -Recurse -File -ErrorAction SilentlyContinue)
    $sourceFingerprintAfter = "{0}:{1}" -f $sourceFilesAfter.Count, (($sourceFilesAfter | Measure-Object -Property Length -Sum).Sum)
    if ($sourceFingerprintAfter -ne $sourceFingerprint) { throw '构建期间源目录发生变化。已停止生成交付包，请在文件稳定后重新构建。' }
    Add-Log "Payload built: $payloadExe ($([math]::Round((Get-Item $payloadExe).Length/1KB, 1)) KB)"

    # 无论载荷大小都生成自定义启动器；载荷在最终EXE尾部追加，避免.NET资源的2GB限制。
    $payloadLength = (Get-Item -LiteralPath $payloadExe).Length
    $progress = 85
    Add-Log 'Compiling custom launcher for appended payload...'

    # Read launcher template
    $launcherTemplate = Join-Path $ScriptDir 'launcher.cs'
    if (-not (Test-Path $launcherTemplate)) { throw 'launcher.cs template not found' }
    $launcherContent = [IO.File]::ReadAllText($launcherTemplate, [Text.Encoding]::UTF8)
    $launcherContent = $launcherContent.Replace('__PRODUCT_NAME__', $name.Replace('"', '\"'))
    $launcherContent = $launcherContent.Replace('__PRODUCT_VERSION__', $version.Replace('"', '\"'))
    $launcherContent = $launcherContent.Replace('__PRODUCT_SUBTITLE__', $subtitle.Replace('"', '\"'))
    # 前置选择器只需要产品安装选项与组件元数据；完整配置仍仅交给后置配置器。
    $launcherConfig = [ordered]@{
        installPath = $installPath
        allowInstallDirSelection = $allowInstallDirSelection
        componentTitle = [string]$config.componentTitle
        optionalComponents = @($components)
    } | ConvertTo-Json -Depth 12 -Compress
    $launcherContent = $launcherContent.Replace('__LAUNCHER_CONFIG_JSON__', $launcherConfig.Replace('"', '""'))
    $launcherPath = Join-Path $buildTemp 'launcher.cs'
    [IO.File]::WriteAllText($launcherPath, $launcherContent, (New-Object Text.UTF8Encoding $true))

    # Compile the small launcher stub; payload is appended after compilation.
    $manifestPath = Join-Path $ScriptDir 'app.manifest'
    $launcherExe = Join-Path $out ($base + '.exe')
    $launcherStub = Join-Path $buildTemp 'launcher-stub.exe'
    # 正式覆盖由调用方显式授权：仅在新单文件已通过尾部结构校验后才替换既有交付包。
    $partialExe = $launcherExe + '.partial'
    # 不删除或覆盖中断临时文件；若同名临时输出存在，停止构建以保护其内容。
    if (Test-Path -LiteralPath $partialExe) { throw "临时输出文件已存在，构建已停止以保护现有内容：$partialExe" }

    $cscArgs = '/nologo /target:winexe /optimize+ '
    $cscArgs += '/reference:System.Windows.Forms.dll /reference:System.Drawing.dll '
    if ($prepLogoFile) { $cscArgs += "/resource:`"$prepLogoFile`",preparation-logo " }
    # 同一 config.iconPath 同时作为启动器 PE 图标和嵌入式产品图标，供主窗体可靠加载。
    if ($iconFile -and (Test-Path -LiteralPath $iconFile -PathType Leaf)) { $cscArgs += "/resource:`"$iconFile`",installer-product-icon.ico /win32icon:`"$iconFile`" " }
    if (Test-Path $manifestPath) { $cscArgs += "/win32manifest:`"$manifestPath`" " }
    $cscArgs += "/out:`"$launcherStub`" `"$launcherPath`""
    
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
    if (-not (Test-Path -LiteralPath $launcherStub)) { throw "Launcher stub not found: $launcherStub" }

    # 单文件结构：小型启动器 + 原始 Inno 载荷 + 32 字节尾部元数据（标识、版本、偏移、长度、保留位）。
    $progress = 95; Add-Log 'Appending payload to single-file installer...'
    $stubLength = (Get-Item -LiteralPath $launcherStub).Length
    $footer = New-Object byte[] 32; [Text.Encoding]::ASCII.GetBytes('NCIAPAY2').CopyTo($footer, 0); [BitConverter]::GetBytes([Int32]2).CopyTo($footer, 8); [BitConverter]::GetBytes([Int64]$stubLength).CopyTo($footer, 12); [BitConverter]::GetBytes([Int64]$payloadLength).CopyTo($footer, 20)
    $buffer = New-Object byte[] (1024KB); $destination = New-Object IO.FileStream($partialExe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { foreach ($part in @($launcherStub, $payloadExe)) { $source = New-Object IO.FileStream($part, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read); try { while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) { $destination.Write($buffer, 0, $read) } } finally { $source.Dispose() } }; $destination.Write($footer, 0, $footer.Length) } finally { $destination.Dispose() }
    # 输出前重新读取尾部，确认启动器可以定位完整载荷。
    $verify = New-Object IO.FileStream($partialExe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read); try { $verify.Seek(-32, [IO.SeekOrigin]::End) | Out-Null; $check = New-Object byte[] 32; if ($verify.Read($check, 0, 32) -ne 32 -or [Text.Encoding]::ASCII.GetString($check,0,8) -ne 'NCIAPAY2' -or [BitConverter]::ToInt64($check,12) -ne $stubLength -or [BitConverter]::ToInt64($check,20) -ne $payloadLength) { throw 'Single-file payload footer validation failed.' } } finally { $verify.Dispose() }
    # 新产物已完成尾部结构校验；按本次显式授权原子替换旧安装器。
    if (Test-Path -LiteralPath $launcherExe) {
        # File.Replace 需要一个实际备份路径；保留旧交付包，且不删除任何文件。
        $previousExe = $launcherExe + '.previous'
        if (Test-Path -LiteralPath $previousExe) { $previousExe = $launcherExe + '.previous.' + (Get-Date -Format 'yyyyMMddHHmmss') }
        [IO.File]::Replace($partialExe, $launcherExe, $previousExe)
    } else {
        Move-Item -LiteralPath $partialExe -Destination $launcherExe -ErrorAction Stop
    }
    Add-Log "Single-file installer created: $launcherExe (payload $([math]::Round($payloadLength/1GB,2)) GB)"

    $output = $launcherExe
    $progress = 100
    Add-Log 'BUILD COMPLETE'
    Add-Log "Output: $launcherExe"

    # 保留本次独立构建临时目录，避免删除任何调试或构建证据。
    Add-Log "构建临时目录已保留：$buildTemp"

    Save-Status 'done'

} catch {
    Add-Log ('[ERROR] ' + $_.Exception.Message)
    Save-Status 'error' $_.Exception.Message
    exit 1
} finally {
    # 不删除、不改写任何锁文件或构建产物。
}
