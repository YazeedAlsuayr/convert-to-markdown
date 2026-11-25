@echo off
rem Shows what AI files are installed and tries to start the AI.
cd /d "%~dp0"
taskkill /im ConvertToMarkdown.exe /f >nul 2>&1
dotnet run -c Release -- --check-ai
if errorlevel 1 pause
