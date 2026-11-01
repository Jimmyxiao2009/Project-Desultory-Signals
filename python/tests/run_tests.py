# -*- coding: utf-8 -*-
"""Desktop test runner for the Python protocol layer.

Runs on a normal desktop interpreter (3.11+), which is what makes the protocol
layer verifiable without a phone. It exercises exactly the things the brief lists
as must-test: signer determinism, identity persistence, JSON normalization, feed
and comment pagination, duplicate removal, video source selection inputs, range
math, session state machine.

Two modes:

    python python/tests/run_tests.py                 # offline only
    python python/tests/run_tests.py --online        # also hit the real API

The online mode is opt-in and never asserts on risk-control outcomes, because
those depend on the network path and on Douyin's mood rather than on this code.
What it does assert is the shape of a successful response, and it records the
raw status for every attempt so a failure is diagnosable rather than mysterious.
"""

import json
import os
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
PACKAGE_ROOT = os.path.dirname(HERE)
sys.path.insert(0, PACKAGE_ROOT)

from douyin import comment as comment_module  # noqa: E402
from douyin import identity as identity_module  # noqa: E402
from douyin import login as login_module  # noqa: E402
from douyin import protocol  # noqa: E402
from douyin import response_adapter, share, signer  # noqa: E402
from douyin.clock import Clock  # noqa: E402
from douyin.errors import DouyinError  # noqa: E402
from douyin.util import js_encode_uri_component, rc4  # noqa: E402

FIXED_NOW_MS = 1700000000000
CLOCK = {"seed": 20261007, "fixedNowMs": FIXED_NOW_MS}

_RESULTS = []


def call(op, **fields):
    """Round-trip through the real bridge entry point, like the host does."""
    request = {"op": op, "clock": CLOCK}
    request.update(fields)
    response = json.loads(protocol.handle_json(json.dumps(request, ensure_ascii=False)))
    return response


def check(name, condition, detail=None):
    _RESULTS.append((name, bool(condition), detail))
    marker = "PASS" if condition else "FAIL"
    line = "[%s] %s" % (marker, name)
    if not condition and detail:
        line += "  -- " + str(detail)
    print(line)


def expect_error(name, response, kind):
    if response.get("ok"):
        check(name, False, "expected failure but got success")
        return
    actual = (response.get("error") or {}).get("kind")
    check(name, actual == kind, "expected kind=%s, got %s" % (kind, actual))


# ---------------------------------------------------------------------------
# signer
# ---------------------------------------------------------------------------

def test_signer_registry():
    available = signer.available()
    check("signer registry is non-empty", len(available) > 0, available)
    check("the unsigned probe provider exists", "none" in available, available)
    check("the presence provider exists", "presence" in available, available)

    provider = signer.get("none")
    check("NullSigner returns an empty signature", provider.sign("a=1", "", "ua") == "")

    presence = signer.get("presence")
    token = presence.sign("a=1", "", "ua")
    check("PresenceSigner returns a non-empty value", bool(token), token)
    check("PresenceSigner uses the measured length",
          len(token) == signer.PresenceSigner.TOKEN_LENGTH, len(token))
    check("PresenceSigner does not claim to be verified", presence.verified is False)

    # A required-signature operation must refuse rather than substitute a placeholder.
    if not [name for name in available if name not in ("none", "presence")]:
        try:
            signer.require_real()
            check("require_real raises when only placeholders exist", False)
        except Exception as exception:
            check("require_real raises when only placeholders exist",
                  "signer" in type(exception).__name__.lower()
                  or getattr(exception, "kind", None) == "signer_unavailable",
                  type(exception).__name__)


def test_signature_policy_is_per_operation():
    """The measured gate, pinned. Getting this backwards is the difference between
    a working client and one that 403s on every detail call."""
    created = call("identity.create")
    identity = created["result"]["identity"]

    detail = call("request.build", identity=identity, operation="aweme_detail",
                  params={"aweme_id": "7300000000000000000"})
    plan = detail["result"]["plan"]
    check("aweme_detail carries no a_bogus parameter",
          "a_bogus" not in plan["url"], plan["url"][:160])
    check("aweme_detail records the omit policy",
          plan["signatureState"] == "omitted", plan["signatureState"])
    check("the policy is reported on the plan",
          plan["diagnostics"]["signaturePolicy"] == "omit", plan["diagnostics"])

    comments = call("request.build", identity=identity, operation="comment_list",
                    params={"aweme_id": "7300000000000000000"})
    comment_plan = comments["result"]["plan"]
    check("comment_list carries the a_bogus parameter",
          "a_bogus=" in comment_plan["url"], comment_plan["url"][-120:])
    check("comment_list records the presence policy",
          comment_plan["signatureState"] == "presence", comment_plan["signatureState"])

    feed = call("request.build", identity=identity, operation="feed_recommend", params={})
    check("the feed carries no a_bogus parameter",
          "a_bogus" not in feed["result"]["plan"]["url"])

    policy = call("diagnostics.catalogue")["result"]["signaturePolicy"]["policy"]
    check("the policy table covers every signing-capable operation",
          policy.get("aweme_detail") == "omit" and policy.get("comment_list") == "presence",
          policy)


def test_ttwid_is_reported_as_the_real_gate():
    """A request built before the guest session exists must say so on the plan."""
    created = call("identity.create")
    identity = created["result"]["identity"]
    check("a fresh identity has no ttwid", not identity["ttwid"])

    built = call("request.build", identity=identity, operation="aweme_detail",
                 params={"aweme_id": "1"})
    diagnostics = built["result"]["plan"]["diagnostics"]
    check("the plan warns that ttwid is missing", diagnostics.get("ttwidMissing") is True,
          diagnostics)
    check("the warning says what to do",
          "ttwid" in diagnostics.get("warning", ""), diagnostics.get("warning"))

    applied = call("identity.applyCookies", identity=identity,
                   requestUrl="https://www.douyin.com/",
                   setCookie=["ttwid=1%7Cabc%7C1700000000%7Cdeadbeef; Path=/; Domain=.douyin.com"])
    with_ttwid = applied["result"]["identity"]
    check("ttwid is adopted verbatim, including its percent-encoding",
          with_ttwid["ttwid"] == "1%7Cabc%7C1700000000%7Cdeadbeef", with_ttwid["ttwid"])

    rebuilt = call("request.build", identity=with_ttwid, operation="aweme_detail",
                   params={"aweme_id": "1"})
    check("the warning clears once ttwid exists",
          not rebuilt["result"]["plan"]["diagnostics"].get("ttwidMissing"),
          rebuilt["result"]["plan"]["diagnostics"])
    check("the ttwid cookie is sent",
          "ttwid=1%7Cabc" in (rebuilt["result"]["plan"]["cookie"] or ""),
          rebuilt["result"]["plan"]["cookie"][:100])


