# -*- coding: utf-8 -*-
"""Cookie jar with real scoping rules and secret redaction.

The jar is owned by :class:`douyin.identity.VirtualWebIdentity`, never by an
individual service. That is the invariant the brief calls out: a cookie must
never be sent under a fingerprint that did not obtain it.

Two rules are enforced here rather than left to callers:

* **Scoping** -- domain-match, path-match, ``Secure`` and expiry are applied the
  way a browser applies them, so we do not leak a ``sessionid`` to an unrelated
  host or keep a stale value after the server rotates it.
* **Redaction** -- the credential cookies are listed in :data:`SENSITIVE_NAMES`
  and every logging path goes through :meth:`CookieJar.redacted`, so a session
  identifier cannot reach a log, a crash report or the diagnostics page.
"""

from urllib.parse import urlparse

# Cookie names that must never be written to a log, a UI or a file that is not
# the private session store. Matched case-insensitively.
SENSITIVE_NAMES = frozenset(
    name.lower()
    for name in (
        "sessionid",
        "sessionid_ss",
        "sessionid_ss_ads",
        "sid_tt",
        "sid_guard",
        "sid_ucp_v1",
        "ssid_ucp_v1",
        "uid_tt",
        "uid_tt_ss",
        "sso_uid_tt",
        "sso_uid_tt_ss",
        "odin_tt",
        "passport_csrf_token",
        "passport_csrf_token_default",
        "passport_auth_status",
        "passport_auth_status_ss",
        "d_ticket",
        "ticket",
        "login_status",
        "p_login_status",
        "store_idc",
        "store_region",
        "multi_sids",
        "n_mh",
        "s_v_web_id",
    )
)


def is_sensitive(name):
    return name.lower() in SENSITIVE_NAMES


def redact_value(name, value):
    """Return a log-safe placeholder for a cookie value."""
    if value is None:
        return None
    if is_sensitive(name):
        return "<redacted:%d>" % len(value)
    return value


def _parse_set_cookie(header):
    """Parse one ``Set-Cookie`` value into ``(name, value, attrs)``.

    Written by hand instead of using ``http.cookies``: the server emits
    attributes we must honour verbatim (``Expires`` with a comma inside it,
    ``Max-Age``, repeated ``Domain``), and a permissive parser that silently
    drops them would produce exactly the stale-cookie bug this layer exists to
    prevent.
    """
    header = header.strip()
    if not header:
        return None
    parts = header.split(";")
    first = parts[0]
    if "=" not in first:
        return None
    name, _, value = first.partition("=")
    name = name.strip()
    if not name:
        return None
    value = value.strip()

    attrs = {}
    for raw in parts[1:]:
        item = raw.strip()
        if not item:
            continue
        key, _, val = item.partition("=")
        key = key.strip().lower()
        val = val.strip()
        if key in ("expires", "max-age", "domain", "path", "samesite"):
            attrs[key] = val
        elif key in ("secure", "httponly"):
            attrs[key] = True
    return name, value, attrs


def _domain_matches(host, cookie_domain):
    """RFC 6265 domain-match (with the leading dot ignored)."""
    if not cookie_domain:
        return True
    host = host.lower()
    domain = cookie_domain.lower().lstrip(".")
    if host == domain:
        return True
    return host.endswith("." + domain)


def _path_matches(request_path, cookie_path):
    if not cookie_path or cookie_path == "/":
        return True
    if request_path == cookie_path:
        return True
    if request_path.startswith(cookie_path):
        if cookie_path.endswith("/"):
            return True
        return request_path[len(cookie_path) : len(cookie_path) + 1] == "/"
    return False


class Cookie(object):
    __slots__ = (
        "name",
        "value",
        "domain",
        "path",
        "expires_at_s",
        "secure",
        "http_only",
        "host_only",
        "created_at_s",
    )

    def __init__(
        self,
        name,
        value,
        domain="",
        path="/",
        expires_at_s=None,
        secure=False,
        http_only=False,
        host_only=True,
        created_at_s=0,
    ):
        self.name = name
        self.value = value
        self.domain = domain or ""
        self.path = path or "/"
        self.expires_at_s = expires_at_s
        self.secure = bool(secure)
        self.http_only = bool(http_only)
        self.host_only = bool(host_only)
        self.created_at_s = created_at_s

    def is_expired(self, now_s):
        return self.expires_at_s is not None and self.expires_at_s <= now_s

    def matches(self, host, path, secure_ok):
        """RFC 6265 request-matching.

        ``host_only`` cookies store their originating host in ``domain`` as well
        (same trick browsers use), so that a jar entry created for
        ``www.douyin.com`` is never sent to ``sso.douyin.com``.
        """
        if self.secure and not secure_ok:
            return False
        if self.host_only:
            if self.domain and host.lower() != self.domain.lower():
                return False
        elif not _domain_matches(host, self.domain):
            return False
        return _path_matches(path, self.path)

    def to_json(self):
        return {
            "name": self.name,
            "value": self.value,
            "domain": self.domain,
            "path": self.path,
            "expiresAtS": self.expires_at_s,
            "secure": self.secure,
            "httpOnly": self.http_only,
            "hostOnly": self.host_only,
            "createdAtS": self.created_at_s,
        }

    @classmethod
    def from_json(cls, obj):
        return cls(
            name=obj.get("name", ""),
            value=obj.get("value", ""),
            domain=obj.get("domain", ""),
            path=obj.get("path", "/"),
            expires_at_s=obj.get("expiresAtS"),
            secure=obj.get("secure", False),
            http_only=obj.get("httpOnly", False),
            host_only=obj.get("hostOnly", True),
            created_at_s=obj.get("createdAtS", 0),
        )

    def __repr__(self):
        return "Cookie(%s=%s domain=%s path=%s)" % (
            self.name,
            redact_value(self.name, self.value),
            self.domain or "(host-only)",
            self.path,
        )


