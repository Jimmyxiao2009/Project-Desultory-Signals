# -*- coding: utf-8 -*-
"""The bridge: one JSON request in, one JSON response out.

This module is the entire contract between the embedded CPython runtime and the
C# host. The host calls :func:`handle_json` (through ``PyUwpRuntime_CallJsonEx``)
and never imports any other module in this package, so the boundary is a single
function and a single schema.

Why a stateless request/response protocol rather than a live object graph:

* The embedded runtime is not thread safe and the host owns its thread; a
  synchronous call per operation is the only shape that cannot race.
* Storage belongs to the host (it has the file APIs, the app container
  permissions and the lifetime hooks). So every operation that changes the
  virtual browser identity -- a rotated ``msToken``, an ingested ``Set-Cookie``,
  a new session -- hands the *updated identity* back in the response for the host
  to persist. Nothing is cached on this side between calls.
* It makes the whole protocol layer testable on a desktop interpreter with no
  device, no transport and no storage: build a request, feed a recorded response,
  assert the normalized output.

Response envelope, always::

    {"ok": true,  "result": {...}}
    {"ok": false, "error": {"kind": "...", "message": "...",
                            "httpStatus": null|int, "retryable": bool,
                            "detail": {...}}}

:func:`handle` never raises. A bug here surfaces as ``kind: "internal"`` with a
message, not as an interop exception in the middle of a feed refresh.
"""

import json
import sys

from douyin.clock import Clock
from douyin.errors import DouyinError, KIND_INTERNAL, KIND_INVALID_INPUT
from douyin import (
    comment as comment_module,
    endpoints,
    identity as identity_module,
    login as login_module,
    request_builder,
    response_adapter,
    share,
    signer,
)

PROTOCOL_VERSION = "1.0.0"


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------

def _clock_from(payload):
    """Build the injectable clock.

    Production sends no ``clock`` field and gets the real wall clock and a real
    PRNG. The test runner sends ``{"seed": ..., "fixedNowMs": ...}`` and gets
    reproducible signing.
    """
    spec = payload.get("clock")
    if not spec:
        return Clock()
    return Clock(seed=spec.get("seed"), fixed_now_ms=spec.get("fixedNowMs"))


def _identity_from(payload, clock, required=True):
    raw = payload.get("identity")
    if not raw:
        if not required:
            return None
        # Creating on demand would hide a host bug where it forgot to load the
        # stored identity, and the symptom would be a session that vanishes.
        raise DouyinError(
            KIND_INVALID_INPUT,
            "this operation requires the 'identity' produced by identity.create/load",
        )
    return identity_module.VirtualWebIdentity.from_json(raw, clock)


def _identity_result(identity):
    """Everything the host needs to persist and to display the identity."""
    problems, warnings = identity.validate()
    return {
        "identity": identity.to_json(),
        "redacted": identity.redacted(),
        "problems": problems,
        "warnings": warnings,
    }


def _variant_name(operation, index):
    variants = endpoints.variants(operation)
    if index is None or index >= len(variants):
        return None
    return variants[index].name


# ---------------------------------------------------------------------------
# operation handlers
# ---------------------------------------------------------------------------

def _op_identity_create(payload, clock):
    profile = payload.get("profile")
    identity = identity_module.VirtualWebIdentity.create(clock, profile)
    return _identity_result(identity)


def _op_identity_load(payload, clock):
    identity = _identity_from(payload, clock)
    return _identity_result(identity)


def _op_identity_reset(payload, clock):
    identity = _identity_from(payload, clock)
    identity.reset(clock)
    return _identity_result(identity)


def _op_identity_ensure_fresh(payload, clock):
    identity = _identity_from(payload, clock)
    rotated = identity.ensure_fresh(clock)
    result = _identity_result(identity)
    result["rotated"] = rotated
    return result


def _op_identity_redacted(payload, clock):
    identity = _identity_from(payload, clock)
    redacted = identity.redacted(clock.now_s())
    redacted["cookies"] = identity.cookies.redacted(clock.now_s())
    return {"redacted": redacted}


