# -*- coding: utf-8 -*-
"""The persistent virtual browser identity.

The brief is explicit about this and so is the failure mode it prevents: a
request must never look like a Lumia, and identity / user-agent / signature /
cookies must belong to *one* browser. Concretely, three invariants hold here:

1. **The profile is not the device.** The user agent describes a desktop Chrome
   on Windows 10 x64. Nothing derived from the phone (its UA, its real screen,
   its real locale host) reaches the wire.
2. **One session, one identity, for a long time.** ``webid``, ``verifyFp``,
   ``msToken`` seed and the cookie jar are persisted together and only rotate
   deliberately -- never per request.
3. **Locally generated vs server issued is tracked.** ``ttwid`` and ``uifid``
   are *server issued*; we start them empty and let the transport fill them from
   ``Set-Cookie``. Inventing values for them would be a fingerprint mismatch,
   which is exactly what the brief forbids.

Locally generated values (``webid``, ``verifyFp``/``s_v_web_id``, ``msToken``)
are shape-faithful but **not** server-validated. Where the real value can be
observed, prefer importing it (see ``AccountService.ImportCookieSession``);
:meth:`VirtualWebIdentity.validate` reports what is still synthetic.
"""

from urllib.parse import urlparse

from douyin.cookies import CookieJar
from douyin.util import md5_hex, to_bytes

IDENTITY_VERSION = 1

#: How long a locally minted ``msToken`` stays in use. The real site rotates it
#: roughly per browsing burst; a short but not per-request lifetime is the
#: behaviour we imitate.
MSTOKEN_TTL_MS = 10 * 60 * 1000

#: ``verifyFp``/``s_v_web_id`` is a long-lived device fingerprint on the real
#: site, so it rotates on the scale of weeks, not minutes.
VERIFY_FP_TTL_MS = 14 * 24 * 60 * 60 * 1000

#: The virtual browser profile. Calibration point: only the platform and browser
#: tokens are consumed by the signer's user-agent lookup; the version numbers
#: are here so the identity is internally coherent. If risk control ever
#: complains, change this one constant rather than scattering a UA string.
DEFAULT_PROFILE = {
    "profileName": "desktop-chrome-win10-x64",
    "browserName": "Chrome",
    "browserVersion": "138.0.0.0",
    "browserMajor": "138",
    "engineName": "Blink",
    "engineVersion": "138.0.0.0",
    "platform": "Win32",
    "osName": "Windows",
    "osVersion": "10.0",
    "architecture": "x86_64",
    "deviceModel": "",
    "screenWidth": 1920,
    "screenHeight": 1080,
    "availWidth": 1920,
    "availHeight": 1032,
    "colorDepth": 24,
    "pixelRatio": 1.0,
    "viewportWidth": 1280,
    "viewportHeight": 720,
    "language": "zh-CN",
    "languages": ["zh-CN", "zh", "en"],
    "timezoneName": "Asia/Shanghai",
    "timezoneOffsetMinutes": -480,
    "hardwareConcurrency": 8,
    "deviceMemory": 8,
    "webglVendor": "Google Inc. (NVIDIA)",
    "webglRenderer": "ANGLE (NVIDIA, NVIDIA GeForce GTX 1060 Direct3D11 vs_5_0 ps_5_0)",
    "platformId": "webapp",
    "aid": 6383,
}


def build_user_agent(profile):
    """Assemble the UA from the profile so it can never drift out of sync."""
    return (
        "Mozilla/5.0 (Windows NT %s; Win64; x64) AppleWebKit/537.36 "
        "(KHTML, like Gecko) %s/%s Safari/537.36"
        % (profile["osVersion"], profile["browserName"], profile["browserVersion"])
    )


def _digits(clock, count):
    """A numeric string whose first digit is never '0'."""
    out = [str(clock.randint(1, 9))]
    for _ in range(count - 1):
        out.append(str(clock.randint(0, 9)))
    return "".join(out)


_ALNUM_LOWER = "abcdefghijklmnopqrstuvwxyz0123456789"
_ALNUM_MIXED = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
_MSTOKEN_ALPHABET = (
    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_"
)


def _pick(clock, alphabet, count):
    size = len(alphabet)
    return "".join(alphabet[clock.randint(0, size - 1)] for _ in range(count))


