@echo off
rem Downloads Qwen3-VL 4B (about 3 GB) and switches the app to it. Gemma stays installed.
cd /d "%~dp0"
taskkill /im ConvertToMarkdown.exe /f >nul 2>&1
dotnet run -c Release -- --setup-ai qwen
if errorlevel 1 pause