def _op_identity_apply_cookies(payload, clock):
    identity = _identity_from(payload, clock)
    changed = identity.ingest_set_cookies(
        payload.get("setCookie") or (),
        payload.get("requestUrl") or "https://www.douyin.com/",
        clock.now_s(),
    )
    result = _identity_result(identity)
    result["changed"] = sorted(set(changed))
    return result


def _op_request_build(payload, clock):
    identity = _identity_from(payload, clock)
    operation = payload.get("operation")
    if not operation:
        raise DouyinError(KIND_INVALID_INPUT, "operation is required")

    # msToken expires on its own schedule; refreshing it here keeps every caller
    # from having to remember, and keeps the refresh out of the signature path.
    identity.ensure_fresh(clock)

    plan = request_builder.build(
        operation=operation,
        params=payload.get("params") or {},
        identity=identity,
        clock=clock,
        body=payload.get("body"),
        variant_index=int(payload.get("variantIndex") or 0),
        method=payload.get("method"),
        allow_unsigned=bool(payload.get("allowUnsigned")),
        provider_name=payload.get("providerName"),
        extra_headers=payload.get("extraHeaders"),
        url_override=payload.get("urlOverride"),
    )
    return {
        "plan": plan.to_json(),
        "variantCount": len(endpoints.variants(operation)),
        "headersRedacted": request_builder.redact_headers(plan.headers),
        "identity": identity.to_json(),
    }


def _op_request_build_plain(payload, clock):
    identity = _identity_from(payload, clock)
    url = payload.get("url")
    if not url:
        raise DouyinError(KIND_INVALID_INPUT, "url is required")
    plan = request_builder.build_plain(
        url=url,
        identity=identity,
        clock=clock,
        method=(payload.get("method") or "GET").upper(),
        extra_headers=payload.get("extraHeaders"),
        body=payload.get("body"),
    )
    return {
        "plan": plan.to_json(),
        "headersRedacted": request_builder.redact_headers(plan.headers),
        "identity": identity.to_json(),
    }


def _op_response_classify(payload, clock):
    """Classify a transport result, and opportunistically absorb its cookies.

    Absorbing cookies here rather than in a separate step is deliberate: a
    response that rotates ``sessionid`` and a response that does not are treated
    identically by the caller, so nothing can forget.
    """
    identity = _identity_from(payload, clock, required=False)
    operation = payload.get("operation") or "unknown"
    variant = payload.get("variant") or _variant_name(operation, payload.get("variantIndex") or 0)
    http_status = int(payload.get("httpStatus") or 0)
    body_text = payload.get("body")

    result = {}
    if identity is not None:
        set_cookie = payload.get("setCookie") or ()
        if set_cookie:
            changed = identity.ingest_set_cookies(
                set_cookie, payload.get("requestUrl") or "https://www.douyin.com/", clock.now_s()
            )
            result["cookiesChanged"] = sorted(set(changed))
        result["identity"] = identity.to_json()

    parsed = response_adapter.classify(
        http_status,
        body_text,
        operation,
        variant,
        check_status=not bool(payload.get("skipStatusCheck")),
    )
    result["payload"] = parsed
    return result


def _op_aweme_detail(payload, clock):
    detail = payload.get("payload")
    if not isinstance(detail, dict):
        raise DouyinError(KIND_INVALID_INPUT, "payload must be the classified response object")

    raw = detail.get("aweme_detail")
    if raw is None:
        # Not found is a legitimate answer to "open this link", not a fault.
        filter_detail = detail.get("filter_detail")
        if filter_detail:
            raise DouyinError(
                "risk_control",
                "the server filtered this video out of the response",
                retryable=False,
                detail={"filterDetail": filter_detail},
            )
        raise DouyinError(
            "not_found",
            "the response carried no aweme_detail",
            retryable=False,
            detail={"keys": sorted(detail.keys())},
        )
    return {"aweme": response_adapter.normalize_aweme(raw)}


def _op_aweme_page(payload, clock):
    page = response_adapter.normalize_aweme_page(
        payload.get("payload"), payload.get("operation") or "feed_recommend"
    )
    return {"page": page}


def _op_comment_page(payload, clock):
    page = response_adapter.normalize_comment_page(
        payload.get("payload"), payload.get("operation") or "comment_list"
    )
    return {"page": page}


