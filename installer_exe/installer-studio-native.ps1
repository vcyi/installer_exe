#Requires -Version 4.0
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $scriptDir 'installer-studio-native.cs'
$output = Join-Path $scriptDir 'installer-studio-native.exe'
$csc = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $csc) { throw 'C# compiler csc.exe was not found.' }
if (-not (Test-Path -LiteralPath $source)) { throw "Source file was not found: $source" }

$needsCompile = -not (Test-Path -LiteralPath $output) -or ((Get-Item -LiteralPath $source).LastWriteTime -gt (Get-Item -LiteralPath $output).LastWriteTime)
if ($needsCompile) {
    & $csc /nologo /target:winexe /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll "/out:$output" $source
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $output)) {
        throw "Compilation failed with exit code: $LASTEXITCODE"
    }
}

Start-Process -FilePath $output -WorkingDirectory $scriptDir