def new_webid(clock):
    """Douyin's ``webid`` is a 19-digit numeric identifier.

    Composed as 13 digits of the current millisecond plus 6 random digits, which
    mirrors how the real value behaves: it is time-ordered, and two browsers that
    start in the same second still differ. It also means a reset always yields a new
    id even when the PRNG is deterministic, which is what makes ``ResetIdentity``
    testable rather than a coin flip.
    """
    stamp = str(clock.now_ms())
    if len(stamp) > 13:
        stamp = stamp[:13]
    return stamp + _pick(clock, "0123456789", 19 - len(stamp))


def new_verify_fp(clock):
    """``verifyFp`` / ``s_v_web_id``.

    Shape: ``verify_<8 [a-z0-9]>_<8 mixed>_<4 mixed>_<4 mixed>``.

    This is the observed shape, not a value we can validate offline: the server
    decides whether it likes the fingerprint. A real value imported from a
    logged-in desktop browser takes precedence whenever one is available.
    """
    return "verify_%s_%s_%s_%s" % (
        _pick(clock, _ALNUM_LOWER, 8),
        _pick(clock, _ALNUM_MIXED, 8),
        _pick(clock, _ALNUM_MIXED, 4),
        _pick(clock, _ALNUM_MIXED, 4),
    )


def new_ms_token(clock, length=128):
    """``msToken``: a long base64url-shaped random token minted client side."""
    return _pick(clock, _MSTOKEN_ALPHABET, length)


