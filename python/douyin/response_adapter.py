# -*- coding: utf-8 -*-
"""Response schema adaptation.

This is the module that changes when Douyin changes. Everything downstream of it
consumes the normalized shapes declared in ``docs/ARCHITECTURE.md``, which are
ours, not ByteDance's. Two consequences worth stating:

* The raw payload never reaches the UI or the C# models. Field renames,
  ``null``-vs-``[]`` changes and codec-key churn stop here.
* Being *tolerant* is not the same as being *silent*. Where a field is missing we
  either omit it (documented) or raise ``schema_changed`` with the keys we saw, so
  a drift shows up as one classified error instead of eleven null-reference
  crashes three layers up.

The codec labelling deserves a note, because it is the one place where a guess is
unavoidable: ``video.play_addr`` carries no codec field. We only label a source
``h264`` when the payload gives evidence (a distinct ``play_addr_h264`` entry, a
``bit_rate`` gear name, or ``is_h265``), otherwise the codec is reported as
``unknown`` and the media layer decides -- see ``Media/VideoSourceSelector.cs``.
"""

import json

from douyin.errors import (
    DouyinError,
    KIND_EMPTY_BODY,
    KIND_HTTP_ERROR,
    KIND_NOT_FOUND,
    KIND_RATE_LIMITED,
    KIND_RISK_CONTROL,
    KIND_SCHEMA_CHANGED,
    KIND_SESSION_EXPIRED,
    KIND_LOGIN_REQUIRED,
    KIND_SIGNATURE_INVALID,
)
from douyin.util import to_text

#: HTTP statuses that mean "the server accepted the request but declined it".
RISK_STATUSES = frozenset((401, 403, 405, 429))

#: Markers that mean the response is a risk-control challenge rather than an
#: answer. Kept lowercase; the body is lowercased before matching.
_CHALLENGE_MARKERS = (
    "verify",
    "captcha",
    "slardar",
    "risk",
    "验证",
    "滑块",
    "安全验证",
)

#: The Argus gate's own message, matched case-sensitively because it is a literal
#: diagnostic string. Its presence means the request was refused *before* reaching
#: the handler, and it names the missing credential, which is far more actionable
#: than "403".
_ARGUS_MARKER = "ArgusSecurityPlugin"

#: Credentials the Argus gate has been observed to demand. Each one has a different
#: remedy, so the classifier reports which one is missing rather than a generic
#: "risk control".
_ARGUS_REQUIREMENTS = (
    ("uifid not found", "uifid"),
    ("ttwid not found", "ttwid"),
    ("msToken not found", "msToken"),
)

CODEC_H264 = "h264"
CODEC_HEVC = "hevc"
CODEC_AV1 = "av1"
CODEC_UNKNOWN = "unknown"

CONTAINER_MP4 = "mp4"
CONTAINER_WEBM = "webm"


# ---------------------------------------------------------------------------
# transport-facing helpers
# ---------------------------------------------------------------------------

def parse_json(body_text):
    """Parse a JSON body, tolerating a BOM and trailing whitespace."""
    if body_text is None:
        return None, "empty"
    text = to_text(body_text).strip()
    if not text:
        return None, "empty"
    if text[0] == "\ufeff":
        text = text[1:]
    try:
        return json.loads(text), None
    except ValueError as exc:
        return None, to_text(exc)


