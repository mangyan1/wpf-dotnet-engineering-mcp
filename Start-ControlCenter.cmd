@echo off
setlocal
cd /d "%~dp0"
set "PROJECT=src\EngineeringMcp.ControlCenter\EngineeringMcp.ControlCenter.csproj"
set "EXE=src\EngineeringMcp.ControlCenter\bin\Debug\net10.0-windows10.0.19041.0\EngineeringMcp.ControlCenter.exe"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 10 SDK was not found on PATH.
  echo Install the .NET 10 SDK, then double-click this file again.
  pause
  exit /b 1
)

echo Preparing .NET/WPF Engineering MCP Developer Control Center...
dotnet build "%PROJECT%" --nologo --verbosity:minimal
if errorlevel 1 (
  echo.
  echo Control Center build failed. The compiler output above is the authoritative error report.
  pause
  exit /b 1
)

if not exist "%EXE%" (
  echo Control Center executable was not produced at:
  echo %EXE%
  pause
  exit /b 1
)

start "" "%EXE%"
exit /b 0
