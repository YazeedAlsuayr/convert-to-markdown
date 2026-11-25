@echo off
rem Deletes the Gemma 4 model files (about 5 GB). Qwen stays.
cd /d "%~dp0"
taskkill /im ConvertToMarkdown.exe /f >nul 2>&1
dotnet run -c Release -- --remove-model gemma
if errorlevel 1 pause
