# -*- coding: utf-8 -*-
"""Request construction.

Everything that decides what goes on the wire lives here, and nothing else does:
parameter order, percent-encoding, signature placement, headers and cookies. The
signature covers the *literal* query string we send, so the order in this module
is not cosmetic -- reordering a parameter invalidates the signature and the
server answers with an empty body rather than an error.

Two rules the brief calls out, honoured here:

* The UA used for signing is always ``identity.userAgent``. There is no path that
  signs with one identity and sends another.
* If the signature provider is unavailable, the request is either refused
  (default) or explicitly marked unsigned (``allow_unsigned``). It is never
  silently sent unsigned, because "403 because unsigned" and "403 because
  rejected" need different fixes.
"""

from urllib.parse import urlparse

from douyin import endpoints, signer
from douyin.errors import DouyinError, KIND_INVALID_INPUT
from douyin.util import js_encode_uri_component, to_text

#: Which parameters each operation contributes, in the order they appear. The
#: web client puts the operation's own parameters first and the device/platform
#: block after it; matching that order keeps our queries shaped like a real one.
OPERATION_PARAMS = {
    "aweme_detail": ["aweme_id", "verifyFp", "fp"],
    "aweme_post": ["sec_user_id", "max_cursor", "count", "locate_query", "show_live_replay_strategy"],
    "feed_recommend": [
        "count", "refresh_index", "refer_type", "refer_share_id", "refer_share_uid",
        "refer_share_channel", "refer_share_form", "volume",
    ],
    "comment_list": ["aweme_id", "cursor", "count", "item_type"],
    "comment_replies": ["item_id", "comment_id", "cursor", "count", "item_type"],
    # The publish body is built by douyin/comment.py and ordered here. Every one of
    # these keys is part of the API's form body, including the two reply ids, which
    # are the literal string "0" for a top-level comment.
    "comment_publish": ["aweme_id", "text", "text_extra", "reply_id", "reply_to_reply_id"],
    "user_detail": ["sec_user_id"],
    "user_self": [],
    "passport_account_info": [],
    "web_home": [],
    "ttwid_register": [],
    "qr_get": [],
    "qr_check": ["token", "is_new_login"],
    "search_single": ["keyword", "cursor", "offset", "count", "type", "search_channel", "search_source", "query_correct_type", "is_filter_search", "search_id"],
    "search_user": ["keyword", "cursor", "offset", "count", "search_channel", "search_source", "query_correct_type", "is_filter_search", "search_id"],
}

#: Per-operation defaults applied when the caller does not supply a value.
OPERATION_DEFAULTS = {
    "feed_recommend": {
        "count": 20,
        "refresh_index": 1,
        "refer_type": "feed",
        "refer_share_id": "",
        "refer_share_uid": "",
        "refer_share_channel": "",
        "refer_share_form": "",
        "volume": 0.0,
    },
    "comment_list": {"cursor": 0, "count": 20, "item_type": 0},
    "comment_replies": {"cursor": 0, "count": 20, "item_type": 0},
    "comment_publish": {"text_extra": "[]", "reply_id": "0", "reply_to_reply_id": "0"},
    "aweme_post": {"max_cursor": 0, "count": 20, "locate_query": "false",
                   "show_live_replay_strategy": 1},
    "search_single": {"offset": 0, "count": 10, "search_channel": "aweme_general", "search_source": "normal_search", "query_correct_type": 1, "is_filter_search": 0},
    "search_user": {"offset": 0, "count": 10, "search_channel": "aweme_user", "search_source": "normal_search", "query_correct_type": 1, "is_filter_search": 0},
}


