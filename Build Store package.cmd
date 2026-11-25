@echo off
rem Makes the package to upload to Microsoft Partner Center (StorePackage\ConvertToMarkdown_x.x.x.0_x64.msix).
rem Fill in StoreIdentity.txt first (from Partner Center > Product identity).
cd /d "%~dp0"
taskkill /im ConvertToMarkdown.exe /f >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0MakeStorePackage.ps1"
if errorlevel 1 (
  echo.
  echo The Store package could not be made. Copy the red lines above and send them to Claude.
  pause
  exit /b 1
)
start "" "%~dp0StorePackage"
pause
