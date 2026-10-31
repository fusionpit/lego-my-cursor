@echo off
rem Shared x64 MSVC configuration. Caller owns setlocal.
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (echo Install Visual Studio with the C++ x64 toolset and Windows SDK. & exit /b 1)
set "VSPATH="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if not defined VSPATH (echo Visual Studio with the C++ toolset was not found. & exit /b 1)
call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
if not "%~1"=="driver" exit /b 0

rem UMDF 2.33 supports all Windows 11 releases; 2.35 would need 24H2 or later.
set "UMDF_MINOR=33"
rem WDK may point to the c directory of an existing Microsoft.Windows.WDK.x64 package.
if not defined WDK set "WDK=%~dp0.wdk\Microsoft.Windows.WDK.x64.10.0.26100.6584\c"
if not exist "%WDK%\Include\wdf\umdf\2.%UMDF_MINOR%\wdf.h" (echo WDK package missing; see setup instructions in build.cmd. & exit /b 1)
set "INCLUDE=%WDK%\Include\wdf\umdf\2.%UMDF_MINOR%;%WDK%\Include\10.0.26100.0\um;%INCLUDE%"
set "LIB=%WDK%\Lib\wdf\umdf\x64\2.%UMDF_MINOR%;%WDK%\Lib\10.0.26100.0\um\x64;%LIB%"
set "LEGOMYCURSOR_DRIVER_FLAGS=/O2 /W4 /MT /DNDEBUG /DUMDF_VERSION_MAJOR=2 /DUMDF_VERSION_MINOR=%UMDF_MINOR% /D_WIN32_WINNT=0x0A00 /wd4324 /guard:cf"
exit /b 0