class RequestPlan(object):
    """A built request plus the provenance needed to explain it.

    ``diagnostics`` never contains a cookie value or a session identifier -- the
    host is allowed to log it verbatim.
    """

    __slots__ = (
        "operation", "variant", "method", "url", "headers", "query",
        "body", "cookie_header", "signature_state", "signer", "diagnostics",
    )

    def __init__(self, **fields):
        for name in self.__slots__:
            setattr(self, name, fields.get(name))

    def to_json(self):
        return {
            "operation": self.operation,
            "variant": self.variant,
            "method": self.method,
            "url": self.url,
            "headers": [[name, value] for name, value in self.headers],
            "body": self.body,
            "cookie": self.cookie_header,
            "query": self.query,
            "signatureState": self.signature_state,
            "signer": self.signer,
            "diagnostics": self.diagnostics,
        }


def _scalar(value):
    """Render a parameter value the way the web client does."""
    if value is None:
        return ""
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, float):
        # 0.0 must render as "0" and 1.5 as "1.5" -- str() already does this,
        # but a very large float would switch to exponent form, which the server
        # does not produce. None of our parameters are that large.
        return str(value)
    return str(value)


def common_params(identity, now_s):
    """The device/platform block every web request carries.

    Order comes from :data:`douyin.endpoints.BASE_PARAM_ORDER`; ``msToken`` and
    ``uifid`` are included only when they exist, exactly as a real client behaves
    before the server has issued them.
    """
    values = dict(endpoints.BASE_PARAM_VALUES)
    values["browser_language"] = identity.language
    values["browser_platform"] = identity.platform
    values["browser_name"] = identity.browserName
    values["browser_version"] = identity.browserVersion
    values["engine_name"] = identity.engineName
    values["engine_version"] = identity.engineVersion
    values["os_name"] = identity.osName
    values["os_version"] = identity.osVersion
    values["cpu_core_num"] = identity.hardwareConcurrency
    values["device_memory"] = identity.deviceMemory
    values["platform"] = identity.platformId
    values["aid"] = identity.aid
    values["screen_width"] = identity.screenWidth
    values["screen_height"] = identity.screenHeight
    values["webid"] = identity.webid
    values["msToken"] = identity.msToken
    values["uifid"] = identity.uifid

    ordered = []
    for name in endpoints.BASE_PARAM_ORDER:
        if name not in values:
            continue
        value = values[name]
        if value is None or value == "":
            # An empty uifid/msToken is omitted rather than sent blank; the real
            # client omits it too and the signature must match.
            continue
        ordered.append((name, _scalar(value)))
    return ordered


def operation_params(operation, params):
    """Order the caller's parameters per :data:`OPERATION_PARAMS`.

    Unknown keys are rejected: a typo'd parameter would otherwise silently
    change the signed string with no visible cause.
    """
    allowed = OPERATION_PARAMS.get(operation)
    if allowed is None:
        raise DouyinError(KIND_INVALID_INPUT, "unknown operation %r" % operation)

    merged = dict(OPERATION_DEFAULTS.get(operation, {}))
    merged.update({k: v for k, v in (params or {}).items() if v is not None})

    ordered = []
    for name in allowed:
        if name in merged and merged[name] is not None:
            ordered.append((name, _scalar(merged[name])))

    extra = [name for name in (params or {}) if name not in allowed]
    if extra:
        raise DouyinError(
            KIND_INVALID_INPUT,
            "operation %r does not accept parameter(s): %s" % (operation, ", ".join(sorted(extra))),
            detail={"allowed": allowed},
        )
    return ordered


def encode_pairs(pairs):
    """Percent-encode the way a browser's URLSearchParams / encodeURIComponent does."""
    return "&".join(
        "%s=%s" % (js_encode_uri_component(name), js_encode_uri_component(value))
        for name, value in pairs
    )


