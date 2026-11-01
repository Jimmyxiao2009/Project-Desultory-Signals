# build-pythoncore.ps1 — build the ARM32 UWP static CPython core (pythoncore.lib)
# out-of-source, into this repository, from the external AlterGate (PyUWP)
# CPython 3.15 source tree. The external tree is treated as read-only input.
#
# Why out-of-source: the upstream project's own build directory does not exist
# at its current location, and we explicitly do not want to write build output
# into a tree we do not own.
#
# Usage:
#   pwsh -NoProfile -File scripts\build-pythoncore.ps1
#   pwsh -NoProfile -File scripts\build-pythoncore.ps1 -PythonSrc 'D:\other\Python UWP'

param(
    [string]$PythonSrc = 'H:\项目\UWP\Python UWP',
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent),
    [switch]$Reconfigure
)

$ErrorActionPreference = 'Continue'

$log = Join-Path $RepoRoot 'native\build-arm.log'
$build = Join-Path $RepoRoot 'native\build-arm'
New-Item -ItemType Directory -Force (Join-Path $RepoRoot 'native') | Out-Null

function Write-Log { param([string]$Message) "$Message" | Out-File -FilePath $log -Append -Encoding utf8 }

Write-Log "=== build-pythoncore $(Get-Date -Format o) ==="
Write-Log "src=$PythonSrc"
Write-Log "build=$build"

if (-not (Test-Path (Join-Path $PythonSrc 'CMakeLists.txt'))) {
    Write-Log "FATAL: $PythonSrc does not look like the AlterGate source tree"
    Get-Content $log -Tail 5
    exit 2
}
if (-not (Test-Path (Join-Path $PythonSrc 'port\Toolchain-ARM32-UWP-clang.cmake'))) {
    Write-Log "FATAL: ARM32 toolchain file missing from $PythonSrc\port"
    Get-Content $log -Tail 5
    exit 2
}

# --- ARM32 UWP MSVC environment (VS18 dropped arm32 vcvars; this hand-rolls it)
try {
    . 'E:\Apotheosis\port\arm32-uwp-env.ps1' *>> $log
} catch {
    Write-Log "ENV FAILED: $_"
    Get-Content $log -Tail 20
    exit 3
}

$cmake = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'

$configured = Test-Path (Join-Path $build 'CMakeCache.txt')
if ($Reconfigure -or -not $configured) {
    & $cmake -S $PythonSrc -B $build -G Ninja `
        "-DCMAKE_MAKE_PROGRAM=$ninja" `
        "-DCMAKE_TOOLCHAIN_FILE=$PythonSrc\port\Toolchain-ARM32-UWP-clang.cmake" `
        -DCMAKE_BUILD_TYPE=Release *>> $log
    $code = $LASTEXITCODE
    Write-Log "configure exit=$code"
    if ($code -ne 0) { Get-Content $log -Tail 40; exit $code }
}

& $ninja -C $build -k 0 *>> $log
$code = $LASTEXITCODE
Write-Log "ninja exit=$code"

$lib = Join-Path $build 'pythoncore.lib'
if (Test-Path $lib) {
    $size = (Get-Item $lib).Length
    Write-Log "pythoncore.lib = $size bytes"
    Write-Host "[build-pythoncore] OK pythoncore.lib = $size bytes"
    Write-Log "=== end $(Get-Date -Format o) ==="
    exit 0
}

Write-Log 'pythoncore.lib MISSING'
$errs = @(Select-String -Path $log -Pattern ': error:|fatal error:')
Write-Host "[build-pythoncore] FAILED exit=$code errors=$($errs.Count)"
$errs | Select-Object -First 30 | ForEach-Object { Write-Host $_.Line }
exit ($code -eq 0 ? 1 : $code)
