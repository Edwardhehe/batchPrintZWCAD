@echo off
setlocal
set "PS64=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" (
  set "PS64=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
)
"%PS64%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
if errorlevel 1 echo.
pause
