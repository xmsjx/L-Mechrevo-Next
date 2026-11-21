@echo off
chcp 65001 >nul
setlocal
"D:\openLM\dotnet-sdk\dotnet.exe" run --project "%~dp0EcKbdAnimProbe.csproj" -c Release -v:q -- --menu
echo.
pause