class CookieJar(object):
    """A browser-shaped cookie store.

    ``identity_id`` ties the jar to the fingerprint that created it; a mismatch
    is surfaced by ``VirtualWebIdentity.validate()`` instead of silently
    producing a session that looks logged in but is not.
    """

    def __init__(self, identity_id=None, cookies=None):
        self.identity_id = identity_id
        self._cookies = {}
        for cookie in cookies or ():
            self._store(cookie)

    # -- storage -----------------------------------------------------------
    def _key(self, cookie):
        return (cookie.domain.lstrip(".").lower(), cookie.path, cookie.name)

    def _store(self, cookie):
        self._cookies[self._key(cookie)] = cookie

    def set(self, name, value, domain="", path="/", expires_at_s=None, secure=False,
            http_only=False, host_only=True, now_s=0):
        cookie = Cookie(
            name=name,
            value=value,
            domain=domain,
            path=path,
            expires_at_s=expires_at_s,
            secure=secure,
            http_only=http_only,
            host_only=host_only,
            created_at_s=now_s,
        )
        self._store(cookie)
        return cookie

    # -- ingestion ---------------------------------------------------------
    def ingest_set_cookies(self, set_cookie_headers, request_url, now_s):
        """Apply every ``Set-Cookie`` from one response.

        Returns the list of cookie names that actually changed, so the caller can
        log *that* a session-bearing cookie rotated without logging its value.
        """
        parsed_url = urlparse(request_url)
        host = (parsed_url.hostname or "").lower()
        request_path = parsed_url.path or "/"
        secure_ok = parsed_url.scheme == "https"
        changed = []

        for header in set_cookie_headers or ():
            parsed = _parse_set_cookie(header)
            if parsed is None:
                continue
            name, value, attrs = parsed

            declared_domain = attrs.get("domain", "")
            host_only = not declared_domain
            if declared_domain:
                domain = declared_domain.lstrip(".").lower()
                if not _domain_matches(host, domain):
                    # A cookie for an unrelated domain is not ours to keep.
                    continue
            else:
                # Host-only cookie: remember the host so scoping still works.
                domain = host

            path = attrs.get("path") or _default_path(request_path)

            expires_at_s = None
            if "max-age" in attrs:
                try:
                    seconds = int(attrs["max-age"])
                    expires_at_s = now_s + seconds if seconds > 0 else now_s - 1
                except (TypeError, ValueError):
                    expires_at_s = None
            elif "expires" in attrs:
                expires_at_s = _parse_http_date(attrs["expires"])

            secure = bool(attrs.get("secure"))

            # A value of "" with a past expiry is the server's delete signal.
            if value == "" and expires_at_s is not None and expires_at_s <= now_s:
                key = (domain.lower(), path, name)
                if key in self._cookies:
                    del self._cookies[key]
                    changed.append(name)
                continue

            cookie = Cookie(
                name=name,
                value=value,
                domain=domain,
                path=path,
                expires_at_s=expires_at_s,
                secure=secure,
                http_only=bool(attrs.get("httponly")),
                host_only=host_only,
                created_at_s=now_s,
            )
            self._store(cookie)
            changed.append(name)

        return changed

    # -- reading -----------------------------------------------------------
    def purge_expired(self, now_s):
        dead = [key for key, cookie in self._cookies.items() if cookie.is_expired(now_s)]
        for key in dead:
            del self._cookies[key]
        return len(dead)

    def get(self, name, now_s=0):
        best = None
        for cookie in self._cookies.values():
            if cookie.name != name:
                continue
            if cookie.is_expired(now_s):
                continue
            if best is None or len(cookie.path) > len(best.path):
                best = cookie
        return best

    def value(self, name, now_s=0):
        cookie = self.get(name, now_s)
        return cookie.value if cookie is not None else None

    def cookie_header(self, url, now_s, extra_pairs=None):
        """Build the ``Cookie`` header for ``url``.

        ``extra_pairs`` lets the identity contribute parameters that are not
        stored cookies (``msToken`` and ``verifyFp`` are query/cookie hybrids on
        the real site).
        """
        parsed = urlparse(url)
        host = (parsed.hostname or "").lower()
        path = parsed.path or "/"
        secure_ok = parsed.scheme == "https"

        selected = {}
        for cookie in self._cookies.values():
            if cookie.is_expired(now_s):
                continue
            if not cookie.matches(host, path, secure_ok):
                continue
            previous = selected.get(cookie.name)
            if previous is None or len(cookie.path) > len(previous.path):
                selected[cookie.name] = cookie

        for name, value in extra_pairs or ():
            if name not in selected and value is not None:
                selected[name] = Cookie(name, value)

        ordered = sorted(selected.values(), key=lambda c: (len(c.path), c.name))
        return "; ".join("%s=%s" % (c.name, c.value) for c in ordered)

    def names(self):
        return sorted(cookie.name for cookie in self._cookies.values())

    def has_any(self, names, now_s=0):
        for name in names:
            cookie = self.get(name, now_s)
            if cookie is not None and cookie.value:
                return True
        return False

    def count(self):
        return len(self._cookies)

    # -- maintenance -------------------------------------------------------
    def merge(self, other, prefer_other=True):
        """Merge another jar in, newest-wins by default."""
        for cookie in other._cookies.values():
            key = self._key(cookie)
            existing = self._cookies.get(key)
            if existing is None:
                self._cookies[key] = cookie
            elif prefer_other or cookie.created_at_s >= existing.created_at_s:
                self._cookies[key] = cookie
        return self

    def cleared(self):
        return CookieJar(self.identity_id)

    def remove(self, name):
        dead = [key for key, cookie in self._cookies.items() if cookie.name == name]
        for key in dead:
            del self._cookies[key]
        return len(dead)

    # -- serialisation -----------------------------------------------------
    def to_json(self):
        return {
            "identityId": self.identity_id,
            "cookies": [cookie.to_json() for cookie in self._cookies.values()],
        }

    @classmethod
    def from_json(cls, obj):
        if not obj:
            return cls()
        return cls(
            identity_id=obj.get("identityId"),
            cookies=[Cookie.from_json(item) for item in obj.get("cookies") or ()],
        )

    def redacted(self, now_s=0):
        """Log-safe view. Never contains a credential value."""
        return {
            "identityId": self.identity_id,
            "count": self.count(),
            "cookies": [
                {
                    "name": cookie.name,
                    "value": redact_value(cookie.name, cookie.value),
                    "domain": cookie.domain,
                    "path": cookie.path,
                    "expiresAtS": cookie.expires_at_s,
                    "expired": cookie.is_expired(now_s),
                }
                for cookie in sorted(self._cookies.values(), key=lambda c: c.name)
            ],
        }


