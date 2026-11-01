/* desultory-smoke.c -- x64 desktop smoke host for the embedded CPython 3.15 runtime.
 *
 * Why this exists: the target device is ARM32 and is not always reachable, but the
 * *host ABI* and the *protocol layer* are architecture independent. Building the same
 * driver against the x64 CPython core and driving it from a desktop executable
 * verifies, for real, the three things that would otherwise only be verified on the
 * device:
 *
 *   1. CPython 3.15 boots and the extended ABI (PyUwpRuntime_CallJsonEx) works.
 *   2. `douyin.protocol` imports and executes inside the *embedded* interpreter --
 *      not the desktop 3.13 interpreter the unit tests use, which is a different
 *      standard library and would hide a packaging mistake.
 *   3. The response buffer really carries a payload larger than the 64 KiB stdout
 *      buffer of the original AlterGate ABI, which is the entire reason the ABI was
 *      extended.
 *
 * It is deliberately a C program using only LoadLibrary/GetProcAddress: if this runs,
 * a C# P/Invoke binding to the same exports cannot fail for ABI reasons.
 *
 * Requests are built here rather than in a helper library so the smoke test also
 * exercises the JSON envelope exactly as the C# host does, including threading an
 * identity document from one call into the next (see extract_identity).
 */

#include <windows.h>

#include <io.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef int(__cdecl *InitializeFn)(const wchar_t *runtime_root, const wchar_t *local_site);
typedef int(__cdecl *CallJsonExFn)(const char *request, const char **response, int *length);
typedef int(__cdecl *FinalizeFn)(void);
typedef const char *(__cdecl *LastErrorFn)(void);
typedef int(__cdecl *BridgeReadyFn)(void);
typedef int(__cdecl *EvalIntFn)(const char *expression, long *value);
typedef int(__cdecl *RunUtf8Fn)(const char *source);
typedef const char *(__cdecl *LastOutputFn)(void);

static InitializeFn pInitialize;
static CallJsonExFn pCallJsonEx;
static FinalizeFn pFinalize;
static LastErrorFn pLastError;
static BridgeReadyFn pBridgeReady;
static EvalIntFn pEvalInt;
static RunUtf8Fn pRunUtf8;
static LastOutputFn pLastOutput;

static int g_checks;
static int g_failures;

#define CLOCK "{\"seed\":20261007,\"fixedNowMs\":1700000000000}"

static void check(const char *name, int ok, const char *detail)
{
    g_checks++;
    if (ok) {
        printf("  [PASS] %s\n", name);
    } else {
        g_failures++;
        printf("  [FAIL] %s%s%s\n", name, (detail && *detail) ? "  -- " : "",
               (detail && *detail) ? detail : "");
    }
}

static const char *last_error_text(void)
{
    return pLastError ? pLastError() : "(no LastError export)";
}

/* ------------------------------------------------------------------ */
/* JSON helpers                                                        */
/* ------------------------------------------------------------------ */

/* Copy the value of "key":"value" into out. Returns 1 on success. */
static int json_string(const char *json, const char *key, char *out, size_t cap)
{
    const char *at = strstr(json, key);
    size_t index = 0;
    if (!at) return 0;
    at += strlen(key);
    while (*at && *at != '"') at++;
    if (*at != '"') return 0;
    at++;
    while (*at && *at != '"' && index + 1 < cap) {
        if (*at == '\\' && at[1]) at++;
        out[index++] = *at++;
    }
    out[index] = '\0';
    return 1;
}

/*
 * Read an unquoted integer value, e.g. "count":8.
 *
 * Separate from json_string because the protocol layer emits counts as numbers, and a
 * quoted-string reader silently skips past the digits to the next quote and returns
 * the *following* key -- which is how this helper's absence first showed up (a count
 * check that reported the string "op").
 */
static int json_int(const char *json, const char *key, int fallback)
{
    const char *at = strstr(json, key);
    if (!at) return fallback;
    at += strlen(key);
    while (*at == ' ') at++;
    if (*at < '0' || *at > '9') return fallback;
    return atoi(at);
}

/*
 * Print a wide string as UTF-8.
 *
 * printf("%ls", ...) converts through the *ANSI* code page, so a path containing a
 * non-ASCII component (this repository lives under 项目) is truncated or mangled. The
 * wide-to-UTF-8 conversion is explicit here for the same reason the host takes its
 * command line as wchar_t.
 */
