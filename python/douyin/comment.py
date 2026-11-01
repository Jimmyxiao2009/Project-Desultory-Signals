# -*- coding: utf-8 -*-
"""Comment publishing.

Writing is the only place where a mistake is visible to other people, so this
module is deliberately conservative:

* **Text is validated before anything else.** Empty, whitespace-only and
  over-length comments are refused locally rather than after a round trip.
* **Double submission is blocked.** :class:`PublishGuard` refuses an identical
  (aweme, text, reply-target) publish inside a short window. The brief forbids
  blind retries on writes; this is the mechanism that makes that policy real
  rather than aspirational, and it also covers the case the network layer cannot
  see: the user tapping publish twice.
* **Failures stay classified.** A publish failure is turned into a
  :class:`~douyin.errors.DouyinError` with a kind the UI can act on, including the
  specific "your comment tripped a review filter" case, which must not be
  presented as a network problem.
"""

import hashlib

from douyin.errors import DouyinError, KIND_INVALID_INPUT, KIND_RISK_CONTROL
from douyin.response_adapter import normalize_comment, parse_json
from douyin.util import to_bytes, to_text

#: Douyin's own comment box stops at 500 characters; we refuse locally at the
#: same limit so the user learns before the request, not after.
MAX_COMMENT_LENGTH = 500

#: Identical-publish suppression window. Long enough to absorb a double tap and a
#: retry-after-timeout, short enough that a user who genuinely wants to post the
#: same words twice only has to wait a moment.
DUPLICATE_WINDOW_MS = 60 * 1000

#: Response phrases that mean "reviewed and refused", not "try again".
_FILTER_MARKERS = (
    "sensitive",
    "违规",
    "审核",
    "敏感",
    "包含不当",
    "risk",
)


def validate_text(text):
    """Return the cleaned text or raise :class:`DouyinError`."""
    if text is None:
        raise DouyinError(KIND_INVALID_INPUT, "comment text is required")
    cleaned = to_text(text).strip()
    if not cleaned:
        raise DouyinError(KIND_INVALID_INPUT, "comment text is empty")
    if len(cleaned) > MAX_COMMENT_LENGTH:
        raise DouyinError(
            KIND_INVALID_INPUT,
            "comment is %d characters; the limit is %d" % (len(cleaned), MAX_COMMENT_LENGTH),
            detail={"length": len(cleaned), "limit": MAX_COMMENT_LENGTH},
        )
    return cleaned


class PublishGuard(object):
    """Refuses a repeat of the last successful-or-in-flight publish.

    State lives in the host, not here: the guard is cheap to reconstruct and the
    protocol layer stays free of storage.
    """

    __slots__ = ("_last_key", "_last_at_ms")

    def __init__(self, last_key=None, last_at_ms=0):
        self._last_key = last_key
        self._last_at_ms = last_at_ms

    @staticmethod
    def key(aweme_id, text, reply_comment_id=None, reply_to_reply_id=None):
        digest = hashlib.md5()
        digest.update(to_bytes(aweme_id or ""))
        digest.update(b"\x00")
        digest.update(to_bytes(text or ""))
        digest.update(b"\x00")
        digest.update(to_bytes(reply_comment_id or ""))
        digest.update(b"\x00")
        digest.update(to_bytes(reply_to_reply_id or ""))
        return digest.hexdigest()

    def check(self, aweme_id, text, now_ms, reply_comment_id=None, reply_to_reply_id=None):
        """Raise when this exact publish was just issued."""
        candidate = self.key(aweme_id, text, reply_comment_id, reply_to_reply_id)
        if candidate == self._last_key and (now_ms - self._last_at_ms) < DUPLICATE_WINDOW_MS:
            raise DouyinError(
                KIND_INVALID_INPUT,
                "this comment was already submitted a moment ago",
                retryable=False,
                detail={
                    "waitMs": DUPLICATE_WINDOW_MS - (now_ms - self._last_at_ms),
                    "reason": "duplicate-suppressed",
                },
            )
        return candidate

    def commit(self, key, now_ms):
        self._last_key = key
        self._last_at_ms = now_ms

    def to_json(self):
        return {"lastKey": self._last_key, "lastAtMs": self._last_at_ms}

    @classmethod
    def from_json(cls, obj):
        obj = obj or {}
        return cls(obj.get("lastKey"), obj.get("lastAtMs", 0))


def build_publish(aweme_id, text, reply_comment_id=None, reply_to_reply_id=None,
                  text_extra=None):
    """Parameters for the publish request.

    A top-level comment leaves the reply ids empty; a reply fills
    ``reply_id`` (the comment being answered) and, for a nested reply,
    ``reply_to_reply_id`` as well. Sending the wrong combination is how a reply
    silently lands at the top level, so the two are validated here.
    """
    if not aweme_id:
        raise DouyinError(KIND_INVALID_INPUT, "aweme_id is required")
    cleaned = validate_text(text)

    if reply_to_reply_id and not reply_comment_id:
        raise DouyinError(
            KIND_INVALID_INPUT,
            "a nested reply needs both reply_comment_id and reply_to_reply_id",
        )

    # ``text_extra`` is the at-mention payload and travels as a JSON *string*
    # inside the form body, so it is serialized here and ordered alongside the
    # other publish parameters by request_builder.
    body = {
        "aweme_id": to_text(aweme_id),
        "text": cleaned,
        "text_extra": text_extra
        if isinstance(text_extra, str)
        else _json_compact(text_extra if text_extra is not None else []),
        "reply_id": to_text(reply_comment_id) if reply_comment_id else "0",
        "reply_to_reply_id": to_text(reply_to_reply_id) if reply_to_reply_id else "0",
    }
    return body


def _json_compact(value):
    import json

    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def parse_publish_response(body_text, http_status):
    """Turn a publish response into a normalized comment or a classified error.

    The success shape is ``{"comment": {...}, "status_code": 0}``. A refusal often
    arrives as ``status_code != 0`` with a human-readable message and no
    ``comment`` key -- that is a *filter*, not a transport failure, and it is
    reported as such so the UI can say so.
    """
    payload, parse_error = parse_json(body_text)
    if parse_error is not None:
        raise DouyinError(
            KIND_RISK_CONTROL if http_status == 403 else KIND_INVALID_INPUT,
            "publish response was not JSON (%s)" % parse_error,
            http_status=http_status,
            detail={"bodyPrefix": to_text(body_text)[:200]},
        )

    status_code = payload.get("status_code")
    comment = payload.get("comment")

    if comment:
        return normalize_comment(comment, level=1)

    message = ""
    for key in ("status_msg", "message", "msg", "extra", "description"):
        value = payload.get(key)
        if isinstance(value, str) and value:
            message = value
            break

    lowered = message.lower()
    if any(marker in lowered or marker in message for marker in _FILTER_MARKERS):
        raise DouyinError(
            KIND_RISK_CONTROL,
            "the comment was refused by review: %s" % message,
            http_status=http_status,
            retryable=False,
            detail={"statusCode": status_code, "statusMessage": message},
        )

    raise DouyinError(
        KIND_INVALID_INPUT if http_status < 500 else KIND_RISK_CONTROL,
        "comment was not published (status_code=%s%s)"
        % (status_code, (", message: %s" % message) if message else ""),
        http_status=http_status,
        retryable=http_status >= 500,
        detail={"statusCode": status_code, "statusMessage": message,
                "keys": sorted(payload.keys())},
    )
