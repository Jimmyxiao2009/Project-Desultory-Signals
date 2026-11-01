#pragma once

/*
 * Project-Desultory-Signals native host ABI for embedded CPython 3.15.
 *
 * Forked from the AlterGate/PyUWP driver ABI (see PROVENANCE.md). Two things
 * changed and nothing else:
 *
 *   1. A JSON request/response entry point (PyUwpRuntime_CallJsonEx) so the host
 *      can call the protocol layer without smuggling payloads through stdout or
 *      a temp file. The response buffer is owned by the runtime and stays valid
 *      until the next ABI call.
 *   2. The module search path also contains <runtime_root>\AppLib, so our own
 *      packages never share a directory with the standard library.
 *
 * Thread ownership is unchanged and is a hard rule: the host creates exactly one
 * engine thread and calls every function below on it. Calling from a UWP UI
 * thread returns PYUWP_E_WRONG_THREAD instead of corrupting the interpreter.
 */

#ifdef _WIN32
#  ifdef __cplusplus
#    define PYUWP_EXTERN extern "C"
#  else
#    define PYUWP_EXTERN extern
#  endif
#  define PYUWP_API PYUWP_EXTERN __declspec(dllimport)
#else
#  define PYUWP_API extern
#endif

enum {
    PYUWP_OK = 0,
    PYUWP_E_INVALID_ARGUMENT = -1,
    PYUWP_E_WRONG_THREAD = -2,
    PYUWP_E_INITIALIZATION = -1000,
    PYUWP_E_BRIDGE_UNAVAILABLE = -1100,
    PYUWP_E_EXECUTION = -2000,
    PYUWP_E_RESPONSE_TOO_LARGE = -2100,
};

/*
 * Initializes one isolated CPython runtime.
 *
 * runtime_root is the installed package directory that contains Lib/ (the
 * standard library) and AppLib/ (this project's Python package).
 * local_site names the writable LocalState site-packages directory used by the
 * bundled local package manager. Both paths remain owned by the caller.
 */
PYUWP_API int PyUwpRuntime_Initialize(const wchar_t *runtime_root,
                                      const wchar_t *local_site);

/*
 * Executes one JSON request against the protocol layer and returns the JSON
 * response.
 *
 * On success returns PYUWP_OK and assigns *response_utf8 to a NUL-terminated
 * UTF-8 buffer owned by the runtime, and *response_length to its byte length
 * (excluding the terminator). The buffer is valid until the next ABI call that
 * produces a response.
 *
 * On failure the return value is negative and *response_utf8 may still hold a
 * structured {"ok":false,...} envelope, because a protocol failure the caller
 * can classify is more useful than a bare error code.
 */
PYUWP_API int PyUwpRuntime_CallJsonEx(const char *request_utf8,
                                      const char **response_utf8,
                                      int *response_length);

/* Convenience wrapper that ignores the length. */
PYUWP_API int PyUwpRuntime_CallJson(const char *request_utf8,
                                    const char **response_utf8);

/* The most recent response buffer, or NULL. */
PYUWP_API const char *PyUwpRuntime_LastResponse(void);

/* Executes UTF-8 Python source in __main__. Retained for diagnostics. */
PYUWP_API int PyUwpRuntime_RunUtf8(const char *source);

/* Evaluates a UTF-8 Python expression and returns a signed C long result. */
PYUWP_API int PyUwpRuntime_EvalInt(const char *expression, long *value);

/* Finalizes the runtime. Must be called by its initializing engine thread. */
PYUWP_API int PyUwpRuntime_Finalize(void);

/* UTF-8 diagnostic for the last failed ABI call. Valid until the next call. */
PYUWP_API const char *PyUwpRuntime_LastError(void);

/* UTF-8 stdout/stderr captured from the most recent RunUtf8 call. */
PYUWP_API const char *PyUwpRuntime_LastOutput(void);

/* Capabilities probe: returns 1 when the protocol layer imported successfully. */
PYUWP_API int PyUwpRuntime_BridgeReady(void);

/* First-package smoke probe retained for deployment diagnostics. */
PYUWP_API int PyUwpSelfTest(void);
PYUWP_API const char *PyUwpLastError(void);
