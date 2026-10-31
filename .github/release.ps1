# Creates or updates the draft GitHub release for a tag. A published release is never replaced.
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [Parameter(Mandatory = $true)][string[]]$Asset,
    [string]$NotesTemplate = (Join-Path $PSScriptRoot 'release-notes.md'),
    [string]$NotesPath = (Join-Path ([IO.Path]::GetTempPath()) 'legomycursor-release-notes.md')
)
$ErrorActionPreference = 'Stop'
# gh release view fails for a missing release; inspect $LASTEXITCODE instead of letting pwsh 7.4+ throw.
$PSNativeCommandUseErrorActionPreference = $false
if ($Tag -cne "v$Version") { throw "Tag $Tag does not match version $Version." }
foreach ($path in $Asset) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Release asset is missing: $path" }
}
$readme = "$ServerUrl/$Repository/blob/$Tag/README.md#installing"
$notes = (Get-Content -LiteralPath $NotesTemplate -Raw).Replace('{{VERSION}}', $Version).Replace('{{README_URL}}', $readme)
[IO.File]::WriteAllText($NotesPath, $notes)
$title = "LeGo my cursor! $Version"
$existing = gh release view $Tag --repo $Repository --json isDraft 2>$null
if ($LASTEXITCODE -eq 0) {
    if (-not ($existing | ConvertFrom-Json).isDraft) { throw "Refusing to replace the published release $Tag." }
    gh release upload $Tag @Asset --repo $Repository --clobber
    if ($LASTEXITCODE) { throw 'Draft asset upload failed.' }
    gh release edit $Tag --repo $Repository --draft --title $title --notes-file $NotesPath
    if ($LASTEXITCODE) { throw 'Draft release update failed.' }
} else {
    gh release create $Tag @Asset --repo $Repository --verify-tag --draft --title $title --notes-file $NotesPath
    if ($LASTEXITCODE) { throw 'Draft release creation failed.' }
}
Write-Host "Draft release $Tag is ready for review at $ServerUrl/$Repository/releases"
