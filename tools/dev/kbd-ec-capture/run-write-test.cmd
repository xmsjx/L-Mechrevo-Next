@echo off
chcp 65001 >nul
echo Write test: 0x41 then 0x30 then 0x50, each with readback.
echo Watch the keyboard LED. Press any key to start...
pause >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0kbd-ec-probe.ps1" -Mode write -Addr 0x78C -Value 0x41 -IUnderstandWrite
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0kbd-ec-probe.ps1" -Mode write -Addr 0x78C -Value 0x30 -IUnderstandWrite
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0kbd-ec-probe.ps1" -Mode write -Addr 0x78C -Value 0x50 -IUnderstandWrite
pause
