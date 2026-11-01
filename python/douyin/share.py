# -*- coding: utf-8 -*-
"""Share-link normalization.

Input forms the client must accept, and what each means for the transport:

==============================================  ==========================
input                                           transport action
==============================================  ==========================
``v.douyin.com/XXXX/``                          follow redirects, then re-parse
``www.douyin.com/video/<id>``                   nothing, we have the id
``www.douyin.com/note/<id>``                    nothing (image post)
``www.douyin.com/discover?modal_id=<id>``       nothing
``www.iesdouyin.com/share/video/<id>/``         nothing
a bare ``<id>``                                 nothing
text containing any of the above                extract, then as above
==============================================  ==========================

Deliberately no HTML scraping: the redirect target carries the id, so resolution
is a redirect follow, and the id is re-parsed from the final URL. If a future
shape only exposes the id inside page markup, that belongs behind an explicit
adapter, not here.
"""

import re

from douyin.errors import DouyinError, KIND_INVALID_INPUT

#: Douyin aweme ids are long decimal strings. 15..20 digits covers the ids in
#: circulation without matching a phone number or a timestamp by accident.
_AWEME_ID = re.compile(r"^[0-9]{15,20}$")

#: A URL inside arbitrary pasted text (share captions, chat messages).
_URL_IN_TEXT = re.compile(r"https?://[A-Za-z0-9\-._~:/?#\[\]@!$&'()*+,;=%]+")

#: Path shapes that carry the id directly.
_PATH_PATTERNS = (
    re.compile(r"/video/(?P<id>[0-9]{15,20})"),
    re.compile(r"/note/(?P<id>[0-9]{15,20})"),
    re.compile(r"/share/video/(?P<id>[0-9]{15,20})"),
    re.compile(r"/share/note/(?P<id>[0-9]{15,20})"),
)

#: Query parameter shapes that carry the id.
_QUERY_PATTERNS = (
    re.compile(r"[?&]modal_id=(?P<id>[0-9]{15,20})"),
    re.compile(r"[?&]aweme_id=(?P<id>[0-9]{15,20})"),
    re.compile(r"[?&]vid=(?P<id>[0-9]{15,20})"),
)

_SHORT_LINK_HOSTS = ("v.douyin.com", "v.iesdouyin.com")

_KIND_ID = "aweme_id"
_KIND_REDIRECT = "redirect"
_KIND_UNSUPPORTED = "unsupported"


def extract_url(text):
    """Pull the first URL out of arbitrary text, if any."""
    match = _URL_IN_TEXT.search(text or "")
    return match.group(0) if match else None


def is_short_link(url):
    host = _host(url)
    return host in _SHORT_LINK_HOSTS


def _host(url):
    without_scheme = url.split("://", 1)[-1]
    return without_scheme.split("/", 1)[0].split("?", 1)[0].lower()


def parse(raw_input):
    """Normalize one share input.

    Returns a dict::

        {"kind": "aweme_id", "awemeId": "7...", "source": "url-path"}
        {"kind": "redirect", "nextUrl": "https://v.douyin.com/XXXX/", "awemeId": None}
        {"kind": "unsupported", "reason": "...", "input": "..."}

    ``unsupported`` is returned rather than raised: the caller decides whether a
    pasted link that is not a video is an error worth showing.
    """
    if raw_input is None:
        return {"kind": _KIND_UNSUPPORTED, "reason": "empty input", "input": None}

    text = str(raw_input).strip()
    if not text:
        return {"kind": _KIND_UNSUPPORTED, "reason": "empty input", "input": text}

    # 1. A bare id.
    if _AWEME_ID.match(text):
        return {"kind": _KIND_ID, "awemeId": text, "source": "bare-id", "input": text}

    # 2. A URL, either the whole input or embedded in text.
    url = text if text.startswith("http") else extract_url(text)
    if not url:
        return {
            "kind": _KIND_UNSUPPORTED,
            "reason": "no Douyin URL or aweme id found in the input",
            "input": text[:200],
        }

    # 3. Short links carry no id; the transport must follow the redirect.
    if is_short_link(url):
        return {
            "kind": _KIND_REDIRECT,
            "nextUrl": url,
            "awemeId": None,
            "source": "short-link",
            "input": text[:200],
        }

    # 4. Long links: pull the id from the path, then the query.
    for pattern in _PATH_PATTERNS:
        match = pattern.search(url)
        if match:
            return {
                "kind": _KIND_ID,
                "awemeId": match.group("id"),
                "source": "url-path",
                "input": text[:200],
            }
    for pattern in _QUERY_PATTERNS:
        match = pattern.search(url)
        if match:
            return {
                "kind": _KIND_ID,
                "awemeId": match.group("id"),
                "source": "url-query",
                "input": text[:200],
            }

    # 5. A trailing path segment that is just an id (some share shapes).
    tail = url.rstrip("/").rsplit("/", 1)[-1].split("?", 1)[0]
    if _AWEME_ID.match(tail):
        return {"kind": _KIND_ID, "awemeId": tail, "source": "url-tail", "input": text[:200]}

    if _host(url).endswith("douyin.com"):
        return {
            "kind": _KIND_UNSUPPORTED,
            "reason": "this Douyin link is not a video or note",
            "input": text[:200],
        }
    return {
        "kind": _KIND_UNSUPPORTED,
        "reason": "this is not a Douyin link",
        "input": text[:200],
    }


def parse_redirect(final_url):
    """Re-parse a URL after following redirects.

    Kept separate from :func:`parse` so the redirect-following loop in the host
    can call it with the *final* location and get an id without repeating the
    short-link branch (the final URL is never a short link).
    """
    result = parse(final_url)
    if result["kind"] == _KIND_REDIRECT:
        # A redirect that points at another redirect means the chain did not
        # terminate; report it instead of looping.
        return {
            "kind": _KIND_UNSUPPORTED,
            "reason": "redirect chain did not resolve to a video id",
            "input": final_url[:200],
        }
    return result


def require_aweme_id(raw_input):
    """Convenience for callers that need the id and nothing else."""
    result = parse(raw_input)
    if result["kind"] == _KIND_ID:
        return result["awemeId"]
    if result["kind"] == _KIND_REDIRECT:
        raise DouyinError(
            KIND_INVALID_INPUT,
            "share link must be resolved through the transport first",
            detail={"nextUrl": result["nextUrl"]},
        )
    raise DouyinError(
        KIND_INVALID_INPUT,
        result.get("reason", "not a Douyin video link"),
        detail={"input": result.get("input")},
    )