static void print_utf8(const wchar_t *text)
{
    if (!text) return;
    int needed = WideCharToMultiByte(CP_UTF8, 0, text, -1, NULL, 0, NULL, NULL);
    if (needed <= 1) return;
    char *buffer = (char *)malloc((size_t)needed);
    if (!buffer) return;
    WideCharToMultiByte(CP_UTF8, 0, text, -1, buffer, needed, NULL, NULL);
    printf("%s", buffer);
    free(buffer);
}

/*
 * Pull a nested object out of a response by brace matching, e.g. the value of
 * "identity": {...} or "guard": {...}.
 *
 * The host has to carry the identity (and the publish guard) from one call to the
 * next, because the protocol layer is stateless by design -- the host owns storage.
 * Doing that with brace matching here keeps the smoke test independent of any JSON
 * library, and it exercises the same "the response is a JSON document I must parse"
 * contract the C# host has. String state and escapes are tracked so a brace inside a
 * description does not end the object early.
 */
static char *extract_object(const char *json, const char *key)
{
    const char *at = strstr(json, key);
    const char *start;
    int depth = 0;
    int in_string = 0;
    int escaped = 0;

    if (!at) return NULL;
    at += strlen(key);
    while (*at == ' ') at++;
    if (*at != '{') return NULL;

    start = at;
    for (; *at; at++) {
        char ch = *at;
        if (in_string) {
            if (escaped) {
                escaped = 0;
            } else if (ch == '\\') {
                escaped = 1;
            } else if (ch == '"') {
                in_string = 0;
            }
            continue;
        }
        if (ch == '"') {
            in_string = 1;
        } else if (ch == '{') {
            depth++;
        } else if (ch == '}') {
            depth--;
            if (depth == 0) {
                size_t length = (size_t)(at - start + 1);
                char *out = (char *)malloc(length + 1);
                if (!out) return NULL;
                memcpy(out, start, length);
                out[length] = '\0';
                return out;
            }
        }
    }
    return NULL;
}

static char *extract_identity(const char *json)
{
    return extract_object(json, "\"identity\":");
}

/* Build {"op":..., "clock":..., <fields>} */
static char *request_plain(const char *op, const char *fields)
{
    size_t cap = strlen(op) + strlen(fields) + 256;
    char *out = (char *)malloc(cap);
    if (!out) return NULL;
    snprintf(out, cap, "{\"op\":\"%s\",\"clock\":%s%s}", op, CLOCK, fields);
    return out;
}

/* Build {"op":..., "clock":..., "identity":<identity>, <fields>} */
static char *request_with_identity(const char *op, const char *identity, const char *fields)
{
    size_t cap = strlen(op) + strlen(identity) + strlen(fields) + 256;
    char *out = (char *)malloc(cap);
    if (!out) return NULL;
    snprintf(out, cap, "{\"op\":\"%s\",\"clock\":%s,\"identity\":%s%s}",
             op, CLOCK, identity, fields);
    return out;
}

/*
 * Send one request and return a heap copy of the response.
 *
 * The copy matters: the runtime owns the response buffer and it is only valid until
 * the next call, so holding a pointer across calls would be a use-after-free. That is
 * a real hazard the C# binding has to handle the same way.
 */
static char *bridge_call(const char *request, int *status_out, int *length_out)
{
    const char *response = NULL;
    int length = 0;
    int status = pCallJsonEx(request, &response, &length);
    char *copy;

    if (status_out) *status_out = status;
    if (length_out) *length_out = length;
    if (!response) return NULL;

    copy = (char *)malloc((size_t)length + 1);
    if (!copy) return NULL;
    memcpy(copy, response, (size_t)length);
    copy[length] = '\0';
    return copy;
}

/* ------------------------------------------------------------------ */
/* tests                                                              */
/* ------------------------------------------------------------------ */

static void test_self_test(void)
{
    int status = 0;
    int length = 0;
    char *response = bridge_call("{\"op\":\"diagnostics.selfTest\",\"clock\":" CLOCK "}", &status, &length);
    int ok;
    int count;

    printf("-- diagnostics.selfTest (runs inside the embedded interpreter)\n");

    if (!response) {
        check("the self test produced a response", 0, last_error_text());
        return;
    }

    ok = strstr(response, "\"ok\":true") != NULL;
    check("the in-process self test passes", ok && status == 0,
          ok ? "" : (strstr(response, "\"message\":\"") ? response : last_error_text()));

    count = json_int(response, "\"count\":", -1);
    check("the self test reported its check count", count >= 8,
          count < 0 ? response : "count reported as a non-number");
    printf("     %d checks, %d bytes\n", count, length);
    free(response);
}

