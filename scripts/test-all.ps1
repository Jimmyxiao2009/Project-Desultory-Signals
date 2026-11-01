# test-all.ps1 -- run every test suite this repository has.
#
# Four suites, in increasing order of what they prove:
#
#   1. test-python.ps1            protocol logic on a desktop interpreter (3.13)
#   2. test-core.ps1              C# core: range math, cache, retry, pagination, ...
#   3. test-pythonhost-desktop.ps1 the REAL embedded CPython 3.15 runtime + host ABI
#   4. (-Online)                  the real Douyin API over the network
#
# Suite 3 is the one that matters most and is easy to overlook: it runs the protocol
# layer inside the actual embedded interpreter, which is a different CPython version
# and a different (trimmed) standard library from the one the unit tests use. A
# packaging mistake passes suites 1 and 2 and fails suite 3.
#
# Usage:
#   pwsh -NoProfile -File scripts\test-all.ps1
#   pwsh -NoProfile -File scripts\test-all.ps1 -Online    # also hit the real API

param(
    [switch]$Online,
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Continue'
$failed = 0

function Run-Suite {
    param([string]$Name, [string]$Script, [string[]]$ExtraArguments = @())

    Write-Host ''
    Write-Host ('=' * 74)
    Write-Host "  $Name"
    Write-Host ('=' * 74)

    $path = Join-Path $RepoRoot "scripts\$Script"
    & pwsh -NoProfile -File $path @ExtraArguments
    $code = $LASTEXITCODE

    if ($code -eq 0) {
        Write-Host "[test-all] $Name -> PASS"
    } else {
        Write-Host "[test-all] $Name -> FAIL (exit $code)" -ForegroundColor Red
        $script:failed++
    }
}

Run-Suite -Name 'Python protocol layer (desktop 3.13)' -Script 'test-python.ps1'
Run-Suite -Name 'C# core' -Script 'test-core.ps1'
Run-Suite -Name 'Embedded CPython 3.15 + host ABI (x64)' -Script 'test-pythonhost-desktop.ps1'

if ($Online) {
    Run-Suite -Name 'Online gate (real API)' -Script 'test-python.ps1' -ExtraArguments @('-Online')
}

Write-Host ''
Write-Host ('=' * 74)
if ($failed -eq 0) {
    Write-Host '  all suites passed' -ForegroundColor Green
} else {
    Write-Host "  $failed suite(s) failed" -ForegroundColor Red
}
Write-Host ('=' * 74)
exit $failed
