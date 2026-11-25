@echo off
rem ============================================================
rem  Remove the Windows scheduled task registered by install-task.bat
rem  Report file: logs\install-report.txt
rem ============================================================
chcp 65001 >nul
start "" /wait "%~dp0FolderSyncer.exe" --config "%~dp0config.ini" --uninstall-task
if exist "%~dp0logs\install-report.txt" type "%~dp0logs\install-report.txt"
echo.
pause
