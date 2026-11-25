@echo off
cd /d "%~dp0"
taskkill /im ConvertToMarkdown.exe /f >nul 2>&1
dotnet run -c Release -- --ai-model qwen
if errorlevel 1 pause