def classify(http_status, body_text, operation, variant, headers=None, check_status=True):
    """Decide what a transport result means, or raise a :class:`DouyinError`.

    The ordering here is the whole point of the function:

    1. A non-JSON, non-empty body on a risk status is a challenge page.
    2. ``HTTP 200`` with a zero-length body is the classic *signature rejected*
       signal on this API -- the request never reached the handler. It is
       reported as ``empty_body`` and marked non-retryable-as-is, because issuing
       the identical request again produces the identical nothing.
    3. Only then do we look at ``status_code`` inside a parsed body.

    ``check_status=False`` stops after step 3's parse and returns the payload
    unvalidated. The write path needs that: a refused comment carries its reason
    in ``status_msg`` alongside a non-zero ``status_code``, and mapping it to a
    generic error here would throw away the sentence the user needs to see.
    """
    text = to_text(body_text) if body_text is not None else ""
    stripped = text.strip()

    # 429 is checked before the generic risk-status branch: rate limiting answers
    # with a plain-text body ("too many requests"), which would otherwise be
    # misread as a signature rejection. It is a retry-after-backoff, not a signing
    # bug, and conflating the two sends the diagnosis in the wrong direction.
    if http_status == 429:
        raise DouyinError(
            KIND_RATE_LIMITED,
            "rate limited (HTTP 429) for %s" % operation,
            http_status=http_status,
            detail={"variant": variant, "bodyPrefix": stripped[:200]},
        )

    if stripped and http_status in RISK_STATUSES:
        lowered = stripped[:4096].lower()
        if stripped[0] not in "{[":
            # The Argus gate names the credential it wanted. Reporting *which* one is
            # the difference between an actionable message and an opaque 403 -- the
            # remedies are entirely different (bootstrap the guest session, wait for a
            # uifid, or clear risk control on the account).
            if _ARGUS_MARKER in stripped:
                missing = None
                for needle, credential in _ARGUS_REQUIREMENTS:
                    if needle in lowered:
                        missing = credential
                        break
                raise DouyinError(
                    KIND_RISK_CONTROL,
                    "the request was refused by the security gate before reaching the "
                    "handler%s (HTTP %s) for %s" % (
                        ("; the missing credential is %s" % missing) if missing else "",
                        http_status, operation),
                    http_status=http_status,
                    retryable=False,
                    detail={
                        "variant": variant,
                        "gate": "argus",
                        "missingCredential": missing,
                        "bodyPrefix": stripped[:200],
                    },
                )
            if any(marker in lowered for marker in _CHALLENGE_MARKERS):
                raise DouyinError(
                    KIND_RISK_CONTROL,
                    "server returned a risk-control challenge (HTTP %s) for %s"
                    % (http_status, operation),
                    http_status=http_status,
                    detail={"variant": variant, "bodyPrefix": stripped[:200]},
                )
            raise DouyinError(
                KIND_HTTP_ERROR if http_status != 403 else KIND_SIGNATURE_INVALID,
                "HTTP %s with a non-JSON body for %s" % (http_status, operation),
                http_status=http_status,
                detail={"variant": variant, "bodyPrefix": stripped[:200]},
            )

    if not stripped:
        if http_status in RISK_STATUSES:
            raise DouyinError(
                KIND_SIGNATURE_INVALID,
                "HTTP %s with an empty body -- the request was dropped before it "
                "reached the handler, which on this API means the signature was "
                "rejected" % http_status,
                http_status=http_status,
                detail={"variant": variant, "operation": operation},
            )
        raise DouyinError(
            KIND_EMPTY_BODY,
            "HTTP %s with an empty body for %s" % (http_status, operation),
            http_status=http_status,
            detail={"variant": variant, "operation": operation},
        )

    if http_status == 429:
        raise DouyinError(
            KIND_RATE_LIMITED,
            "rate limited (HTTP 429) for %s" % operation,
            http_status=http_status,
            detail={"variant": variant},
        )

    parsed, parse_error = parse_json(stripped)
    if parse_error is not None:
        raise DouyinError(
            KIND_HTTP_ERROR if http_status >= 400 else KIND_SCHEMA_CHANGED,
            "response body is not JSON (%s) for %s" % (parse_error, operation),
            http_status=http_status,
            detail={"variant": variant, "bodyPrefix": stripped[:200]},
        )

    if http_status >= 400:
        raise DouyinError(
            KIND_RISK_CONTROL if http_status in RISK_STATUSES else KIND_HTTP_ERROR,
            "HTTP %s for %s" % (http_status, operation),
            http_status=http_status,
            detail={"variant": variant, "body": _brief(parsed)},
        )

    status_code = parsed.get("status_code") if isinstance(parsed, dict) else None
    if check_status and status_code not in (None, 0):
        raise _from_status_code(status_code, parsed, operation, variant, http_status)

    return parsed


