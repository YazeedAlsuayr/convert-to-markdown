@echo off
rem Opens the Settings window (describe tick, AI on/off, model).
cd /d "%~dp0"
taskkill /im ConvertToMarkdown.exe /f >/dev/null 2>&1
dotnet run -c Release -- --settings
if errorlevel 1 pause