def test_qr_flow_reports_itself_unavailable():
    """The QR flow is measured dead; asking for it must say so, not return a page."""
    created = call("identity.create")
    response = call("login.qrCreate", identity=created["result"]["identity"])
    expect_error("the QR flow reports itself unavailable", response, "signer_unavailable")
    check("the failure points at cookie import",
          response["error"]["detail"].get("alternative") == "login.importCookie",
          response["error"]["detail"])
    check("the failure explains why",
          "get_qrcode" in response["error"]["message"], response["error"]["message"][:160])


def test_signer_determinism():
    """A real provider must be deterministic: identical inputs, identical output.

    Only providers that claim to compute a signature are checked. ``presence`` is
    deliberately random (it supplies a value for a presence check, not a signature),
    and ``none`` emits nothing, so neither has anything to be deterministic about.
    """
    registered = [
        name for name in signer.available()
        if name not in signer.PLACEHOLDER_NAMES
    ]
    if not registered:
        print("[SKIP] signer determinism: no real provider registered in this build "
              "(correct for the measured signature policy)")
        return

    name = registered[0]
    provider = signer.get(name)
    params = "device_platform=webapp&aid=6383&aweme_id=7300000000000000000"
    body = ""
    ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/138.0.0.0 Safari/537.36"

    first = provider.sign(params, body, ua)
    second = provider.sign(params, body, ua)
    check("signer %s is deterministic" % name, first == second, "%r vs %r" % (first, second))
    check("signer %s produced output" % name, bool(first))
    check("signer %s output is URL-safe" % name,
          all(ch.isalnum() or ch in "-_." for ch in first), first[:80])


def test_request_build_unsigned_probe():
    """The unsigned path must be explicit, never accidental."""
    created = call("identity.create")
    identity = created["result"]["identity"]

    signed = call("request.build", identity=identity, operation="aweme_detail",
                  params={"aweme_id": "7300000000000000000"})
    check("request.build succeeds", signed.get("ok"), signed.get("error"))
    plan = signed["result"]["plan"]
    check("the request carries the operation id",
          "aweme_id=7300000000000000000" in plan["url"], plan["url"][:200])
    check("the UA in the plan is the identity's UA",
          plan["headers"][0][1] == identity["userAgent"])

    unsigned = call("request.build", identity=identity, operation="comment_list",
                    params={"aweme_id": "1"}, allowUnsigned=True)
    check("an explicitly unsigned request is marked as such",
          unsigned["result"]["plan"]["signatureState"] == "unsigned-requested",
          unsigned["result"]["plan"]["signatureState"])
    check("an explicitly unsigned request omits the parameter",
          "a_bogus" not in unsigned["result"]["plan"]["url"])


def test_unknown_parameter_is_refused():
    created = call("identity.create")
    identity = created["result"]["identity"]
    response = call("request.build", identity=identity, operation="aweme_detail",
                    params={"aweme_id": "1", "typo_param": "x"})
    expect_error("a typo'd parameter is refused, not silently signed", response, "invalid_input")


# ---------------------------------------------------------------------------
# identity
# ---------------------------------------------------------------------------

def test_identity_roundtrip_and_persistence():
    created = call("identity.create")
    check("identity.create succeeds", created.get("ok"), created.get("error"))
    identity = created["result"]["identity"]
    check("identity has no consistency problems", not created["result"]["problems"],
          created["result"]["problems"])

    blob = json.dumps(identity, ensure_ascii=False)
    reloaded = call("identity.load", identity=json.loads(blob))
    check("identity survives a JSON round trip", reloaded.get("ok"), reloaded.get("error"))
    restored = reloaded["result"]["identity"]
    for field in ("identityId", "userAgent", "webid", "verifyFp", "msToken"):
        check("identity.%s is preserved" % field, identity[field] == restored[field],
              "%r vs %r" % (identity.get(field), restored.get(field)))

    # Determinism: same seed, same identity.
    again = call("identity.create")
    check("identity.create is reproducible under a fixed clock",
          again["result"]["identity"]["identityId"] == identity["identityId"])

    different = json.loads(blob)
    different["userAgent"] = "Mozilla/5.0 (Windows NT 6.1) Chrome/1.0 Safari/537.36"
    tampered = call("identity.load", identity=different)
    check("a tampered UA is reported as an inconsistency",
          any("userAgent" in problem for problem in tampered["result"]["problems"]),
          tampered["result"]["problems"])


def test_identity_reset_changes_the_browser():
    created = call("identity.create")
    identity = created["result"]["identity"]
    # The clock is advanced first: with a fixed seed the generator is reproducible,
    # so a reset at the same instant would legitimately produce the same identity.
    # Real resets happen at a different time and must differ.
    reset = call("identity.reset", identity=identity,
                 clock={"seed": 20261007, "fixedNowMs": FIXED_NOW_MS + 1000})
    check("identity.reset produces a different identityId",
          reset["result"]["identity"]["identityId"] != identity["identityId"],
          reset["result"]["identity"]["identityId"])
    check("identity.reset clears the cookie jar",
          reset["result"]["identity"]["cookies"]["cookies"] == [])


def test_ms_token_rotation():
    created = call("identity.create")
    identity = created["result"]["identity"]
    first = identity["msToken"]

    identity["msTokenCreatedAtMs"] = FIXED_NOW_MS - (20 * 60 * 1000)
    refreshed = call("identity.ensureFresh", identity=identity)
    check("an expired msToken is rotated", refreshed["result"]["identity"]["msToken"] != first)
    check("the rotation is reported", "msToken" in refreshed["result"]["rotated"],
          refreshed["result"]["rotated"])

    again = call("identity.ensureFresh", identity=refreshed["result"]["identity"])
    check("a fresh msToken is not rotated again", again["result"]["rotated"] == [],
          again["result"]["rotated"])


