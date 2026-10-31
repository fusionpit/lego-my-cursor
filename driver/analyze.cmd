@echo off
setlocal
cd /d "%~dp0"
call "%~dp0build-env.cmd" driver
if errorlevel 1 exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0analyze.ps1" %*
exit /b %errorlevel%
