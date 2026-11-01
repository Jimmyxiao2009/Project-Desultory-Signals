# -*- coding: utf-8 -*-
"""Login: the session state machine, the QR flow, and the cookie-import fallback.

The brief makes the fallback official, and this module reflects that: cookie
import is a first-class entry point with its own parser, not a debug hatch. Both
paths converge on the same thing -- a :class:`douyin.cookies.CookieJar` that
carries a real session -- and every other service only ever sees that.

State machine. The transitions are declared as data and enforced, because the
failure mode of an unenforced login machine is a UI that says "confirmed" while
holding no session:

    Created ─┬─> WaitingForScan ─┬─> Scanned ─┬─> WaitingForConfirmation ─┬─> Confirmed
             │                   │            │                          │
             └─> Expired         └─> Expired  └─> Expired                └─> Failed

QR confidence is LOW. The pure-HTTP QR flow is the part of Douyin most likely to
be risk-controlled, the field names below are candidates rather than observed
facts, and ``_research/`` records what a live probe actually returned. The cookie
import path is the one we can stand behind.
"""

from douyin.cookies import SENSITIVE_NAMES
from douyin.errors import DouyinError, KIND_INVALID_INPUT, KIND_SESSION_EXPIRED
from douyin.response_adapter import parse_json
from douyin.util import to_text

STATUS_CREATED = "Created"
STATUS_WAITING_FOR_SCAN = "WaitingForScan"
STATUS_SCANNED = "Scanned"
STATUS_WAITING_FOR_CONFIRMATION = "WaitingForConfirmation"
STATUS_CONFIRMED = "Confirmed"
STATUS_EXPIRED = "Expired"
STATUS_FAILED = "Failed"

TERMINAL_STATUSES = frozenset((STATUS_CONFIRMED, STATUS_EXPIRED, STATUS_FAILED))

#: Declared transitions. Anything not listed here is refused, which is what makes
#: "the UI says Confirmed but there is no session" impossible to reach by bug.
TRANSITIONS = {
    STATUS_CREATED: frozenset(
        (STATUS_WAITING_FOR_SCAN, STATUS_SCANNED, STATUS_EXPIRED, STATUS_FAILED)
    ),
    # Confirmed is reachable directly from WaitingForScan, and not only through
    # Scanned: the user can approve on the phone between two polls, so a single
    # poll legitimately observes "waiting" and then "confirmed". Refusing that
    # transition would strand a genuinely successful login.
    STATUS_WAITING_FOR_SCAN: frozenset(
        (STATUS_WAITING_FOR_SCAN, STATUS_SCANNED, STATUS_CONFIRMED, STATUS_EXPIRED, STATUS_FAILED)
    ),
    STATUS_SCANNED: frozenset(
        (STATUS_SCANNED, STATUS_WAITING_FOR_CONFIRMATION, STATUS_CONFIRMED,
         STATUS_EXPIRED, STATUS_FAILED)
    ),
    STATUS_WAITING_FOR_CONFIRMATION: frozenset(
        (STATUS_WAITING_FOR_CONFIRMATION, STATUS_CONFIRMED, STATUS_EXPIRED, STATUS_FAILED)
    ),
    STATUS_CONFIRMED: frozenset(),
    STATUS_EXPIRED: frozenset(),
    STATUS_FAILED: frozenset(),
}

#: Cookies that prove a real logged-in session. ``sessionid`` alone is enough;
#: the others are reported so a partial import is diagnosable.
SESSION_MARKERS = ("sessionid", "sessionid_ss", "sid_tt", "sid_guard")

#: Candidate response keys for the QR flow. Ordered by likelihood; the parser
#: takes the first that is present rather than assuming one shape.
QR_URL_KEYS = ("qrcode_index_url", "qrcode_url", "qr_url", "url")
QR_IMAGE_KEYS = ("qrcode", "qrcode_base64", "qr_image", "image")
QR_TOKEN_KEYS = ("token", "qr_token", "qrcode_token")
QR_EXPIRE_KEYS = ("expire_time", "expires_in", "expire_seconds")