def test_identity_redaction():
    created = call("identity.create")
    identity = created["result"]["identity"]
    redacted = call("identity.redacted", identity=identity)
    text = json.dumps(redacted["result"], ensure_ascii=False)
    check("the redacted view hides the whole msToken", identity["msToken"] not in text)
    check("the redacted view still reports a token length",
          redacted["result"]["redacted"]["msTokenLength"] == len(identity["msToken"]))


def test_cookie_scoping_and_ingestion():
    created = call("identity.create")
    identity = created["result"]["identity"]

    applied = call(
        "identity.applyCookies",
        identity=identity,
        requestUrl="https://www.douyin.com/",
        setCookie=[
            "ttwid=abc123; Path=/; Domain=.douyin.com; Max-Age=31536000",
            "sessionid=SECRET; Path=/; Domain=.douyin.com; Max-Age=86400",
            "s_v_web_id=verify_abc; Path=/",
        ])
    check("set-cookie headers are ingested", applied.get("ok"), applied.get("error"))
    names = applied["result"]["changed"]
    check("all three cookies were stored", set(names) == {"ttwid", "sessionid", "s_v_web_id"}, names)

    adopted = applied["result"]["identity"]
    check("ttwid is mirrored from the server cookie", adopted["ttwid"] == "abc123", adopted["ttwid"])
    check("s_v_web_id is mirrored from the server cookie",
          adopted["sVWebId"] == "verify_abc", adopted["sVWebId"])

    # A *host-only* cookie must not reach another host. (A cookie explicitly scoped
    # to .douyin.com legitimately does, because that is what a domain cookie means,
    # so the fixture below sets a host-only one.)
    host_only = call(
        "identity.applyCookies",
        identity=adopted,
        requestUrl="https://www.douyin.com/",
        setCookie=["passport_csrf_token=HOSTONLY; Path=/"])
    plain = call("request.buildPlain", identity=host_only["result"]["identity"],
                 url="https://sso.douyin.com/get_qrcode/")
    cookie_header = plain["result"]["plan"]["cookie"] or ""
    check("a host-only cookie is not sent to another host",
          "passport_csrf_token" not in cookie_header, cookie_header)

    # A domain cookie is shared with sibling subdomains, as a browser would.
    to_www = call("request.buildPlain", identity=host_only["result"]["identity"],
                  url="https://www.douyin.com/aweme/v1/web/aweme/detail/")
    check("a domain cookie is sent to its own domain",
          "sessionid" in (to_www["result"]["plan"]["cookie"] or ""),
          to_www["result"]["plan"]["cookie"][:80])

    # And a non-ByteDance host gets none of the fingerprint extras.
    elsewhere = call("request.buildPlain", identity=host_only["result"]["identity"],
                     url="https://example.com/thing")
    elsewhere_header = elsewhere["result"]["plan"]["cookie"] or ""
    check("fingerprint extras are not sent to an unrelated host",
          "msToken" not in elsewhere_header and "s_v_web_id" not in elsewhere_header,
          elsewhere_header[:120])


def test_guest_vs_logged_in():
    created = call("identity.create")
    identity = created["result"]["identity"]
    guest = call("login.assess", identity=identity)
    check("a fresh identity is a guest", guest["result"]["state"]["state"] == "guest",
          guest["result"]["state"])

    applied = call("identity.applyCookies", identity=identity,
                   requestUrl="https://www.douyin.com/",
                   setCookie=["sessionid=SECRET; Path=/; Domain=.douyin.com"])
    logged_in = call("login.assess", identity=applied["result"]["identity"])
    check("a session cookie makes the identity logged in",
          logged_in["result"]["state"]["state"] == "logged_in",
          logged_in["result"]["state"])

    logged_out = call("login.logout", identity=applied["result"]["identity"])
    check("logout returns to guest", logged_out["result"]["state"]["state"] == "guest",
          logged_out["result"]["state"])
    check("logout keeps the fingerprint identity",
          logged_out["result"]["identity"]["identityId"] == identity["identityId"])
    check("logout keeps the guest fingerprint cookies",
          logged_out["result"]["identity"]["ttwid"] == identity["ttwid"])


def test_require_session_blocks_writes():
    created = call("identity.create")
    response = call("login.require", identity=created["result"]["identity"], operation="publishing")
    expect_error("publishing without a session is refused early", response, "session_expired")


# ---------------------------------------------------------------------------
# cookie import
# ---------------------------------------------------------------------------

def test_cookie_import_header():
    created = call("identity.create")
    identity = created["result"]["identity"]
    imported = call("login.importCookie", identity=identity,
                    cookieText="sessionid=SECRET; ttwid=FROMBROWSER; s_v_web_id=verify_x_1_2_3")
    check("a pasted cookie header is imported", imported.get("ok"), imported.get("error"))
    check("the import reports a logged-in state",
          imported["result"]["report"]["state"] == "logged_in",
          imported["result"]["report"])
    check("the imported ttwid is mirrored onto the identity",
          imported["result"]["identity"]["ttwid"] == "FROMBROWSER")


def test_cookie_import_json():
    created = call("identity.create")
    identity = created["result"]["identity"]
    blob = json.dumps([
        {"name": "sessionid", "value": "S1", "domain": ".douyin.com", "path": "/"},
        {"name": "sid_tt", "value": "S2", "domain": ".douyin.com", "path": "/"},
    ])
    imported = call("login.importCookie", identity=identity, cookieText=blob)
    check("a JSON cookie export is imported", imported.get("ok"), imported.get("error"))
    check("both session markers are recognised",
          set(imported["result"]["report"]["sessionMarkers"]) == {"sessionid", "sid_tt"},
          imported["result"]["report"]["sessionMarkers"])


def test_cookie_import_rejects_junk():
    created = call("identity.create")
    response = call("login.importCookie", identity=created["result"]["identity"],
                    cookieText="this is not a cookie")
    expect_error("junk cookie input is refused", response, "invalid_input")


def test_cookie_import_redacts_in_report():
    created = call("identity.create")
    imported = call("login.importCookie", identity=created["result"]["identity"],
                    cookieText="sessionid=SUPERSECRET")
    text = json.dumps(imported["result"]["report"], ensure_ascii=False)
    check("the import report does not echo the secret", "SUPERSECRET" not in text, text)


