@echo off
rem Downloads the built-in AI once (about 5 GB).
cd /d "%~dp0"
taskkill /im ConvertToMarkdown.exe /f >nul 2>&1
dotnet run -c Release -- --setup-ai
if errorlevel 1 pause
