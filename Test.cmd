@echo off
rem Quick test: drag a PDF (or Word/PowerPoint/picture) onto this file.
rem It rebuilds only what changed and runs the app on that file. No file = file picker.
cd /d "%~dp0"
taskkill /im ConvertToMarkdown.exe /f >nul 2>&1
dotnet run -c Release -- %*
if errorlevel 1 pause