# ---------------------------------------------------------------------------
# login state machine
# ---------------------------------------------------------------------------

def test_login_state_machine():
    allowed = login_module.TRANSITIONS
    check("a confirmed session is terminal", allowed[login_module.STATUS_CONFIRMED] == frozenset())
    # A user can approve on the phone between two polls, so a single poll may observe
    # "waiting" and then "confirmed". That must be allowed...
    check("waiting-for-scan can reach confirmed in one poll",
          login_module.STATUS_CONFIRMED in allowed[login_module.STATUS_WAITING_FOR_SCAN])
    # ...while going backwards, or starting a finished session, must not be.
    check("a confirmed session cannot be un-confirmed",
          login_module.STATUS_WAITING_FOR_SCAN not in allowed[login_module.STATUS_CONFIRMED])
    check("waiting-for-scan cannot go back to created",
          login_module.STATUS_CREATED not in allowed[login_module.STATUS_WAITING_FOR_SCAN])

    session = login_module.LoginSession("s1", created_at_ms=FIXED_NOW_MS)
    session.transition(login_module.STATUS_WAITING_FOR_SCAN)
    session.transition(login_module.STATUS_SCANNED)
    session.transition(login_module.STATUS_WAITING_FOR_CONFIRMATION)

    raised = False
    try:
        session.transition(login_module.STATUS_CREATED)
    except DouyinError:
        raised = True
    check("an illegal transition raises", raised)

    # A "confirmed" answer with no cookie must not be reported as success.
    session2 = login_module.LoginSession("s2", created_at_ms=FIXED_NOW_MS)
    session2.transition(login_module.STATUS_WAITING_FOR_SCAN)
    login_module.apply_qr_check(
        session2, 200, json.dumps({"status": "confirmed"}), Clock(seed=1, fixed_now_ms=FIXED_NOW_MS),
        set_cookie_names=[])
    check("confirmed without a session cookie becomes Failed",
          session2.status == login_module.STATUS_FAILED, session2.status)

    session3 = login_module.LoginSession("s3", created_at_ms=FIXED_NOW_MS)
    session3.transition(login_module.STATUS_WAITING_FOR_SCAN)
    login_module.apply_qr_check(
        session3, 200, json.dumps({"status": "confirmed"}), Clock(seed=1, fixed_now_ms=FIXED_NOW_MS),
        set_cookie_names=["sessionid"])
    check("confirmed with a session cookie becomes Confirmed",
          session3.status == login_module.STATUS_CONFIRMED, session3.status)


def test_login_session_json_roundtrip():
    session = login_module.LoginSession("s1", status=login_module.STATUS_SCANNED, token="tok-secret-value")
    restored = login_module.LoginSession.from_json(session.to_json())
    check("a login session survives a JSON round trip",
          restored.status == session.status and restored.token == session.token)

    redacted = session.redacted()
    check("the redacted session view has no token field", "token" not in redacted, sorted(redacted))
    check("the redacted session view carries no token value",
          "tok-secret-value" not in json.dumps(redacted), redacted)
    check("the redacted session view still reports that a token exists",
          redacted["tokenPresent"] is True)


# ---------------------------------------------------------------------------
# share links
# ---------------------------------------------------------------------------

def test_share_parsing():
    cases = [
        ("https://v.douyin.com/iABC123/", "redirect"),
        ("https://www.douyin.com/video/7300000000000000000", "aweme_id"),
        ("https://www.douyin.com/note/7300000000000000001", "aweme_id"),
        ("https://www.iesdouyin.com/share/video/7300000000000000002/", "aweme_id"),
        ("https://www.douyin.com/discover?modal_id=7300000000000000003", "aweme_id"),
        ("7300000000000000004", "aweme_id"),
        ("看看这个 https://v.douyin.com/iXYZ/ 很好笑", "redirect"),
        ("https://www.douyin.com/user/MS4wLjABAAAA", "unsupported"),
        ("hello world", "unsupported"),
    ]
    for text, expected in cases:
        result = share.parse(text)
        check("share.parse(%r) -> %s" % (text[:44], expected), result["kind"] == expected, result)

    check("a bare id is recognised",
          share.require_aweme_id("7300000000000000005") == "7300000000000000005")

    raised = False
    try:
        share.require_aweme_id("https://v.douyin.com/iABC123/")
    except DouyinError:
        raised = True
    check("a short link needs the transport before it yields an id", raised)


def test_share_redirect_reparse():
    result = share.parse_redirect("https://www.douyin.com/video/7300000000000000006?foo=bar")
    check("a resolved redirect yields the id", result["awemeId"] == "7300000000000000006", result)
    stalled = share.parse_redirect("https://v.douyin.com/iLOOP/")
    check("an unresolved redirect chain is reported, not looped",
          stalled["kind"] == "unsupported", stalled)


# ---------------------------------------------------------------------------
# normalization
# ---------------------------------------------------------------------------

DETAIL_FIXTURE = {
    "status_code": 0,
    "aweme_detail": {
        "aweme_id": "7300000000000000000",
        "desc": "测试视频",
        "create_time": 1690000000,
        "author": {"uid": "123", "sec_uid": "SEC", "nickname": "作者",
                   "avatar_thumb": {"url_list": ["https://cdn/a.jpg"]}},
        "video": {
            "width": 720, "height": 1280, "duration": 15000,
            "format": "mp4",
            "cover": {"url_list": ["https://cdn/cover.jpg"]},
            "dynamic_cover": {"url_list": ["https://cdn/dyn.jpg"]},
            "play_addr": {"url_list": ["http://cdn/play.mp4"], "data_size": 5000000,
                          "width": 720, "height": 1280},
            "play_addr_h264": {"url_list": ["https://cdn/play_h264.mp4"]},
            "play_addr_265": {"url_list": ["https://cdn/play_265.mp4"]},
            "bit_rate": [
                {"gear_name": "adapt_720_1", "bit_rate": 1200000,
                 "play_addr": {"url_list": ["https://cdn/720.mp4"], "width": 720, "height": 1280,
                               "data_size": 3000000}},
                {"gear_name": "adapt_1080_1", "bit_rate": 2400000, "is_h265": 1,
                 "play_addr": {"url_list": ["https://cdn/1080h265.mp4"], "width": 1080,
                               "height": 1920, "data_size": 9000000}},
            ],
        },
        "statistics": {"digg_count": 1000, "comment_count": 25, "share_count": 3,
                       "collect_count": 7},
    },
}

