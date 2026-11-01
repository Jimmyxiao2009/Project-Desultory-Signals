/* desultory-driver.c -- native host ABI for the embedded CPython 3.15 runtime,
 * forked from AlterGate/PyUWP's pyuwp-driver.c. See PROVENANCE.md.
 *
 * The addition over the original is PyUwpRuntime_CallJsonEx: one JSON request in,
 * one JSON response out. The original ABI could only run source and read back
 * stdout through a 64 KiB static buffer, which is not a usable channel for feed
 * pages. Everything else (thread ownership, isolated PyConfig, stdout capture for
 * diagnostics) is deliberately unchanged.
 *
 * Design notes worth keeping:
 *
 *  - The response buffer is owned here and grows on demand, capped at
 *    PY_UWP_RESPONSE_LIMIT. It is never handed to Python; Python returns a str,
 *    which we copy out and DECREF immediately, so no Python object outlives the
 *    call.
 *
 *  - A Python exception is converted into a structured {"ok":false,...} envelope
 *    with the same shape douyin.protocol produces. A caller that can classify a
 *    failure is worth more than a caller that gets -2000 and a message string,
 *    and it means C# has exactly one response schema to parse.
 *
 *  - The exception text is str(exc) only, never a full traceback: a traceback
 *    frames our own call arguments, which include cookie-bearing request JSON.
 */

#include <windows.h>

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#define PY_SSIZE_T_CLEAN
#include <Python.h>

/* Our own ABI header, included by the *defining* translation unit on purpose:
 * a prototype mismatch between the header the host compiles against and the
 * functions actually exported here becomes a compile error instead of an ABI
 * fault on the device. (Header is in C, so PYUWP_API is plain `extern`, not
 * dllimport.) */
#include "PyUwpDriver.h"

/* 16 MiB is far above any real feed/comment page and still leaves a Lumia-class
 * device enough headroom to survive a pathological response. */
#define PY_UWP_RESPONSE_LIMIT (16u * 1024u * 1024u)

static char g_last_error[512] = "not run";
static char g_last_output[65536];

static char *g_response = NULL;
static size_t g_response_capacity = 0;
static size_t g_response_length = 0;

static DWORD g_runtime_thread_id;
static int g_runtime_initialized;

/* Cached handle for douyin.protocol. g_bridge_state: 0 = not tried,
 * 1 = imported, -1 = import failed (do not retry every call). */
static PyObject *g_bridge_module = NULL;
static int g_bridge_state = 0;

/* ------------------------------------------------------------------ */
/* small utilities                                                     */
/* ------------------------------------------------------------------ */

static void set_last_error_text(const char *text)
{
    snprintf(g_last_error, sizeof(g_last_error), "%s", text ? text : "");
}

static void set_last_errorf(const char *format, ...)
{
    va_list args;
    va_start(args, format);
    vsnprintf(g_last_error, sizeof(g_last_error), format, args);
    va_end(args);
}

static void set_last_error_from_status(const char *operation, PyStatus status)
{
    snprintf(g_last_error, sizeof(g_last_error),
             "%s failed: func=%s; msg=%s; exit=%d", operation,
             status.func ? status.func : "?",
             status.err_msg ? status.err_msg : "?", status.exitcode);
}

static int ensure_runtime_thread(void)
{
    if (!g_runtime_initialized) {
        set_last_error_text("runtime is not initialized");
        return PYUWP_E_INITIALIZATION;
    }
    if (GetCurrentThreadId() != g_runtime_thread_id) {
        set_last_error_text("runtime called from the wrong thread");
        return PYUWP_E_WRONG_THREAD;
    }
    return 0;
}

static void set_last_output(PyObject *value)
{
    const char *utf8 = value ? PyUnicode_AsUTF8(value) : NULL;
    if (!utf8) {
        PyErr_Clear();
        g_last_output[0] = '\0';
        return;
    }
    strncpy_s(g_last_output, sizeof(g_last_output), utf8, _TRUNCATE);
}

