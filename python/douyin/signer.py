# -*- coding: utf-8 -*-
"""Signature providers.

The request signature (``a_bogus``, and historically ``X-Bogus``/``_signature``)
is the one part of this protocol layer that must be reverse engineered from
Douyin's own obfuscated ``webmssdk``. It is therefore kept behind a registry
rather than inlined into the request builder:

* A provider is a pure function ``(params_str, body_str, user_agent) -> str``.
  Nothing else about the request leaks into it, so a provider can be replaced,
  versioned, or A/B compared without touching any other module.
* Missing providers are a *named* failure (:class:`~douyin.errors.SignerUnavailable`),
  never a silent unsigned request. The request builder records
  ``signatureState`` in its diagnostics so an unsigned 403 can never be mistaken
  for a signing bug.

Calibration note: whether the signed string is the *encoded* query or the raw
query, and whether ``a_bogus`` covers the body for GETs, are both properties of
the provider, not of this module. That is why ``sign_params_encoding`` is a
provider attribute -- see :class:`SignatureProvider`.
"""

from douyin.errors import SignerUnavailable

#: How the provider wants the parameter string handed to it.
ENCODING_RAW = "raw"          # key=value&key2=value2, values not percent-encoded
ENCODING_ENCODED = "encoded"  # values percent-encoded the way they go on the wire


class SignatureProvider(object):
    """Base class for signature implementations.

    Subclasses set :attr:`name`, :attr:`sign_params_encoding` and implement
    :meth:`sign`.
    """

    name = "abstract"
    sign_params_encoding = ENCODING_ENCODED

    #: True when the algorithm consumes the request body as well as the query.
    covers_body = True

    #: True when the provider is believed to match the live server. A provider
    #: that is wired up but unverified must report False so callers do not treat
    #: its output as proof that signing works.
    verified = False

    def sign(self, params_str, body_str, user_agent):  # pragma: no cover - abstract
        raise NotImplementedError

    def describe(self):
        return {
            "name": self.name,
            "paramsEncoding": self.sign_params_encoding,
            "coversBody": self.covers_body,
            "verified": self.verified,
        }


class NullSigner(SignatureProvider):
    """Emits no signature.

    Used only for the explicit "unsigned probe" path and for diagnosing whether
    the server is refusing the request because of the signature at all. The
    request builder still records that the request went out unsigned.
    """

    name = "none"
    covers_body = False
    verified = True

    def sign(self, params_str, body_str, user_agent):
        return ""


class PresenceSigner(SignatureProvider):
    """Emits a non-empty placeholder, because that endpoint only checks presence.

    This is not a stub and not a workaround: ``comment/list`` was measured to
    return 200 with a full comment list when ``a_bogus`` was present with an
    arbitrary two-character value, and 200 with an *empty* body when the parameter
    was absent entirely. The endpoint performs a presence check, so a placeholder
    is the correct and complete answer for it.

    Two characters, because that is the length that was measured to work. The
    characters are random rather than a fixed literal so that every client does not
    present the identical value -- but the value is not believed to be verified, and
    the docstring says so rather than implying otherwise.

    When this endpoint is hardened to check the value, ``endpoints.SIGNATURE_POLICY``
    moves it to ``POLICY_REQUIRED`` and a real provider is registered; nothing else
    changes.
    """

    name = "presence"
    covers_body = False
    #: Deliberately False: this is known-good-for-presence, not a real signature.
    verified = False

    #: Length measured to be accepted. See the class docstring and
    #: ``_research/abogus/EVIDENCE.md``.
    TOKEN_LENGTH = 2

    ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"

    def __init__(self, clock=None):
        self._clock = clock

    def sign(self, params_str, body_str, user_agent):
        import random

        return "".join(random.choice(self.ALPHABET) for _ in range(self.TOKEN_LENGTH))


_REGISTRY = {}
_PREFERENCE = ["a_bogus", "x_bogus", "presence", "none"]


def register(provider):
    """Register a provider instance under its :attr:`name`."""
    _REGISTRY[provider.name] = provider
    return provider


def get(name):
    provider = _REGISTRY.get(name)
    if provider is None:
        raise SignerUnavailable(
            "signature provider %r is not available in this build" % name,
            detail={"available": sorted(_REGISTRY), "requested": name},
        )
    return provider


def preferred():
    """The best registered provider, honouring :data:`_PREFERENCE`."""
    for name in _PREFERENCE:
        if name in _REGISTRY:
            return _REGISTRY[name]
    raise SignerUnavailable(
        "no signature provider is registered", detail={"available": sorted(_REGISTRY)}
    )


#: Providers that do not actually compute a signature. A caller that needs a real
#: one must not accept these.
PLACEHOLDER_NAMES = frozenset(("none", "presence"))


def require_real():
    """The best provider that actually computes a signature, or raise.

    Used by ``POLICY_REQUIRED`` operations. It must not fall back to a placeholder:
    silently substituting a placeholder for a required signature would turn a
    clear "not implemented" into a confusing server-side rejection.
    """
    for name in _PREFERENCE:
        provider = _REGISTRY.get(name)
        if provider is not None and name not in PLACEHOLDER_NAMES:
            return provider
    raise SignerUnavailable(
        "this endpoint requires a real request signature, but no verified provider "
        "is registered in this build",
        detail={
            "available": sorted(_REGISTRY),
            "placeholders": sorted(PLACEHOLDER_NAMES & set(_REGISTRY)),
            "hint": "see _research/abogus for the reverse-engineering state and "
                    "register a provider in douyin/signer_abogus.py",
        },
    )


def available():
    return sorted(_REGISTRY)


def describe_all():
    return [provider.describe() for provider in _REGISTRY.values()]


# ``none`` and ``presence`` are always present. ``none`` keeps the explicit
# unsigned-probe path working; ``presence`` is the correct provider for the
# endpoints that only check whether the parameter exists. A real provider
# registers itself on import; see ``_load_builtin_providers``.
register(NullSigner())
register(PresenceSigner())


def _load_builtin_providers():
    """Import optional provider modules.

    Kept as a function so a build with no real signature implementation still works
    -- which, as of the 2026-10-07 measurement, is the *correct* configuration for
    every endpoint this client uses. ``signer_abogus`` is imported when present so
    that a completed reverse is picked up automatically, but its absence is not a
    degraded state.
    """
    try:
        from douyin import signer_abogus  # noqa: F401
    except ImportError:
        pass


_load_builtin_providers()