def build_headers(identity, url, method, now_s, content_type=None, extra=None):
    """Headers for one request.

    ``Sec-Fetch-*`` and the ``Referer`` are part of looking like the real client;
    the referer chain matters because Douyin rejects requests that claim to come
    from nowhere.
    """
    parsed = urlparse(url)
    origin = "%s://%s" % (parsed.scheme, parsed.netloc)

    headers = [
        ("User-Agent", identity.userAgent),
        ("Accept", "application/json, text/plain, */*"),
        ("Accept-Language", "%s,zh;q=0.9,en;q=0.8" % identity.language),
        ("Accept-Encoding", "identity"),  # we do no decoding ourselves
        ("Referer", referer_for(identity, url)),
        ("Origin", origin),
        ("Sec-Fetch-Dest", "empty"),
        ("Sec-Fetch-Mode", "cors"),
        ("Sec-Fetch-Site", "same-origin"),
    ]
    if method == "POST":
        headers.append(("Content-Type", content_type or "application/x-www-form-urlencoded; charset=UTF-8"))
    for name, value in extra or ():
        headers.append((name, value))
    return headers


def referer_for(identity, url):
    """The referer a real browser would have for this request."""
    parsed = urlparse(url)
    if "/comment/" in parsed.path or "/aweme/" in parsed.path:
        return "https://www.douyin.com/"
    if parsed.netloc.endswith("sso.douyin.com"):
        return "https://www.douyin.com/"
    if parsed.netloc.endswith("ttwid.bytedance.com"):
        return "https://www.douyin.com/"
    return "https://www.douyin.com/"