static void test_catalogue(void)
{
    int length = 0;
    char *response = bridge_call("{\"op\":\"diagnostics.catalogue\",\"clock\":" CLOCK "}", NULL, &length);
    char version[128] = "";

    printf("-- diagnostics.catalogue\n");

    if (!response) {
        check("the catalogue produced a response", 0, last_error_text());
        return;
    }

    check("the catalogue succeeds", strstr(response, "\"ok\":true") != NULL, response);

    if (json_string(response, "\"pythonVersion\":", version, sizeof(version))) {
        printf("     embedded python: %s\n", version);
        check("the embedded interpreter is CPython 3.15",
              strncmp(version, "3.15", 4) == 0, version);
    } else {
        check("the embedded interpreter is CPython 3.15", 0, "no pythonVersion in the response");
    }

    check("the catalogue carries the signature policy",
          strstr(response, "\"signaturePolicy\"") != NULL, "");
    check("the catalogue reports the signing capability",
          strstr(response, "\"providerRegistered\"") != NULL, "");
    check("the catalogue reports the QR flow state",
          strstr(response, "\"qrFlowAvailable\"") != NULL, "");
    printf("     %d bytes\n", length);
    free(response);
}

static char *g_identity;

static void test_identity_and_requests(void)
{
    int length = 0;
    char *created = bridge_call("{\"op\":\"identity.create\",\"clock\":" CLOCK "}", NULL, &length);
    char *built;
    char identity_id[64] = "";

    printf("-- identity + request construction\n");

    if (!created) {
        check("identity.create produced a response", 0, last_error_text());
        return;
    }
    check("identity.create succeeds", strstr(created, "\"ok\":true") != NULL, created);
    check("the identity is self-consistent", strstr(created, "\"problems\":[]") != NULL, created);

    g_identity = extract_identity(created);
    check("the identity document can be extracted from the response", g_identity != NULL,
          "brace matching found no identity object");

    if (json_string(created, "\"identityId\":", identity_id, sizeof(identity_id)))
        printf("     identityId=%s\n", identity_id);
    free(created);

    if (!g_identity) return;

    /* The measured signature policy, verified inside the real runtime. */
    built = bridge_call(request_with_identity("request.build", g_identity,
                                       ",\"operation\":\"aweme_detail\",\"params\":{\"aweme_id\":\"7300000000000000000\"}"),
                 NULL, &length);
    check("aweme_detail builds", built && strstr(built, "\"ok\":true") != NULL,
          built ? built : last_error_text());
    if (built) {
        check("aweme_detail omits a_bogus", strstr(built, "a_bogus") == NULL, built);
        check("aweme_detail records the omit policy",
              strstr(built, "\"signatureState\":\"omitted\"") != NULL, built);
        check("the plan warns that ttwid is missing (the real gate)",
              strstr(built, "\"ttwidMissing\":true") != NULL, built);
        free(built);
    }

    built = bridge_call(request_with_identity("request.build", g_identity,
                                       ",\"operation\":\"comment_list\",\"params\":{\"aweme_id\":\"1\"}"),
                 NULL, &length);
    check("comment_list builds", built && strstr(built, "\"ok\":true") != NULL,
          built ? built : last_error_text());
    if (built) {
        check("comment_list carries the a_bogus parameter", strstr(built, "a_bogus=") != NULL, built);
        check("comment_list records the presence policy",
              strstr(built, "\"signatureState\":\"presence\"") != NULL, built);
        free(built);
    }

    built = bridge_call(request_with_identity("request.build", g_identity,
                                       ",\"operation\":\"comment_list\",\"params\":{\"aweme_id\":\"1\"},\"allowUnsigned\":true"),
                 NULL, &length);
    if (built) {
        check("an explicitly unsigned request omits the parameter",
              strstr(built, "a_bogus") == NULL, built);
        free(built);
    }

    built = bridge_call(request_with_identity("request.build", g_identity,
                                       ",\"operation\":\"aweme_detail\",\"params\":{\"aweme_id\":\"1\",\"typo\":\"x\"}"),
                 NULL, &length);
    if (built) {
        check("a typo'd parameter is refused, not silently signed",
              strstr(built, "\"kind\":\"invalid_input\"") != NULL, built);
        free(built);
    }
}