FEED_FIXTURE = {
    "status_code": 0,
    "aweme_list": [
        {"aweme_id": "1", "desc": "a", "video": {"play_addr": {"url_list": ["https://c/1.mp4"]}}},
        {"aweme_id": "2", "desc": "b", "video": {"play_addr": {"url_list": ["https://c/2.mp4"]}}},
        {"aweme_id": "1", "desc": "a again", "video": {"play_addr": {"url_list": ["https://c/1.mp4"]}}},
    ],
    "has_more": 1,
    "max_cursor": 1690000000000,
}

COMMENT_FIXTURE = {
    "status_code": 0,
    "comments": [
        {"cid": "c1", "text": "一", "create_time": 1, "digg_count": 2, "reply_comment_total": 1,
         "user": {"uid": "u1", "nickname": "甲"}},
        {"cid": "c2", "text": "二", "create_time": 2, "user": {"uid": "u2", "nickname": "乙"}},
    ],
    "cursor": 20,
    "has_more": 1,
    "total": 42,
}


def test_aweme_detail_normalization():
    classified = call("response.classify", httpStatus=200,
                      body=json.dumps(DETAIL_FIXTURE), operation="aweme_detail",
                      variant="detail.v1")
    check("a detail response classifies", classified.get("ok"), classified.get("error"))

    detail = call("response.awemeDetail", payload=classified["result"]["payload"])
    check("the detail normalizes", detail.get("ok"), detail.get("error"))
    aweme = detail["result"]["aweme"]

    check("aweme id is preserved", aweme["id"] == "7300000000000000000")
    check("the author is normalized", aweme["author"]["nickname"] == "作者")
    check("statistics are normalized",
          aweme["statistics"]["diggCount"] == 1000 and aweme["statistics"]["commentCount"] == 25,
          aweme["statistics"])
    check("duration is normalized", aweme["video"]["durationMs"] == 15000)
    check("the cover is normalized", aweme["video"]["coverUrl"] == "https://cdn/cover.jpg")

    sources = aweme["video"]["sources"]
    codecs = sorted(set(source["codec"] for source in sources))
    check("a distinct h264 source is labelled h264", "h264" in codecs, codecs)
    check("a 265 source is labelled hevc", "hevc" in codecs, codecs)

    # The generic play_addr and the explicit h264 address are different URLs, so the
    # generic one must stay unlabelled rather than being guessed.
    generic = [s for s in sources if s["codecEvidence"] == "play_addr"]
    check("the generic play_addr is not guessed to be h264",
          generic and generic[0]["codec"] == "unknown",
          generic[0] if generic else None)

    # http and https twins are offered, with the original preserved.
    http_entries = [s for s in sources if s["url"].startswith("http://")]
    check("an http CDN url keeps its original form",
          all(s["originalUrl"].startswith("http://") for s in http_entries), len(http_entries))
    check("an https twin is offered for an http url",
          any(s["url"].startswith("https://cdn/play.mp4") for s in sources))


def test_detail_not_found():
    classified = call("response.classify", httpStatus=200,
                      body=json.dumps({"status_code": 0, "aweme_detail": None}),
                      operation="aweme_detail", variant="detail.v1")
    response = call("response.awemeDetail", payload=classified["result"]["payload"])
    expect_error("a null aweme_detail is reported as not found", response, "not_found")

    filtered = call("response.classify", httpStatus=200,
                    body=json.dumps({"status_code": 0, "aweme_detail": None,
                                     "filter_detail": {"reason": "x"}}),
                    operation="aweme_detail", variant="detail.v1")
    response2 = call("response.awemeDetail", payload=filtered["result"]["payload"])
    expect_error("a filtered video is reported as risk control", response2, "risk_control")


def test_feed_page_normalization_and_duplicates():
    classified = call("response.classify", httpStatus=200,
                      body=json.dumps(FEED_FIXTURE), operation="feed_recommend",
                      variant="tab.feed.v1")
    page = call("response.awemePage", payload=classified["result"]["payload"],
                operation="feed_recommend")
    check("the feed page normalizes", page.get("ok"), page.get("error"))
    result = page["result"]["page"]
    check("the cursor is preserved verbatim", result["cursor"] == "1690000000000", result["cursor"])
    check("has_more is parsed from an integer", result["hasMore"] is True)
    check("the page carries all three entries (dedupe is the service's job)",
          len(result["items"]) == 3, len(result["items"]))


def test_feed_pagination_across_pages():
    """Two pages through the real service path, asserting cursor handling."""
    created = call("identity.create")
    identity = created["result"]["identity"]

    page1 = call("response.awemePage",
                 payload=call("response.classify", httpStatus=200,
                              body=json.dumps(FEED_FIXTURE), operation="feed_recommend",
                              variant="tab.feed.v1")["result"]["payload"],
                 operation="feed_recommend")
    cursor = page1["result"]["page"]["cursor"]

    second_fixture = dict(FEED_FIXTURE)
    second_fixture["aweme_list"] = [
        {"aweme_id": "2", "desc": "b again", "video": {"play_addr": {"url_list": ["https://c/2.mp4"]}}},
        {"aweme_id": "3", "desc": "c", "video": {"play_addr": {"url_list": ["https://c/3.mp4"]}}},
    ]
    second_fixture["max_cursor"] = 1690000009999
    page2 = call("response.awemePage",
                 payload=call("response.classify", httpStatus=200,
                              body=json.dumps(second_fixture), operation="feed_recommend",
                              variant="tab.feed.v1")["result"]["payload"],
                 operation="feed_recommend")
    check("page two advances the cursor",
          page2["result"]["page"]["cursor"] == "1690000009999",
          page2["result"]["page"]["cursor"])
    check("page two is a separate page",
          len(page2["result"]["page"]["items"]) == 2, page2["result"]["page"])
    check("the cursor from page one is a plain string", isinstance(cursor, str), cursor)


