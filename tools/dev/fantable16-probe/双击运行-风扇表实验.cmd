@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
title 同方风扇表实验

rem 没有管理员权限就用 PowerShell 以管理员身份重新拉起本脚本。
net session >nul 2>nul
if errorlevel 1 (
    echo 正在请求管理员权限...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo 已获得管理员权限。
"%~dp0LM-FanTable16Probe.exe" %*
echo.
echo 程序已退出。按任意键关闭窗口。
pause >nul