static void test_share_and_classify(void)
{
    int length = 0;
    char *response;

    printf("-- share parsing\n");

    response = bridge_call("{\"op\":\"share.parse\",\"clock\":" CLOCK ",\"input\":\"https://v.douyin.com/iABC123/\"}",
                    NULL, &length);
    check("a short link is recognised as needing a redirect",
          response && strstr(response, "\"kind\":\"redirect\"") != NULL,
          response ? response : last_error_text());
    free(response);

    response = bridge_call("{\"op\":\"share.parse\",\"clock\":" CLOCK ",\"input\":\"7300000000000000000\"}",
                    NULL, &length);
    check("a bare id is recognised",
          response && strstr(response, "\"kind\":\"aweme_id\"") != NULL,
          response ? response : last_error_text());
    free(response);

    printf("-- response classification\n");

    /* The single most valuable classification: the Argus gate names its missing
     * credential, which is what turns an opaque 403 into an actionable message. */
    response = bridge_call("{\"op\":\"response.classify\",\"clock\":" CLOCK
                    ",\"httpStatus\":403,\"body\":\"Blocked by ArgusSecurityPlugin Uifid Not Found\","
                    "\"operation\":\"aweme_detail\",\"variant\":\"detail.v1\"}",
                    NULL, &length);
    check("the Argus gate is classified as risk control",
          response && strstr(response, "\"kind\":\"risk_control\"") != NULL,
          response ? response : last_error_text());
    check("the missing credential is named",
          response && strstr(response, "\"missingCredential\":\"uifid\"") != NULL,
          response ? response : last_error_text());
    check("the refusal is marked non-retryable",
          response && strstr(response, "\"retryable\":false") != NULL,
          response ? response : last_error_text());
    free(response);

    response = bridge_call("{\"op\":\"response.classify\",\"clock\":" CLOCK
                    ",\"httpStatus\":200,\"body\":\"\",\"operation\":\"aweme_detail\"}",
                    NULL, &length);
    check("an empty 200 is classified as empty_body",
          response && strstr(response, "\"kind\":\"empty_body\"") != NULL,
          response ? response : last_error_text());
    free(response);

    response = bridge_call("{\"op\":\"response.classify\",\"clock\":" CLOCK
                    ",\"httpStatus\":429,\"body\":\"too many requests\",\"operation\":\"feed_recommend\"}",
                    NULL, &length);
    check("a 429 is rate limiting, not a signature problem",
          response && strstr(response, "\"kind\":\"rate_limited\"") != NULL,
          response ? response : last_error_text());
    free(response);
}

