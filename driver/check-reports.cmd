@echo off
rem Runs without installing a driver or changing device settings.
setlocal
cd /d "%~dp0"
call "%~dp0build-env.cmd" || exit /b 1
if not exist out mkdir out || exit /b 1
cl /nologo /W4 /WX /MT check-reports.cpp /Foout\check-reports.obj /Feout\check-reports.exe || exit /b 1
out\check-reports.exe
exit /b %errorlevel%
