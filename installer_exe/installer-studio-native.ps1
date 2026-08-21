#Requires -Version 4.0
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms

function Get-ChineseText {
    param([int[]]$Codes)
    return -join ($Codes | ForEach-Object { [char]$_ })
}

function Show-LaunchError {
    param([string]$Message)
    $title = 'Installer Studio Native'
    $prefix = Get-ChineseText @(21046,20316,21488,26080,27861,21551,12290)
    [void][System.Windows.Forms.MessageBox]::Show(
        ($prefix + "`r`n`r`n" + $Message),
        $title,
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error
    )
}

try {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $source = Join-Path $scriptDir 'installer-studio-native.cs'
    $output = Join-Path $scriptDir 'installer-studio-native.exe'
    $csc = @(
        'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
        'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

    if (-not $csc) { throw (Get-ChineseText @(26410,25214,21040,67,35,32534,35793,22120,12290)) }
    if (-not (Test-Path -LiteralPath $source)) { throw ((Get-ChineseText @(26410,25214,21040,28304,25991,20214,65306)) + $source) }

    $outputArgument = '/out:' + $output
    $compileArgs = @(
        '/nologo'
        '/target:winexe'
        '/optimize+'
        '/reference:System.Windows.Forms.dll'
        '/reference:System.Drawing.dll'
        '/reference:System.Web.dll'
        '/reference:System.Web.Extensions.dll'
        $outputArgument
        $source
    )
    & $csc @compileArgs
    $compileExitCode = $LASTEXITCODE
    if ($compileExitCode -ne 0 -or -not (Test-Path -LiteralPath $output)) {
        throw ((Get-ChineseText @(32534,35793,22833,36133,65292,36864,20986,20195,30721,65306)) + $compileExitCode)
    }

    $process = Start-Process -FilePath $output -WorkingDirectory $scriptDir -PassThru -ErrorAction Stop
    if (-not $process -or $process.HasExited) {
        throw (Get-ChineseText @(21046,20316,21488,36827,31243,26410,33021,25104,21151,21551,12290))
    }
    exit 0
}
catch {
    $detail = $_.Exception.Message
    if ([string]::IsNullOrWhiteSpace($detail)) { $detail = Get-ChineseText @(21457,29983,26410,30693,38169,35823,12290) }
    Show-LaunchError $detail
    exit 1
}