/* Reserve room for `need` bytes plus a NUL terminator. Returns 0 on success. */
static int reserve_response(size_t need)
{
    size_t wanted;
    char *grown;

    if (need > PY_UWP_RESPONSE_LIMIT) {
        set_last_errorf("response of %u bytes exceeds the %u byte limit",
                        (unsigned)need, (unsigned)PY_UWP_RESPONSE_LIMIT);
        return PYUWP_E_RESPONSE_TOO_LARGE;
    }
    if (need + 1 <= g_response_capacity) {
        return 0;
    }

    /* Grow geometrically so a paginated burst does not realloc per call. */
    wanted = g_response_capacity ? g_response_capacity : 8192;
    while (wanted < need + 1) {
        wanted *= 2;
        if (wanted > PY_UWP_RESPONSE_LIMIT) {
            wanted = PY_UWP_RESPONSE_LIMIT;
            break;
        }
    }
    if (wanted < need + 1) {
        wanted = need + 1;
    }

    grown = (char *)realloc(g_response, wanted);
    if (!grown) {
        set_last_error_text("could not grow the response buffer");
        return PYUWP_E_EXECUTION;
    }
    g_response = grown;
    g_response_capacity = wanted;
    return 0;
}

static void set_response_bytes(const char *data, size_t length)
{
    if (reserve_response(length) != 0) {
        /* reserve_response already explained the failure. */
        g_response_length = 0;
        if (g_response && g_response_capacity) {
            g_response[0] = '\0';
        }
        return;
    }
    if (length) {
        memcpy(g_response, data, length);
    }
    g_response[length] = '\0';
    g_response_length = length;
}

/* Minimal JSON string escaper. Passes UTF-8 through unchanged, which is what
 * the response contract promises. */
static void json_escape_append(char *out, size_t out_size, const char *text)
{
    size_t used = strlen(out);
    const unsigned char *cursor = (const unsigned char *)text;

    while (cursor && *cursor) {
        unsigned char ch = *cursor++;
        const char *replacement = NULL;
        char encoded[8];

        switch (ch) {
        case '"':  replacement = "\\\""; break;
        case '\\': replacement = "\\\\"; break;
        case '\n': replacement = "\\n"; break;
        case '\r': replacement = "\\r"; break;
        case '\t': replacement = "\\t"; break;
        default:
            if (ch < 0x20) {
                snprintf(encoded, sizeof(encoded), "\\u%04x", ch);
                replacement = encoded;
            }
            break;
        }

        if (replacement) {
            size_t need = strlen(replacement);
            if (used + need + 1 >= out_size) {
                break;
            }
            memcpy(out + used, replacement, need);
            used += need;
        } else {
            if (used + 2 >= out_size) {
                break;
            }
            out[used++] = (char)ch;
        }
    }
    out[used] = '\0';
}

/* Emit {"ok":false,...} with the same field names douyin.protocol uses. */
static void set_json_error(const char *kind, const char *message)
{
    char *buffer;
    size_t capacity = strlen(message ? message : "") * 6 + 512;

    buffer = (char *)malloc(capacity);
    if (!buffer) {
        static const char fallback[] =
            "{\"ok\":false,\"error\":{\"kind\":\"internal\","
            "\"message\":\"native error buffer unavailable\","
            "\"httpStatus\":null,\"retryable\":false,\"detail\":{}}}";
        set_response_bytes(fallback, strlen(fallback));
        return;
    }

    buffer[0] = '\0';
    snprintf(buffer, capacity,
             "{\"ok\":false,\"error\":{\"kind\":\"%s\",\"message\":\"", kind);
    json_escape_append(buffer, capacity, message ? message : "");
    {
        size_t used = strlen(buffer);
        snprintf(buffer + used, capacity - used,
                 "\",\"httpStatus\":null,\"retryable\":false,\"detail\":{}}}");
    }
    set_response_bytes(buffer, strlen(buffer));
    free(buffer);
}

/* ------------------------------------------------------------------ */
/* bridge import                                                       */
/* ------------------------------------------------------------------ */