def _from_status_code(status_code, parsed, operation, variant, http_status):
    """Map a non-zero ``status_code`` onto the taxonomy."""
    message = _first_text(parsed, ("status_msg", "message", "msg", "description"))

    if status_code == 2483:
        return DouyinError(
            KIND_LOGIN_REQUIRED, message or "请先登录，再继续搜索吧",
            http_status=http_status, retryable=False,
            detail={"variant": variant, "statusCode": status_code, "statusMessage": message},
        )
    if status_code in (8, 10000):
        return DouyinError(
            KIND_RISK_CONTROL,
            "API refused the request (status_code=%s) for %s" % (status_code, operation),
            http_status=http_status,
            detail={"variant": variant, "statusCode": status_code, "statusMessage": message},
        )
    if status_code in (2154, 2156, 4001, 4002, 4003, 4004):
        return DouyinError(
            KIND_SESSION_EXPIRED,
            "session rejected (status_code=%s) for %s" % (status_code, operation),
            http_status=http_status,
            detail={"variant": variant, "statusCode": status_code, "statusMessage": message},
        )
    if status_code in (10009, 10010, 2095):
        return DouyinError(
            KIND_NOT_FOUND,
            "object not found (status_code=%s) for %s" % (status_code, operation),
            http_status=http_status,
            retryable=False,
            detail={"variant": variant, "statusCode": status_code, "statusMessage": message},
        )
    return DouyinError(
        KIND_SCHEMA_CHANGED,
        "unrecognised status_code=%s for %s" % (status_code, operation),
        http_status=http_status,
        detail={"variant": variant, "statusCode": status_code, "statusMessage": message},
    )


def _brief(value, limit=400):
    text = value if isinstance(value, str) else json.dumps(value, ensure_ascii=False)
    return text[:limit]


def _first_text(source, keys):
    for key in keys:
        value = source.get(key)
        if isinstance(value, str) and value:
            return value
    return None


def require_mapping(value, what, operation):
    if not isinstance(value, dict):
        raise DouyinError(
            KIND_SCHEMA_CHANGED,
            "%s is missing or not an object in the %s response" % (what, operation),
            detail={"received": type(value).__name__},
        )
    return value


def require_list(value, what, operation):
    if value is None:
        return []
    if not isinstance(value, list):
        raise DouyinError(
            KIND_SCHEMA_CHANGED,
            "%s is not a list in the %s response" % (what, operation),
            detail={"received": type(value).__name__},
        )
    return value


# ---------------------------------------------------------------------------
# normalizers
# ---------------------------------------------------------------------------

def first_url(container):
    """``{"url_list": [...]}`` -> first usable URL."""
    urls = url_list(container)
    return urls[0] if urls else None


def url_list(container):
    if isinstance(container, str):
        return [container]
    if not isinstance(container, dict):
        return []
    raw = container.get("url_list")
    if isinstance(raw, str):
        return [raw] if raw else []
    if not isinstance(raw, list):
        return []
    return [item for item in raw if isinstance(item, str) and item]


def _upgrade_scheme(url):
    """Offer an https twin of an http CDN URL.

    The web payload mixes schemes on the same host. We keep the original and add
    the twin rather than rewriting, because not every CDN host answers on 443 and
    a rewritten URL that 404s is worse than an http one that works. The media
    layer prefers https and falls back.
    """
    if url.startswith("http://"):
        return [url, "https://" + url[len("http://") :]]
    return [url]


def quality_from_size(width, height):
    if not height:
        return "unknown"
    if height >= 2160:
        return "2160p"
    if height >= 1440:
        return "1440p"
    if height >= 1080:
        return "1080p"
    if height >= 720:
        return "720p"
    if height >= 540:
        return "540p"
    if height >= 480:
        return "480p"
    if height >= 360:
        return "360p"
    return "240p"