static void test_publish_guard(void)
{
    int length = 0;
    /* Initialised to NULL: each of these is freed on a path reachable before it is
     * assigned, and free() on an indeterminate pointer corrupts the heap (observed as
     * STATUS_HEAP_CORRUPTION at process exit, which is a miserable thing to diagnose
     * from the symptom alone). */
    char *first = NULL;
    char *committed = NULL;
    char *duplicate = NULL;
    char *guard = NULL;
    char issued_key[64] = "";

    printf("-- comment publishing (validation + duplicate guard)\n");

    first = bridge_call("{\"op\":\"comment.buildPublish\",\"clock\":" CLOCK
                 ",\"awemeId\":\"1\",\"text\":\"  hello  \"}", NULL, &length);
    check("a comment body builds", first && strstr(first, "\"ok\":true") != NULL,
          first ? first : last_error_text());
    check("a top-level comment sends reply ids of 0",
          first && strstr(first, "\"reply_id\":\"0\"") != NULL, first ? first : "");
    check("text_extra travels as a JSON string",
          first && strstr(first, "\"text_extra\":\"[]\"") != NULL, first ? first : "");
    free(first);

    first = bridge_call("{\"op\":\"comment.buildPublish\",\"clock\":" CLOCK
                 ",\"awemeId\":\"1\",\"text\":\"   \"}", NULL, &length);
    check("an empty comment is refused",
          first && strstr(first, "\"kind\":\"invalid_input\"") != NULL, first ? first : "");
    free(first);

    first = bridge_call("{\"op\":\"comment.buildPublish\",\"clock\":" CLOCK
                 ",\"awemeId\":\"1\",\"text\":\"hi\",\"replyToReplyId\":\"r1\"}", NULL, &length);
    check("a nested reply without a parent is refused",
          first && strstr(first, "\"kind\":\"invalid_input\"") != NULL, first ? first : "");
    free(first);

    /* The duplicate guard, which is what makes "no blind retries on writes" real. */
    first = bridge_call("{\"op\":\"comment.guard\",\"clock\":" CLOCK
                 ",\"awemeId\":\"1\",\"text\":\"hello\"}", NULL, &length);
    check("the guard issues a key", first && strstr(first, "\"key\":") != NULL,
          first ? first : last_error_text());

    guard = first ? extract_object(first, "\"guard\":") : NULL;
    check("the guard object can be extracted", guard != NULL,
          guard ? "" : (first ? first : "no response"));

    /*
     * Commit the key the guard actually computed, not a literal.
     *
     * This mirrors the C# layer exactly (it persists `guardResult["key"]`). An earlier
     * version of this test committed a hardcoded string, so the guard's stored key
     * never matched the next request's computed key and the duplicate check could not
     * fire -- a harness bug that looked like a product failure.
     */
    if (first && json_string(first, "\"key\":", issued_key, sizeof(issued_key)))
        check("the guard returned a key to commit", issued_key[0] != '\0', issued_key);

    if (guard && issued_key[0]) {
        size_t cap = strlen(guard) + strlen(issued_key) + 320;
        char *request = (char *)malloc(cap);
        char *advanced = NULL;

        snprintf(request, cap,
                 "{\"op\":\"comment.guardCommit\",\"clock\":%s,\"guard\":%s,\"key\":\"%s\"}",
                 CLOCK, guard, issued_key);
        committed = bridge_call(request, NULL, &length);
        check("committing the guard succeeds",
              committed && strstr(committed, "\"ok\":true") != NULL,
              committed ? committed : last_error_text());

        /* The duplicate check must be made with the guard the commit returned. */
        advanced = committed ? extract_object(committed, "\"guard\":") : NULL;
        check("the committed guard can be read back", advanced != NULL,
              committed ? committed : "no commit response");

        if (advanced) {
            size_t cap2 = strlen(advanced) + 320;
            char *second = (char *)malloc(cap2);

            snprintf(second, cap2,
                     "{\"op\":\"comment.guard\",\"clock\":%s,\"guard\":%s,\"awemeId\":\"1\",\"text\":\"hello\"}",
                     CLOCK, advanced);
            duplicate = bridge_call(second, NULL, &length);
            check("an identical publish inside the window is refused",
                  duplicate && strstr(duplicate, "\"kind\":\"invalid_input\"") != NULL,
                  duplicate ? duplicate : last_error_text());
            check("the refusal says it was a duplicate",
                  duplicate && strstr(duplicate, "duplicate-suppressed") != NULL,
                  duplicate ? duplicate : "");

            /* The window is time-based, so the same publish must be allowed again later. */
            snprintf(second, cap2,
                     "{\"op\":\"comment.guard\",\"clock\":{\"seed\":1,\"fixedNowMs\":1700000120000},"
                     "\"guard\":%s,\"awemeId\":\"1\",\"text\":\"hello\"}", advanced);
            duplicate = bridge_call(second, NULL, &length);
            check("the guard expires after its window",
                  duplicate && strstr(duplicate, "\"ok\":true") != NULL,
                  duplicate ? duplicate : last_error_text());

            free(second);
            free(advanced);
        }

        free(request);
    }

    free(guard);
    free(committed);
    free(first);
}

static void test_large_payload(void)
{
    /*
     * The reason the ABI was extended: the original AlterGate entry point could only
     * read back a 64 KiB static stdout buffer, which is not a channel for a feed page.
     * Build a response far larger than that and prove it survives.
     *
     * The per-item budget is generous on purpose. An earlier 160-byte estimate
     * truncated the last item, which produced a *malformed request* and a confusing
     * "Unterminated string" error from the interpreter -- a reminder that a harness
     * bug can look exactly like a product bug.
     */
    const int items = 2000;
    const size_t per_item = 320;
    size_t cap = (size_t)items * per_item + 4096;
    char *body = (char *)malloc(cap);
    char *request;
    char *response;
    size_t used;
    int index;
    int length = 0;

    printf("-- large payload (response buffer growth)\n");

    if (!body) {
        check("a large payload can be assembled", 0, "out of memory");
        return;
    }

    used = (size_t)snprintf(body, cap, "{\"status_code\":0,\"has_more\":1,\"max_cursor\":123,");
    used += (size_t)snprintf(body + used, cap - used, "\"aweme_list\":[");
    for (index = 0; index < items; index++) {
        int written = snprintf(
            body + used, cap - used,
            "%s{\"aweme_id\":\"%lld\",\"desc\":\"item %d\",\"statistics\":{\"digg_count\":%d},"
            "\"video\":{\"play_addr\":{\"url_list\":[\"https://cdn/%d.mp4\"],\"data_size\":%d}}}",
            index ? "," : "",
            (long long)7300000000000000000LL + index,
            index, index * 3, index, index * 1024);
        if (written < 0 || (size_t)written >= cap - used) {
            check("the large payload fits its buffer", 0, "the per-item budget is too small");
            free(body);
            return;
        }
        used += (size_t)written;
    }
    used += (size_t)snprintf(body + used, cap - used, "]}");

    printf("     raw payload: %u bytes\n", (unsigned)used);

    request = (char *)malloc(used + 512);
    snprintf(request, used + 512,
             "{\"op\":\"response.awemePage\",\"clock\":%s,\"operation\":\"feed_recommend\","
             "\"payload\":%s}", CLOCK, body);
    free(body);

    response = bridge_call(request, NULL, &length);
    free(request);

    if (!response) {
        check("a payload above 64 KiB round-trips", 0, last_error_text());
        return;
    }

    check("a payload above 64 KiB round-trips", strstr(response, "\"ok\":true") != NULL,
          response);
    printf("     normalized response: %d bytes\n", length);
    check("the response exceeds the old 64 KiB ABI limit", length > 65536, "smaller than 64 KiB");
    check("all 2000 items were normalized",
          strstr(response, "\"id\":\"7300000000000001999\"") != NULL, "");

    free(response);
}

