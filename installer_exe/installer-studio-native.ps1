#Requires -Version 4.0
# 编译并启动无 HTTP 的原生 WinForms Installer Studio。
$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $scriptDir 'installer-studio-native.cs'
$output = Join-Path $scriptDir 'installer-studio-native.exe'
$csc = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw '未找到 .NET Framework 4 C# 编译器 csc.exe。' }
if (-not (Test-Path -LiteralPath $source)) { throw "未找到源文件：$source" }

$needsCompile = -not (Test-Path -LiteralPath $output) -or ((Get-Item -LiteralPath $source).LastWriteTime -gt (Get-Item -LiteralPath $output).LastWriteTime)
if ($needsCompile) {
    $args = '/nologo /target:winexe /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /out:"' + $output + '" "' + $source + '"'
    & $csc $args
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $output)) { throw "编译失败（退出码：$LASTEXITCODE）。" }
}
Start-Process -FilePath $output -WorkingDirectory $scriptDir
