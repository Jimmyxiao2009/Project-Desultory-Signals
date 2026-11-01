# build-pythonhost.ps1 -- build this project's ARM32 UWP host driver
# (native/out/python-uwp.dll) from native/pythonhost/*.c, linked against the
# static CPython core built by build-pythoncore.ps1.
#
# Usage:
#   pwsh -NoProfile -File scripts\build-pythonhost.ps1
#   pwsh -NoProfile -File scripts\build-pythonhost.ps1 -PythonSrc 'D:\other\Python UWP'

param(
    [string]$PythonSrc = 'H:\项目\UWP\Python UWP',
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Continue'

$host_dir = Join-Path $RepoRoot 'native\pythonhost'
$build = Join-Path $RepoRoot 'native\build-arm'
$out = Join-Path $RepoRoot 'native\out'
$log = Join-Path $RepoRoot 'native\build-pythonhost.log'
$cpy = Join-Path $PythonSrc 'cpython'

New-Item -ItemType Directory -Force $out | Out-Null
function Write-Log { param([string]$Message) "$Message" | Out-File -FilePath $log -Append -Encoding utf8 }
Write-Log "=== build-pythonhost $(Get-Date -Format o) ==="

$core = Join-Path $build 'pythoncore.lib'
if (-not (Test-Path $core)) {
    Write-Log "FATAL: $core missing -- run scripts\build-pythoncore.ps1 first"
    Get-Content $log -Tail 5
    exit 2
}

try {
    . 'E:\Apotheosis\port\arm32-uwp-env.ps1' *>> $log
} catch {
    Write-Log "ENV FAILED: $_"
    Get-Content $log -Tail 20
    exit 3
}

$clang = 'C:\Program Files\LLVM\bin\clang-cl.exe'
$lld = 'C:\Program Files\LLVM\bin\lld-link.exe'

function Invoke-Step {
    param([string]$Exe, [string[]]$ArgumentList, [string]$Label)
    & $Exe @ArgumentList *>> $log
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Log "$Label FAILED exit=$code"
        Write-Host "[build-pythonhost] $Label FAILED (exit $code)"
        Get-Content $log -Tail 40
        exit $code
    }
    Write-Log "$Label ok"
}

# --- the driver ------------------------------------------------------------
Invoke-Step -Exe $clang -Label 'compile driver' -ArgumentList @(
    '--target=thumbv7-unknown-windows-msvc', '-c',
    '-fms-extensions', '-fms-compatibility',
    "/FI$(Join-Path $host_dir 'pyuwp-shim.h')",
    (Join-Path $host_dir 'desultory-driver.c'),
    "-Fo$(Join-Path $out 'desultory-driver.obj')",
    "-I$(Join-Path $cpy 'PC')",
    "-I$(Join-Path $cpy 'Include')",
    "-I$(Join-Path $cpy 'Include\internal')",
    '/bigobj',
    '-DWINAPI_FAMILY=WINAPI_FAMILY_APP', '-D_WIN32_WINNT=0x0A00', '-DPY_UWP=1',
    '-DPy_NO_ENABLE_SHARED', '-DPy_BUILD_CORE'
)

# --- App Container stubs ---------------------------------------------------
Invoke-Step -Exe $clang -Label 'compile stubs' -ArgumentList @(
    '--target=thumbv7-unknown-windows-msvc', '-c',
    (Join-Path $host_dir 'pyuwp-stubs.c'),
    "-Fo$(Join-Path $out 'pyuwp-stubs.obj')",
    '-DWINAPI_FAMILY=WINAPI_FAMILY_APP', '-D_WIN32_WINNT=0x0A00', '-DPY_UWP=1',
    '-DMS_DLL_ID="pyuwp"'
)

# --- link ------------------------------------------------------------------
$dll = Join-Path $out 'python-uwp.dll'
Invoke-Step -Exe $lld -Label 'link python-uwp.dll' -ArgumentList @(
    '/DLL', '/MACHINE:ARM', '/APPCONTAINER',
    "/OUT:$dll",
    (Join-Path $out 'desultory-driver.obj'),
    (Join-Path $out 'pyuwp-stubs.obj'),
    $core,
    '/LIBPATH:C:\vcpkg\installed\arm-uwp\lib', 'z.lib',
    'msvcrt.lib', 'vcruntime.lib', 'ucrt.lib', 'ws2_32.lib', 'iphlpapi.lib',
    'rpcrt4.lib', 'WindowsApp.lib',
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

# --- zlib runtime dependency ----------------------------------------------
Copy-Item 'C:\vcpkg\installed\arm-uwp\bin\z.dll' (Join-Path $out 'z.dll') -Force

$dll_item = Get-Item $dll
$zlib_item = Get-Item (Join-Path $out 'z.dll')
Write-Log "python-uwp.dll = $($dll_item.Length) bytes"
Write-Host "[build-pythonhost] OK python-uwp.dll = $($dll_item.Length) bytes, z.dll = $($zlib_item.Length) bytes"
Write-Host "[build-pythonhost] out=$out"
exit 0
