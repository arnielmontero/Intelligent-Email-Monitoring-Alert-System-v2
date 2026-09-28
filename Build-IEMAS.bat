@echo off
rem Double-click to build and start IEMAS with Docker and check that everything works.
rem The work is done by scripts\build-and-run.ps1; a log is written to the logs folder.
title IEMAS - build and start
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-and-run.ps1" %*
set RESULT=%ERRORLEVEL%
echo.
if not "%RESULT%"=="0" (
  echo Something went wrong - read the FAIL lines above.
) else (
  echo Done.
)
pause
exit /b %RESULT%