static PyObject *bridge_module(void)
{
    PyObject *name;

    if (g_bridge_state == 1) {
        return g_bridge_module;
    }
    if (g_bridge_state == -1) {
        return NULL;
    }

    name = PyUnicode_FromString("douyin.protocol");
    if (!name) {
        PyErr_Clear();
        g_bridge_state = -1;
        return NULL;
    }
    g_bridge_module = PyImport_Import(name);
    Py_DECREF(name);

    if (!g_bridge_module) {
        /* Record the reason once, then stop retrying: an import failure is
         * always a packaging error, not a transient condition. */
        PyObject *type = NULL, *value = NULL, *traceback = NULL;
        PyObject *text = NULL;

        PyErr_Fetch(&type, &value, &traceback);
        PyErr_NormalizeException(&type, &value, &traceback);
        if (value) {
            text = PyObject_Str(value);
        }
        set_last_errorf("could not import douyin.protocol: %s",
                        text ? PyUnicode_AsUTF8(text) : "(no detail)");
        Py_XDECREF(text);
        Py_XDECREF(type);
        Py_XDECREF(value);
        Py_XDECREF(traceback);
        PyErr_Clear();
        g_bridge_state = -1;
        return NULL;
    }

    g_bridge_state = 1;
    return g_bridge_module;
}

/* ------------------------------------------------------------------ */
/* public ABI                                                          */
/* ------------------------------------------------------------------ */

__declspec(dllexport) const char *PyUwpRuntime_LastError(void)
{
    return g_last_error;
}

__declspec(dllexport) const char *PyUwpRuntime_LastOutput(void)
{
    return g_last_output;
}

__declspec(dllexport) const char *PyUwpRuntime_LastResponse(void)
{
    return g_response;
}

__declspec(dllexport) int PyUwpRuntime_BridgeReady(void)
{
    if (!g_runtime_initialized) {
        return 0;
    }
    return bridge_module() ? 1 : 0;
}

__declspec(dllexport) int PyUwpRuntime_Initialize(const wchar_t *runtime_root,
                                                   const wchar_t *local_site)
{
    wchar_t lib_path[MAX_PATH * 4];
    wchar_t app_lib_path[MAX_PATH * 4];
    PyConfig config;
    PyStatus status;

    if (!runtime_root || !*runtime_root) {
        set_last_error_text("runtime_root is required");
        return PYUWP_E_INVALID_ARGUMENT;
    }
    if (g_runtime_initialized) {
        set_last_error_text("runtime is already initialized");
        return PYUWP_E_INVALID_ARGUMENT;
    }

    if (swprintf_s(lib_path, _countof(lib_path), L"%s\\Lib", runtime_root) < 0 ||
        swprintf_s(app_lib_path, _countof(app_lib_path), L"%s\\AppLib", runtime_root) < 0) {
        set_last_error_text("runtime_root is too long");
        return PYUWP_E_INVALID_ARGUMENT;
    }

    PyConfig_InitIsolatedConfig(&config);
    config.site_import = 0;
    config.write_bytecode = 0;
    config.use_environment = 0;
    config.parse_argv = 0;
    config.module_search_paths_set = 1;

    /* Order matters: the standard library first, then our own package root. */
    status = PyWideStringList_Append(&config.module_search_paths, lib_path);
    if (!PyStatus_Exception(status)) {
        status = PyWideStringList_Append(&config.module_search_paths, app_lib_path);
    }
    if (!PyStatus_Exception(status) && local_site && *local_site) {
        status = PyWideStringList_Append(&config.module_search_paths, local_site);
    }
    if (!PyStatus_Exception(status)) {
        status = PyConfig_SetString(&config, &config.program_name, L"desultory");
    }
    if (!PyStatus_Exception(status)) {
        status = Py_InitializeFromConfig(&config);
    }
    PyConfig_Clear(&config);

    if (PyStatus_Exception(status)) {
        set_last_error_from_status("initialization", status);
        return PYUWP_E_INITIALIZATION;
    }

    if (local_site && *local_site) {
        PyObject *site_path = PyUnicode_FromWideChar(local_site, -1);
        PyObject *sys_module = PyImport_ImportModule("sys");
        if (!site_path || !sys_module ||
            PyObject_SetAttrString(sys_module, "_pyuwp_site_packages", site_path) < 0) {
            Py_XDECREF(site_path);
            Py_XDECREF(sys_module);
            PyErr_Clear();
            Py_FinalizeEx();
            set_last_error_text("could not register the private package directory");
            return PYUWP_E_INITIALIZATION;
        }
        Py_DECREF(site_path);
        Py_DECREF(sys_module);
    }

    g_runtime_thread_id = GetCurrentThreadId();
    g_runtime_initialized = 1;
    g_bridge_state = 0;
    g_bridge_module = NULL;
    g_last_output[0] = '\0';
    set_last_error_text("ok");
    return PYUWP_OK;
}