def _op_user(payload, clock):
    raw = payload.get("payload")
    if isinstance(raw, dict) and isinstance(raw.get("user"), dict):
        raw = raw["user"]
    return {"user": response_adapter.normalize_user(raw)}


def _op_share_parse(payload, clock):
    return {"share": share.parse(payload.get("input"))}


def _op_share_parse_redirect(payload, clock):
    return {"share": share.parse_redirect(payload.get("url") or "")}


def _op_share_require(payload, clock):
    return {"awemeId": share.require_aweme_id(payload.get("input"))}


def _op_login_qr_create(payload, clock):
    """Create a Passport QR session and a transport request plan."""
    if not login_module.QR_FLOW_AVAILABLE:
        raise DouyinError(
            "signer_unavailable",
            login_module.QR_UNAVAILABLE_MESSAGE,
            retryable=False,
            detail={"alternative": "login.importCookie"},
        )

    identity = _identity_from(payload, clock)
    session = login_module.LoginSession(
        session_id=login_module.new_session_id(clock),
        created_at_ms=clock.now_ms(),
    )
    plan = request_builder.build(
        operation="qr_get",
        params={"sessionId": session.id},
        identity=identity,
        clock=clock,
        variant_index=int(payload.get("variantIndex") or 0),
    )
    return {"session": session.to_json(), "plan": plan.to_json(), "identity": identity.to_json()}


def _op_login_qr_apply_create(payload, clock):
    """Fill a session from the ``get_qrcode`` answer the transport received."""
    session = login_module.LoginSession.from_json(payload.get("session"))
    http_status = int(payload.get("httpStatus") or 0)
    body_text = payload.get("body")
    parsed = response_adapter.classify(http_status, body_text, "qr_get", "sso.qrcode")
    session = login_module.parse_qr_response(parsed, session, clock)
    return {"session": session.to_json()}


def _op_login_qr_check_plan(payload, clock):
    identity = _identity_from(payload, clock)
    session = login_module.LoginSession.from_json(payload.get("session"))
    if session.status in login_module.TERMINAL_STATUSES:
        raise DouyinError(
            KIND_INVALID_INPUT,
            "QR login session is already %s" % session.status,
            retryable=False,
        )
    plan = request_builder.build(
        operation="qr_check",
        params=login_module.check_qr_request_params(session),
        identity=identity,
        clock=clock,
        variant_index=int(payload.get("variantIndex") or 0),
    )
    return {"plan": plan.to_json(), "identity": identity.to_json()}


def _op_login_qr_check_apply(payload, clock):
    identity = _identity_from(payload, clock)
    session = login_module.LoginSession.from_json(payload.get("session"))

    set_cookie = payload.get("setCookie") or ()
    if set_cookie:
        identity.ingest_set_cookies(
            set_cookie, payload.get("requestUrl") or "https://sso.douyin.com/", clock.now_s()
        )

    session = login_module.apply_qr_check(
        session,
        int(payload.get("httpStatus") or 0),
        payload.get("body"),
        clock,
        set_cookie_names=[name for name in login_module.SESSION_MARKERS if identity.cookies.value(name, clock.now_s())],
    )

    result = {"session": session.to_json(), "identity": identity.to_json()}
    if session.status == login_module.STATUS_CONFIRMED:
        result["state"] = login_module.assess_session(identity, clock.now_s())
    return result


def _op_login_import_cookie(payload, clock):
    identity = _identity_from(payload, clock)
    report, identity = login_module.import_cookies(
        identity, payload.get("cookieText"), clock, default_domain=payload.get("defaultDomain") or ".douyin.com"
    )
    result = {"report": report, "identity": identity.to_json()}
    result["state"] = login_module.assess_session(identity, clock.now_s())
    return result


def _op_login_assess(payload, clock):
    identity = _identity_from(payload, clock)
    return {"state": login_module.assess_session(identity, clock.now_s())}


def _op_login_logout(payload, clock):
    identity = _identity_from(payload, clock)
    report = login_module.logout(identity, clock)
    result = {"report": report, "identity": identity.to_json()}
    result["state"] = login_module.assess_session(identity, clock.now_s())
    return result


