# test-core.ps1 -- run the C# core test suite on a desktop interpreter.
#
# No phone, no CPython, no network: the suite compiles the core from source and
# exercises range arithmetic, cache eviction, codec ranking, retry policy, cookie
# adoption, feed pagination, comment pagination, history and playback decisions.
#
# Usage:
#   pwsh -NoProfile -File scripts\test-core.ps1
#   pwsh -NoProfile -File scripts\test-core.ps1 -Configuration Debug

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$project = Join-Path $RepoRoot 'tests\DesultorySignals.Tests\DesultorySignals.Tests.csproj'

Push-Location (Split-Path $project -Parent)
try {
    & dotnet run -c $Configuration --nologo
    $code = $LASTEXITCODE
} finally {
    Pop-Location
}

Write-Host "[test-core] EXIT=$code"
exit $code
