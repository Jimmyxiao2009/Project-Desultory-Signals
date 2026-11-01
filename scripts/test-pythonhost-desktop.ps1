# test-pythonhost-desktop.ps1 -- run the embedded CPython 3.15 runtime on THIS machine.
#
# Why this matters: the target device is ARM32 and is not always reachable, but the
# host ABI and the protocol layer are architecture independent. This script builds the
# *same* driver source against the x64 CPython core and drives it from a desktop
# executable, which verifies for real:
#
#   1. CPython 3.15 boots and PyUwpRuntime_CallJsonEx works.
#   2. `douyin.protocol` imports and runs inside the *embedded* interpreter -- not the
#      desktop 3.13 interpreter the unit tests use. Different standard library, so a
#      packaging mistake shows up here and not there.
#   3. The response buffer carries a payload larger than the 64 KiB stdout buffer of
#      the original AlterGate ABI (the reason the ABI was extended).
#
# The staged runtime root uses the SAME trimmed standard library as the appx, so this
# also validates the shipped deployment layout rather than an idealised one.
#
# Usage:
#   pwsh -NoProfile -File scripts\test-pythonhost-desktop.ps1
#   pwsh -NoProfile -File scripts\test-pythonhost-desktop.ps1 -PythonSrc 'D:\other\Python UWP'

param(
    [string]$PythonSrc = 'H:\项目\UWP\Python UWP',
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Continue'

$host_dir = Join-Path $RepoRoot 'native\pythonhost'
$stage = Join-Path $RepoRoot 'native\desktop-stage'
$work = Join-Path $RepoRoot 'native\desktop-build'
$log = Join-Path $RepoRoot 'native\desktop-smoke.log'

New-Item -ItemType Directory -Force $stage, $work | Out-Null
function Write-Log { param([string]$Message) "$Message" | Out-File -FilePath $log -Append -Encoding utf8 }
Write-Log "=== test-pythonhost-desktop $(Get-Date -Format o) ==="

$core = Join-Path $PythonSrc 'build-x64\pythoncore.lib'
if (-not (Test-Path $core)) {
    Write-Host "[desktop-smoke] FATAL: $core missing." -ForegroundColor Red
    Write-Host "                The x64 control build is produced by the AlterGate tree's own"
    Write-Host "                port\build-x64.ps1; it is not part of this repository's build."
    exit 2
}

# --- 1. stage the runtime root -------------------------------------------------
# Same layout and same exclusions as the appx: Lib\ (stdlib) and AppLib\ (our package).
# One source of truth for the exclusion list is not possible across a .ps1 and a
# .csproj, so the list is duplicated here with a pointer to the other copy.
$excluded = @('test', 'ensurepip', 'idlelib', 'tkinter', 'turtledemo', 'pydoc_data',
    'profiling', '_pyrepl', 'lib2to3', 'sqlite3', 'ctypes', 'unittest', 'multiprocessing',
    'concurrent', 'asyncio', 'wsgiref', 'xmlrpc')

$stageLib = Join-Path $stage 'Lib'
$stageAppLib = Join-Path $stage 'AppLib\douyin'
Remove-Item $stageLib, (Join-Path $stage 'AppLib') -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stageLib, $stageAppLib | Out-Null

$sourceLib = Join-Path $PythonSrc 'cpython\Lib'
$copied = 0
Get-ChildItem $sourceLib -Recurse -File -Filter '*.py' | ForEach-Object {
    $relative = $_.FullName.Substring($sourceLib.Length + 1)
    $top = $relative.Split('\')[0]
    if ($excluded -contains $top) { return }
    $target = Join-Path $stageLib $relative
    $targetDir = Split-Path $target -Parent
    if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Force $targetDir | Out-Null }
    Copy-Item $_.FullName $target -Force
    $copied++
}
Write-Host "[desktop-smoke] staged $copied stdlib files (same exclusions as the appx)"
Write-Log "staged $copied stdlib files"

Copy-Item (Join-Path $RepoRoot 'python\douyin\*.py') $stageAppLib -Force
Write-Host "[desktop-smoke] staged $((Get-ChildItem $stageAppLib -File).Count) douyin modules"

# --- 2. compile the driver, the stubs and the host for x64 ---------------------
$clang = 'C:\Program Files\LLVM\bin\clang-cl.exe'
$lld = 'C:\Program Files\LLVM\bin\lld-link.exe'
$cpy = Join-Path $PythonSrc 'cpython'

function Invoke-Step {
    param([string]$Exe, [string[]]$ArgumentList, [string]$Label)
    & $Exe @ArgumentList *>> $log
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Host "[desktop-smoke] $Label FAILED (exit $code)" -ForegroundColor Red
        Get-Content $log -Tail 30
        exit $code
    }
    Write-Log "$Label ok"
}

