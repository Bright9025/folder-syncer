@echo off
rem ============================================================
rem  Archive mode (the always-on PC): pull files from the server,
rem  save them locally, delete the server copies, update manifest.
rem  Extra arguments are passed through, e.g.:
rem     archive-now.bat --dry-run
rem ============================================================
chcp 65001 >nul
start "" /wait "%~dp0SyncAgent.exe" --config "%~dp0archive.ini" --archive %*
if exist "%~dp0logs\archive-report.txt" type "%~dp0logs\archive-report.txt"
echo.
pause
