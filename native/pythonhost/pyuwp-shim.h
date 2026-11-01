/* pyuwp-shim.h — force-included (/FI) back-fill of Win32 declarations that
 * the Windows SDK hides behind WINAPI_FAMILY_PARTITION(WINAPI_PARTITION_
 * DESKTOP) when WINAPI_FAMILY=WINAPI_FAMILY_APP. Everything here is stable,
 * documented public ABI — re-declaring the *constants/types* is harmless and
 * lets CPython's Windows code parse unchanged.
 *
 * IMPORTANT: this does NOT make the underlying APIs callable. Functions that
 * are genuinely absent from an App Container (CreateProcessW, the ToolHelp32
 * snapshot family, ...) will still surface as undefined symbols at LINK time.
 * That link-time list is the real "what can't work in the sandbox" inventory —
 * we deliberately let it fail loudly there rather than hide it. Same spirit as
 * Apotheosis's stubs-*.cpp: name exactly what the platform can't provide.
 *
 * Guard style mirrors WK_WINUWP: everything gated on PY_UWP so a desktop
 * build is completely unaffected.
 */
#ifndef PYUWP_SHIM_H
#define PYUWP_SHIM_H

#if defined(PY_UWP)

#include <windows.h>

/* --- struct timeval: forward-declare at file scope so that when pytime.h's
 *     prototypes for _PyTime_AsTimeval(..., struct timeval *, ...) are parsed
 *     (via Python.h, BEFORE pytime.c pulls in <winsock2.h>), the tag is
 *     already file-scoped. Otherwise clang binds it as a prototype-scope tag
 *     and the later winsock2 definition "conflicts". Harmless everywhere. --- */
struct timeval;

/* --- SetHandleInformation flags (the API itself IS in App Container) --- */
#ifndef HANDLE_FLAG_INHERIT
#define HANDLE_FLAG_INHERIT 0x00000001
#endif
#ifndef HANDLE_FLAG_PROTECT_FROM_CLOSE
#define HANDLE_FLAG_PROTECT_FROM_CLOSE 0x00000002
#endif

/* --- SetErrorMode / SetThreadErrorMode flags (SetThreadErrorMode IS in App
 *     Container; the flag macros just live in a desktop-gated header) --- */
#ifndef SEM_FAILCRITICALERRORS
#define SEM_FAILCRITICALERRORS 0x0001
#endif
#ifndef SEM_NOGPFAULTERRORBOX
#define SEM_NOGPFAULTERRORBOX 0x0002
#endif
#ifndef SEM_NOALIGNMENTFAULTEXCEPT
#define SEM_NOALIGNMENTFAULTEXCEPT 0x0004
#endif
#ifndef SEM_NOOPENFILEERRORBOX
#define SEM_NOOPENFILEERRORBOX 0x8000
#endif

/* --- STARTUPINFO flags + extended struct (process-creation path; the
 *     CreateProcess* functions are NOT in App Container and WILL fail at
 *     link — see header note) --- */
#ifndef STARTF_USESHOWWINDOW
#define STARTF_USESHOWWINDOW 0x00000001
#endif
#ifndef STARTF_USESTDHANDLES
#define STARTF_USESTDHANDLES 0x00000100
#endif
#ifndef PROC_THREAD_ATTRIBUTE_HANDLE_LIST
#define PROC_THREAD_ATTRIBUTE_HANDLE_LIST 0x00020002
#endif

#ifndef PYUWP_HAVE_STARTUPINFOEXW
#define PYUWP_HAVE_STARTUPINFOEXW 1
typedef struct _PROC_THREAD_ATTRIBUTE_LIST *PPROC_THREAD_ATTRIBUTE_LIST,
    *LPPROC_THREAD_ATTRIBUTE_LIST;
typedef struct _STARTUPINFOEXW {
    STARTUPINFOW StartupInfo;
    LPPROC_THREAD_ATTRIBUTE_LIST lpAttributeList;
} STARTUPINFOEXW, *LPSTARTUPINFOEXW;
#endif

/* --- ToolHelp32 process snapshot (whole family absent from App Container;
 *     types provided so posixmodule parses, calls fail at link) --- */
