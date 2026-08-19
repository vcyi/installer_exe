@echo off
setlocal
set "APP=%~dp0installer-studio-native.exe"

if not exist "%APP%" (
  echo.
  echo 未找到制作台程序：%APP%
  echo 请确认 installer-studio-native.exe 与本脚本位于同一目录。
  pause
  exit /b 1
)

start "Installer Studio" /D "%~dp0" "%APP%"
exit /b 0
