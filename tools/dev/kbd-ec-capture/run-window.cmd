@echo off
chcp 65001 >nul
echo Window scan (read only), 0x700-0x7FF, 60 seconds.
echo Press Fn brightness keys 2-3 times while it runs.
pause
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0kbd-ec-probe.ps1" -Mode window -Seconds 60
pause
