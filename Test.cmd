@echo off
rem Quick test: drag a PDF (or Word, PowerPoint, Excel, picture) onto this file.
rem It builds the code in this folder and converts that file with it. No file = Settings window.
rem The build messages are also saved in test-build.log.
cd /d "%~dp0"
dotnet --list-sdks 2>nul | findstr /r "^8\. ^9\. ^1[0-9]\." >nul
if errorlevel 1 goto nosdk
taskkill /im ConvertToMarkdown.exe /f >nul 2>&1
echo Building the app - the first time takes about a minute...
dotnet build ConvertToMarkdown.csproj -c Release -nologo -v q > test-build.log 2>&1
if errorlevel 1 goto failed
echo Build OK. Converting...
dotnet run -c Release --no-build -- %*
echo.
echo Finished. The .md file is next to the original file.
pause
exit /b 0

:failed
echo.
echo BUILD FAILED. The errors are below and in test-build.log:
echo.
findstr /i /c:"error" test-build.log
echo.
echo Copy the lines above and send them to Claude.
pause
exit /b 1

:nosdk
echo.
echo The .NET 8 SDK is not installed, so the app cannot be built.
echo Install it from: https://dotnet.microsoft.com/download/dotnet/8.0
echo Choose "SDK 8.0" - Windows x64 installer. Then run this again.
echo.
start "" "https://dotnet.microsoft.com/download/dotnet/8.0"
pause
exit /b 1