/*
 * Chinese text must survive the whole round trip.
 *
 * This client is for a Chinese-language service, so a comment or a description is
 * almost always non-ASCII. The path crosses: C string -> PyUnicode_DecodeUTF8 ->
 * Python str -> json.dumps(ensure_ascii=False) -> PyUnicode_AsUTF8AndSize -> C string.
 * A mistake at any hop (an ANSI conversion, an ensure_ascii=True, a wrong length)
 * shows up here and nowhere else.
 */
static void test_unicode_roundtrip(void)
{
    int length = 0;
    char *response;
    static const char *text =
        "\xe6\xb5\x8b\xe8\xaf\x95\xe8\xaf\x84\xe8\xae\xba"   /* 测试评论 */
        " \xf0\x9f\x98\x80"                                    /* emoji */
        " \xe3\x80\x8c\xe5\xbc\x95\xe5\x8f\xb7\xe3\x80\x8d";  /* 「引号」 */

    printf("-- unicode round trip (Chinese + emoji)\n");

    response = bridge_call(
        "{\"op\":\"comment.buildPublish\",\"clock\":" CLOCK
        ",\"awemeId\":\"1\",\"text\":\"\xe6\xb5\x8b\xe8\xaf\x95\xe8\xaf\x84\xe8\xae\xba"
        " \xf0\x9f\x98\x80\"}",
        NULL, &length);

    check("a Chinese comment builds", response && strstr(response, "\"ok\":true") != NULL,
          response ? response : last_error_text());
    check("the Chinese text survives verbatim",
          response && strstr(response, "\xe6\xb5\x8b\xe8\xaf\x95\xe8\xaf\x84\xe8\xae\xba") != NULL,
          response ? response : "");
    check("the emoji survives verbatim",
          response && strstr(response, "\xf0\x9f\x98\x80") != NULL, response ? response : "");
    free(response);

    /*
     * Then through a normalization path, where the text is echoed back inside a model.
     *
     * The payload is passed as a JSON *object*, not as a JSON string containing JSON.
     * An earlier version embedded the payload as an escaped string and got the double
     * escaping wrong, which produced an "Expecting property name" error that looked
     * like a protocol failure. Passing an object is both simpler and closer to what the
     * C# host does (it forwards the classified payload object).
     */
    response = bridge_call(
        "{\"op\":\"response.commentPage\",\"clock\":" CLOCK ",\"operation\":\"comment_list\","
        "\"payload\":{\"status_code\":0,\"comments\":[{\"cid\":\"c1\","
        "\"text\":\"\xe6\xb5\x8b\xe8\xaf\x95\xe8\xaf\x84\xe8\xae\xba\","
        "\"user\":{\"uid\":\"u1\",\"nickname\":\"\xe4\xbd\x9c\xe8\x80\x85\"}}],"
        "\"cursor\":\"1\",\"has_more\":0}}",
        NULL, &length);
    check("a Chinese comment page normalizes",
          response && strstr(response, "\"ok\":true") != NULL,
          response ? response : last_error_text());
    check("the normalized page keeps the Chinese text",
          response && strstr(response, "\xe6\xb5\x8b\xe8\xaf\x95\xe8\xaf\x84\xe8\xae\xba") != NULL,
          response ? response : last_error_text());
    check("the normalized page keeps the Chinese nickname",
          response && strstr(response, "\xe4\xbd\x9c\xe8\x80\x85") != NULL,
          response ? response : "");
    free(response);

    /* The same, for an aweme description -- the most common Chinese field of all. */
    response = bridge_call(
        "{\"op\":\"response.awemeDetail\",\"clock\":" CLOCK ",\"payload\":"
        "{\"status_code\":0,\"aweme_detail\":{\"aweme_id\":\"7300000000000000000\","
        "\"desc\":\"\xe6\xb5\x8b\xe8\xaf\x95\xe8\xa7\x86\xe9\xa2\x91 \xf0\x9f\x8e\xac\","
        "\"author\":{\"uid\":\"1\",\"nickname\":\"\xe4\xbd\x9c\xe8\x80\x85\xe5\x90\x8d\"},"
        "\"video\":{\"play_addr\":{\"url_list\":[\"https://cdn/a.mp4\"]}}}}}",
        NULL, &length);
    check("a Chinese aweme detail normalizes",
          response && strstr(response, "\"ok\":true") != NULL,
          response ? response : last_error_text());
    check("the aweme description survives with its emoji",
          response && strstr(response, "\xe6\xb5\x8b\xe8\xaf\x95\xe8\xa7\x86\xe9\xa2\x91") != NULL
              && strstr(response, "\xf0\x9f\x8e\xac") != NULL,
          response ? response : last_error_text());
    free(response);

    (void)text;
}

