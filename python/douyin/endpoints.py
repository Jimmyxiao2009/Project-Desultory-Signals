# -*- coding: utf-8 -*-
"""Endpoint catalogue.

Every endpoint string in this project lives here. Callers never learn a path,
a host or a query key. Because these are undocumented web APIs, each logical
operation has an ordered list of *variants*: when a variant fails in a way that
means "this endpoint moved" (404, 403 with an empty body, schema mismatch) the
adapter walks to the next one instead of hard-failing the feature.

Provenance: the paths below are the web-client shapes documented by the public
Douyin downloader ecosystem as of 2026-10. ``confidence`` records how sure we
are, and ``_research/abogus/EVIDENCE.md`` records what was actually observed on
a live request. Anything marked ``low`` must be confirmed on hardware before a
gate that depends on it is called passed.
"""

WEB_ORIGIN = "https://www.douyin.com"
SSO_ORIGIN = "https://sso.douyin.com"
TTWID_ORIGIN = "https://ttwid.bytedance.com"

CONFIDENCE_HIGH = "high"
CONFIDENCE_MEDIUM = "medium"
CONFIDENCE_LOW = "low"


class EndpointVariant(object):
    __slots__ = ("name", "method", "origin", "path", "confidence", "notes", "requires_login")

    def __init__(self, name, method, origin, path, confidence=CONFIDENCE_MEDIUM,
                 notes="", requires_login=False):
        self.name = name
        self.method = method
        self.origin = origin
        self.path = path
        self.confidence = confidence
        self.notes = notes
        self.requires_login = requires_login

    def url(self):
        return self.origin + self.path

    def describe(self):
        return {
            "name": self.name,
            "method": self.method,
            "path": self.path,
            "confidence": self.confidence,
            "requiresLogin": self.requires_login,
            "notes": self.notes,
        }