CHECK_STATUS_KEYS = ("status", "qr_status", "state", "login_status")

#: QR poll status vocabulary -> our state machine.
CHECK_STATUS_MAP = {
    "new": STATUS_WAITING_FOR_SCAN,
    "1": STATUS_WAITING_FOR_SCAN,
    "waiting": STATUS_WAITING_FOR_SCAN,
    "scanning": STATUS_WAITING_FOR_SCAN,
    "scanned": STATUS_SCANNED,
    "2": STATUS_SCANNED,
    "confirm": STATUS_WAITING_FOR_CONFIRMATION,
    "confirmed": STATUS_CONFIRMED,
    "3": STATUS_CONFIRMED,
    "success": STATUS_CONFIRMED,
    "expired": STATUS_EXPIRED,
    "4": STATUS_EXPIRED,
    "canceled": STATUS_FAILED,
    "cancelled": STATUS_FAILED,
    "5": STATUS_FAILED,
    "failed": STATUS_FAILED,
    "refused": STATUS_FAILED,
}

#: QR lifetime we assume when the server does not say.
DEFAULT_QR_TTL_MS = 5 * 60 * 1000
DEFAULT_POLL_INTERVAL_MS = 2000

# Current Passport QR creation was observed with the normal web SDK query.
# Polling and confirmation still require device verification; never infer login
# success merely from a QR payload or an unscanned response.
QR_FLOW_AVAILABLE = True
QR_UNAVAILABLE_MESSAGE = "二维码登录暂不可用"


class LoginSession(object):
    """One in-flight QR login attempt. Contains no credential."""

    __slots__ = (
        "id", "status", "token", "qrUrl", "qrImageBase64", "createdAtMs",
        "expiresAtMs", "pollIntervalMs", "statusMessage", "errorKind",
        "failureReason",
    )

    def __init__(self, session_id, status=STATUS_CREATED, token=None, qr_url=None,
                 qr_image_base64=None, created_at_ms=0, expires_at_ms=0,
                 poll_interval_ms=DEFAULT_POLL_INTERVAL_MS, status_message=None,
                 error_kind=None, failure_reason=None):
        self.id = session_id
        self.status = status
        self.token = token
        self.qrUrl = qr_url
        self.qrImageBase64 = qr_image_base64
        self.createdAtMs = created_at_ms
        self.expiresAtMs = expires_at_ms
        self.pollIntervalMs = poll_interval_ms
        self.statusMessage = status_message
        self.errorKind = error_kind
        self.failureReason = failure_reason

    def to_json(self):
        """Transport-facing view.

        ``token`` is a short-lived QR token rather than a session credential, so
        it is included; nothing here is written to a log.
        """
        return {
            "id": self.id,
            "status": self.status,
            "token": self.token,
            "qrUrl": self.qrUrl,
            "qrImageBase64": self.qrImageBase64,
            "createdAtMs": self.createdAtMs,
            "expiresAtMs": self.expiresAtMs,
            "pollIntervalMs": self.pollIntervalMs,
            "statusMessage": self.statusMessage,
            "errorKind": self.errorKind,
            "failureReason": self.failureReason,
            "isTerminal": self.status in TERMINAL_STATUSES,
        }

    @classmethod
    def from_json(cls, obj):
        obj = obj or {}
        return cls(
            session_id=obj.get("id"),
            status=obj.get("status", STATUS_CREATED),
            token=obj.get("token"),
            qr_url=obj.get("qrUrl"),
            qr_image_base64=obj.get("qrImageBase64"),
            created_at_ms=obj.get("createdAtMs", 0),
            expires_at_ms=obj.get("expiresAtMs", 0),
            poll_interval_ms=obj.get("pollIntervalMs", DEFAULT_POLL_INTERVAL_MS),
            status_message=obj.get("statusMessage"),
            error_kind=obj.get("errorKind"),
            failure_reason=obj.get("failureReason"),
        )

    def transition(self, new_status, message=None):
        """Move to ``new_status`` or raise."""
        if new_status == self.status:
            if message:
                self.statusMessage = message
            return self.status
        allowed = TRANSITIONS.get(self.status, frozenset())
        if new_status not in allowed:
            raise DouyinError(
                KIND_INVALID_INPUT,
                "illegal login transition %s -> %s" % (self.status, new_status),
                detail={"from": self.status, "to": new_status, "allowed": sorted(allowed)},
            )
        self.status = new_status
        self.statusMessage = message
        if new_status in TERMINAL_STATUSES:
            self.token = None
        return self.status

    def is_expired(self, now_ms):
        return self.expiresAtMs > 0 and now_ms >= self.expiresAtMs

    def redacted(self):
        return {
            "id": self.id,
            "status": self.status,
            "hasQrImage": bool(self.qrImageBase64),
            "createdAtMs": self.createdAtMs,
            "expiresAtMs": self.expiresAtMs,
            "tokenPresent": bool(self.token),
            "statusMessage": self.statusMessage,
            "errorKind": self.errorKind,
        }