def build(operation, params, identity, clock, body=None, variant_index=0,
          method=None, allow_unsigned=False, provider_name=None, extra_headers=None,
          url_override=None):
    """Build one request plan.

    ``allow_unsigned`` exists for diagnosis only: it produces a request with no
    signature and records ``signatureState == "unsigned-requested"`` so the
    caller can attribute a 403 to the missing signature without guessing.
    """
    if operation in ("qr_get", "qr_check"):
        return build_qr(operation, params, identity, clock)
    variants = endpoints.variants(operation)
    if variant_index >= len(variants):
        raise DouyinError(
            KIND_INVALID_INPUT,
            "operation %r has no variant #%d" % (operation, variant_index),
            detail={"available": [v.name for v in variants]},
        )
    variant = variants[variant_index]

    http_method = (method or variant.method).upper()
    now_s = clock.now_s()

    op_pairs = operation_params(operation, params)
    device_pairs = common_params(identity, now_s)
    # Device measurements: these reads accept the small webapp request, but
    # the synthetic desktop fingerprint block triggers HTTP 200 with no body.
    # Keep the same UA and cookie jar, without submitting locally minted fields.
    if operation in ("aweme_detail", "comment_list", "comment_replies", "user_detail", "search_single", "search_user"):
        op_pairs = [(k, v) for k, v in op_pairs if k not in ("verifyFp", "fp")]
        device_pairs = [(k, v) for k, v in device_pairs if k in ("device_platform", "aid")]
    all_pairs = op_pairs + device_pairs

    body_text = body if body is not None else ""

    # The per-operation signature policy is data, not a decision made here. See
    # endpoints.SIGNATURE_POLICY for why sending a signature where it is not wanted
    # is worse than omitting it.
    policy = endpoints.signature_policy(operation)
    signature_state = "none"
    signer_info = None

    def signature_pairs():
        """The extra pair to append for the placeholder policies, plus the state.

        Returns ``([], state)`` for the policies that append nothing.
        """
        if allow_unsigned:
            return [], "unsigned-requested"
        if policy == endpoints.POLICY_OMIT:
            # Deliberately nothing: this endpoint rejects a signed request.
            return [], "omitted"
        if policy == endpoints.POLICY_PRESENCE:
            provider = signer.get("presence")
            # The parameter must be named literally "a_bogus": that is the name the
            # endpoint checks for. The provider supplies the *value* only, which is
            # why the name is a constant here and not ``provider.name``.
            return [("a_bogus", provider.sign("", "", identity.signer_user_agent()))], "presence"
        return [], "none"

    if http_method == "GET":
        signed_pairs = list(all_pairs)
        if policy == endpoints.POLICY_REQUIRED and not allow_unsigned:
            provider = signer.require_real()
            raw_query = "&".join("%s=%s" % (k, v) for k, v in signed_pairs)
            signed_string = (
                raw_query if provider.sign_params_encoding == signer.ENCODING_RAW
                else encode_pairs(signed_pairs)
            )
            value = provider.sign(signed_string, body_text, identity.signer_user_agent())
            signed_pairs.append((provider.name, value))
            signature_state = "signed"
            signer_info = provider.describe()
        else:
            extra, signature_state = signature_pairs()
            if extra:
                # The placeholder provider's output is a value, not a computation.
                name, value = extra[0]
                signed_pairs.append((name, value))

        query = encode_pairs(signed_pairs)
        url = (url_override or variant.url()) + "?" + query
        headers = build_headers(identity, url, http_method, now_s, extra=extra_headers)
        cookie = identity.cookie_header(url, now_s)
    else:
        # POST: the operation parameters travel in the body, not the query.
        body_pairs = all_pairs
        body_text = encode_pairs(body_pairs)
        if policy == endpoints.POLICY_REQUIRED and not allow_unsigned:
            provider = signer.require_real()
            raw_query = "&".join("%s=%s" % (k, v) for k, v in body_pairs)
            signed_string = raw_query if provider.sign_params_encoding == signer.ENCODING_RAW else body_text
            value = provider.sign(signed_string, body_text, identity.signer_user_agent())
            body_text = body_text + "&" + provider.name + "=" + js_encode_uri_component(value)
            signature_state = "signed"
            signer_info = provider.describe()
        else:
            extra, signature_state = signature_pairs()
            if extra:
                name, value = extra[0]
                body_text = body_text + "&" + name + "=" + js_encode_uri_component(value)

        url = url_override or variant.url()
        query = ""
        headers = build_headers(identity, url, http_method, now_s, extra=extra_headers)
        cookie = identity.cookie_header(url, now_s)

    diagnostics = {
        "operation": operation,
        "variant": variant.name,
        "variantConfidence": variant.confidence,
        "parameterCount": len(all_pairs),
        "bodyBytes": len(body_text),
        "signatureState": signature_state,
        "signaturePolicy": policy,
        "requiresLogin": variant.requires_login,
        "host": urlparse(url).netloc,
    }

    # The actual gate, measured on 2026-10-07: the server refuses a request that
    # carries no server-issued ttwid. This is a *request-build* fact rather than a
    # transport concern, so it is surfaced here, on the plan, where the caller can
    # act on it before spending a round trip.
    if not identity.ttwid:
        diagnostics["warning"] = (
            "no ttwid has been issued yet; the server will refuse this request until "
            "the guest session is bootstrapped (GET https://www.douyin.com/ and keep "
            "the Set-Cookie)"
        )
        diagnostics["ttwidMissing"] = True

    return RequestPlan(
        operation=operation,
        variant=variant.name,
        method=http_method,
        url=url,
        headers=headers,
        query=query,
        body=body_text if http_method == "POST" else None,
        cookie_header=cookie,
        signature_state=signature_state,
        signer=signer_info,
        diagnostics=diagnostics,
    )


def build_plain(url, identity, clock, method="GET", extra_headers=None, body=None):
    """A request with no signing and no device block.

    Used for redirect resolution and for fetching a page shell, where the web
    client would not sign either.
    """
    now_s = clock.now_s()
    headers = build_headers(identity, url, method, now_s, extra=extra_headers)
    cookie = identity.cookie_header(url, now_s)
    return RequestPlan(
        operation="plain",
        variant="plain",
        method=method,
        url=url,
        headers=headers,
        query="",
        body=body,
        cookie_header=cookie,
        signature_state="not-applicable",
        signer=None,
        diagnostics={"operation": "plain", "host": urlparse(url).netloc},
    )