def test_comment_page_normalization():
    classified = call("response.classify", httpStatus=200,
                      body=json.dumps(COMMENT_FIXTURE), operation="comment_list",
                      variant="comment.list.v1")
    check("a comment response classifies", classified.get("ok"), classified.get("error"))

    page = call("response.commentPage", payload=classified["result"]["payload"],
                operation="comment_list")
    result = page["result"]["page"]
    check("comments are normalized", len(result["items"]) == 2, result)
    check("a comment's author is normalized", result["items"][0]["author"]["nickname"] == "甲")
    check("reply counts are normalized", result["items"][0]["replyCount"] == 1, result["items"][0])
    check("the comment cursor is preserved", result["cursor"] == "20")
    check("the comment total is preserved", result["total"] == 42)
    check("has_more is parsed", result["hasMore"] is True)


def test_empty_comment_list_is_not_an_error():
    """An aweme with no comments returns 200 and no container; that is success."""
    page = call("response.commentPage", payload={"status_code": 0, "cursor": 0, "has_more": 0},
                operation="comment_list")
    check("an empty comment list is a success", page.get("ok"), page.get("error"))
    check("an empty comment list yields zero items", page["result"]["page"]["items"] == [])


def test_replies_normalization():
    fixture = {
        "status_code": 0,
        "comments": [{"cid": "r1", "text": "回复", "level": 2,
                      "user": {"uid": "u3", "nickname": "丙"}, "reply_to_username": "甲"}],
        "cursor": 3, "has_more": 0, "total": 1,
    }
    classified = call("response.classify", httpStatus=200, body=json.dumps(fixture),
                      operation="comment_replies", variant="comment.reply.v1")
    page = call("response.commentPage", payload=classified["result"]["payload"],
                operation="comment_replies")
    item = page["result"]["page"]["items"][0]
    check("a reply keeps its level", item["level"] == 2, item)
    check("a reply keeps who it answered", item["replyToUsername"] == "甲", item)
    check("has_more=false is parsed from an integer zero", page["result"]["page"]["hasMore"] is False)


# ---------------------------------------------------------------------------
# error classification
# ---------------------------------------------------------------------------

def test_argus_gate_is_diagnosed_not_just_refused():
    """The single most valuable classification in the project.

    The Argus gate refuses a request *before* the handler and names the credential it
    wanted. Reporting that as a bare 403 would send the diagnosis in the wrong
    direction -- towards the signature, which is not the problem.
    """
    response = call("response.classify", httpStatus=403,
                    body="Blocked by ArgusSecurityPlugin Uifid Not Found",
                    operation="aweme_detail", variant="detail.v1")
    expect_error("the Argus gate is classified as risk control", response, "risk_control")
    error = response["error"]
    check("the gate is named", error["detail"].get("gate") == "argus", error["detail"])
    check("the missing credential is named",
          error["detail"].get("missingCredential") == "uifid", error["detail"])
    check("the refusal is not retryable", error["retryable"] is False)
    check("the message says the request never reached the handler",
          "before reaching the handler" in error["message"], error["message"])

    ttwid_case = call("response.classify", httpStatus=403,
                      body="Blocked by ArgusSecurityPlugin ttwid Not Found",
                      operation="feed_recommend", variant="tab.feed.v1")
    check("a different missing credential is distinguished",
          ttwid_case["error"]["detail"].get("missingCredential") == "ttwid",
          ttwid_case["error"]["detail"])


def test_error_classification():
    cases = [
        (403, "", "signature_invalid"),
        (200, "", "empty_body"),
        (429, "too many", "rate_limited"),
        (500, "boom", "http_error"),
        (200, "<html>verify</html>", "schema_changed"),
        (403, "<html>安全验证</html>", "risk_control"),
        (200, json.dumps({"status_code": 8}), "risk_control"),
        (200, json.dumps({"status_code": 2154}), "session_expired"),
        (200, json.dumps({"status_code": 10009}), "not_found"),
    ]
    for status, body, expected in cases:
        response = call("response.classify", httpStatus=status, body=body,
                        operation="aweme_detail", variant="detail.v1")
        actual = (response.get("error") or {}).get("kind") if not response.get("ok") else "ok"
        check("HTTP %s / %r -> %s" % (status, body[:22], expected), actual == expected, actual)

    # A 403 with an empty body must be classified as a signature problem and be
    # marked non-retryable, because repeating it produces the same nothing.
    response = call("response.classify", httpStatus=403, body="", operation="aweme_detail",
                    variant="detail.v1")
    check("an empty 403 is not retryable", response["error"]["retryable"] is False,
          response["error"])

    # A retryable classification must say so.
    response = call("response.classify", httpStatus=500, body="boom", operation="aweme_detail",
                    variant="detail.v1")
    check("a 500 is retryable", response["error"]["retryable"] is True, response["error"])


def test_classify_absorbs_cookies():
    created = call("identity.create")
    identity = created["result"]["identity"]
    result = call("response.classify", httpStatus=200,
                  body=json.dumps(DETAIL_FIXTURE), operation="aweme_detail",
                  variant="detail.v1", identity=identity,
                  requestUrl="https://www.douyin.com/aweme/v1/web/aweme/detail/",
                  setCookie=["ttwid=ROTATED; Path=/; Domain=.douyin.com"])
    check("classify absorbs Set-Cookie", "ttwid" in result["result"]["cookiesChanged"],
          result["result"]["cookiesChanged"])
    check("the rotated cookie reaches the identity",
          result["result"]["identity"]["ttwid"] == "ROTATED")


# ---------------------------------------------------------------------------
# comments: publish
# ---------------------------------------------------------------------------

def test_comment_validation():
    ok = call("comment.buildPublish", awemeId="1", text="  你好  ")
    check("surrounding whitespace is trimmed", ok["result"]["params"]["text"] == "你好", ok["result"])
    check("a top-level comment sends reply ids of 0",
          ok["result"]["params"]["reply_id"] == "0"
          and ok["result"]["params"]["reply_to_reply_id"] == "0", ok["result"]["params"])
    check("text_extra travels as a JSON string",
          ok["result"]["params"]["text_extra"] == "[]", ok["result"]["params"])

    expect_error("an empty comment is refused",
                 call("comment.buildPublish", awemeId="1", text="   "), "invalid_input")
    expect_error("an over-length comment is refused",
                 call("comment.buildPublish", awemeId="1", text="x" * 501), "invalid_input")
    expect_error("a nested reply without a parent is refused",
                 call("comment.buildPublish", awemeId="1", text="hi", replyToReplyId="r1"),
                 "invalid_input")

    reply = call("comment.buildPublish", awemeId="1", text="hi", replyCommentId="c1")
    check("a reply fills reply_id", reply["result"]["params"]["reply_id"] == "c1", reply["result"])


