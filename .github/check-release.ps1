# Run locally: powershell -NoProfile -ExecutionPolicy Bypass -File .github\check-release.ps1
# Exercises the release scripts the workflow runs; gh is mocked and never contacts GitHub.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path ([IO.Path]::GetTempPath()) ('legomycursor-release-check-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $version = Join-Path $root 'driver\version.ps1'
    foreach ($valid in '0.0.0', '1.2.3', '65534.65534.65534') { & $version -Version $valid }
    foreach ($invalid in 'v1.2.3', '1.2.3-rc1', '1.2', '65535.0.0', '65536.0.0', "1.2.3`n", '') {
        $rejected = $false
        try { & $version -Version $invalid } catch { $rejected = $true }
        if (-not $rejected) { throw "Invalid version accepted: '$invalid'" }
    }

    $exe = Join-Path $temp 'LeGoMyCursor.Setup.exe'
    $sha = "$exe.sha256"
    [IO.File]::WriteAllBytes($exe, [byte[]](0..255))
    [IO.File]::WriteAllText($sha, "$((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant())  LeGoMyCursor.Setup.exe`n")
    $notes = Join-Path $temp 'release-notes.md'
    # release.ps1 runs in its own script scope, so the mock keeps its state in a captured hashtable.
    $mock = @{ Commands = [Collections.Generic.List[string]]::new(); Release = 'missing' }
    ${function:gh} = {
        $mock.Commands.Add(($args -join ' '))
        $global:LASTEXITCODE = 0
        if ($args[1] -eq 'view') {
            if ($mock.Release -eq 'missing') { $global:LASTEXITCODE = 1; return }
            if ($mock.Release -eq 'draft') { '{"isDraft":true}' } else { '{"isDraft":false}' }
        }
    }.GetNewClosure()
    function Invoke-Release {
        & (Join-Path $PSScriptRoot 'release.ps1') -Tag 'v1.2.3' -Version '1.2.3' -Repository 'example/lego-my-cursor' `
            -ServerUrl 'https://github.com' -Asset $exe, $sha -NotesPath $notes @args
    }
    foreach ($state in 'missing', 'draft', 'published') {
        $mock.Release = $state
        $mock.Commands.Clear()
        $rejected = $false
        try { Invoke-Release | Out-Null } catch { $rejected = $true }
        if (-not $mock.Commands[0].StartsWith('release view v1.2.3')) { throw 'The release must be inspected first.' }
        switch ($state) {
            'missing' {
                if ($rejected -or $mock.Commands.Count -ne 2) { throw 'Missing release handling failed.' }
                $create = $mock.Commands[1]
                if (-not $create.StartsWith('release create v1.2.3') -or $create -notmatch '--draft' -or $create -notmatch '--verify-tag') {
                    throw 'A missing release must be created as a verified draft.'
                }
            }
            'draft' {
                if ($rejected -or $mock.Commands.Count -ne 3) { throw 'Draft release handling failed.' }
                if (-not $mock.Commands[1].StartsWith('release upload v1.2.3') -or $mock.Commands[1] -notmatch '--clobber') {
                    throw 'Draft assets must be replaced.'
                }
                if (-not $mock.Commands[2].StartsWith('release edit v1.2.3') -or $mock.Commands[2] -notmatch '--draft') {
                    throw 'An updated draft must stay a draft.'
                }
            }
            'published' {
                if (-not $rejected -or $mock.Commands.Count -ne 1) { throw 'Published release protection failed.' }
            }
        }
    }
    $mock.Release = 'missing'
    foreach ($arguments in @(@{ Tag = 'v1.2.4' }, @{ Asset = @($exe, (Join-Path $temp 'missing.sha256')) })) {
        $mock.Commands.Clear()
        $rejected = $false
        try { Invoke-Release @arguments | Out-Null } catch { $rejected = $true }
        if (-not $rejected -or $mock.Commands.Count) { throw 'A mismatched tag or missing asset was accepted.' }
    }
    $text = [IO.File]::ReadAllText($notes)
    if ($text.Contains('{{') -or -not $text.Contains('LeGo my cursor! 1.2.3') -or
        -not $text.Contains('https://github.com/example/lego-my-cursor/blob/v1.2.3/README.md#installing')) {
        throw 'Release note substitution failed.'
    }
    'Version boundaries, draft creation/update, published-release protection and note substitution passed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    $prefix = Join-Path ([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) 'legomycursor-release-check-'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