Invoke-Step -Exe $clang -Label 'compile driver (x64)' -ArgumentList @(
    '--target=x86_64-pc-windows-msvc', '-c',
    '-fms-extensions', '-fms-compatibility',
    "/FI$(Join-Path $host_dir 'pyuwp-shim.h')",
    (Join-Path $host_dir 'desultory-driver.c'),
    "-Fo$(Join-Path $work 'desultory-driver.obj')",
    "-I$(Join-Path $cpy 'PC')",
    "-I$(Join-Path $cpy 'Include')",
    "-I$(Join-Path $cpy 'Include\internal')",
    '-DWINAPI_FAMILY=WINAPI_FAMILY_APP', '-D_WIN32_WINNT=0x0A00', '-DPY_UWP=1',
    '-DPy_NO_ENABLE_SHARED', '-DPy_BUILD_CORE'
)

Invoke-Step -Exe $clang -Label 'compile stubs (x64)' -ArgumentList @(
    '--target=x86_64-pc-windows-msvc', '-c',
    (Join-Path $host_dir 'pyuwp-stubs.c'),
    "-Fo$(Join-Path $work 'pyuwp-stubs.obj')",
    '-DWINAPI_FAMILY=WINAPI_FAMILY_APP', '-D_WIN32_WINNT=0x0A00', '-DPY_UWP=1',
    '-DMS_DLL_ID="pyuwp"'
)

Invoke-Step -Exe $lld -Label 'link desultory-desktop.dll' -ArgumentList @(
    '/DLL', '/MACHINE:X64',
    "/OUT:$(Join-Path $work 'desultory-desktop.dll')",
    (Join-Path $work 'desultory-driver.obj'),
    (Join-Path $work 'pyuwp-stubs.obj'),
    $core,
    '/LIBPATH:C:\vcpkg\installed\x64-windows\lib', 'z.lib',
    'msvcrt.lib', 'vcruntime.lib', 'ucrt.lib', 'OneCore.lib', 'bcrypt.lib', 'ws2_32.lib',
    '/EXPORT:PyUwpRuntime_Initialize',
    '/EXPORT:PyUwpRuntime_CallJson',
    '/EXPORT:PyUwpRuntime_CallJsonEx',
    '/EXPORT:PyUwpRuntime_LastResponse',
    '/EXPORT:PyUwpRuntime_BridgeReady',
    '/EXPORT:PyUwpRuntime_RunUtf8',
    '/EXPORT:PyUwpRuntime_EvalInt',
    '/EXPORT:PyUwpRuntime_Finalize',
    '/EXPORT:PyUwpRuntime_LastError',
    '/EXPORT:PyUwpRuntime_LastOutput',
    '/EXPORT:PyUwpSelfTest',
    '/EXPORT:PyUwpLastError',
    '/OPT:REF', '/OPT:NOICF', '/INCREMENTAL:NO', '/errorlimit:0'
)

# --- 3. build the host ---------------------------------------------------------
# Two steps: clang-cl compiles the C, lld-link links it. Passing /link options
# through clang-cl for a plain C file is unreliable, so the linker is invoked
# directly, exactly as the DLL link above does.
$hostExe = Join-Path $work 'desultory-smoke.exe'
Invoke-Step -Exe $clang -Label 'compile host (x64)' -ArgumentList @(
    '--target=x86_64-pc-windows-msvc', '-c',
    (Join-Path $host_dir 'desultory-smoke.c'),
    "-Fo$(Join-Path $work 'desultory-smoke.obj')"
)
Invoke-Step -Exe $lld -Label 'link host (x64)' -ArgumentList @(
    '/MACHINE:X64', '/SUBSYSTEM:CONSOLE',
    "/OUT:$hostExe",
    (Join-Path $work 'desultory-smoke.obj'),
    'kernel32.lib', 'ucrt.lib', 'msvcrt.lib', 'vcruntime.lib',
    '/OPT:REF', '/INCREMENTAL:NO'
)

Copy-Item 'C:\vcpkg\installed\x64-windows\bin\z.dll' (Join-Path $work 'z.dll') -Force

# --- 4. run -------------------------------------------------------------------
Write-Host ''
Push-Location $work
try {
    & $hostExe $stage
    $code = $LASTEXITCODE
} finally {
    Pop-Location
}

Write-Host ''
Write-Host "[desktop-smoke] EXIT=$code  log=$log"
exit $code
