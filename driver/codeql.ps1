param([string]$CodeQL = 'codeql')
$ErrorActionPreference = 'Stop'
if ($CodeQL -eq 'codeql' -and !(Get-Command $CodeQL -ErrorAction SilentlyContinue)) {
    $CodeQL = Join-Path $PSScriptRoot '..\build\analyzers\codeql-cli\codeql\codeql.exe'
}
$CodeQL = (Get-Command $CodeQL -ErrorAction Stop).Source
$output = Join-Path $PSScriptRoot ('out\codeql\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $CodeQL version
if ($LASTEXITCODE) { throw 'CodeQL could not start.' }
# Install these packs once with `codeql pack download`; analysis does not upload source or results.
$suites = @{
    'cpp-security' = 'codeql/cpp-queries@1.9.2:codeql-suites/cpp-security-extended.qls'
    'windows-driver' = 'microsoft/windows-drivers@1.10.0:windows-driver-suites/recommended.qls'
}
$database = Join-Path $output 'database'
& $CodeQL database create $database --language=cpp "--source-root=$PSScriptRoot" `
    "--command=cmd /d /c `"$PSScriptRoot\build.cmd`"" --threads=4
if ($LASTEXITCODE) { throw "CodeQL database creation failed. See $output" }
$findings = 0
foreach ($name in $suites.Keys | Sort-Object) {
    $sarif = Join-Path $output "$name.sarif"
    & $CodeQL database analyze $database $suites[$name] --format=sarifv2.1.0 "--output=$sarif" --threads=4
    if ($LASTEXITCODE) { throw "CodeQL $name analysis failed. See $output" }
    $report = Get-Content -LiteralPath $sarif -Raw | ConvertFrom-Json
    $results = @($report.runs | ForEach-Object { $_.results } | Where-Object { $_ })
    $findings += $results.Count
    Write-Host "$name findings: $($results.Count)"
    foreach ($result in $results) { Write-Host "$($result.ruleId): $($result.message.text)" }
}
Write-Host "CodeQL reports: $output"
if ($findings) { throw "CodeQL reported $findings findings; review the SARIF reports." }
