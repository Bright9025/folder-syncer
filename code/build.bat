@echo off
rem ============================================================
rem  Build FolderSyncer.exe from code\src and copy it into both
rem  release folders (upload + archive). No IDE needed.
rem ============================================================
setlocal
set "HERE=%~dp0"
set "SRC=%HERE%src"
set "OUT_UPLOAD=%HERE%..\release\upload"
set "OUT_ARCHIVE=%HERE%..\release\archive"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto :nocsc

if not exist "%OUT_UPLOAD%" mkdir "%OUT_UPLOAD%"
if not exist "%OUT_ARCHIVE%" mkdir "%OUT_ARCHIVE%"

echo Compiling FolderSyncer.exe ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:"%OUT_UPLOAD%\FolderSyncer.exe" /reference:System.dll /reference:System.Core.dll "%SRC%\*.cs"
if errorlevel 1 goto :failed

copy /y "%OUT_UPLOAD%\FolderSyncer.exe" "%OUT_ARCHIVE%\FolderSyncer.exe" >nul
echo [OK] built:
echo   %OUT_UPLOAD%\FolderSyncer.exe
echo   %OUT_ARCHIVE%\FolderSyncer.exe
endlocal
exit /b 0

:nocsc
echo [ERROR] csc.exe not found. .NET Framework 4.x is required.
exit /b 1

:failed
echo [ERROR] build failed
exit /b 1
