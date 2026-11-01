# test-python.ps1 -- run the Python protocol layer tests.
#
# Two suites:
#   run_tests.py    offline, deterministic, no network. Safe to run always.
#   online_gate.py  hits the real API. Opt-in, because it depends on the network
#                   path and on Douyin's risk control rather than on this code.
#
# Usage:
#   pwsh -NoProfile -File scripts\test-python.ps1
#   pwsh -NoProfile -File scripts\test-python.ps1 -Online
#   pwsh -NoProfile -File scripts\test-python.ps1 -Python 'py -3.13'

param(
    [string]$Python = '',
    [switch]$Online,
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Continue'

# Resolve an interpreter. The protocol layer is standard-library-only and runs on
# 3.11+, so any of the interpreters this machine has will do.
$candidates = @()
if ($Python) { $candidates += $Python }
$candidates += @('py -3.13', 'py -3.12', 'py -3.11', 'python')

$resolved = $null
foreach ($candidate in $candidates) {
    $parts = $candidate.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)
    $exe = $parts[0]
    $args = @()
    if ($parts.Length -gt 1) { $args = $parts[1..($parts.Length - 1)] }

    & $exe @args -c "import sys; sys.exit(0 if sys.version_info >= (3, 11) else 1)" 2>$null
    if ($LASTEXITCODE -eq 0) {
        $resolved = @{ Exe = $exe; Args = $args }
        break
    }
}

if (-not $resolved) {
    Write-Host '[test-python] FATAL: no Python 3.11+ interpreter found' -ForegroundColor Red
    exit 2
}

Write-Host "[test-python] interpreter: $($resolved.Exe) $($resolved.Args -join ' ')"

$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'

$failed = 0

Write-Host ''
Write-Host '=== protocol layer (offline) ==='
& $resolved.Exe @($resolved.Args) -X utf8 (Join-Path $RepoRoot 'python\tests\run_tests.py')
if ($LASTEXITCODE -ne 0) { $failed++ }

if ($Online) {
    Write-Host ''
    Write-Host '=== online gate (real API) ==='
    Write-Host 'A failure here is not necessarily a client bug: risk control and a'
    Write-Host 'missing ttwid both look like a refusal. Read the raw HTTP status printed.'
    & $resolved.Exe @($resolved.Args) -X utf8 (Join-Path $RepoRoot 'python\tests\online_gate.py')
    if ($LASTEXITCODE -ne 0) { $failed++ }
}

Write-Host ''
Write-Host "[test-python] suites failed: $failed"
exit $failed