def _op_login_require(payload, clock):
    identity = _identity_from(payload, clock)
    login_module.require_session(identity, clock.now_s(), payload.get("operation") or "this action")
    return {"ok": True}


def _op_comment_build_publish(payload, clock):
    params = comment_module.build_publish(
        aweme_id=payload.get("awemeId"),
        text=payload.get("text"),
        reply_comment_id=payload.get("replyCommentId"),
        reply_to_reply_id=payload.get("replyToReplyId"),
    )
    return {"params": params, "bodyOrder": list(params.keys())}


def _op_comment_parse_publish(payload, clock):
    normalized = comment_module.parse_publish_response(
        payload.get("body"), int(payload.get("httpStatus") or 200)
    )
    return {"comment": normalized}


def _op_comment_guard(payload, clock):
    guard = comment_module.PublishGuard.from_json(payload.get("guard"))
    now_ms = clock.now_ms()
    key = guard.check(
        payload.get("awemeId"),
        payload.get("text"),
        now_ms,
        payload.get("replyCommentId"),
        payload.get("replyToReplyId"),
    )
    return {"key": key, "guard": guard.to_json(), "nowMs": now_ms}


def _op_comment_guard_commit(payload, clock):
    guard = comment_module.PublishGuard.from_json(payload.get("guard"))
    guard.commit(payload.get("key"), clock.now_ms())
    return {"guard": guard.to_json()}


def _op_diagnostics_catalogue(payload, clock):
    from douyin import signer_abogus

    return {
        "protocolVersion": PROTOCOL_VERSION,
        "pythonVersion": sys.version,
        "implementation": sys.implementation.name,
        "endpoints": endpoints.describe_catalogue(),
        "signaturePolicy": endpoints.describe_policy(),
        "signers": signer.describe_all(),
        "signing": signer_abogus.status(),
        "operations": sorted(HANDLERS.keys()),
        "constants": {
            "maxCommentLength": comment_module.MAX_COMMENT_LENGTH,
            "duplicateWindowMs": comment_module.DUPLICATE_WINDOW_MS,
            "sessionMarkers": list(login_module.SESSION_MARKERS),
            "qrFlowAvailable": login_module.QR_FLOW_AVAILABLE,
        },
    }