static void test_bridge_robustness(void)
{
    int length = 0;
    char *response;

    printf("-- bridge robustness\n");

    response = bridge_call("{\"op\":\"nope\"}", NULL, &length);
    check("an unknown operation is reported, not crashed on",
          response && strstr(response, "\"kind\":\"invalid_input\"") != NULL,
          response ? response : last_error_text());
    free(response);

    response = bridge_call("not json at all", NULL, &length);
    check("malformed JSON is reported",
          response && strstr(response, "\"kind\":\"invalid_input\"") != NULL,
          response ? response : last_error_text());
    free(response);

    response = bridge_call("[]", NULL, &length);
    check("a non-object request is reported",
          response && strstr(response, "\"ok\":false") != NULL,
          response ? response : last_error_text());
    free(response);
}

static void test_sandbox_probe(void)
{
    /*
     * Record what the embedded interpreter can and cannot do, so a device run has a
     * baseline to compare against. The two absences that shaped this project's
     * architecture are `_ssl` (so TLS must live in the native transport) and
     * `_sqlite3` (so persistence is a file store). Verifying them here means the
     * device run only has to confirm, not discover.
     */
    static const char probe[] =
        "import sys\n"
        "out = []\n"
        "for name in ('_ssl', 'ssl', '_sqlite3', 'sqlite3', '_socket', 'socket',\n"
        "             'zlib', '_hashlib', 'hashlib', 'json', 'urllib.parse',\n"
        "             'select', 'ctypes', 'lzma', 'bz2'):\n"
        "    try:\n"
        "        __import__(name)\n"
        "        out.append(name + '+yes')\n"
        "    except Exception:\n"
        "        out.append(name + '-no')\n"
        "print(' '.join(out))\n"
        "print('executable=' + sys.executable)\n"
        "print('maxsize=' + str(sys.maxsize))\n"
        "print('encodings=' + str(len(__import__('encodings').__path__)))\n";

    printf("-- sandbox capability probe\n");

    if (!pRunUtf8 || !pLastOutput) {
        printf("  [SKIP] RunUtf8/LastOutput are not exported by this build\n");
        return;
    }

    if (pRunUtf8(probe) != 0) {
        check("the capability probe runs", 0, last_error_text());
        return;
    }

    check("the capability probe runs", 1, "");

    {
        const char *text = pLastOutput();
        const char *line = text;
        if (!text || !*text) {
            check("the capability probe produced output", 0, "(empty)");
            return;
        }
        while (*line) {
            const char *end = strchr(line, '\n');
            size_t length = end ? (size_t)(end - line) : strlen(line);
            if (length > 0) printf("     %.*s\n", (int)length, line);
            if (!end) break;
            line = end + 1;
        }
    }
}

/*
 * wmain, not main.
 *
 * `main(int, char **)` receives the command line in the *ANSI code page*, which on a
 * Chinese Windows is GBK. Treating those bytes as UTF-8 (the obvious-looking choice)
 * silently mangles any non-ASCII path component -- and this repository lives under a
 * directory named 项目, so the runtime root became garbage and `import douyin` failed
 * with "No module named 'douyin'" while the interpreter itself worked perfectly.
 * The wide entry point gets the path correctly and needs no conversion at all.
 */
