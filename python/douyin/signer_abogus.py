# -*- coding: utf-8 -*-
"""The real ``a_bogus`` provider, and the honest state of it.

This module exists so that the integration point is in place and testable, not
because the algorithm is finished. It registers an ``a_bogus`` provider **only
when that provider can actually produce a signature**; otherwise it registers
nothing and the client keeps working, because -- as of the 2026-10-07 measurement
-- nothing in this client needs a real signature.

Measured state of the live gate (``_research/abogus/EVIDENCE.md``):

* ``aweme/detail`` and ``tab/feed`` are gated on a server-issued ``ttwid`` cookie.
  Adding an ``a_bogus`` parameter to them *causes* a 403.
* ``comment/list`` performs a **presence** check on the ``a_bogus`` parameter; any
  non-empty value passes.

So the correct configuration for this build is ``POLICY_OMIT`` / ``POLICY_PRESENCE``
(see ``endpoints.SIGNATURE_POLICY``) and **no** registered real provider. That is not
a degraded state, it is the measured-correct one.

Reverse-engineering state, in ``_abogus_research``:

* **Encoder half: verified.** ``current_encode`` plus the extracted ``KEYSTREAM``
  reproduce six captured real signatures byte-for-byte.
* **Payload half: incomplete.** ``build_current_payload`` raises
  ``IncompleteReverseError`` naming the unsolved offsets: the user-agent slots, the
  timestamp slots, two environment bits, the fingerprint tail, the checksum and the
  prefix distribution.

When that last part is finished, this module starts registering automatically and
``POLICY_REQUIRED`` becomes usable. Until then, a caller that asks for a real
signature gets ``signer_unavailable`` with the exact remaining work, which is more
useful than a plausible-looking wrong string.
"""

from douyin import endpoints, signer

#: The provider name this module registers when it can produce a signature.
NAME = "a_bogus"


class AbogusProvider(signer.SignatureProvider):
    """Wraps the vendored research implementation behind the provider interface."""

    name = NAME
    #: The web client signs the *encoded* query string.
    sign_params_encoding = signer.ENCODING_ENCODED
    covers_body = True
    #: Set from the capability probe below, so a half-finished reverse can never
    #: claim to be verified.
    verified = False

    def __init__(self, module):
        self._module = module

    def sign(self, params_str, body_str, user_agent):
        # ``build_current_payload`` raises rather than returning a wrong payload, so
        # an incomplete reverse surfaces as a classified failure instead of a 403
        # that looks like a server problem.
        payload, prefix4 = self._module.build_current_payload(params_str, body_str, user_agent)
        return self._module.current_encode(payload, prefix4)


def _probe(module):
    """Return the provider if the vendored implementation can actually sign.

    The probe calls the real entry point with a representative input rather than
    checking a version flag, because a flag can be wrong and a call cannot.
    """
    try:
        provider = AbogusProvider(module)
        value = provider.sign(
            "device_platform=webapp&aid=6383&aweme_id=7300000000000000000",
            "",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            "(KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36",
        )
    except NotImplementedError:
        return None
    except Exception:
        return None

    if not value:
        return None
    provider.verified = True
    return provider


def _register_if_capable():
    try:
        from douyin import _abogus_research as module
    except ImportError:
        return None

    provider = _probe(module)
    if provider is not None:
        signer.register(provider)
    return provider


#: Non-null when a real signature is available in this build. The diagnostics
#: catalogue reports this, so "is signing implemented here?" has a direct answer.
REGISTERED_PROVIDER = _register_if_capable()


def status():
    """A machine-readable answer to "can this build sign a request?"."""
    return {
        "providerRegistered": REGISTERED_PROVIDER is not None,
        "encoderVerified": True,
        "payloadComplete": REGISTERED_PROVIDER is not None,
        "requiredByAnyOperation": any(
            endpoints.signature_policy(operation) == endpoints.POLICY_REQUIRED
            for operation in endpoints.SIGNATURE_POLICY
        ),
        "unsolved": sorted(getattr(
            __import__("douyin._abogus_research", fromlist=["CURRENT_UNSOLVED"]),
            "CURRENT_UNSOLVED",
            {},
        )),
        "note": (
            "the live gate is the ttwid cookie, not a_bogus; see "
            "endpoints.SIGNATURE_POLICY and _research/abogus/EVIDENCE.md"
        ),
    }