def _codec_of_key(key):
    lowered = key.lower()
    if "bytevc1" in lowered or "265" in lowered or "hevc" in lowered:
        return CODEC_HEVC
    if "h264" in lowered or "avc" in lowered:
        return CODEC_H264
    if "av1" in lowered:
        return CODEC_AV1
    return None


def sources_from_video(video):
    """Flatten every playable candidate in a ``video`` object.

    Yields dicts, deduplicated by URL, each carrying the evidence for its codec
    label so a wrong guess is debuggable rather than mysterious.
    """
    candidates = []
    seen = set()
    #: url -> codec, recorded for every URL we see, *including* duplicates that
    #: deduplication drops. The generic ``play_addr`` frequently points at the same
    #: file as an explicitly labelled variant, and that label is the only codec
    #: evidence the payload contains.
    hints = {}

    def add(container, key, codec, gear_name=None, bitrate=None, width=None,
            height=None, size_bytes=None, quality=None, format_hint=None):
        for url in url_list(container):
            if codec and codec != CODEC_UNKNOWN:
                hints.setdefault(url, codec)
            if url in seen:
                continue
            seen.add(url)
            for variant in _upgrade_scheme(url):
                candidates.append(
                    {
                        "url": variant,
                        "originalUrl": url,
                        "codec": codec or CODEC_UNKNOWN,
                        "codecEvidence": key,
                        "container": format_hint or CONTAINER_MP4,
                        "quality": quality or quality_from_size(width, height),
                        "width": width,
                        "height": height,
                        "bitrate": bitrate,
                        "sizeBytes": size_bytes,
                        "gearName": gear_name,
                    }
                )

    format_hint = None
    raw_format = video.get("format")
    if isinstance(raw_format, str) and raw_format:
        format_hint = raw_format.lower()

    default_width = video.get("width")
    default_height = video.get("height")

    # The generic address. Its codec is genuinely unknown from the payload alone;
    # the "h264"/"hevc" fields below are what tell us, when they exist.
    default_addr = video.get("play_addr")
    if isinstance(default_addr, dict):
        add(
            default_addr,
            "play_addr",
            CODEC_UNKNOWN,
            width=default_addr.get("width") or default_width,
            height=default_addr.get("height") or default_height,
            size_bytes=default_addr.get("data_size"),
            format_hint=format_hint,
        )

    if isinstance(video.get("play_addr_h264"), dict):
        entry = video["play_addr_h264"]
        add(
            entry,
            "play_addr_h264",
            CODEC_H264,
            width=entry.get("width") or default_width,
            height=entry.get("height") or default_height,
            size_bytes=entry.get("data_size"),
            format_hint=format_hint,
        )

    for key in ("play_addr_265", "play_addr_h265", "play_addr_bytevc1"):
        if isinstance(video.get(key), dict):
            entry = video[key]
            add(
                entry,
                key,
                CODEC_HEVC,
                width=entry.get("width") or default_width,
                height=entry.get("height") or default_height,
                size_bytes=entry.get("data_size"),
                format_hint=format_hint,
            )

    for entry in require_list(video.get("bit_rate"), "video.bit_rate", "aweme") or ():
        if not isinstance(entry, dict):
            continue
        gear_name = entry.get("gear_name")
        codec = None
        if entry.get("is_h265") in (1, True):
            codec = CODEC_HEVC
        if gear_name:
            codec = _codec_of_key(gear_name) or codec
        if codec is None:
            for probe in ("play_addr", "play_addr_265", "play_addr_h264"):
                if isinstance(entry.get(probe), dict):
                    codec = _codec_of_key(probe)
                    break
        add(
            entry.get("play_addr"),
            "bit_rate:%s" % (gear_name or "?"),
            codec or CODEC_UNKNOWN,
            gear_name=gear_name,
            bitrate=entry.get("bit_rate"),
            width=entry.get("play_addr", {}).get("width") if isinstance(entry.get("play_addr"), dict) else None,
            height=entry.get("play_addr", {}).get("height") if isinstance(entry.get("play_addr"), dict) else None,
            size_bytes=(
                entry.get("play_addr", {}).get("data_size")
                if isinstance(entry.get("play_addr"), dict)
                else None
            ),
            format_hint=format_hint,
        )

    # A generic address that is byte-identical to a labelled one cannot be two
    # codecs, so it inherits the label. Anything still unlabelled stays "unknown"
    # and is flagged for a decode probe rather than guessed at.
    for candidate in candidates:
        if candidate["codec"] == CODEC_UNKNOWN and candidate["originalUrl"] in hints:
            candidate["codec"] = hints[candidate["originalUrl"]]
            candidate["codecEvidence"] = candidate["codecEvidence"] + "+inherited"

    return candidates