def _op_diagnostics_selftest(payload, clock):
    """Prove the protocol layer is functional inside this interpreter.

    This is what the device writes to the result file on first launch: if it
    fails, the packaging or the search path is wrong, and that is worth knowing
    before any network call is attempted.
    """
    checks = []

    def check(name, fn):
        try:
            fn()
            checks.append({"name": name, "ok": True})
        except Exception as exc:  # noqa: BLE001 - a self test reports, never raises
            checks.append({"name": name, "ok": False, "error": "%s: %s" % (type(exc).__name__, exc)})

    probe = Clock(seed=1, fixed_now_ms=1700000000000)

    def identity_roundtrip():
        created = identity_module.VirtualWebIdentity.create(probe)
        restored = identity_module.VirtualWebIdentity.from_json(created.to_json(), probe)
        assert restored.identityId == created.identityId
        assert restored.userAgent == created.userAgent
        problems, _ = restored.validate()
        assert not problems, problems

    def cookie_scoping():
        jar = identity_module.CookieJar(None)
        jar.set("sessionid", "secret", domain="www.douyin.com", host_only=True, now_s=0)
        header_same = jar.cookie_header("https://www.douyin.com/aweme/v1/", 0)
        header_other = jar.cookie_header("https://sso.douyin.com/", 0)
        assert "sessionid=secret" in header_same
        assert "sessionid" not in header_other

    def redaction():
        jar = identity_module.CookieJar(None)
        jar.set("sessionid", "secret-value")
        view = jar.redacted()
        assert "secret-value" not in json.dumps(view)

    def share_parse():
        assert share.parse("https://v.douyin.com/ABC123/")["kind"] == "redirect"
        assert share.parse("https://www.douyin.com/video/7300000000000000000")["awemeId"] == "7300000000000000000"
        assert share.parse("7300000000000000000")["awemeId"] == "7300000000000000000"

    def comment_guard():
        guard = comment_module.PublishGuard()
        key = guard.check("1", "hello", 1000)
        guard.commit(key, 1000)
        try:
            guard.check("1", "hello", 1500)
        except DouyinError:
            return
        raise AssertionError("duplicate publish was not refused")

    def signing_pipeline():
        created = identity_module.VirtualWebIdentity.create(probe)
        plan = request_builder.build("aweme_detail", {"aweme_id": "7300000000000000000"},
                                     created, probe)
        assert plan.method == "GET"
        assert "aweme_id=7300000000000000000" in plan.url
        # The measured-correct state for this endpoint: no signature at all.
        assert plan.signature_state == "omitted", plan.signature_state
        assert "a_bogus" not in plan.url

    def signature_policy_is_applied():
        created = identity_module.VirtualWebIdentity.create(probe)
        detail = request_builder.build("aweme_detail", {"aweme_id": "1"}, created, probe)
        assert "a_bogus" not in detail.url, "detail must not be signed"
        comments = request_builder.build("comment_list", {"aweme_id": "1"}, created, probe)
        assert "a_bogus=" in comments.url, "comment/list needs the parameter present"

    def normalization():
        payload = {
            "status_code": 0,
            "aweme_detail": {
                "aweme_id": "7300000000000000000",
                "desc": "selftest",
                "author": {"uid": "1", "sec_uid": "s", "nickname": "n"},
                "video": {
                    "width": 720, "height": 1280, "duration": 15000,
                    "play_addr": {"url_list": ["http://cdn.example/a.mp4"], "data_size": 10},
                    "play_addr_h264": {"url_list": ["http://cdn.example/a_h264.mp4"]},
                },
                "statistics": {"digg_count": 1, "comment_count": 2, "share_count": 3},
            },
        }
        aweme = response_adapter.normalize_aweme(payload["aweme_detail"])
        assert aweme["id"] == "7300000000000000000"
        assert any(source["codec"] == "h264" for source in aweme["video"]["sources"])

    for name, fn in (
        ("identity_roundtrip", identity_roundtrip),
        ("cookie_scoping", cookie_scoping),
        ("redaction", redaction),
        ("share_parse", share_parse),
        ("comment_guard", comment_guard),
        ("signing_pipeline", signing_pipeline),
        ("signature_policy_is_applied", signature_policy_is_applied),
        ("normalization", normalization),
    ):
        check(name, fn)

    failed = [item for item in checks if not item["ok"]]
    if failed:
        raise DouyinError(
            KIND_INTERNAL,
            "%d of %d protocol self-test checks failed" % (len(failed), len(checks)),
            detail={"checks": checks},
        )
    return {"checks": checks, "count": len(checks)}


HANDLERS = {
    "identity.create": _op_identity_create,
    "identity.load": _op_identity_load,
    "identity.reset": _op_identity_reset,
    "identity.ensureFresh": _op_identity_ensure_fresh,
    "identity.redacted": _op_identity_redacted,
    "identity.applyCookies": _op_identity_apply_cookies,
    "request.build": _op_request_build,
    "request.buildPlain": _op_request_build_plain,
    "response.classify": _op_response_classify,
    "response.awemeDetail": _op_aweme_detail,
    "response.awemePage": _op_aweme_page,
    "response.commentPage": _op_comment_page,
    "response.user": _op_user,
    "response.searchPage": lambda payload, clock: {"page": response_adapter.normalize_search_page(payload.get("payload"))},
    "share.parse": _op_share_parse,
    "share.parseRedirect": _op_share_parse_redirect,
    "share.requireAwemeId": _op_share_require,
    "login.qrCreate": _op_login_qr_create,
    "login.qrApplyCreate": _op_login_qr_apply_create,
    "login.qrCheckPlan": _op_login_qr_check_plan,
    "login.qrCheckApply": _op_login_qr_check_apply,
    "login.qrDiagnostic": lambda payload, clock: {"diagnostic": login_module.qr_diagnostic(int(payload.get("httpStatus") or 0), payload.get("body"))},
    "login.qrRedirect": lambda payload, clock: {"url": login_module.qr_redirect(int(payload.get("httpStatus") or 0), payload.get("body"))},
    "login.importCookie": _op_login_import_cookie,
    "login.assess": _op_login_assess,
    "login.logout": _op_login_logout,
    "login.require": _op_login_require,
    "comment.buildPublish": _op_comment_build_publish,
    "comment.parsePublish": _op_comment_parse_publish,
    "comment.guard": _op_comment_guard,
    "comment.guardCommit": _op_comment_guard_commit,
    "diagnostics.catalogue": _op_diagnostics_catalogue,
    "diagnostics.selfTest": _op_diagnostics_selftest,
}


