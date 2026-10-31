param(
    [string]$ClangTidy = 'clang-tidy',
    [string]$Cppcheck = 'cppcheck'
)
$ErrorActionPreference = 'Stop'
if (!$env:LEGOMYCURSOR_DRIVER_FLAGS) { throw 'Run driver\analyze.cmd to load the build configuration.' }
if ($env:CL -or $env:_CL_) { throw 'Clear CL and _CL_ so all analyzers use the shared explicit compiler flags.' }
if ($ClangTidy -eq 'clang-tidy' -and !(Get-Command $ClangTidy -ErrorAction SilentlyContinue)) {
    $ClangTidy = Join-Path $PSScriptRoot '..\build\analyzers\clang-tidy\clang_tidy\data\bin\clang-tidy.exe'
}
if ($Cppcheck -eq 'cppcheck' -and !(Get-Command $Cppcheck -ErrorAction SilentlyContinue)) {
    $Cppcheck = Join-Path $PSScriptRoot '..\build\analyzers\cppcheck\PFiles\Cppcheck\cppcheck.exe'
}
$ClangTidy = (Get-Command $ClangTidy -ErrorAction Stop).Source
$Cppcheck = (Get-Command $Cppcheck -ErrorAction Stop).Source
$output = Join-Path $PSScriptRoot 'out\analysis'
New-Item -ItemType Directory -Force $output | Out-Null
# UMDF 2.33 forward-declares this enum with ': int' but omits it on the definition.
# Clang requires matching declarations; MSVC does not. Patch an overlay, leaving the WDK untouched.
$wdkHeader = Join-Path $env:WDK "Include\wdf\umdf\2.$env:UMDF_MINOR\wdfrequest.h"
$header = [IO.File]::ReadAllText($wdkHeader)
$declaration = 'typedef enum _WDF_REQUEST_TYPE {'
if (($header.Split(@($declaration), [StringSplitOptions]::None)).Count -ne 2) {
    throw 'WDK enum declaration changed; review the Clang compatibility overlay.'
}
$patchedHeader = Join-Path $output 'wdfrequest.h'
[IO.File]::WriteAllText($patchedHeader, $header.Replace($declaration, 'typedef enum _WDF_REQUEST_TYPE : int {'))
$overlay = Join-Path $output 'wdk-overlay.json'
[IO.File]::WriteAllText($overlay, (ConvertTo-Json -Depth 5 -InputObject @{
    version = 0; roots = @(
        @{ type = 'file'; name = $wdkHeader; 'external-contents' = $patchedHeader }
    )
}))
@(& $ClangTidy --version; & $Cppcheck --version; "MSVC toolset: $env:VCToolsVersion";
    "Windows SDK: $env:WindowsSDKVersion"; "WDK: $env:WDK"; "UMDF: 2.$env:UMDF_MINOR") |
    Tee-Object -FilePath "$output\versions.txt"
$failed = $false
$source = Join-Path $PSScriptRoot 'legomycursor.cpp'
$flags = $env:LEGOMYCURSOR_DRIVER_FLAGS -split ' '
$arguments = @('clang-cl.exe', '--target=x86_64-pc-windows-msvc') + $flags +
    @($env:INCLUDE -split ';' | Where-Object { $_ } | ForEach-Object { "/imsvc$_" }) + @('/c', $source)
$database = @(@{ directory = $PSScriptRoot; file = $source; arguments = $arguments })
[IO.File]::WriteAllText((Join-Path $output 'compile_commands.json'), (ConvertTo-Json -InputObject $database -Depth 5))
# Remove stale YAML; clang-tidy writes it only for findings.
if (Test-Path "$output\clang-tidy.yaml") { Remove-Item -LiteralPath "$output\clang-tidy.yaml" }
Write-Host 'Analyzing legomycursor.cpp'
# Windows PowerShell treats native stderr as ErrorRecord objects; use the process exit code.
$ErrorActionPreference = 'Continue'
& $ClangTidy $source "-p=$output" "--config-file=$PSScriptRoot\.clang-tidy" `
    "--vfsoverlay=$overlay" "--export-fixes=$output\clang-tidy.yaml" `
    2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath "$output\clang-tidy.txt"
if ($LASTEXITCODE) { $failed = $true }
# Use MSVC's real preprocessor: Cppcheck cannot parse the installed SDK/STL headers directly.
& cl.exe /nologo @flags /P "/Fi$output\preprocessed.cpp" $source
if ($LASTEXITCODE) { throw 'MSVC preprocessing failed.' }
$ErrorActionPreference = 'Stop'
# Keep project bodies and their line directives; Windows/CRT APIs use Cppcheck's library models.
$project = [Collections.Generic.List[string]]::new()
$keep = $false
foreach ($line in [IO.File]::ReadLines("$output\preprocessed.cpp")) {
    if ($line -match '^#line \d+ "(.+)"') {
        $path = $Matches[1].Replace('\\', '\')
        $keep = (Split-Path -Parent $path) -eq $PSScriptRoot
    }
    if ($keep) { $project.Add($line) }
}
$cppcheckSource = "$output\project.cpp"
if (!$project.Count) { throw 'No project code survived preprocessing.' }
[IO.File]::WriteAllLines($cppcheckSource, $project)
$ErrorActionPreference = 'Continue'
& $Cppcheck $cppcheckSource --platform=win64 --library=windows --std=c++14 `
    "--suppressions-list=$PSScriptRoot\cppcheck-suppressions.txt" `
    --enable=warning,style,performance,portability --error-exitcode=1 --xml --xml-version=2 `
    "--output-file=$output\cppcheck.xml"
if ($LASTEXITCODE) { $failed = $true }
$ErrorActionPreference = 'Stop'
[xml]$report = Get-Content -LiteralPath "$output\cppcheck.xml"
foreach ($finding in $report.SelectNodes('/results/errors/error')) {
    Write-Host "Cppcheck $($finding.id): $($finding.msg)"
}
if ($failed) { throw "Static analysis failed. See $output" }
Write-Host "Static analysis passed. Reports: $output"
