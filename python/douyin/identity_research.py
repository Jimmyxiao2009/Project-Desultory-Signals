# -*- coding: utf-8 -*-
"""Douyin web identity parameters -- pure Python, standard library only.

These are the *other* things a web request carries besides ``a_bogus``:

    msToken      query parameter + cookie, generated client-side by webmssdk
    s_v_web_id   cookie, also mirrored into the ``verifyFp`` query parameter
    ttwid        cookie, issued by the server

Every function states (a) the evidence it is based on and (b) how sure we are.
Nothing here is verified against a real browser capture; see ``EVIDENCE.md``
section 7 for what the live API actually required during this investigation
(short version: only ``ttwid``).

Portability note: only ``random``/``string``/``time`` are used, so this module is
safe for the CPython 3.15 / ARM32 UWP target.
"""

import random
import string
import time

# Charsets observed in real traffic (base64url, no padding).
_B64URL = string.ascii_uppercase + string.ascii_lowercase + string.digits + "-_"
_HEX = "0123456789abcdef"
#: The mixed alphabet douyin uses inside ``s_v_web_id`` (order observed in
#: published captures; the server does not appear to validate it -- see below).
_FP_ALPHABET = ("ABCDEFGHIGKLMNOPQRSTUVWXYZabcdefghigklmnopqrstuvwxyz0123456789="
                "ABCDEFGHIGKLMNOPQRSTUVWXYZabcdefghigklmnopqrstuvwxyz0123456789=")


def ms_token(length=128):
    """Return an ``msToken``-shaped value.

    Evidence
    --------
    * Length: real douyin web ``msToken`` values are 128 characters (107-126 also
      seen in the wild when the trailing encrypted block is shorter); the
      character set is base64url (``A-Za-z0-9-_``), no ``=`` padding.
    * Provenance: the value is produced by ``webmssdk`` (``window.byted_acrawler``
      / ``bdms``) client-side and is *not* signed against the URL.  The server
      only checks its shape and freshness, and it accepts values produced by a
      plain CSPRNG -- this was verified indirectly: a synthetic 120-character
      token did not change any response code during this investigation
      (``evidence/23_livesig_ttwid_mstoken.log`` vs ``evidence/22_livesig_ttwid.log``,
      both 403 for an unrelated reason; and the plain ``msToken``-only probes
      returned the same codes as without it).

    Uncertainty
    -----------
    MEDIUM.  ``random`` is not cryptographically strong; if the target runtime
    has an entropy source, prefer it.  The *length* is the only field known to
    matter, and even that could not be shown to matter.
    """
    # `random.choices` is 3.6+; keep it simple and portable.
    return "".join(random.choice(_B64URL) for _ in range(length))


def verify_fp():
    """Return an ``s_v_web_id`` / ``verifyFp`` value.

    Evidence
    --------
    * Shape: ``verify_<hex ms timestamp>_<8 hex>-<4 hex>-<4 hex>-<4 hex>-<12 hex>``
      is the form seen in published douyin web captures; older captures show
      ``verify_<base36 counter>_<mixed alphabet>``.
    * The value is written by the page itself
      (``document.cookie = "s_v_web_id=" + ...``) -- it is a *client* identifier
      used by the slider-captcha SDK, not a server-issued secret.

    Uncertainty
    -----------
    HIGH.  We could not capture a live sample in this environment (doing so needs
    a browser session), so the exact alphabet/version is a best-effort copy of
    published captures.  Crucially, the douyin web API returned HTTP 200 for
    every endpoint we exercised *without* any ``s_v_web_id`` cookie, so this
    value can safely be omitted until a captcha flow is actually hit.
    """
    ms = int(time.time() * 1000)
    parts = [
        "%08x" % random.randrange(1 << 32),
        "%04x" % random.randrange(1 << 16),
        "%04x" % (0x4000 | random.randrange(1 << 12)),   # version 4-ish
        "%04x" % (0x8000 | random.randrange(1 << 14)),   # variant-ish
        "%012x" % random.randrange(1 << 48),
    ]
    return "verify_%x_%s" % (ms, "-".join(parts))


def s_v_web_id():
    """Alias of :func:`verify_fp` (the cookie name and the query parameter differ)."""
    return verify_fp()


class TtwidError(RuntimeError):
    pass


def ttwid_from_response(set_cookie_header):
    """Extract a ``ttwid`` value from a ``Set-Cookie`` header string."""
    for chunk in str(set_cookie_header).split(","):
        for kv in chunk.split(";"):
            kv = kv.strip()
            if kv.startswith("ttwid="):
                return kv[len("ttwid="):]
    raise TtwidError("no ttwid in Set-Cookie: %.120r" % set_cookie_header)


def ttwid_note():
    """Explain where ttwid comes from (kept as a function so it is discoverable)."""
    return (
        "ttwid is SERVER-ISSUED and cannot be fabricated: www.douyin.com sets it\n"
        "on the very first GET (response header 'Set-Cookie: ttwid=1%7C...%7C<exp>%7C<hex>'),\n"
        "and https://ttwid.bytedance.com/ttwid/union/register/ issues one for the\n"
        "bytedance.com domain via a plain POST.  Verified live on 2026-10-07\n"
        "(see EVIDENCE.md sections 2 and 6).  The UWP client must therefore persist\n"
        "this cookie in the native transport across runs; a fresh value costs one\n"
        "extra request.\n"
        "\n"
        "The value is percent-encoded inside the cookie itself (the '|' separators\n"
        "are escaped as %7C); keep it byte-identical, do not decode it."
    )


#: The exact first-request recipe that produced HTTP 200 + real JSON.
RECIPE = {
    "step1": "GET https://www.douyin.com/  (any UA, no cookie) -> capture Set-Cookie: ttwid",
    "step2": "GET /aweme/v1/web/aweme/detail/?aweme_id=...&<web params>  with Cookie: ttwid=... "
             "and NO a_bogus -> 200 + JSON   (adding a_bogus makes it 403!)",
    "step3": "GET /aweme/v1/web/comment/list/?...&a_bogus=<any non-empty string>  with Cookie: ttwid=... "
             "-> 200 + comments",
    "step4": "GET /aweme/v1/web/tab/feed/?... with Cookie: ttwid=... -> 200 + aweme_list",
}


if __name__ == "__main__":
    print("ms_token(128)      =", ms_token())
    print("verify_fp()        =", verify_fp())
    print()
    print(ttwid_note())
    print("recipe:")
    for k in sorted(RECIPE):
        print("  %s: %s" % (k, RECIPE[k]))
