@echo off
rem Usage: build-wpf.cmd [X.Y.Z]. Builds an unsigned driver package and bundled WPF installer.
rem Requires Visual Studio C++ x64 tools, .NET desktop development, .NET Framework 4.8 targeting pack,
rem Windows SDK, and the WDK package described in driver\build.cmd. No certificates or drivers are installed.
setlocal
cd /d "%~dp0"
set "LEGOMYCURSOR_VERSION=%~1"
if not defined LEGOMYCURSOR_VERSION set "LEGOMYCURSOR_VERSION=0.0.0"
if not "%~2"=="" (echo Usage: build-wpf.cmd [X.Y.Z] & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File driver\version.ps1 || exit /b 1
if not exist "%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\PresentationFramework.dll" (echo Install the .NET Framework 4.8 targeting pack through Visual Studio Installer. & exit /b 1)
call driver\build.cmd "%LEGOMYCURSOR_VERSION%" || exit /b 1
cd /d "%~dp0"
call driver\out\tools.cmd || exit /b 1
if not exist "%INF2CAT%" (echo Inf2Cat is missing from the WDK package. & exit /b 1)
if not exist build\release\pkg mkdir build\release\pkg || exit /b 1
copy /y driver\out\legomycursor.inf build\release\pkg\legomycursor.inf >nul || exit /b 1
copy /y driver\out\legomycursor.dll build\release\pkg\legomycursor.dll >nul || exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -File installer-wpf\prepare-build.ps1 -Version "%LEGOMYCURSOR_VERSION%" || exit /b 1
if exist build\release\pkg\legomycursor.cat del build\release\pkg\legomycursor.cat || exit /b 1
"%INF2CAT%" /driver:"%~dp0build\release\pkg" /os:10_GE_X64 || exit /b 1
cd /d "%~dp0"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VSPATH="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -property installationPath`) do set "VSPATH=%%i"
if not defined VSPATH (echo Visual Studio MSBuild was not found. Install .NET desktop development. & exit /b 1)
"%VSPATH%\MSBuild\Current\Bin\MSBuild.exe" installer-wpf\LeGoMyCursor.Setup.csproj /nologo /v:minimal /t:Rebuild /p:Configuration=Release /p:Platform=x64 || exit /b 1
echo Installer: build\release\LeGoMyCursor.Setup.exe
exit /b 0
