@echo off
rem Builds Convert to Markdown into one self-contained file, plus "Convert to Markdown Setup.exe" to install it.
rem The first build downloads what it needs (the .NET parts and the packaging tools), so it needs internet once.
cd /d "%~dp0"
taskkill /im ConvertToMarkdown.exe /f >nul 2>&1
echo Getting the build parts...
dotnet restore ConvertToMarkdown.csproj -r win-x64
if errorlevel 1 (
  echo Restore failed. Copy the red error lines above and send them to Claude.
  pause
  exit /b 1
)
echo.
echo Making the right-click menu package...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0MakeMenuPackage.ps1"
if errorlevel 1 (
  echo The right-click menu package could not be made - continuing without it. Send the red lines above to Claude.
  if exist "%~dp0menu-package\ConvertToMarkdown.Menu.msix" del "%~dp0menu-package\ConvertToMarkdown.Menu.msix"
)
echo.
echo Building Convert to Markdown...
echo.
dotnet publish ConvertToMarkdown.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%~dp0App"
if errorlevel 1 (
  echo.
  echo Build failed. Copy the red error lines above and send them to Claude.
  pause
  exit /b 1
)
copy /y "%~dp0App\ConvertToMarkdown.exe" "%~dp0App\Convert to Markdown Setup.exe" >nul
echo.
echo Done: App\Convert to Markdown Setup.exe  (double-click it to install)
start "" "%~dp0App"
pause