def normalize_author(raw):
    if not isinstance(raw, dict):
        return {
            "id": None, "secUid": None, "nickname": None, "avatarUrl": None,
            "uniqueId": None, "signature": None,
        }
    return {
        "id": _string_or_none(raw.get("uid") or raw.get("user_id")),
        "secUid": _string_or_none(raw.get("sec_uid")),
        "nickname": _string_or_none(raw.get("nickname")),
        "avatarUrl": first_url(raw.get("avatar_thumb") or raw.get("avatar_medium") or raw.get("avatar_larger")),
        "uniqueId": _string_or_none(raw.get("unique_id") or raw.get("short_id")),
        "signature": _string_or_none(raw.get("signature")),
    }


def normalize_statistics(raw):
    raw = raw if isinstance(raw, dict) else {}
    return {
        "diggCount": _int_or_zero(raw.get("digg_count")),
        "commentCount": _int_or_zero(raw.get("comment_count")),
        "shareCount": _int_or_zero(raw.get("share_count")),
        "collectCount": _int_or_zero(raw.get("collect_count")),
        "playCount": _int_or_zero(raw.get("play_count")),
    }


def normalize_video(raw):
    raw = raw if isinstance(raw, dict) else {}
    sources = sources_from_video(raw)
    return {
        "width": _int_or_none(raw.get("width")),
        "height": _int_or_none(raw.get("height")),
        "durationMs": _int_or_none(raw.get("duration")),
        "coverUrl": first_url(raw.get("cover") or raw.get("origin_cover")),
        "dynamicCoverUrl": first_url(raw.get("dynamic_cover")),
        "format": (raw.get("format") or "").lower() or None,
        "hasWatermark": bool(raw.get("has_watermark")),
        "sources": sources,
    }


def normalize_image_post(raw):
    """Image posts carry ``images`` instead of ``video``; phase 1 only needs to
    recognise them so the feed does not treat them as broken videos."""
    if not isinstance(raw, list) or not raw:
        return None
    images = []
    for entry in raw:
        if not isinstance(entry, dict):
            continue
        urls = url_list(entry.get("url_list"))
        if urls:
            images.append({"url": urls[0], "urls": urls,
                           "width": _int_or_none(entry.get("width")),
                           "height": _int_or_none(entry.get("height"))})
    return images or None


def normalize_aweme(raw, require_video=True):
    """Raw ``aweme`` object -> the normalized schema the rest of the client uses."""
    raw = require_mapping(raw, "aweme", "aweme_detail")

    images = normalize_image_post(raw.get("images")) or normalize_image_post(
        (raw.get("image_post_info") or {}).get("images")
        if isinstance(raw.get("image_post_info"), dict)
        else None
    )
    video = raw.get("video")
    video = video if isinstance(video, dict) else {}

    normalized_video = normalize_video(video)
    if require_video and not normalized_video["sources"] and not images:
        raise DouyinError(
            KIND_SCHEMA_CHANGED,
            "aweme %s has neither playable sources nor images" % raw.get("aweme_id"),
            detail={"keys": sorted(video.keys())},
        )

    return {
        "id": _string_or_none(raw.get("aweme_id")),
        "desc": _string_or_none(raw.get("desc")) or "",
        "createTime": _int_or_none(raw.get("create_time")),
        "author": normalize_author(raw.get("author")),
        "video": normalized_video,
        "statistics": normalize_statistics(raw.get("statistics")),
        "isImage": bool(images),
        "images": images or [],
        "shareUrl": _string_or_none((raw.get("share_info") or {}).get("share_url"))
        if isinstance(raw.get("share_info"), dict)
        else None,
        "durationMs": normalized_video["durationMs"],
    }


