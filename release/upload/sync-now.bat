@echo off
rem ============================================================
rem  Run one sync now, then print the summary.
rem  Extra arguments are passed through, e.g.:
rem     sync-now.bat --dry-run     (preview only, no upload/delete)
rem ============================================================
chcp 65001 >nul
start "" /wait "%~dp0SyncAgent.exe" --config "%~dp0config.ini" --sync %*
if exist "%~dp0logs\last-run.txt" type "%~dp0logs\last-run.txt"
echo.
pause
