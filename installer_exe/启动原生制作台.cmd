@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0installer-studio-native.ps1"
exit /b %ERRORLEVEL%
