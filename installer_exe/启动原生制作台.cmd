@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer-studio-native.ps1"
if errorlevel 1 (
  echo.
  echo 制作台启动失败。请检查 .NET Framework 4 和脚本文件是否完整。
  pause
)