#: Ordered variant lists per logical operation. Order is preference order.
ENDPOINTS = {    # -- read paths --------------------------------------------------------
    "aweme_detail": [
        EndpointVariant("detail.v1", "GET", WEB_ORIGIN, "/aweme/v1/web/aweme/detail/",
                        CONFIDENCE_HIGH, "canonical web detail endpoint"),
    ],
    "feed_recommend": [
        EndpointVariant("tab.feed.v1", "GET", WEB_ORIGIN, "/aweme/v1/web/tab/feed/",
                        CONFIDENCE_MEDIUM,
                        "recommend feed; historically also /aweme/v2/web/module/feed/"),
        EndpointVariant("module.feed.v1", "GET", WEB_ORIGIN, "/aweme/v1/web/module/feed/",
                        CONFIDENCE_LOW, "older recommend feed shape"),
    ],
    "aweme_post": [
        EndpointVariant("post.v1", "GET", WEB_ORIGIN, "/aweme/v1/web/aweme/post/",
                        CONFIDENCE_HIGH, "author post list; needs sec_user_id"),
    ],
    "comment_list": [
        EndpointVariant("comment.list.v1", "GET", WEB_ORIGIN, "/aweme/v1/web/comment/list/",
                        CONFIDENCE_HIGH, "top-level comments"),
    ],
    "comment_replies": [
        EndpointVariant("comment.reply.v2", "GET", WEB_ORIGIN,
                        "/aweme/v2/web/comment/list/reply/",
                        CONFIDENCE_HIGH, "2026-10-07: real replies accepted on v2; v1 returned empty"),
        EndpointVariant("comment.reply.v1", "GET", WEB_ORIGIN,
                        "/aweme/v1/web/comment/list/reply/",
                        CONFIDENCE_MEDIUM, "legacy fallback; needs comment_id + item_id"),
    ],
    "user_detail": [
        EndpointVariant("user.other.v1", "GET", WEB_ORIGIN, "/aweme/v1/web/user/profile/other/",
                        CONFIDENCE_MEDIUM, "needs sec_user_id"),
    ],
    "user_self": [
        EndpointVariant("user.self.v1", "GET", WEB_ORIGIN, "/aweme/v1/web/user/profile/self/",
                        CONFIDENCE_MEDIUM, "session validity probe", requires_login=True),
    ],
    "search_single": [
        EndpointVariant("search.single.v1", "GET", WEB_ORIGIN,
                        "/aweme/v1/web/general/search/single/",
                        CONFIDENCE_MEDIUM, "video search; guest response 2483 explicitly requires login"),
    ],

    "search_user": [
        EndpointVariant("search.user.v1", "GET", WEB_ORIGIN,
                        "/aweme/v1/web/discover/search/",
                        CONFIDENCE_MEDIUM, "user search cards; authenticated results pending device validation"),
    ],

    # -- write paths -------------------------------------------------------
    "comment_publish": [
        EndpointVariant("comment.publish.v1", "POST", WEB_ORIGIN,
                        "/aweme/v1/web/comment/publish/",
                        CONFIDENCE_MEDIUM,
                        "body carries aweme_id/text/reply ids; login required",
                        requires_login=True),
    ],

    # -- session -----------------------------------------------------------
    "ttwid_register": [
        EndpointVariant("ttwid.register", "POST", TTWID_ORIGIN,
                        "/ttwid/union/register/",
                        CONFIDENCE_MEDIUM, "issues the ttwid cookie for a guest"),
    ],
    "ttwid_web": [
        EndpointVariant("ttwid.web", "GET", WEB_ORIGIN, "/ttwid/union/register/",
                        CONFIDENCE_LOW, "same service behind the web origin"),
    ],
    "web_home": [
        EndpointVariant("home", "GET", WEB_ORIGIN, "/",
                        CONFIDENCE_HIGH, "first page visit; source of Set-Cookie identity"),
    ],
    "passport_account_info": [
        EndpointVariant("passport.account_info", "GET", WEB_ORIGIN,
                        "/passport/account/info/",
                        CONFIDENCE_LOW, "cheap session validity probe", requires_login=True),
    ],

    # -- login -------------------------------------------------------------
    "qr_get": [
        EndpointVariant("passport.qrcode", "GET", "https://imdesktop.douyin.com", "/passport/web/get_qrcode/",
                        CONFIDENCE_MEDIUM, "desktop Passport QR create observed on phone; confirmation pending"),
    ],
    "qr_check": [
        EndpointVariant("passport.check_qrconnect", "POST", "https://imdesktop.douyin.com", "/passport/web/check_qrconnect/",
                        CONFIDENCE_MEDIUM, "desktop Passport unscanned polling observed on phone"),
    ],

    # -- share link resolution --------------------------------------------
    "share_redirect": [
        EndpointVariant("v.douyin", "GET", "https://v.douyin.com", "/",
                        CONFIDENCE_HIGH, "short link; resolved via redirect, not scraping"),
        EndpointVariant("www.redirect", "GET", WEB_ORIGIN, "/share/video/",
                        CONFIDENCE_MEDIUM, "iesdouyin share page redirect"),
    ],
}


def variants(operation):
    """Ordered variants for a logical operation."""
    items = ENDPOINTS.get(operation)
    if not items:
        raise KeyError("unknown operation %r" % operation)
    return items


def primary(operation):
    return variants(operation)[0]


# ---------------------------------------------------------------------------
# Signature policy
# ---------------------------------------------------------------------------
#
# This section is the most important empirical result in the project, and it is
# counter-intuitive enough to state plainly.
#
# Live measurement on 2026-10-07 (see ``_research/abogus/EVIDENCE.md``) showed:
#
#   * ``aweme/detail`` with a ``ttwid`` cookie and **no** ``a_bogus`` parameter
#     returns HTTP 200 with the full 67 KiB JSON payload.
#   * The same request **with** an ``a_bogus`` parameter -- including one produced
#     by ByteDance's own SDK, byte-for-byte -- returns HTTP 403.
#   * ``comment/list`` with a ``ttwid`` and ``&a_bogus=AA`` returns HTTP 200 with
#     the comment list; with **no** ``a_bogus`` it returns 200 with an *empty* body.
#     So that endpoint performs a **presence** check on the parameter, not a value
#     check.
#   * ``tab/feed`` with a ``ttwid`` and no ``a_bogus`` returns 200 with ``aweme_list``.
#
# The gate is therefore not "a valid signature" but "a server-issued ``ttwid``",
# and adding a signature where it is not wanted is *worse than omitting it*. That
# is why the policy is per-operation data rather than a global switch, and why
# getting it backwards would produce a client that works on one endpoint and 403s
# on another for no visible reason.
#
# Three policies:
#
#   POLICY_OMIT      Do not send ``a_bogus`` at all. Sending one breaks the call.
#   POLICY_PRESENCE  Send ``a_bogus`` with a non-empty placeholder. The value is
#                    not verified, only its presence.
#   POLICY_REQUIRED  A real signature is needed. Nothing in the current client
#                    needs this; it exists so that when the gate hardens the fix is
#                    one table entry plus a registered provider, not a refactor.
POLICY_OMIT = "omit"
POLICY_PRESENCE = "presence"
POLICY_REQUIRED = "required"