def new_session_id(clock):
    return "login-%d-%s" % (clock.now_ms(), clock.hex(8))


def parse_qr_response(payload, session, clock):
    """Fill a session from ``get_qrcode``'s answer."""
    payload = qr_payload(payload)
    for key in QR_TOKEN_KEYS:
        if payload.get(key):
            session.token = to_text(payload[key])
            break
    for key in QR_URL_KEYS:
        if payload.get(key):
            session.qrUrl = to_text(payload[key])
            break
    for key in QR_IMAGE_KEYS:
        value = payload.get(key)
        if isinstance(value, str) and value:
            # The API commonly returns a data URL; keep only the payload.
            session.qrImageBase64 = value.split(",", 1)[-1] if "," in value else value
            break

    session.expiresAtMs = clock.now_ms() + DEFAULT_QR_TTL_MS
    for key in QR_EXPIRE_KEYS:
        value = payload.get(key)
        if isinstance(value, (int, float)) and value > 0:
            seconds = int(value)
            session.expiresAtMs = seconds * 1000 if seconds > 1000000000 else clock.now_ms() + seconds * 1000
            break

    if not session.token and not session.qrUrl:
        session.transition(STATUS_FAILED, "the QR response carried neither a token nor a URL")
        session.errorKind = "risk_control"
        session.failureReason = session.statusMessage
        return session

    session.transition(STATUS_WAITING_FOR_SCAN)
    return session


def check_qr_request_params(session):
    return {
        "token": session.token or "",
        "sessionId": session.id,
        "is_new_login": "true",
    }


def apply_qr_check(session, http_status, body_text, clock, set_cookie_names=None):
    """Advance a session from one ``check_qrconnect`` answer.

    ``Confirmed`` is reached only when the transport also saw the session cookies
    arrive. A "confirmed" answer with no cookie is reported as ``Failed``, because
    a UI that shows success and then 401s on the first write is worse than an
    honest failure.
    """
    if session.is_expired(clock.now_ms()) and session.status not in TERMINAL_STATUSES:
        session.transition(STATUS_EXPIRED, "the QR code expired before it was scanned")
        return session

    payload, parse_error = parse_json(body_text)
    if parse_error is not None:
        session.transition(STATUS_FAILED, "QR check response was not JSON (%s)" % parse_error)
        session.errorKind = "schema_changed"
        session.failureReason = session.statusMessage
        return session

    diagnostic = qr_diagnostic(http_status, body_text)
    if http_status < 200 or http_status >= 300:
        session.transition(STATUS_FAILED, "二维码查询 HTTP %s" % http_status)
        session.errorKind = "http_error"
        return session
    if diagnostic["verificationRequired"]:
        session.transition(STATUS_FAILED, "抖音要求额外身份验证，本次扫码尚未登录；请在官方抖音中完成验证。")
        session.errorKind = "risk_control"
        return session
    try:
        payload = qr_payload(payload)
    except DouyinError as error:
        session.transition(STATUS_FAILED, error.message)
        session.errorKind = error.kind
        session.failureReason = error.message
        return session

    raw_status = None
    for key in CHECK_STATUS_KEYS:
        if payload.get(key) is not None:
            raw_status = to_text(payload[key]).strip().lower()
            break

    if raw_status is None:
        status_code = payload.get("status_code")
        if status_code not in (None, 0):
            session.transition(STATUS_FAILED, payload.get("status_msg") or payload.get("message"))
            session.errorKind = "risk_control"
            session.failureReason = session.statusMessage
            return session
        raw_status = "waiting"

    mapped = CHECK_STATUS_MAP.get(raw_status)
    if mapped is None:
        session.transition(STATUS_FAILED, "二维码状态无法识别，请查看诊断日志")
        session.errorKind = "schema_changed"
        return session

    if mapped == STATUS_CONFIRMED:
        issued = set(set_cookie_names or ())
        if not (issued & set(SESSION_MARKERS)):
            session.transition(
                STATUS_FAILED,
                "the server reported the login confirmed but issued no session cookie",
            )
            session.errorKind = "risk_control"
            session.failureReason = session.statusMessage
            return session

    session.transition(mapped)
    return session