class VirtualWebIdentity(object):
    """Persistent virtual browser identity plus the cookie jar that belongs to it."""

    __slots__ = (
        "version",
        "identityId",
        "profileName",
        "userAgent",
        "browserName",
        "browserVersion",
        "browserMajor",
        "engineName",
        "engineVersion",
        "platform",
        "osName",
        "osVersion",
        "architecture",
        "deviceModel",
        "screenWidth",
        "screenHeight",
        "availWidth",
        "availHeight",
        "colorDepth",
        "pixelRatio",
        "viewportWidth",
        "viewportHeight",
        "language",
        "languages",
        "timezoneName",
        "timezoneOffsetMinutes",
        "hardwareConcurrency",
        "deviceMemory",
        "webglVendor",
        "webglRenderer",
        "platformId",
        "aid",
        "webid",
        "verifyFp",
        "sVWebId",
        "verifyFpCreatedAtMs",
        "msToken",
        "msTokenCreatedAtMs",
        "ttwid",
        "ttwidObtainedAtMs",
        "uifid",
        "uifidObtainedAtMs",
        "cookies",
        "createdAtMs",
        "updatedAtMs",
    )

    def __init__(self, **fields):
        for name in self.__slots__:
            setattr(self, name, fields.get(name))
        if self.cookies is None:
            self.cookies = CookieJar(self.identityId)
        if self.languages is None:
            self.languages = list(DEFAULT_PROFILE["languages"])

    # -- construction ------------------------------------------------------
    @classmethod
    def create(cls, clock, profile=None):
        profile = dict(DEFAULT_PROFILE, **(profile or {}))
        identity = cls()
        identity.version = IDENTITY_VERSION
        identity.identityId = new_webid(clock)
        for name in (
            "profileName",
            "browserName",
            "browserVersion",
            "browserMajor",
            "engineName",
            "engineVersion",
            "platform",
            "osName",
            "osVersion",
            "architecture",
            "deviceModel",
            "screenWidth",
            "screenHeight",
            "availWidth",
            "availHeight",
            "colorDepth",
            "pixelRatio",
            "viewportWidth",
            "viewportHeight",
            "language",
            "languages",
            "timezoneName",
            "timezoneOffsetMinutes",
            "hardwareConcurrency",
            "deviceMemory",
            "webglVendor",
            "webglRenderer",
            "platformId",
            "aid",
        ):
            setattr(identity, name, profile[name])
        identity.userAgent = build_user_agent(profile)
        identity.webid = new_webid(clock)
        identity.verifyFp = new_verify_fp(clock)
        identity.sVWebId = identity.verifyFp
        identity.verifyFpCreatedAtMs = clock.now_ms()
        identity.msToken = new_ms_token(clock)
        identity.msTokenCreatedAtMs = clock.now_ms()
        # Server issued: never invented.
        identity.ttwid = ""
        identity.ttwidObtainedAtMs = 0
        identity.uifid = ""
        identity.uifidObtainedAtMs = 0
        identity.createdAtMs = clock.now_ms()
        identity.updatedAtMs = identity.createdAtMs
        identity.cookies = CookieJar(identity.identityId)
        return identity

    @classmethod
    def from_json(cls, obj, clock):
        if not obj:
            return cls.create(clock)
        identity = cls()
        for name in cls.__slots__:
            if name == "cookies":
                identity.cookies = CookieJar.from_json(obj.get("cookies"))
            elif name in obj:
                setattr(identity, name, obj[name])
        if identity.cookies is None:
            identity.cookies = CookieJar(identity.identityId)
        # A jar that lost or never had its owner id adopts the identity's, but
        # only when it is empty; a *mismatched non-empty* jar is a real fault and
        # must survive to validate() instead of being papered over.
        if identity.cookies.identity_id is None:
            identity.cookies.identity_id = identity.identityId
        if identity.version is None:
            identity.version = IDENTITY_VERSION
        return identity

    def to_json(self):
        out = {}
        for name in self.__slots__:
            if name == "cookies":
                out["cookies"] = self.cookies.to_json()
            else:
                out[name] = getattr(self, name)
        return out

    # -- lifecycle ---------------------------------------------------------
    def reset(self, clock):
        """Full reset: a brand new browser, discarding the old session."""
        fresh = VirtualWebIdentity.create(clock)
        for name in self.__slots__:
            setattr(self, name, getattr(fresh, name))
        return self

    def touch(self, clock):
        self.updatedAtMs = clock.now_ms()

    def rotate_ms_token(self, clock):
        self.msToken = new_ms_token(clock)
        self.msTokenCreatedAtMs = clock.now_ms()
        self.touch(clock)
        return self.msToken

    def rotate_verify_fp(self, clock):
        self.verifyFp = new_verify_fp(clock)
        self.sVWebId = self.verifyFp
        self.verifyFpCreatedAtMs = clock.now_ms()
        self.touch(clock)
        return self.verifyFp

    def ensure_fresh(self, clock):
        """Rotate only the sub-fields that are designed to expire."""
        now = clock.now_ms()
        rotated = []
        if not self.msToken or now - (self.msTokenCreatedAtMs or 0) > MSTOKEN_TTL_MS:
            self.rotate_ms_token(clock)
            rotated.append("msToken")
        if not self.verifyFp or now - (self.verifyFpCreatedAtMs or 0) > VERIFY_FP_TTL_MS:
            self.rotate_verify_fp(clock)
            rotated.append("verifyFp")
        if rotated:
            self.touch(clock)
        return rotated

    def ingest_set_cookies(self, set_cookie_headers, request_url, now_s):
        """Adopt server-issued cookies and the values derived from them.

        ``ttwid``, ``uifid`` and ``s_v_web_id`` become authoritative here, from the
        server, which is the only legitimate source for them. A locally minted
        ``s_v_web_id`` is a placeholder; once the real one arrives it must replace
        the placeholder *and* the jar, or the signature and the cookie header would
        disagree about which fingerprint this browser is.
        """
        changed = self.cookies.ingest_set_cookies(set_cookie_headers, request_url, now_s)

        mirrors = (
            ("ttwid", "ttwid", "ttwidObtainedAtMs"),
            ("uifid", "uifid", "uifidObtainedAtMs"),
            ("s_v_web_id", "sVWebId", "verifyFpCreatedAtMs"),
        )
        for cookie_name, attribute, stamp_attribute in mirrors:
            cookie = self.cookies.get(cookie_name, now_s)
            if cookie is None or not cookie.value or cookie.value == getattr(self, attribute):
                continue
            setattr(self, attribute, cookie.value)
            setattr(self, stamp_attribute, now_s * 1000)
            if cookie_name == "s_v_web_id":
                # verifyFp and s_v_web_id are the same value under two names.
                self.verifyFp = cookie.value

        if changed:
            self.updatedAtMs = now_s * 1000
        return changed

    # -- derived request material -----------------------------------------
    def cookie_extras(self, now_s=None, host=None):
        """Parameters the real site passes as cookies without storing them.

        ``msToken`` and ``s_v_web_id`` behave this way on douyin.com.

        Scoped to the *request's* host, not the identity's nominal host: these are
        fingerprint material, and sending them to an unrelated origin would hand a
        third party the exact values the signature is computed from.
        """
        target = (host or self.host() or "").lower()
        if not _is_bytedance_host(target):
            return []

        pairs = []
        if self.msToken:
            pairs.append(("msToken", self.msToken))
        if self.sVWebId or self.verifyFp:
            pairs.append(("s_v_web_id", self.sVWebId or self.verifyFp))
        if self.ttwid:
            pairs.append(("ttwid", self.ttwid))
        return pairs

    def cookie_header(self, url, now_s):
        return self.cookies.cookie_header(url, now_s, self.cookie_extras(now_s, host_of(url)))

    def signer_user_agent(self):
        """The UA the signature must be computed against (always this profile)."""
        return self.userAgent

    def host(self):
        return "www.douyin.com"

    # -- integrity ---------------------------------------------------------
    def validate(self):
        """Report self-consistency problems instead of hiding them.

        Returns ``(problems, warnings)``. Problems are hard contradictions that
        would produce a fingerprint mismatch on the wire; warnings are things
        that are synthetic-but-acceptable for a guest session.
        """
        problems = []
        warnings = []

        if not self.userAgent:
            problems.append("userAgent is empty")
        else:
            expected = build_user_agent(
                {
                    "osVersion": self.osVersion,
                    "browserName": self.browserName,
                    "browserVersion": self.browserVersion,
                }
            )
            if expected != self.userAgent:
                problems.append("userAgent does not match the browser profile fields")

        if self.cookies.identity_id and self.cookies.identity_id != self.identityId:
            problems.append(
                "cookie jar belongs to identity %s but identity is %s"
                % (self.cookies.identity_id, self.identityId)
            )

        if not self.webid:
            problems.append("webid is missing")
        if not self.verifyFp:
            problems.append("verifyFp is missing")

        if self.platform and "Windows" not in self.userAgent:
            problems.append("platform/userAgent disagree about the operating system")

        if not self.msToken:
            warnings.append("msToken is missing; requests may be dropped silently")
        if not self.ttwid:
            warnings.append("ttwid has not been issued by the server yet")
        if not self.uifid:
            warnings.append("uifid has not been issued by the server yet")
        warnings.append(
            "webid/verifyFp/msToken are locally minted shapes, not server-validated values"
        )

        return problems, warnings

    def redacted(self, now_s=0):
        """Log-safe view: no cookie values, no whole cookie header."""
        return {
            "identityId": self.identityId,
            "profileName": self.profileName,
            "userAgent": self.userAgent,
            "platform": self.platform,
            "viewport": [self.viewportWidth, self.viewportHeight],
            "screen": [self.screenWidth, self.screenHeight],
            "language": self.language,
            "timezone": self.timezoneName,
            "webid": self.webid,
            "verifyFp": self.verifyFp,
            "msTokenLength": len(self.msToken or ""),
            "msTokenFingerprint": _short_hash(self.msToken),
            "ttwid": "present" if self.ttwid else "absent",
            "uifid": "present" if self.uifid else "absent",
            "cookieCount": self.cookies.count(),
            "createdAtMs": self.createdAtMs,
            "updatedAtMs": self.updatedAtMs,
        }


def _short_hash(value):
    """A stable, non-reversible tag for a secret-ish value."""
    if not value:
        return None
    return md5_hex(to_bytes(value))[:8]


def host_of(url):
    return (urlparse(url).hostname or "").lower()


#: Hosts that belong to the platform we are imitating. Fingerprint extras
#: (``msToken``/``s_v_web_id``/``ttwid``) are only ever sent to these.
_BYTEDANCE_SUFFIXES = (
    "douyin.com",
    "iesdouyin.com",
    "bytedance.com",
    "byteoversea.com",
    "ibytedtos.com",
    "snssdk.com",
    "amemv.com",
)


def _is_bytedance_host(host):
    if not host:
        return False
    for suffix in _BYTEDANCE_SUFFIXES:
        if host == suffix or host.endswith("." + suffix):
            return True
    return False
