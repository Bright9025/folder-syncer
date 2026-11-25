@echo off
rem ============================================================
rem  Check server connection / config, then print the report.
rem  Report file: logs\test-report.txt
rem ============================================================
chcp 65001 >nul
start "" /wait "%~dp0SyncAgent.exe" --config "%~dp0config.ini" --test
if exist "%~dp0logs\test-report.txt" type "%~dp0logs\test-report.txt"
echo.
pause