SIGNATURE_POLICY = {
    # Measured: omit. A signature here is actively harmful.
    "aweme_detail": POLICY_OMIT,
    "feed_recommend": POLICY_OMIT,
    "aweme_post": POLICY_OMIT,
    "user_detail": POLICY_OMIT,
    "user_self": POLICY_OMIT,
    "passport_account_info": POLICY_OMIT,

    # Measured: presence only.
    "comment_list": POLICY_PRESENCE,
    "comment_replies": POLICY_PRESENCE,

    # Not measured. The write path is the most likely to be gated, and a presence
    # value is the conservative choice: it satisfies a presence check without
    # claiming a signature we do not have. If publishing starts failing with a
    # signature complaint, this becomes POLICY_REQUIRED and a real provider must be
    # registered -- see ``signer.py``.
    "comment_publish": POLICY_PRESENCE,

    "search_single": POLICY_PRESENCE,
    "search_user": POLICY_PRESENCE,

    # No signing concept applies to these.
    "web_home": POLICY_OMIT,
    "ttwid_register": POLICY_OMIT,
    "ttwid_web": POLICY_OMIT,
    "share_redirect": POLICY_OMIT,
    "qr_get": POLICY_OMIT,
    "qr_check": POLICY_OMIT,
}

#: Operations where the payload is known to be absent, so a caller that expects one
#: should be told rather than left to time out.
UNSUPPORTED_OPERATIONS = {
    "qr_get": (
        "the pure-HTTP QR login flow is gone: sso.douyin.com/get_qrcode/ now serves a "
        "JavaScript application rather than returning a QR payload. Use cookie import "
        "(AccountService.ImportCookieSession) instead."
    ),
}


def signature_policy(operation):
    return SIGNATURE_POLICY.get(operation, POLICY_OMIT)


def is_supported(operation):
    return operation not in UNSUPPORTED_OPERATIONS


def unsupported_reason(operation):
    return UNSUPPORTED_OPERATIONS.get(operation)


def describe_policy():
    return {
        "policy": dict(SIGNATURE_POLICY),
        "unsupported": dict(UNSUPPORTED_OPERATIONS),
    }


def describe_catalogue():
    return {
        operation: [variant.describe() for variant in items]
        for operation, items in sorted(ENDPOINTS.items())
    }


#: Parameters that are part of the web client's baseline for *every* request.
#: Order is preserved on the wire, and the signature covers that exact order.
BASE_PARAM_ORDER = [
    "device_platform",
    "aid",
    "channel",
    "pc_client_type",
    "pc_libra_divert",
    "version_code",
    "version_name",
    "cookie_enabled",
    "screen_width",
    "screen_height",
    "browser_language",
    "browser_platform",
    "browser_name",
    "browser_version",
    "browser_online",
    "engine_name",
    "engine_version",
    "os_name",
    "os_version",
    "cpu_core_num",
    "device_memory",
    "platform",
    "downlink",
    "effective_type",
    "round_trip_time",
    "webid",
    "uifid",
    "msToken",
]

#: Values the web client sends for the fields above. Kept as data so a schema
#: change is a one-line edit rather than a code change.
BASE_PARAM_VALUES = {
    "device_platform": "webapp",
    "channel": "channel_pc_web",
    "pc_client_type": "1",
    "pc_libra_divert": "Windows",
    "version_code": "190500",
    "version_name": "19.5.0",
    "cookie_enabled": "true",
    "browser_language": "zh-CN",
    "browser_platform": "Win32",
    "browser_online": "true",
    "downlink": "10",
    "effective_type": "4g",
    "round_trip_time": "50",
}
