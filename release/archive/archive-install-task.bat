@echo off
rem ============================================================
rem  Register the archive scheduled task (every N minutes,
rem  see repeat_minutes in archive.ini).
rem  Report file: logs\install-report.txt
rem ============================================================
chcp 65001 >nul
start "" /wait "%~dp0FolderSyncer.exe" --config "%~dp0archive.ini" --install-task
if exist "%~dp0logs\install-report.txt" type "%~dp0logs\install-report.txt"
echo.
pause