def _default_path(request_path):
    """RFC 6265 default-path algorithm."""
    if not request_path or not request_path.startswith("/"):
        return "/"
    index = request_path.rfind("/")
    if index <= 0:
        return "/"
    return request_path[:index]


_MONTHS = {
    "jan": 1, "feb": 2, "mar": 3, "apr": 4, "may": 5, "jun": 6,
    "jul": 7, "aug": 8, "sep": 9, "oct": 10, "nov": 11, "dec": 12,
}


def _parse_http_date(text):
    """Parse an ``Expires`` attribute to an epoch second, or None if unreadable.

    Deliberately small: the only consumer is cookie expiry, and returning None
    (treat as session cookie) is safer than guessing.
    """
    if not text:
        return None
    cleaned = text.replace(",", " ").replace("-", " ").replace(":", " ").split()
    if len(cleaned) < 5:
        return None
    try:
        # Either "Wed 21 Oct 2015 07:28:00 GMT" or "21 Oct 2015 07:28:00 GMT"
        offset = 0
        if not cleaned[0][0].isdigit():
            offset = 1
        day = int(cleaned[offset])
        month = _MONTHS.get(cleaned[offset + 1][:3].lower())
        year = int(cleaned[offset + 2])
        hour = int(cleaned[offset + 3])
        minute = int(cleaned[offset + 4])
        second = int(cleaned[offset + 5]) if len(cleaned) > offset + 5 else 0
        if month is None:
            return None
    except (ValueError, IndexError):
        return None

    import calendar

    try:
        return int(calendar.timegm((year, month, day, hour, minute, second, 0, 0, 0)))
    except (ValueError, OverflowError):
        return None