# ---------------------------------------------------------------------------
# cookie import -- the dependable path
# ---------------------------------------------------------------------------

def parse_cookie_input(text):
    """Parse a pasted cookie blob.

    Accepts the three shapes a user can actually produce:

    1. a raw ``Cookie:`` header (``a=b; c=d``) -- copied from devtools;
    2. a JSON array of ``{name, value, domain}`` objects -- from a browser
       extension or our own export;
    3. a Netscape ``cookies.txt`` file with tab-separated columns.
    """
    if text is None:
        raise DouyinError(KIND_INVALID_INPUT, "cookie input is required")
    raw = to_text(text).strip()
    if not raw:
        raise DouyinError(KIND_INVALID_INPUT, "cookie input is empty")

    if raw.startswith("Cookie:") or raw.startswith("cookie:"):
        raw = raw.split(":", 1)[1].strip()

    if raw.startswith("[") or raw.startswith("{"):
        return _parse_cookie_json(raw)
    if "\t" in raw and not raw.startswith("{"):
        parsed = _parse_netscape(raw)
        if parsed:
            return parsed
    return _parse_cookie_header(raw)


def _parse_cookie_header(raw):
    pairs = []
    for chunk in raw.split(";"):
        item = chunk.strip()
        if not item or "=" not in item:
            continue
        name, _, value = item.partition("=")
        name = name.strip()
        value = value.strip()
        if not name:
            continue
        if (value.startswith('"') and value.endswith('"')) or (
            value.startswith("'") and value.endswith("'")
        ):
            value = value[1:-1]
        pairs.append({"name": name, "value": value, "domain": "", "path": "/"})
    if not pairs:
        raise DouyinError(
            KIND_INVALID_INPUT,
            "no name=value pairs found in the pasted cookie text",
            detail={"hint": "expected something like: sessionid=...; ttwid=..."},
        )
    return pairs


def _parse_cookie_json(raw):
    import json

    try:
        data = json.loads(raw)
    except ValueError as exc:
        raise DouyinError(KIND_INVALID_INPUT, "cookie JSON is not parseable: %s" % exc)

    if isinstance(data, dict) and "cookies" in data:
        data = data["cookies"]
    if isinstance(data, dict):
        data = [dict(name=name, value=value) for name, value in data.items()]
    if not isinstance(data, list):
        raise DouyinError(KIND_INVALID_INPUT, "cookie JSON must be a list or an object map")

    pairs = []
    for entry in data:
        if not isinstance(entry, dict):
            continue
        name = entry.get("name")
        value = entry.get("value")
        if not name or value is None:
            continue
        pairs.append(
            {
                "name": to_text(name),
                "value": to_text(value),
                "domain": to_text(entry.get("domain") or ""),
                "path": to_text(entry.get("path") or "/"),
                "expiresAtS": entry.get("expiresAtS") or entry.get("expires") or None,
                "secure": bool(entry.get("secure")),
                "httpOnly": bool(entry.get("httpOnly") or entry.get("httponly")),
            }
        )
    if not pairs:
        raise DouyinError(KIND_INVALID_INPUT, "cookie JSON contained no usable entries")
    return pairs


