# -*- coding: utf-8 -*-
"""Error taxonomy for the Douyin protocol layer.

The whole point of this module is that ``HTTP 403`` is not an answer. The
caller must be able to tell *why* a request failed, because the recovery
action differs completely:

======================  ==========================================
kind                    recovery
======================  ==========================================
``network``             retry with backoff (transport-level)
``http_error``          retry only if ``retryable``
``empty_body``          the API accepted the request but returned nothing --
                        usually a silently-dropped request; re-sign and retry
``signature_invalid``   re-sign (our signer drifted), never blind-retry
``risk_control``        stop. Needs identity/cookie repair or user action
``session_expired``     refresh session, then re-issue once
``schema_changed``      the response parsed as JSON but not as the shape we
                        expect -- the API moved. Fallback endpoint, then fail
``not_found``           the aweme/comment is gone; do not retry
``rate_limited``        back off, then retry
``unsupported_codec``   no playable video source; not a network problem
``signer_unavailable``  the a_bogus implementation is missing/not wired
``internal``            a bug in this layer
======================  ==========================================
"""

KIND_NETWORK = "network"
KIND_HTTP_ERROR = "http_error"
KIND_EMPTY_BODY = "empty_body"
KIND_SIGNATURE_INVALID = "signature_invalid"
KIND_RISK_CONTROL = "risk_control"
KIND_SESSION_EXPIRED = "session_expired"
KIND_LOGIN_REQUIRED = "login_required"
KIND_SCHEMA_CHANGED = "schema_changed"
KIND_NOT_FOUND = "not_found"
KIND_RATE_LIMITED = "rate_limited"
KIND_UNSUPPORTED_CODEC = "unsupported_codec"
KIND_SIGNER_UNAVAILABLE = "signer_unavailable"
KIND_INVALID_INPUT = "invalid_input"
KIND_INTERNAL = "internal"

#: Kinds where re-issuing the identical request is pointless. The caller must
#: change something (re-sign, refresh session, give up) first.
NON_RETRYABLE = frozenset(
    (
        KIND_NOT_FOUND,
        KIND_SIGNATURE_INVALID,
        KIND_SCHEMA_CHANGED,
        KIND_RISK_CONTROL,
        KIND_UNSUPPORTED_CODEC,
        KIND_INVALID_INPUT,
        KIND_SIGNER_UNAVAILABLE,
        KIND_LOGIN_REQUIRED,
    )
)

#: Kinds that mean "the session is no longer usable as-is".
SESSION_KINDS = frozenset((KIND_SESSION_EXPIRED, KIND_LOGIN_REQUIRED))


class DouyinError(Exception):
    """Structured protocol failure.

    ``kind`` is one of the ``KIND_*`` constants. ``retryable`` says whether the
    *same* request is worth issuing again; callers should not invent their own
    policy on top of ``kind``.
    """

    __slots__ = ("kind", "message", "http_status", "retryable", "detail")

    def __init__(self, kind, message, http_status=None, retryable=None, detail=None):
        Exception.__init__(self, message)
        self.kind = kind
        self.message = message
        self.http_status = http_status
        if retryable is None:
            retryable = kind not in NON_RETRYABLE
        self.retryable = bool(retryable)
        self.detail = detail or {}

    def to_json(self):
        return {
            "kind": self.kind,
            "message": self.message,
            "httpStatus": self.http_status,
            "retryable": self.retryable,
            "detail": self.detail,
        }

    def __repr__(self):
        return "DouyinError(%s, %r, http=%s)" % (self.kind, self.message, self.http_status)


class SignerUnavailable(DouyinError):
    """The requested signature algorithm is not wired into this build."""

    def __init__(self, message, detail=None):
        DouyinError.__init__(
            self, KIND_SIGNER_UNAVAILABLE, message, retryable=False, detail=detail
        )


def error_response(message, kind=KIND_INTERNAL, http_status=None, retryable=None, detail=None):
    """Build the ``{"ok": false, ...}`` envelope used across the bridge."""
    err = DouyinError(kind, message, http_status, retryable, detail)
    return {"ok": False, "error": err.to_json()}
