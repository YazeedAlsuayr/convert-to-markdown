@echo off
rem Makes a signed TEST copy of the Store package and installs it on this PC,
rem so you can try the Store version before the Store has it.
rem Uninstall the Setup version first (Settings > Apps > Convert to Markdown), or you will see the menu entry twice.
cd /d "%~dp0"
taskkill /im ConvertToMarkdown.exe /f >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0MakeStorePackage.ps1" -Test
if errorlevel 1 (
  echo.
  echo The test package could not be made. Copy the red lines above and send them to Claude.
  pause
  exit /b 1
)
echo.
echo Installing the test package...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem '%~dp0StorePackage\*_test.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | ForEach-Object { Add-AppxPackage -Path $_.FullName -ForceUpdateFromAnyVersion -ForceApplicationShutdown }"
if errorlevel 1 (
  echo Install failed. Copy the red lines above and send them to Claude.
  pause
  exit /b 1
)
echo.
echo Installed. Open "Convert to Markdown" from the Start menu: the welcome screens appear the first time.
echo To remove the test copy: Settings ^> Apps ^> Convert to Markdown ^> Uninstall.
pause
