@echo off
rem ============================================================
rem  Register the Windows scheduled task for FolderSyncer.
rem  Run this once. If your C: drive gets reset, run it again.
rem  Report file: logs\install-report.txt
rem ============================================================
chcp 65001 >nul
start "" /wait "%~dp0FolderSyncer.exe" --config "%~dp0config.ini" --install-task
if exist "%~dp0logs\install-report.txt" type "%~dp0logs\install-report.txt"
echo.
pause
