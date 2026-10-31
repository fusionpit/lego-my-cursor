param([Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference = 'Stop'
$driverVersion = & "$PSScriptRoot/../driver/version.ps1" -Version $Version -DriverVersion
$normalized = (($Version.Split('.') | ForEach-Object { [uint16]::Parse($_) }) -join '.') + '.0'
$payload = Join-Path $PSScriptRoot '../build/release/pkg'
# pnputil reports DriverVer, so the INF is the version of record; the DLL's VERSIONINFO must agree with it.
if ((Get-Content (Join-Path $payload 'legomycursor.inf') -Raw) -notmatch ('(?m)^DriverVer\s*=\s*[^,\r\n]+,\s*' + [regex]::Escape($driverVersion) + '\s*$')) {
    throw "Driver payload version does not match $Version. Run build-wpf.cmd $Version."
}
$fileVersion = (Get-Item (Join-Path $payload 'legomycursor.dll')).VersionInfo.FileVersion
if ($fileVersion -ne $driverVersion) {
    throw "Driver DLL FileVersion '$fileVersion' does not match DriverVer $driverVersion. Run build-wpf.cmd $Version."
}
# Local catalog signing happens on the installing computer; never embed a development-signed DLL.
$bytes = [IO.File]::ReadAllBytes((Join-Path $payload 'legomycursor.dll'))
$pe = [BitConverter]::ToInt32($bytes, 60)
if ([BitConverter]::ToUInt16($bytes, $pe + 4) -ne 0x8664 -or [BitConverter]::ToUInt16($bytes, $pe + 24) -ne 0x20b -or [BitConverter]::ToUInt32($bytes, $pe + 168) -ne 0) {
    throw 'Expected an unsigned x64 driver payload.'
}
$output = Join-Path $PSScriptRoot '../build/wpf-obj'
[IO.Directory]::CreateDirectory($output) | Out-Null
@"
using System.Reflection;
[assembly: AssemblyTitle("LeGo My Cursor")]
[assembly: AssemblyProduct("LeGo My Cursor")]
[assembly: AssemblyVersion("$normalized")]
[assembly: AssemblyFileVersion("$normalized")]
"@ | Set-Content -LiteralPath (Join-Path $output 'Version.cs') -Encoding UTF8