__declspec(dllexport) int PyUwpRuntime_CallJsonEx(const char *request_utf8,
                                                   const char **response_utf8,
                                                   int *response_length)
{
    PyObject *module, *handler, *argument, *result;
    int state;

    if (response_utf8) {
        *response_utf8 = NULL;
    }
    if (response_length) {
        *response_length = 0;
    }

    state = ensure_runtime_thread();
    if (state != 0) {
        return state;
    }
    if (!request_utf8) {
        set_last_error_text("request_utf8 is required");
        return PYUWP_E_INVALID_ARGUMENT;
    }

    module = bridge_module();
    if (!module) {
        set_json_error("signer_unavailable",
                       g_last_error /* the import failure text */);
        if (response_utf8) {
            *response_utf8 = g_response;
        }
        if (response_length) {
            *response_length = (int)g_response_length;
        }
        return PYUWP_E_BRIDGE_UNAVAILABLE;
    }

    handler = PyObject_GetAttrString(module, "handle_json");
    if (!handler) {
        PyErr_Clear();
        set_last_error_text("douyin.protocol.handle_json is missing");
        set_json_error("internal", "douyin.protocol.handle_json is missing");
        if (response_utf8) {
            *response_utf8 = g_response;
        }
        if (response_length) {
            *response_length = (int)g_response_length;
        }
        return PYUWP_E_BRIDGE_UNAVAILABLE;
    }

    argument = PyUnicode_DecodeUTF8(request_utf8, (Py_ssize_t)strlen(request_utf8), "replace");
    if (!argument) {
        Py_DECREF(handler);
        PyErr_Clear();
        set_last_error_text("request is not valid UTF-8");
        set_json_error("invalid_input", "request is not valid UTF-8");
        if (response_utf8) {
            *response_utf8 = g_response;
        }
        if (response_length) {
            *response_length = (int)g_response_length;
        }
        return PYUWP_E_INVALID_ARGUMENT;
    }

    result = PyObject_CallFunctionObjArgs(handler, argument, NULL);
    Py_DECREF(argument);
    Py_DECREF(handler);

    if (!result) {
        /* handle_json is written never to raise; reaching here means a bug in
         * the protocol layer itself. Report str(exc) only -- a traceback would
         * echo the request, which carries cookies. */
        PyObject *type = NULL, *value = NULL, *traceback = NULL;
        PyObject *text = NULL;

        PyErr_Fetch(&type, &value, &traceback);
        PyErr_NormalizeException(&type, &value, &traceback);
        if (value) {
            text = PyObject_Str(value);
        }
        set_last_errorf("handle_json raised: %s",
                        text ? PyUnicode_AsUTF8(text) : "(no detail)");
        set_json_error("internal", g_last_error);
        Py_XDECREF(text);
        Py_XDECREF(type);
        Py_XDECREF(value);
        Py_XDECREF(traceback);
        PyErr_Clear();

        if (response_utf8) {
            *response_utf8 = g_response;
        }
        if (response_length) {
            *response_length = (int)g_response_length;
        }
        return PYUWP_E_EXECUTION;
    }

    if (PyUnicode_Check(result)) {
        Py_ssize_t size = 0;
        const char *utf8 = PyUnicode_AsUTF8AndSize(result, &size);
        if (!utf8) {
            PyErr_Clear();
            Py_DECREF(result);
            set_last_error_text("handle_json did not return valid UTF-8");
            set_json_error("internal", "handle_json did not return valid UTF-8");
            if (response_utf8) {
                *response_utf8 = g_response;
            }
            if (response_length) {
                *response_length = (int)g_response_length;
            }
            return PYUWP_E_EXECUTION;
        }
        state = reserve_response((size_t)size);
        if (state != 0) {
            Py_DECREF(result);
            set_json_error("internal", g_last_error);
            if (response_utf8) {
                *response_utf8 = g_response;
            }
            if (response_length) {
                *response_length = (int)g_response_length;
            }
            return state;
        }
        set_response_bytes(utf8, (size_t)size);
    } else {
        PyObject *text = PyObject_Str(result);
        const char *utf8 = text ? PyUnicode_AsUTF8(text) : NULL;
        if (!utf8) {
            PyErr_Clear();
            Py_DECREF(text);
            Py_DECREF(result);
            set_last_error_text("handle_json returned a non-text value");
            set_json_error("internal", "handle_json returned a non-text value");
            if (response_utf8) {
                *response_utf8 = g_response;
            }
            if (response_length) {
                *response_length = (int)g_response_length;
            }
            return PYUWP_E_EXECUTION;
        }
        set_response_bytes(utf8, strlen(utf8));
        Py_DECREF(text);
    }
    Py_DECREF(result);

    if (response_utf8) {
        *response_utf8 = g_response;
    }
    if (response_length) {
        *response_length = (int)g_response_length;
    }
    set_last_error_text("ok");
    return PYUWP_OK;
}

