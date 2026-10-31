@echo off
rem Builds the unsigned x64 UMDF 2 DLL and stamps its INF. Usage: build.cmd [X.Y.Z]
rem Requires MSVC C++ x64 tools, Windows SDK, and Microsoft.Windows.WDK.x64 10.0.26100.6584.
rem From this directory, install the WDK with nuget.exe (https://www.nuget.org/downloads):
rem   nuget.exe install Microsoft.Windows.WDK.x64 -Version 10.0.26100.6584 -OutputDirectory .wdk -DependencyVersion Ignore
rem Alternatively, set WDK to the c directory in an existing package of that version.
setlocal
cd /d "%~dp0"
set "LEGOMYCURSOR_VERSION=%~1"
set "STAMP_VERSION=*"
if defined LEGOMYCURSOR_VERSION (
    set "STAMP_VERSION="
    for /f "delims=" %%i in ('powershell -NoProfile -ExecutionPolicy Bypass -File "version.ps1" -DriverVersion') do set "STAMP_VERSION=%%i"
)
if not defined STAMP_VERSION exit /b 1
call "%~dp0build-env.cmd" driver
if errorlevel 1 exit /b 1

if not exist out mkdir out
rem Keep WDK API contracts while excluding external header warnings from static analysis.
cl /nologo %LEGOMYCURSOR_DRIVER_FLAGS% /WX /analyze /analyze:log out\legomycursor.sarif ^
   /external:I"%WDK%\Include" /external:W4 /analyze:external- ^
   legomycursor.cpp /Foout\ ^
   /link /DLL /GUARD:CF /OPT:REF /OPT:ICF /INCREMENTAL:NO /RELEASE WdfDriverStubUm.lib ntdll.lib kernel32.lib /OUT:out\legomycursor.dll
if errorlevel 1 exit /b 1

copy /y legomycursor.inf out\legomycursor.inf >nul || exit /b 1
"%WDK%\bin\10.0.26100.0\x64\stampinf.exe" -f out\legomycursor.inf -a amd64 -d * -v %STAMP_VERSION% -u 2.%UMDF_MINOR%.0
if errorlevel 1 exit /b 1

set "SIGNTOOL="
for /f "delims=" %%i in ('where signtool.exe') do if not defined SIGNTOOL set "SIGNTOOL=%%i"
> out\tools.cmd echo set "INF2CAT=%WDK%\bin\10.0.26100.0\x86\Inf2Cat.exe"
>> out\tools.cmd echo set "SIGNTOOL=%SIGNTOOL%"
echo Built out\legomycursor.dll and out\legomycursor.inf; packaging tool paths are in out\tools.cmd.
exit /b 0