def normalize_comment(raw, level=1):
    raw = require_mapping(raw, "comment", "comment_list")
    return {
        "id": _string_or_none(raw.get("cid") or raw.get("comment_id")),
        "text": _string_or_none(raw.get("text")) or "",
        "createTime": _int_or_none(raw.get("create_time")),
        "diggCount": _int_or_zero(raw.get("digg_count")),
        "replyCount": _int_or_zero(raw.get("reply_comment_total") or raw.get("reply_count")),
        "author": normalize_author(raw.get("user")),
        "replyToUsername": _string_or_none(raw.get("reply_to_username")),
        "replyToCommentId": _string_or_none(raw.get("reply_id")),
        "level": level,
        "status": _int_or_zero(raw.get("status")),
        "isAuthor": bool(raw.get("is_author")),
        "isPinned": bool(raw.get("is_pinned") or raw.get("stick_position")),
    }


# ---------------------------------------------------------------------------
# pages
# ---------------------------------------------------------------------------

def _flag(value):
    """``has_more`` arrives as 1/0, true/false or "1"."""
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return value != 0
    if isinstance(value, str):
        return value.strip().lower() in ("1", "true", "yes")
    return False


def normalize_aweme_page(payload, operation="aweme_detail"):
    """Feed / post-list payload -> ``{items, cursor, hasMore}``."""
    payload = require_mapping(payload, "payload", operation)

    container = None
    for key in ("aweme_list", "data", "aweme_detail"):
        value = payload.get(key)
        if isinstance(value, list):
            container = (key, value)
            break
        if isinstance(value, dict) and key == "aweme_detail":
            container = (key, [value])
            break
    if container is None:
        raise DouyinError(
            KIND_SCHEMA_CHANGED,
            "no aweme list in the %s response" % operation,
            detail={"keys": sorted(payload.keys())},
        )

    key, raw_items = container
    items = []
    skipped = 0
    for raw in raw_items:
        try:
            # A list entry that cannot be normalized is skipped, not fatal: a feed
            # with one broken item should still render the other nineteen.
            items.append(normalize_aweme(raw, require_video=False))
        except DouyinError:
            skipped += 1

    cursor = None
    for candidate in ("max_cursor", "cursor", "min_cursor"):
        if payload.get(candidate) is not None:
            cursor = _string_or_none(payload.get(candidate))
            break

    has_more = _flag(payload.get("has_more"))
    if payload.get("has_more") is None and payload.get("hasMore") is None:
        # Feeds that omit the flag: infer it from whether the page was full.
        has_more = len(items) > 0

    return {
        "items": items,
        "cursor": cursor,
        "hasMore": has_more,
        "sourceKey": key,
        "skipped": skipped,
        "logId": _string_or_none(payload.get("log_pb", {}).get("impr_id"))
        if isinstance(payload.get("log_pb"), dict)
        else None,
    }


