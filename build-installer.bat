@echo off
setlocal
cd /d "%~dp0"
echo WorkPilot Hybrid V1.4 - Release Builder
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-installer.ps1" -InstallPrerequisites
if errorlevel 1 (
  echo.
  echo Build failed. Review the error above and docs\BUILD_WINDOWS.md.
  pause
  exit /b 1
)
echo.
echo Installer created in artifacts\installer
pause