def redact_headers(headers):
    """Log-safe header list: the Cookie header is replaced by a length tag."""
    out = []
    for name, value in headers or ():
        if name.lower() == "cookie":
            out.append([name, "<redacted:%d chars, %d pairs>" % (len(to_text(value)), to_text(value).count(";") + 1)])
        else:
            out.append([name, value])
    return out


def build_qr(operation, params, identity, clock):
    """Official desktop Passport channel; observed QR create + unscanned poll.

    The public desktop SDK app key is used for its request checksum and aid
    header. This is distinct from feed a_bogus; no placeholder is submitted.
    """
    from douyin.util import sha256_hex, hmac_sha256, hmac_sha256_hex
    app_key = "3c452fb664e3de0e936108429a0bc697"
    variant = endpoints.variants(operation)[0]
    ts = str(int(clock.now_s()) // 86400 * 86400 + 43200)
    pairs = [("passport_jssdk_version", "2.4.12"), ("passport_jssdk_type", "normal"),
             ("is_from_ttaccountsdk", "1"), ("aid", "339757"), ("language", "zh"),
             ("ts", ts), ("is_new_login", "1"), ("is_from_iesaccountsaas", "1"),
             ("account_sdk_source", "web")]
    body_pairs = []
    if operation == "qr_get":
        pairs += [("next", "https://www.douyin.com"), ("need_logo", "false"), ("need_short_url", "false")]
    else:
        body_pairs = [("need_logo", "false"), ("need_short_url", "false"), ("is_frontier", "true"),
                      ("token", to_text((params or {}).get("token") or "")),
                      ("is_new_login", "1"), ("next", "https://www.douyin.com")]
    fields = dict(pairs)
    names = sorted(fields)[:10]
    canonical = "&".join(k + "=" + fields[k] for k in names)
    canonical += "&" + "&".join(k + "=" + v for k, v in sorted(body_pairs)) + "&app_key=" + app_key
    checksum = sha256_hex(canonical)
    qs = "".join(format(value ^ 5, "x") for value in ",".join(names).encode("utf-8"))
    pairs += [("sign", checksum), ("qs", qs)]
    query = encode_pairs(pairs)
    url = variant.url() + "?" + query
    headers = build_headers(identity, url, variant.method, clock.now_s())
    headers = [(k, "https://imdesktop.douyin.com/" if k == "Referer" else
                "https://imdesktop.douyin.com" if k == "Origin" else v) for k, v in headers]
    extracted = hmac_sha256(ts, app_key)
    derived = hmac_sha256(extracted, bytes([1]))
    headers.append(("x-tt-passport-aid-sign", hmac_sha256_hex(derived,
        "aid=339757&path=" + variant.path + "&ts=" + ts)))
    # SDK correlation headers belong to this QR attempt, not a trusted-device identity.
    session_id = to_text((params or {}).get("sessionId") or "")
    correlation = sha256_hex(session_id) if session_id else clock.hex(32)
    headers.append(("x-tt-passport-trace-id", correlation[:8]))
    portrait = correlation[:8] + "-" + correlation[8:12] + "-" + correlation[12:16] + "-" + correlation[16:20] + "-" + correlation[20:32] + ".login"
    headers.append(("x-tt-passport-verify-portrait", portrait))
    csrf = identity.cookies.value("passport_csrf_token", clock.now_s()) or identity.cookies.value("passport_csrf_token_default", clock.now_s())
    if csrf:
        headers.append(("x-tt-passport-csrf-token", csrf))
    return RequestPlan(operation=operation, variant=variant.name, method=variant.method,
        url=url, query=query, headers=headers, body=encode_pairs(body_pairs) if body_pairs else None,
        cookie_header=identity.cookie_header(url, clock.now_s()), signature_state="passport-sdk", signer=None,
        diagnostics={"operation": operation, "variant": variant.name, "host": "imdesktop.douyin.com", "signatureState": "passport-sdk"})
