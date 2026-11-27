@echo off
rem cmd entry point for ota-check.ps1; no arguments starts the interactive menu
setlocal
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
where pwsh >nul 2>nul && set "PS=pwsh"
set "ARGS=%*"
if "%ARGS%"=="" set "ARGS=-Menu"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0ota-check.ps1" %ARGS%
set "RC=%ERRORLEVEL%"
if "%~1"=="" pause
endlocal & exit /b %RC%
