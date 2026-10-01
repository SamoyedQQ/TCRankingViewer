$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$ocelotRoot = Join-Path $repoRoot 'Ocelot'
$patchPath = Join-Path $PSScriptRoot 'ocelot-zh-TW.patch'

# Idempotent: preserve any existing local edits and refuse conflicting patches.
# Windows PowerShell treats git's expected probe stderr as an error record.
$ErrorActionPreference = 'Continue'
& git -C $ocelotRoot apply --reverse --check $patchPath 2>$null
$probeExitCode = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($probeExitCode -eq 0) {
    Write-Output 'Ocelot localization patch is already applied.'
    exit 0
}
& git -C $ocelotRoot apply --check $patchPath
if ($LASTEXITCODE -ne 0) {
    throw 'The Ocelot localization patch conflicts with the current source. No files were changed.'
}
& git -C $ocelotRoot apply $patchPath
if ($LASTEXITCODE -ne 0) { throw 'Could not apply the Ocelot localization patch.' }
Write-Output 'Applied the Ocelot localization patch.'