def _parse_netscape(raw):
    pairs = []
    for line in raw.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        columns = line.split("\t")
        if len(columns) < 7:
            continue
        domain, _flag, path, secure, expires, name, value = columns[:7]
        try:
            expires_at = int(expires) if expires and int(expires) > 0 else None
        except ValueError:
            expires_at = None
        pairs.append(
            {
                "name": name,
                "value": value,
                "domain": domain,
                "path": path or "/",
                "expiresAtS": expires_at,
                "secure": secure.strip().upper() == "TRUE",
            }
        )
    return pairs


#: Cookie names that are pure fingerprint material rather than session state.
#: Importing these is what makes an imported session *consistent*: the signature
#: then runs against the same fingerprint the browser had.
FINGERPRINT_COOKIES = ("ttwid", "s_v_web_id", "msToken", "odin_tt", "passport_csrf_token")


def import_cookies(identity, cookie_input, clock, default_domain=".douyin.com"):
    """Merge a pasted cookie blob into the identity's jar.

    Returns ``(report, identity)``; the host persists the returned identity.
    Fingerprint cookies are mirrored onto the identity fields so that the
    signature, the fingerprint and the cookie jar cannot disagree -- this is the
    "Cookie belongs to identity A but signed with identity B" failure the brief
    calls out, and importing is where it would otherwise be introduced.
    """
    pairs = parse_cookie_input(cookie_input)
    now_s = clock.now_s()

    imported = []
    for pair in pairs:
        domain = pair.get("domain") or default_domain
        identity.cookies.set(
            name=pair["name"],
            value=pair["value"],
            domain=domain.lstrip("."),
            path=pair.get("path") or "/",
            expires_at_s=pair.get("expiresAtS"),
            secure=bool(pair.get("secure")),
            http_only=bool(pair.get("httpOnly")),
            host_only=not pair.get("domain"),
            now_s=now_s,
        )
        imported.append(pair["name"])

    # Mirror fingerprint material onto the identity so both sides agree.
    mirrored = []
    for name in FINGERPRINT_COOKIES:
        cookie = identity.cookies.get(name, now_s)
        if cookie is None or not cookie.value:
            continue
        if name == "ttwid" and identity.ttwid != cookie.value:
            identity.ttwid = cookie.value
            identity.ttwidObtainedAtMs = now_s * 1000
            mirrored.append(name)
        elif name == "s_v_web_id" and identity.sVWebId != cookie.value:
            identity.sVWebId = cookie.value
            identity.verifyFp = cookie.value
            mirrored.append(name)

    # A jar and an identity must still be the same browser afterwards.
    if identity.cookies.identity_id is None:
        identity.cookies.identity_id = identity.identityId
    identity.touch(clock)

    markers = [name for name in SESSION_MARKERS if identity.cookies.get(name, now_s)]
    if not markers:
        # Not an exception: an imported guest cookie blob is a legitimate thing to
        # paste. The caller decides whether a session was required.
        return {
            "ok": True,
            "state": "guest",
            "importedCount": len(imported),
            "importedNames": sorted(set(imported)),
            "mirroredFingerprint": mirrored,
            "sessionMarkers": [],
            "warning": "no session cookie was present in the pasted value",
        }, identity

    return {
        "ok": True,
        "state": "logged_in",
        "importedCount": len(imported),
        "importedNames": sorted(set(imported)),
        "mirroredFingerprint": mirrored,
        "sessionMarkers": markers,
    }, identity


def assess_session(identity, now_s):
    """Guest or logged in. Reads the jar, never a cached flag."""
    present = [name for name in SESSION_MARKERS if identity.cookies.get(name, now_s)]
    return {
        "state": "logged_in" if present else "guest",
        "sessionMarkers": present,
        "cookieCount": identity.cookies.count(),
        "sensitiveCookieCount": len(
            [name for name in identity.cookies.names() if name.lower() in SENSITIVE_NAMES]
        ),
    }


