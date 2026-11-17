@echo off
rem Test sequence step: launch QQ (see TestSequenceData MAIN_BAT_START_QQ).
rem Exit code 0 = QQ started (or check mode passed), non-zero = not found / launch failed.
rem Usage: StartQQ.bat [check]   -- "check" only reports the resolved path, never launches QQ.
setlocal

set "QQ_CHECK=0"
if /I "%~1"=="check" set "QQ_CHECK=1"

set "QQ_EXE="
for %%P in (
  "D:\QQ\QQ.exe"
  "%ProgramFiles%\Tencent\QQNT\QQ.exe"
  "%ProgramFiles(x86)%\Tencent\QQNT\QQ.exe"
  "%LocalAppData%\Programs\Tencent\QQNT\QQ.exe"
  "%ProgramFiles%\Tencent\QQ\Bin\QQ.exe"
  "%ProgramFiles(x86)%\Tencent\QQ\Bin\QQ.exe"
) do if not defined QQ_EXE if exist "%%~P" set "QQ_EXE=%%~P"

if "%QQ_CHECK%"=="1" (
  if defined QQ_EXE (
    echo [StartQQ] found %QQ_EXE%
  ) else (
    echo [StartQQ] QQ.exe not found
  )
  exit /b 0
)

if not defined QQ_EXE (
  echo [StartQQ] QQ.exe not found
  exit /b 1
)

echo [StartQQ] launching %QQ_EXE%
rem Start QQ with its install directory as working directory
for %%I in ("%QQ_EXE%") do set "QQ_DIR=%%~dpI"
start "" /d "%QQ_DIR%" "%QQ_EXE%"
if errorlevel 1 (
  echo [StartQQ] launch failed
  exit /b 1
)

echo [StartQQ] launched
exit /b 0