def normalize_comment_page(payload, operation="comment_list"):
    """Comment list / reply payload -> ``{items, cursor, hasMore, total}``.

    ``status_code == 0`` with no ``comments`` key at all means "this aweme has no
    comments", which is a success, not a schema change. Only a payload that has
    neither the container nor a recognisable success marker is treated as drift.
    """
    payload = require_mapping(payload, "payload", operation)

    raw_items = None
    for key in ("comments", "comment_list"):
        if isinstance(payload.get(key), list):
            raw_items = payload[key]
            break

    if raw_items is None:
        if payload.get("status_code") in (0, None) and "comments" not in payload:
            raw_items = []
        else:
            raise DouyinError(
                KIND_SCHEMA_CHANGED,
                "no comment list in the %s response" % operation,
                detail={"keys": sorted(payload.keys())},
            )

    items = []
    skipped = 0
    for raw in raw_items:
        try:
            items.append(normalize_comment(raw, level=_int_or_zero(raw.get("level")) or 1))
        except DouyinError:
            skipped += 1

    return {
        "items": items,
        "cursor": _string_or_none(payload.get("cursor")),
        "hasMore": _flag(payload.get("has_more")),
        "total": _int_or_zero(payload.get("total")),
        "skipped": skipped,
    }


def normalize_user(raw):
    raw = require_mapping(raw, "user", "user_detail")
    return {
        "id": _string_or_none(raw.get("uid") or raw.get("user_id")),
        "secUid": _string_or_none(raw.get("sec_uid")),
        "nickname": _string_or_none(raw.get("nickname")),
        "avatarUrl": first_url(raw.get("avatar_thumb") or raw.get("avatar_larger")),
        "uniqueId": _string_or_none(raw.get("unique_id") or raw.get("short_id")),
        "signature": _string_or_none(raw.get("signature")),
        "followingCount": _int_or_zero(raw.get("following_count")),
        "followerCount": _int_or_zero(raw.get("follower_count")),
        "awemeCount": _int_or_zero(raw.get("aweme_count")),
    }


# ---------------------------------------------------------------------------
# scalars
# ---------------------------------------------------------------------------

def _string_or_none(value):
    if value is None:
        return None
    if isinstance(value, str):
        return value
    if isinstance(value, bool):
        return "true" if value else "false"
    return str(value)


def _int_or_none(value):
    if value is None or isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    if isinstance(value, float):
        return int(value)
    if isinstance(value, str):
        try:
            return int(value)
        except ValueError:
            return None
    return None


def _int_or_zero(value):
    parsed = _int_or_none(value)
    return parsed if parsed is not None else 0


def normalize_search_page(payload):
    """Search cards are wrappers, unlike the direct aweme list in a feed."""
    payload = require_mapping(payload, "search payload", "search_single")
    videos, users = [], []
    seen_video, seen_user = set(), set()
    skipped = 0
    for card in (payload.get("data") or payload.get("aweme_list") or []):
        if not isinstance(card, dict):
            skipped += 1
            continue
        raw = card.get("aweme_info") or card.get("aweme")
        if raw is None and card.get("aweme_id"):
            raw = card
        if raw:
            try:
                item = normalize_aweme(raw, require_video=False)
                if item["id"] and item["id"] not in seen_video:
                    seen_video.add(item["id"])
                    videos.append(item)
            except DouyinError:
                skipped += 1
        raw_users = card.get("user_list") or []
        one = card.get("user_info") or card.get("user")
        if one:
            raw_users = [one] + raw_users
        for entry in raw_users:
            if not isinstance(entry, dict):
                skipped += 1
                continue
            entry = entry.get("user_info") or entry.get("user") or entry
            user = normalize_user(entry)
            key = user.get("secUid") or user.get("id")
            if key and key not in seen_user:
                seen_user.add(key)
                users.append(user)
    for raw in payload.get("user_list") or []:
        if not isinstance(raw, dict):
            skipped += 1
            continue
        user = normalize_user(raw.get("user_info") or raw.get("user") or raw)
        key = user.get("secUid") or user.get("id")
        if key and key not in seen_user:
            seen_user.add(key)
            users.append(user)
    return {
        "videos": videos, "users": users,
        "cursor": _string_or_none(payload.get("cursor") if payload.get("cursor") is not None else payload.get("offset")),
        "searchId": _string_or_none(payload.get("search_id") or (payload.get("log_pb") or {}).get("impr_id")),
        "hasMore": _flag(payload.get("has_more")), "skipped": skipped,
    }