__declspec(dllexport) int PyUwpRuntime_CallJson(const char *request_utf8,
                                                 const char **response_utf8)
{
    return PyUwpRuntime_CallJsonEx(request_utf8, response_utf8, NULL);
}

__declspec(dllexport) int PyUwpRuntime_RunUtf8(const char *source)
{
    PyObject *main_module, *dict, *source_text, *result;
    int ok;
    int state = ensure_runtime_thread();
    static const char capture_wrapper[] =
        "import sys as __pyuwp_sys, io as __pyuwp_io\n"
        "__pyuwp_stdout = __pyuwp_sys.stdout\n"
        "__pyuwp_stderr = __pyuwp_sys.stderr\n"
        "__pyuwp_capture = __pyuwp_io.StringIO()\n"
        "__pyuwp_sys.stdout = __pyuwp_capture\n"
        "__pyuwp_sys.stderr = __pyuwp_capture\n"
        "try:\n"
        "    exec(compile(__pyuwp_source, '<terminal>', 'exec'), globals(), globals())\n"
        "except BaseException:\n"
        "    import traceback as __pyuwp_traceback\n"
        "    __pyuwp_traceback.print_exc()\n"
        "    __pyuwp_ok = False\n"
        "else:\n"
        "    __pyuwp_ok = True\n"
        "finally:\n"
        "    __pyuwp_sys.stdout = __pyuwp_stdout\n"
        "    __pyuwp_sys.stderr = __pyuwp_stderr\n"
        "__pyuwp_output = __pyuwp_capture.getvalue()\n";

    if (state != 0) {
        return state;
    }
    if (!source) {
        set_last_error_text("source is required");
        return PYUWP_E_INVALID_ARGUMENT;
    }

    main_module = PyImport_AddModule("__main__");
    dict = main_module ? PyModule_GetDict(main_module) : NULL;
    source_text = PyUnicode_DecodeUTF8(source, (Py_ssize_t)strlen(source), "strict");
    if (!dict || !source_text || PyDict_SetItemString(dict, "__pyuwp_source", source_text) < 0) {
        Py_XDECREF(source_text);
        PyErr_Clear();
        set_last_error_text("could not prepare Python source");
        return PYUWP_E_EXECUTION;
    }
    Py_DECREF(source_text);

    result = PyRun_String(capture_wrapper, Py_file_input, dict, dict);
    if (!result) {
        PyErr_Clear();
        set_last_error_text("terminal wrapper failed");
        return PYUWP_E_EXECUTION;
    }
    Py_DECREF(result);

    set_last_output(PyDict_GetItemString(dict, "__pyuwp_output"));
    ok = PyObject_IsTrue(PyDict_GetItemString(dict, "__pyuwp_ok"));
    if (ok < 0 || !ok) {
        PyErr_Clear();
        set_last_error_text("Python source execution failed");
        return PYUWP_E_EXECUTION;
    }
    set_last_error_text("ok");
    return PYUWP_OK;
}