int wmain(int argc, wchar_t **argv)
{
    const wchar_t *root;
    wchar_t site_buffer[MAX_PATH * 4];
    HMODULE module;
    int status;
    int result = 0;

    SetConsoleOutputCP(CP_UTF8);

    printf("=======================================================================\n");
    printf("Desultory Signals - embedded CPython 3.15 host smoke test (x64)\n");
    printf("=======================================================================\n");

    if (argc < 2) {
        printf("usage: desultory-smoke <runtime-root>\n");
        return 2;
    }
    root = argv[1];

    if (_waccess(root, 0) != 0) {
        printf("\n  [FAIL] the runtime root does not exist: %ls\n", root);
        return 2;
    }

    swprintf_s(site_buffer, MAX_PATH * 4, L"%s\\site-packages", root);
    CreateDirectoryW(site_buffer, NULL);

    printf("\n-- runtime root\n     ");
    print_utf8(root);
    printf("\n");

    printf("\n-- loading desultory-desktop.dll\n");
    module = LoadLibraryW(L"desultory-desktop.dll");
    if (!module) {
        printf("  [FAIL] LoadLibrary failed, GetLastError=%lu\n", GetLastError());
        return 3;
    }

    pInitialize = (InitializeFn)GetProcAddress(module, "PyUwpRuntime_Initialize");
    pCallJsonEx = (CallJsonExFn)GetProcAddress(module, "PyUwpRuntime_CallJsonEx");
    pFinalize = (FinalizeFn)GetProcAddress(module, "PyUwpRuntime_Finalize");
    pLastError = (LastErrorFn)GetProcAddress(module, "PyUwpRuntime_LastError");
    pBridgeReady = (BridgeReadyFn)GetProcAddress(module, "PyUwpRuntime_BridgeReady");
    pEvalInt = (EvalIntFn)GetProcAddress(module, "PyUwpRuntime_EvalInt");
    pRunUtf8 = (RunUtf8Fn)GetProcAddress(module, "PyUwpRuntime_RunUtf8");
    pLastOutput = (LastOutputFn)GetProcAddress(module, "PyUwpRuntime_LastOutput");

    check("every exported symbol resolves",
          pInitialize && pCallJsonEx && pFinalize && pLastError && pBridgeReady && pEvalInt
              && pRunUtf8 && pLastOutput,
          "one or more GetProcAddress calls returned NULL");

    printf("-- PyUwpRuntime_Initialize\n");
    status = pInitialize(root, site_buffer);
    if (status != 0) {
        printf("  [FAIL] initialize returned %d: %s\n", status, last_error_text());
        FreeLibrary(module);
        return 4;
    }
    check("the runtime initializes", 1, "");
    check("the protocol layer imports inside the embedded interpreter",
          pBridgeReady() == 1, last_error_text());

    printf("\n-- PyUwpRuntime_EvalInt (interpreter sanity)\n");
    {
        long value = 0;
        int eval_status = pEvalInt("6 * 7", &value);
        check("the interpreter evaluates arithmetic", eval_status == 0 && value == 42,
              eval_status == 0 ? "wrong value" : last_error_text());
    }

    /*
     * Dump sys.path. When the protocol layer fails to import, this is the difference
     * between "the module is missing" and "the search path is wrong", and those two
     * have entirely different fixes. Kept permanently for that reason.
     */
    printf("\n-- embedded sys.path\n");
    if (pRunUtf8 && pLastOutput) {
        if (pRunUtf8("import sys\nprint('\\n'.join(sys.path))") == 0) {
            const char *text = pLastOutput();
            const char *line = text ? text : "";
            while (*line) {
                const char *end = strchr(line, '\n');
                size_t length = end ? (size_t)(end - line) : strlen(line);
                if (length > 0) printf("     %.*s\n", (int)length, line);
                if (!end) break;
                line = end + 1;
            }
        }
    }

    printf("\n");
    test_self_test();
    printf("\n");
    test_catalogue();
    printf("\n");
    test_identity_and_requests();
    printf("\n");
    test_share_and_classify();
    printf("\n");
    test_publish_guard();
    printf("\n");
    test_large_payload();
    printf("\n");
    test_unicode_roundtrip();
    printf("\n");
    test_bridge_robustness();
    printf("\n");
    test_sandbox_probe();

    printf("\n-- PyUwpRuntime_Finalize\n");
    status = pFinalize();
    check("the runtime finalizes cleanly", status == 0, last_error_text());

    FreeLibrary(module);

    printf("\n");
    printf("=======================================================================\n");
    printf("checks: %d passed, %d failed, %d total\n",
           g_checks - g_failures, g_failures, g_checks);
    printf("=======================================================================\n");

    result = g_failures == 0 ? 0 : 1;
    return result;
}