def logout(identity, clock):
    """Drop every session-bearing cookie, keep the fingerprint identity."""
    now_s = clock.now_s()
    removed = []
    for name in SESSION_MARKERS + tuple(
        name for name in SENSITIVE_NAMES if name != "s_v_web_id"
    ):
        if identity.cookies.remove(name):
            removed.append(name)
    identity.touch(clock)
    return {
        "removedCount": len(set(removed)),
        "removedNames": sorted(set(removed)),
        "state": assess_session(identity, now_s)["state"],
    }


def require_session(identity, now_s, operation):
    """Raise ``session_expired`` when a write/authenticated read has no session."""
    state = assess_session(identity, now_s)
    if state["state"] != "logged_in":
        raise DouyinError(
            KIND_SESSION_EXPIRED,
            "%s requires a signed-in session" % operation,
            retryable=False,
            detail={"state": state["state"]},
        )
    return state


def qr_payload(payload):
    from douyin.errors import KIND_RISK_CONTROL, KIND_SCHEMA_CHANGED
    if not isinstance(payload, dict):
        raise DouyinError(KIND_SCHEMA_CHANGED, "二维码接口返回格式不正确", retryable=False)
    data = payload.get("data") if isinstance(payload.get("data"), dict) else payload
    code = data.get("error_code") or payload.get("status_code") or 0
    if code or payload.get("message") == "error":
        raise DouyinError(KIND_RISK_CONTROL, data.get("description") or payload.get("status_msg") or "二维码登录被服务端拒绝",
                          retryable=False, detail={"statusCode": code, "operation": "qr_login"})
    return data


def qr_redirect(http_status, body):
    from urllib.parse import urlparse
    from douyin.errors import KIND_RISK_CONTROL
    payload, error = parse_json(body)
    if error is not None:
        return None
    diagnostic = qr_diagnostic(http_status, body)
    if diagnostic["errorCode"] or diagnostic["verificationRequired"] or diagnostic["serverMessage"] == "error":
        return None
    if http_status < 200 or http_status >= 300:
        return None
    data = qr_payload(payload)
    if to_text(data.get("status") or "").lower() not in ("3", "confirmed", "success"):
        return None
    url = data.get("redirect_url")
    if not url:
        return None
    parts = urlparse(url)
    host = (parts.hostname or "").lower()
    if parts.scheme != "https" or not (host == "douyin.com" or host.endswith(".douyin.com")) or parts.username or parts.password:
        raise DouyinError(KIND_RISK_CONTROL, "登录跳转地址不属于抖音", retryable=False)
    return url


def qr_diagnostic(http_status, body):
    """Strict safe projection: no token, image, redirect, decision or account values."""
    payload, error = parse_json(body)
    payload = payload if isinstance(payload, dict) else {}
    data = payload.get("data") if isinstance(payload.get("data"), dict) else payload
    nested = data.get("data") if isinstance(data.get("data"), dict) else {}
    code = data.get("error_code") or payload.get("error_code") or payload.get("status_code") or 0
    try:
        code = int(code)
    except (ValueError, TypeError):
        code = None
    raw_status = to_text(data.get("status") or "").lower()
    status = raw_status if raw_status in CHECK_STATUS_MAP else ("unknown" if raw_status else None)
    primary = bool(data.get("verify_center_decision_conf") or nested.get("verify_center_decision_conf"))
    secondary = bool(data.get("verify_center_secondary_decision_conf") or nested.get("verify_center_secondary_decision_conf"))
    mfa = data.get("account_flow") == "verify" or bool(data.get("biz_params"))
    message = payload.get("message")
    return {"httpStatus": http_status, "errorCode": code, "serverStatus": status,
            "serverMessage": message if message in ("success", "error") else None,
            "jsonValid": error is None and bool(payload), "hasPrimaryVerification": primary,
            "hasSecondaryVerification": secondary, "hasMfa": mfa,
            "verificationRequired": primary or secondary or mfa}