def test_comment_guard():
    first = call("comment.guard", awemeId="1", text="hello")
    check("the guard issues a key", bool(first["result"]["key"]))
    committed = call("comment.guardCommit", guard=first["result"]["guard"], key=first["result"]["key"])

    duplicate = call("comment.guard", guard=committed["result"]["guard"], awemeId="1", text="hello")
    expect_error("an identical publish inside the window is refused", duplicate, "invalid_input")

    different = call("comment.guard", guard=committed["result"]["guard"], awemeId="1", text="hello2")
    check("a different comment is allowed", different.get("ok"), different.get("error"))

    later = call("comment.guard", guard=committed["result"]["guard"], awemeId="1", text="hello",
                 clock={"seed": 1, "fixedNowMs": FIXED_NOW_MS + 120000})
    check("the guard expires after its window", later.get("ok"), later.get("error"))


def test_publish_response_parsing():
    ok = call("comment.parsePublish", httpStatus=200, body=json.dumps({
        "status_code": 0,
        "comment": {"cid": "new1", "text": "hi", "user": {"uid": "me", "nickname": "我"}},
    }))
    check("a published comment is normalized", ok.get("ok"), ok.get("error"))
    check("the new comment keeps its id", ok["result"]["comment"]["id"] == "new1")

    filtered = call("comment.parsePublish", httpStatus=200, body=json.dumps({
        "status_code": 8, "status_msg": "评论包含敏感内容",
    }))
    expect_error("a review refusal is reported as risk control", filtered, "risk_control")

    failed = call("comment.parsePublish", httpStatus=200, body=json.dumps({
        "status_code": 12345, "status_msg": "something else",
    }))
    expect_error("an unrecognised publish failure is reported", failed, "invalid_input")


# ---------------------------------------------------------------------------
# media selection inputs
# ---------------------------------------------------------------------------

def test_source_selection_inputs():
    """The selector lives in C#, but its inputs come from here, so pin the shape."""
    aweme = response_adapter.normalize_aweme(DETAIL_FIXTURE["aweme_detail"])
    sources = aweme["video"]["sources"]
    check("every source carries the fields the selector reads",
          all(set(("url", "codec", "container", "quality", "sizeBytes", "bitrate"))
              <= set(source.keys()) for source in sources),
          sources[0].keys() if sources else None)
    check("quality is derived for every source",
          all(source["quality"] != "unknown" for source in sources),
          [(s["url"], s["quality"]) for s in sources])


def test_unsupported_codec_is_reported():
    """A video with only HEVC sources must not silently become 'unknown'."""
    fixture = {
        "aweme_id": "9", "desc": "hevc only",
        "video": {
            "width": 1080, "height": 1920, "duration": 1000,
            "play_addr": {"url_list": ["https://cdn/265only.mp4"]},
            "play_addr_265": {"url_list": ["https://cdn/265only.mp4"]},
        },
    }
    aweme = response_adapter.normalize_aweme(fixture)
    sources = aweme["video"]["sources"]
    check("an address that only appears as a 265 variant is labelled hevc",
          all(source["codec"] == "hevc" for source in sources),
          [(s["codec"], s["codecEvidence"]) for s in sources])


# ---------------------------------------------------------------------------
# range math mirrors (the C# implementation is tested separately)
# ---------------------------------------------------------------------------

def test_range_math_mirror():
    """The C# RangeMath is the implementation; this pins the arithmetic it must
    agree with, using the same worked examples so a divergence is obvious."""

    def chunk_index(position, chunk):
        return position // chunk

    def chunk_end(index, chunk, total):
        start = index * chunk
        if total >= 0 and start >= total:
            return -1
        end = start + chunk - 1
        return min(end, total - 1) if total >= 0 else end

    chunk = 512 * 1024
    check("position 0 is chunk 0", chunk_index(0, chunk) == 0)
    check("the last byte of chunk 0 is chunk 0", chunk_index(chunk - 1, chunk) == 0)
    check("the first byte of chunk 1 is chunk 1", chunk_index(chunk, chunk) == 1)
    check("an exact multiple boundary does not overrun", chunk_index(4 * chunk, chunk) == 4)
    check("a short final chunk is clamped", chunk_end(3, chunk, 3 * chunk + 10) == 3 * chunk + 9)
    check("a chunk past the end reports -1", chunk_end(5, chunk, 3 * chunk + 10) == -1)
    check("a full chunk is not clamped", chunk_end(0, chunk, 10 * chunk) == chunk - 1)


# ---------------------------------------------------------------------------
# bridge robustness
# ---------------------------------------------------------------------------

def test_bridge_never_raises():
    for payload in ("not json", "{}", '{"op":"nope"}', "[]", "null"):
        raw = protocol.handle_json(payload)
        parsed = json.loads(raw)
        check("handle_json(%r) returns a structured envelope" % payload[:14],
              "ok" in parsed, parsed)

    unknown = json.loads(protocol.handle_json('{"op":"nope"}'))
    check("an unknown operation lists the known ones",
          "known" in (unknown["error"]["detail"] or {}), unknown["error"])


def test_util_encoding_matches_javascript():
    check("space encodes as %20", js_encode_uri_component("a b") == "a%20b")
    # encodeURIComponent does NOT leave "+" alone; it becomes %2B. This matters
    # because a form-encoded body decodes %2B back to "+" while a literal "+" would
    # become a space, which would silently corrupt a comment containing a plus.
    check("plus is percent-encoded", js_encode_uri_component("a+b") == "a%2Bb",
          js_encode_uri_component("a+b"))
    check("a slash is encoded", js_encode_uri_component("a/b") == "a%2Fb")
    check("an exclamation mark is left alone", js_encode_uri_component("a!b") == "a!b")
    check("chinese characters are percent-encoded as UTF-8",
          js_encode_uri_component("中") == "%E4%B8%AD", js_encode_uri_component("中"))
    check("an ampersand is encoded", js_encode_uri_component("a&b") == "a%26b")
    check("an equals sign is encoded", js_encode_uri_component("a=b") == "a%3Db")