# ---------------------------------------------------------------------------
# entry points
# ---------------------------------------------------------------------------

def handle(request):
    """Dispatch one request dict. Never raises."""
    if not isinstance(request, dict):
        return {
            "ok": False,
            "error": {
                "kind": KIND_INVALID_INPUT,
                "message": "request must be a JSON object",
                "httpStatus": None,
                "retryable": False,
                "detail": {"received": type(request).__name__},
            },
        }

    op = request.get("op")
    handler = HANDLERS.get(op)
    if handler is None:
        return {
            "ok": False,
            "error": {
                "kind": KIND_INVALID_INPUT,
                "message": "unknown operation %r" % op,
                "httpStatus": None,
                "retryable": False,
                "detail": {"known": sorted(HANDLERS)},
            },
        }

    try:
        clock = _clock_from(request)
        result = handler(request, clock)
        return {"ok": True, "result": result, "op": op}
    except DouyinError as exc:
        return {"ok": False, "error": exc.to_json(), "op": op}
    except Exception as exc:  # noqa: BLE001 - the bridge must never propagate
        import traceback

        return {
            "ok": False,
            "error": {
                "kind": KIND_INTERNAL,
                "message": "%s: %s" % (type(exc).__name__, exc),
                "httpStatus": None,
                "retryable": False,
                # The traceback names functions, never request contents, and it is
                # the only way to debug this layer on a device.
                "detail": {"traceback": traceback.format_exc()[-2000:]},
            },
            "op": op,
        }


def handle_json(request_json):
    """The C# entry point. Always returns a JSON document."""
    try:
        request = json.loads(request_json) if isinstance(request_json, str) else request_json
    except ValueError as exc:
        # Compact separators, matching every other response this layer produces. A
        # caller that parses the envelope should not have to cope with two spacings,
        # and a substring test against the output (as the host smoke test does) would
        # otherwise fail on a correct response.
        return json.dumps(
            {
                "ok": False,
                "error": {
                    "kind": KIND_INVALID_INPUT,
                    "message": "request is not valid JSON: %s" % exc,
                    "httpStatus": None,
                    "retryable": False,
                    "detail": {},
                },
            },
            ensure_ascii=False,
            separators=(",", ":"),
        )
    return json.dumps(handle(request), ensure_ascii=False, separators=(",", ":"))


def handle_file(request_path, response_path):
    """File-based fallback for a host that cannot call :func:`handle_json`.

    Kept because the original AlterGate ABI could only run source and read back a
    64 KiB stdout buffer. Writing the response to a file has no size limit, so a
    host that has not yet adopted ``PyUwpRuntime_CallJsonEx`` can still drive the
    whole protocol layer. Encoded as UTF-8 in both directions.
    """
    with open(request_path, "r", encoding="utf-8") as handle_in:
        request_text = handle_in.read()
    response_text = handle_json(request_text)
    with open(response_path, "w", encoding="utf-8") as handle_out:
        handle_out.write(response_text)
    return 0


def selftest_to_string():
    """Convenience for the device smoke path: run the self test, return text."""
    try:
        result = handle({"op": "diagnostics.selfTest", "clock": {"seed": 1, "fixedNowMs": 1700000000000}})
    except Exception as exc:  # noqa: BLE001
        return "selftest crashed: %s: %s" % (type(exc).__name__, exc)
    if result.get("ok"):
        return "selftest ok (%d checks)" % result["result"]["count"]
    return "selftest failed: %s" % json.dumps(result.get("error"), ensure_ascii=False)[:800]
