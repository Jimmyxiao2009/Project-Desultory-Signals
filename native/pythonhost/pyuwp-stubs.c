/* pyuwp-stubs.c — satisfy the handful of symbols CPython's Windows core
 * references that an App Container doesn't provide (or that our static-core
 * build configuration compiles out). Same role as Apotheosis's stubs-*.cpp:
 * name exactly what the platform can't give us, and degrade gracefully instead
 * of failing to link. All of these sit in non-critical, non-bootstrap paths.
 *
 * These are stubs for a LINK milestone, NOT device-validated behaviour. Where a
 * real UWP-legal equivalent exists (GetActiveProcessorCount) we implement it for
 * real; where the capability is genuinely absent (getlogin, named pipes, version
 * resource) we return a benign failure/placeholder so the corresponding Python
 * feature raises or falls back rather than crashing.
 */
#include <windows.h>

/* --- CPython internal: normally defined by PC/dl_nt.c's DllMain, but that
 *     lives under #ifdef Py_ENABLE_SHARED and we build the core static (only a
 *     curated driver ABI is exported from python-uwp.dll, not all of libpython).
 *     dynload_win.c reads PyWin_DLLhModule only to locate python3.dll for
 *     RELATIVE .pyd loading — a path our frozen-stdlib + in-memory-TCC model
 *     never takes, so NULL is correct here. --- */
HMODULE PyWin_DLLhModule = NULL;

/* --- os.cpu_count(): GetActiveProcessorCount is desktop-partition-gated in the
 *     SDK headers, but GetNativeSystemInfo IS callable in an App Container and
 *     gives the same answer for the single processor group we care about. Real
 *     implementation, not a stub. --- */
DWORD WINAPI GetActiveProcessorCount(WORD GroupNumber)
{
    (void)GroupNumber;
    SYSTEM_INFO si;
    GetNativeSystemInfo(&si);
    return si.dwNumberOfProcessors ? si.dwNumberOfProcessors : 1;
}

/* --- os.getlogin(): GetUserNameW (advapi32/secur32) is not reachable from an
 *     App Container. Hand back a fixed placeholder so getlogin() returns a
 *     value instead of raising; the sandbox has no real per-user identity to
 *     report anyway. --- */
BOOL WINAPI GetUserNameW(LPWSTR lpBuffer, LPDWORD pcbBuffer)
{
    static const WCHAR name[] = L"uwp";
    DWORD need = (DWORD)(sizeof(name) / sizeof(WCHAR)); /* incl NUL */
    if (!pcbBuffer) { SetLastError(ERROR_INVALID_PARAMETER); return FALSE; }
    if (!lpBuffer || *pcbBuffer < need) {
        *pcbBuffer = need;
        SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return FALSE;
    }
    memcpy(lpBuffer, name, sizeof(name));
    *pcbBuffer = need;
    return TRUE;
}

/* --- sys.getwindowsversion(): reads the version resource of a system DLL via
 *     GetFileVersionInfoSize*. Not available to App Container; return 0 so the
 *     caller treats the version info as unavailable and falls back. --- */
DWORD WINAPI GetFileVersionInfoSizeW(LPCWSTR lptstrFilename, LPDWORD lpdwHandle)
{
    (void)lptstrFilename;
    if (lpdwHandle) *lpdwHandle = 0;
    SetLastError(ERROR_RESOURCE_DATA_NOT_FOUND);
    return 0;
}

/* --- _winapi.WaitNamedPipe: named-pipe client waits are a subprocess/
 *     multiprocessing facility that App Container blocks. Fail cleanly. Named
 *     literally `WaitNamedPipe` because the SDK's UNICODE macro mapping is
 *     absent under WINAPI_FAMILY_APP, so _winapi.c references the bare name. --- */
BOOL WINAPI WaitNamedPipe(LPCWSTR lpNamedPipeName, DWORD nTimeOut)
{
    (void)lpNamedPipeName; (void)nTimeOut;
    SetLastError(ERROR_NOT_SUPPORTED);
    return FALSE;
}

/* --- mimalloc NUMA fallback: GetNumaNodeProcessorMask is desktop-partition
 *     only. Return FALSE so _mi_prim_numa_node_count walks down to node 0. --- */
BOOL WINAPI GetNumaNodeProcessorMask(UCHAR Node, PULONGLONG ProcessorMask)
{
    (void)Node;
    if (ProcessorMask) *ProcessorMask = 0;
    SetLastError(ERROR_NOT_SUPPORTED);
    return FALSE;
}