__declspec(dllexport) int PyUwpRuntime_EvalInt(const char *expression, long *value)
{
    PyObject *main_module, *dict, *result;
    int state = ensure_runtime_thread();

    if (state != 0) {
        return state;
    }
    if (!expression || !value) {
        set_last_error_text("expression and value are required");
        return PYUWP_E_INVALID_ARGUMENT;
    }

    main_module = PyImport_AddModule("__main__");
    dict = main_module ? PyModule_GetDict(main_module) : NULL;
    result = dict ? PyRun_String(expression, Py_eval_input, dict, dict) : NULL;
    if (!result) {
        PyErr_Clear();
        set_last_error_text("Python expression evaluation failed");
        return PYUWP_E_EXECUTION;
    }
    *value = PyLong_AsLong(result);
    Py_DECREF(result);
    if (PyErr_Occurred()) {
        PyErr_Clear();
        set_last_error_text("expression result is not a C long");
        return PYUWP_E_EXECUTION;
    }
    set_last_error_text("ok");
    return PYUWP_OK;
}

__declspec(dllexport) int PyUwpRuntime_Finalize(void)
{
    int result;
    int state = ensure_runtime_thread();

    if (state != 0) {
        return state;
    }

    Py_CLEAR(g_bridge_module);
    g_bridge_state = 0;

    result = Py_FinalizeEx();
    g_runtime_initialized = 0;
    g_runtime_thread_id = 0;

    if (g_response) {
        free(g_response);
        g_response = NULL;
        g_response_capacity = 0;
        g_response_length = 0;
    }

    if (result < 0) {
        set_last_error_text("Py_FinalizeEx failed");
        return PYUWP_E_EXECUTION;
    }
    set_last_error_text("ok");
    return PYUWP_OK;
}

__declspec(dllexport) const char *PyUwpLastError(void)
{
    return g_last_error;
}

__declspec(dllexport) int PyUwpSelfTest(void)
{
    PyStatus status;
    PyConfig config;
    int result = -1;

    PyConfig_InitIsolatedConfig(&config);
    config.site_import = 0;
    config.write_bytecode = 0;
    config.use_environment = 0;
    config.parse_argv = 0;

    status = Py_InitializeFromConfig(&config);
    PyConfig_Clear(&config);
    if (PyStatus_Exception(status)) {
        set_last_error_from_status("initialization", status);
        return PYUWP_E_INITIALIZATION;
    }

    {
        PyObject *module = PyImport_AddModule("__main__");
        if (module) {
            PyObject *dict = PyModule_GetDict(module);
            PyObject *value = PyRun_String("19 + 23", Py_eval_input, dict, dict);
            if (value) {
                long number = PyLong_AsLong(value);
                result = (number == 42) ? 0 : (int)number;
                Py_DECREF(value);
            } else {
                PyErr_Clear();
                set_last_error_text("evaluation of 19 + 23 failed");
                result = PYUWP_E_EXECUTION;
            }
        }
    }

    Py_Finalize();
    if (result == 0) {
        set_last_error_text("ok");
    } else if (result == -1) {
        set_last_error_text("__main__ unavailable");
    }
    return result;
}

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved)
{
    (void)hinst;
    (void)reason;
    (void)reserved;
    return TRUE;
}