#ifndef TH32CS_SNAPPROCESS
#define TH32CS_SNAPPROCESS 0x00000002
#endif
#ifndef PYUWP_HAVE_PROCESSENTRY32
#define PYUWP_HAVE_PROCESSENTRY32 1
typedef struct tagPROCESSENTRY32W {
    DWORD  dwSize;
    DWORD  cntUsage;
    DWORD  th32ProcessID;
    ULONG_PTR th32DefaultHeapID;
    DWORD  th32ModuleID;
    DWORD  cntThreads;
    DWORD  th32ParentProcessID;
    LONG   pcPriClassBase;
    DWORD  dwFlags;
    WCHAR  szExeFile[MAX_PATH];
} PROCESSENTRY32W, *PPROCESSENTRY32W, *LPPROCESSENTRY32W;
typedef struct tagPROCESSENTRY32 {
    DWORD  dwSize;
    DWORD  cntUsage;
    DWORD  th32ProcessID;
    ULONG_PTR th32DefaultHeapID;
    DWORD  th32ModuleID;
    DWORD  cntThreads;
    DWORD  th32ParentProcessID;
    LONG   pcPriClassBase;
    DWORD  dwFlags;
    CHAR   szExeFile[MAX_PATH];
} PROCESSENTRY32, *PPROCESSENTRY32, *LPPROCESSENTRY32;
#endif

/* --- CreateSymbolicLink flag (os.symlink) --- */
#ifndef SYMBOLIC_LINK_FLAG_DIRECTORY
#define SYMBOLIC_LINK_FLAG_DIRECTORY 0x1
#endif
#ifndef SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE
#define SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE 0x2
#endif

/* winioctl.h hides reparse-point controls from the App partition on newer
 * x64 SDK layouts.  CPython uses them for symlink inspection/creation. */
#ifndef FSCTL_SET_REPARSE_POINT
#define FSCTL_SET_REPARSE_POINT 0x000900A4
#endif
#ifndef FSCTL_GET_REPARSE_POINT
#define FSCTL_GET_REPARSE_POINT 0x000900A8
#endif

/* --- lmcons.h: username buffer length (os.getlogin via GetUserNameW) --- */
#ifndef UNLEN
#define UNLEN 256
#endif

/* --- VS_FIXEDFILEINFO for sys.getwindowsversion()'s version-resource read
 *     (GetFileVersionInfo* is reachable via api-ms-win-core-version) --- */
#ifndef PYUWP_HAVE_VS_FIXEDFILEINFO
#define PYUWP_HAVE_VS_FIXEDFILEINFO 1
typedef struct tagVS_FIXEDFILEINFO {
    DWORD dwSignature;
    DWORD dwStrucVersion;
    DWORD dwFileVersionMS;
    DWORD dwFileVersionLS;
    DWORD dwProductVersionMS;
    DWORD dwProductVersionLS;
    DWORD dwFileFlagsMask;
    DWORD dwFileFlags;
    DWORD dwFileOS;
    DWORD dwFileType;
    DWORD dwFileSubtype;
    DWORD dwFileDateMS;
    DWORD dwFileDateLS;
} VS_FIXEDFILEINFO;
#endif


/* --- PyUWP pyconfig partition: pyconfig.h defines every Windows partition by
 *     default. Force the App Container set so desktop-only modules stay out of
 *     the static core when compiled with PY_UWP. --- */
#ifdef PY_UWP
#  ifdef MS_WINDOWS_DESKTOP
#    undef MS_WINDOWS_DESKTOP
#  endif
#  ifdef MS_WINDOWS_SYSTEM
#    undef MS_WINDOWS_SYSTEM
#  endif
#  ifdef MS_WINDOWS_GAMES
#    undef MS_WINDOWS_GAMES
#  endif
#  ifndef MS_WINDOWS_APP
#    define MS_WINDOWS_APP
#  endif
#endif


/* --- RPC status codes used by AF_HYPERV UUID helpers in socketmodule.
 *     Full rpcrt4 is not part of the App partition header set we see. --- */
#ifndef RPC_S_OK
#define RPC_S_OK 0
#endif
#ifndef RPC_S_INVALID_STRING_UUID
#define RPC_S_INVALID_STRING_UUID 1705L
#endif

/* --- ntdll PROCESSINFOCLASS bits for os.getppid() on UWP SDKs that hide
 *     winternl's full PROCESSINFOCLASS enum. Values match public MSDN. --- */
#if !defined(_WINTERNL_) && !defined(_NTDEF_)
typedef LONG NTSTATUS;
#endif
#if !defined(_WINTERNL_)
typedef enum _PROCESSINFOCLASS {
    ProcessBasicInformation = 0
} PROCESSINFOCLASS;
#endif
#ifndef NT_SUCCESS
#define NT_SUCCESS(Status) (((NTSTATUS)(Status)) >= 0)
#endif
#endif /* PY_UWP */
#endif /* PYUWP_SHIM_H */