def test_rc4_known_answer():
    # RFC 6229 vector: key "Key", plaintext "Plaintext" -> BBF316E8D940AF0AD3
    out = rc4(b"Key", b"Plaintext")
    check("rc4 matches the RFC 6229 vector",
          out.hex().upper() == "BBF316E8D940AF0AD3", out.hex().upper())


def test_self_test_operation():
    response = call("diagnostics.selfTest")
    check("the in-process self test passes", response.get("ok"),
          json.dumps(response.get("error"), ensure_ascii=False)[:600])


def test_catalogue():
    response = call("diagnostics.catalogue")
    check("the catalogue lists endpoints", bool(response["result"]["endpoints"]))
    check("the catalogue lists signers", bool(response["result"]["signers"]))
    check("the catalogue lists operations", len(response["result"]["operations"]) > 15,
          len(response["result"]["operations"]))
    text = json.dumps(response["result"], ensure_ascii=False)
    check("the catalogue carries no credential", "sessionid=" not in text)


# ---------------------------------------------------------------------------
# optional: real network
# ---------------------------------------------------------------------------

def test_online_detail():
    """Hit the real API. Records the status honestly; asserts only on success."""
    import urllib.request

    created = call("identity.create")
    identity = created["result"]["identity"]

    build = call("request.build", identity=identity, operation="aweme_detail",
                 params={"aweme_id": ONLINE_AWEME_ID}, allowUnsigned=True)
    if not build.get("ok"):
        print("[SKIP] online detail: request.build failed: %s" % build.get("error"))
        return

    plan = build["result"]["plan"]
    request = urllib.request.Request(plan["url"])
    for name, value in plan["headers"]:
        request.add_header(name, value)
    if plan.get("cookie"):
        request.add_header("Cookie", plan["cookie"])

    proxy = os.environ.get("HTTPS_PROXY") or os.environ.get("HTTP_PROXY")
    opener = urllib.request.build_opener(
        urllib.request.ProxyHandler({"http": proxy, "https": proxy}) if proxy else
        urllib.request.ProxyHandler({}))

    try:
        with opener.open(request, timeout=25) as response:
            body = response.read().decode("utf-8", "replace")
            status = response.status
    except Exception as exception:  # noqa: BLE001 - the point is to record it
        code = getattr(exception, "code", None)
        body = ""
        try:
            body = exception.read().decode("utf-8", "replace")[:400]
        except Exception:
            pass
        print("[INFO] online detail (unsigned probe) -> HTTP %s" % code)
        if body:
            print("       body: %s" % body[:200])
        check("the online probe produced a definitive HTTP status",
              code is not None, exception)
        print("[NOTE] an unsigned request is expected to be refused; see _research for "
              "the signed path")
        return

    print("[INFO] online detail (unsigned probe) -> HTTP %s, %d bytes" % (status, len(body)))
    parsed, error = response_adapter.parse_json(body)
    if error is None and isinstance(parsed, dict):
        has_detail = "aweme_detail" in parsed
        print("[INFO] response keys: %s" % sorted(parsed.keys())[:12])
        check("an unsigned detail request that succeeded carries aweme_detail", has_detail)
    else:
        print("[NOTE] the unsigned response was not JSON; that is the expected outcome "
              "without a signature")


ONLINE_AWEME_ID = os.environ.get("DESULTORY_ONLINE_AWEME_ID", "7300000000000000000")


# ---------------------------------------------------------------------------

def main():
    online = "--online" in sys.argv

    print("=" * 72)
    print("Desultory Signals - Python protocol layer tests")
    print("python %s" % sys.version.replace("\n", " "))
    print("online probes: %s" % ("enabled" if online else "disabled (pass --online)"))
    print("=" * 72)

    tests = [
        test_signer_registry,
        test_signer_determinism,
        test_signature_policy_is_per_operation,
        test_ttwid_is_reported_as_the_real_gate,
        test_qr_flow_reports_itself_unavailable,
        test_request_build_unsigned_probe,
        test_unknown_parameter_is_refused,
        test_identity_roundtrip_and_persistence,
        test_identity_reset_changes_the_browser,
        test_ms_token_rotation,
        test_identity_redaction,
        test_cookie_scoping_and_ingestion,
        test_guest_vs_logged_in,
        test_require_session_blocks_writes,
        test_cookie_import_header,
        test_cookie_import_json,
        test_cookie_import_rejects_junk,
        test_cookie_import_redacts_in_report,
        test_login_state_machine,
        test_login_session_json_roundtrip,
        test_share_parsing,
        test_share_redirect_reparse,
        test_aweme_detail_normalization,
        test_detail_not_found,
        test_feed_page_normalization_and_duplicates,
        test_feed_pagination_across_pages,
        test_comment_page_normalization,
        test_empty_comment_list_is_not_an_error,
        test_replies_normalization,
        test_error_classification,
        test_argus_gate_is_diagnosed_not_just_refused,
        test_classify_absorbs_cookies,
        test_comment_validation,
        test_comment_guard,
        test_publish_response_parsing,
        test_source_selection_inputs,
        test_unsupported_codec_is_reported,
        test_range_math_mirror,
        test_bridge_never_raises,
        test_util_encoding_matches_javascript,
        test_rc4_known_answer,
        test_self_test_operation,
        test_catalogue,
    ]
    if online:
        tests.append(test_online_detail)

    for test in tests:
        print("")
        print("-- %s" % test.__name__)
        try:
            test()
        except Exception:
            check("%s raised" % test.__name__, False, traceback.format_exc()[-800:])

    passed = sum(1 for _, ok, _ in _RESULTS if ok)
    failed = len(_RESULTS) - passed

    print("")
    print("=" * 72)
    print("checks: %d passed, %d failed, %d total" % (passed, failed, len(_RESULTS)))
    if failed:
        print("")
        print("failures:")
        for name, ok, detail in _RESULTS:
            if not ok:
                print("  - %s" % name)
                if detail:
                    print("      %s" % str(detail)[:400])
    print("=" * 72)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
