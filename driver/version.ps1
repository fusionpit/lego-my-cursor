param([string]$Version = $env:LEGOMYCURSOR_VERSION, [switch]$DriverVersion)
$ErrorActionPreference = 'Stop'
if ($Version -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') {
    throw 'Version must be numeric X.Y.Z, with each component from 0 to 65534 (no v prefix or suffix).'
}
# DriverVer requires each component to be strictly less than 65535.
$parts = @($Version.Split('.') | ForEach-Object { [uint16]::Parse($_) })
if ($parts -contains 65535) { throw 'Version components must be less than 65535.' }
if ($DriverVersion) {
    # DriverVer disallows 0.0.0.0; the unpublished default installer remains 0.0.0.
    if (($parts -join '.') -eq '0.0.0') { '0.0.0.1' } else { ($parts -join '.') + '.0' }
}
