# -*- coding: utf-8 -*-
"""End-to-end online gate check, driving the real protocol layer over the real API.

This is the Phase 1 gate ("input an aweme id, get real JSON back") executed on a
desktop interpreter, plus the Phase 2 and 5 gates. It is the same code path the
device uses -- ``request_build`` -> transport -> ``response_classify`` ->
``response_*`` -- with a standard-library transport standing in for
``Windows.Web.Http``.

Run:
    python python/tests/online_gate.py
    python python/tests/online_gate.py --aweme-id 7300000000000000000

Requires a proxy if the network needs one; honours HTTP_PROXY/HTTPS_PROXY.

Every step prints the raw HTTP status and a bounded body prefix, so a failure is
diagnosable from the output alone. Nothing here asserts on a risk-control outcome:
those depend on the network path and on Douyin, not on this code. What it asserts is
the shape of a success.
"""

import json
import os
import sys
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from douyin import protocol  # noqa: E402
from douyin.util import to_text  # noqa: E402

CLOCK = {"seed": 20261007, "fixedNowMs": 1700000000000}

_failures = []
_steps = []


def step(name, ok, detail=None):
    _steps.append((name, bool(ok), detail))
    print("  [%s] %s%s" % ("PASS" if ok else "FAIL", name,
                           "" if ok or detail is None else "  -- " + str(detail)[:300]))
    if not ok:
        _failures.append(name)


def call(op, **fields):
    request = {"op": op, "clock": CLOCK}
    request.update(fields)
    return json.loads(protocol.handle_json(json.dumps(request, ensure_ascii=False)))


def _opener():
    proxy = os.environ.get("HTTPS_PROXY") or os.environ.get("HTTP_PROXY")
    if proxy:
        return urllib.request.build_opener(
            urllib.request.ProxyHandler({"http": proxy, "https": proxy}))
    return urllib.request.build_opener(urllib.request.ProxyHandler({}))


def send(opener, plan, tag):
    """Send a built plan through a plain urllib transport. Returns (status, body, headers)."""
    request = urllib.request.Request(plan["url"])
    for name, value in plan.get("headers") or ():
        request.add_header(name, value)
    cookie = plan.get("cookie")
    if cookie:
        request.add_header("Cookie", cookie)
    body = plan.get("body")
    if body:
        request.data = body.encode("utf-8")

    try:
        with opener.open(request, timeout=30) as response:
            return response.status, response.read().decode("utf-8", "replace"), response.headers
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", "replace"), error.headers
    except Exception as error:  # noqa: BLE001
        return None, "transport error: %s: %s" % (type(error).__name__, error), None


def main():
    print("=" * 74)
    print("Desultory Signals - online gate check")
    print("python %s" % sys.version.replace("\n", " "))
    print("=" * 74)

    aweme_id = None
    for index, value in enumerate(sys.argv):
        if value == "--aweme-id" and index + 1 < len(sys.argv):
            aweme_id = sys.argv[index + 1]

    opener = _opener()

    # -- 0. identity ---------------------------------------------------------
    print("")
    print("-- identity")
    created = call("identity.create")
    step("identity.create succeeds", created.get("ok"), created.get("error"))
    identity = created["result"]["identity"]
    print("     identityId=%s" % identity["identityId"])
    print("     userAgent=%s" % identity["userAgent"])

    # -- 1. guest session: the ttwid is the actual gate ----------------------
    print("")
    print("-- guest session (ttwid)")
    bootstrap = call("request.buildPlain", identity=identity,
                     url="https://www.douyin.com/", method="GET")
    step("the home-page request builds", bootstrap.get("ok"), bootstrap.get("error"))
    status, body, headers = send(opener, bootstrap["result"]["plan"], "home")
    print("     GET https://www.douyin.com/ -> HTTP %s, %d bytes" % (status, len(body)))

    set_cookies = []
    if headers is not None:
        try:
            set_cookies = headers.get_all("Set-Cookie") or []
        except Exception:
            set_cookies = []
    print("     Set-Cookie headers: %d" % len(set_cookies))
    for cookie in set_cookies[:6]:
        print("       %s" % cookie.split(";")[0][:110])

    if set_cookies:
        applied = call("identity.applyCookies", identity=identity,
                       requestUrl="https://www.douyin.com/",
                       setCookie=set_cookies)
        identity = applied["result"]["identity"]
        print("     changed: %s" % applied["result"]["changed"])

    if not identity.get("ttwid"):
        # Fall back to the union registration endpoint, which issues one over a plain
        # POST, then follow the callback it hands back. Measured 2026-10-07: the
        # callback is a real part of the handshake, not a formality -- following it
        # cleared the Argus gate that had been refusing the detail endpoint.
        print("     no ttwid from the home page; trying the union registration endpoint")
        register = call("request.buildPlain", identity=identity,
                        url="https://ttwid.bytedance.com/ttwid/union/register/", method="POST")
        register_plan = register["result"]["plan"]
        register_plan["body"] = json.dumps({
            "region": "cn", "aid": 1768, "needFid": False,
            "service": "www.douyin.com", "migrate_info": {"ticket": "", "source": "node"},
            "cbUrlProtocol": "https", "union": True,
        })
        status2, body2, headers2 = send(opener, register_plan, "ttwid-register")
        print("     POST ttwid/union/register -> HTTP %s, %d bytes" % (status2, len(body2)))

        callback = None
        if body2.strip().startswith("{"):
            try:
                callback = json.loads(body2).get("redirect_url")
            except ValueError:
                callback = None

        if headers2 is not None:
            try:
                more = headers2.get_all("Set-Cookie") or []
            except Exception:
                more = []
            if more:
                applied2 = call("identity.applyCookies", identity=identity,
                                requestUrl="https://ttwid.bytedance.com/",
                                setCookie=more)
                identity = applied2["result"]["identity"]

        if callback:
            print("     following the callback: %s" % callback[:110])
            cb_plan = call("request.buildPlain", identity=identity, url=callback, method="GET")
            status3, body3, headers3 = send(opener, cb_plan["result"]["plan"], "ttwid-callback")
            print("     GET callback -> HTTP %s, %d bytes" % (status3, len(body3)))
            if headers3 is not None:
                try:
                    more3 = headers3.get_all("Set-Cookie") or []
                except Exception:
                    more3 = []
                if more3:
                    applied3 = call("identity.applyCookies", identity=identity,
                                    requestUrl="https://www.douyin.com/",
                                    setCookie=more3)
                    identity = applied3["result"]["identity"]

    has_ttwid = bool(identity.get("ttwid"))
    step("a server-issued ttwid was obtained", has_ttwid,
         "this is the real gate; without it every API call is refused")
    if has_ttwid:
        print("     ttwid=%s..." % identity["ttwid"][:44])
    print("     uifid=%s (the Argus gate demands this one too; see the summary)"
          % ("present" if identity.get("uifid") else "absent"))

    # -- 2. feed (also yields a real aweme id if none was supplied) ----------
    print("")
    print("-- feed (Phase 2 gate)")
    feed_build = call("request.build", identity=identity, operation="feed_recommend",
                      params={"count": 6})
    step("the feed request builds", feed_build.get("ok"), feed_build.get("error"))
    feed_plan = feed_build["result"]["plan"]
    print("     policy=%s state=%s" % (feed_plan["diagnostics"].get("signaturePolicy"),
                                       feed_plan["signatureState"]))
    print("     GET %s" % feed_plan["url"][:150])
    status, body, _ = send(opener, feed_plan, "feed")
    print("     -> HTTP %s, %d bytes" % (status, len(body)))

    feed_page = None
    if status == 200 and body.strip().startswith("{"):
        classified = call("response.classify", httpStatus=status, body=body,
                          operation="feed_recommend", variant="tab.feed.v1")
        if classified.get("ok"):
            parsed = call("response.awemePage",
                          payload=classified["result"]["payload"],
                          operation="feed_recommend")
            if parsed.get("ok"):
                feed_page = parsed["result"]["page"]
                print("     items=%d cursor=%s hasMore=%s skipped=%d" % (
                    len(feed_page["items"]), feed_page["cursor"],
                    feed_page["hasMore"], feed_page["skipped"]))
                for item in feed_page["items"][:3]:
                    print("       %s | %s | digg=%s comments=%s" % (
                        item["id"], (item["desc"] or "")[:26],
                        item["statistics"]["diggCount"], item["statistics"]["commentCount"]))
        else:
            print("     classify said: %s" % classified["error"]["kind"])
    step("the feed returned at least one item", bool(feed_page and feed_page["items"]),
         "HTTP %s, %d bytes" % (status, len(body)))

    if not aweme_id and feed_page and feed_page["items"]:
        aweme_id = feed_page["items"][0]["id"]
    if not aweme_id:
        aweme_id = "7300000000000000000"
    print("     using aweme id %s" % aweme_id)

    # -- 3. detail (Phase 1 gate) -------------------------------------------
    print("")
    print("-- aweme detail (Phase 1 gate)")
    detail_build = call("request.build", identity=identity, operation="aweme_detail",
                        params={"aweme_id": aweme_id})
    step("the detail request builds", detail_build.get("ok"), detail_build.get("error"))
    detail_plan = detail_build["result"]["plan"]
    step("the detail request carries no a_bogus",
         "a_bogus" not in detail_plan["url"], detail_plan["url"][-80:])
    print("     GET %s" % detail_plan["url"][:150])

    status, body, _ = send(opener, detail_plan, "detail")
    print("     -> HTTP %s, %d bytes" % (status, len(body)))
    aweme = None
    if status == 200 and body.strip().startswith("{"):
        classified = call("response.classify", httpStatus=status, body=body,
                          operation="aweme_detail", variant="detail.v1")
        if classified.get("ok"):
            parsed = call("response.awemeDetail", payload=classified["result"]["payload"])
            if parsed.get("ok"):
                aweme = parsed["result"]["aweme"]
                print("     id=%s" % aweme["id"])
                print("     desc=%s" % (aweme["desc"] or "")[:60])
                print("     author=%s (%s)" % (aweme["author"]["nickname"], aweme["author"]["id"]))
                print("     stats=%s" % json.dumps(aweme["statistics"], ensure_ascii=False))
                print("     video=%dx%d %dms cover=%s" % (
                    aweme["video"]["width"], aweme["video"]["height"],
                    aweme["video"]["durationMs"],
                    "yes" if aweme["video"]["coverUrl"] else "no"))
                print("     sources=%d" % len(aweme["video"]["sources"]))
                for source in aweme["video"]["sources"][:6]:
                    print("       %-8s %-6s %-6s %s" % (
                        source["codec"], source["quality"], source["container"],
                        source["url"][:74]))
        else:
            print("     classify said: %s -- %s" % (
                classified["error"]["kind"], classified["error"]["message"][:120]))
    step("aweme detail returned normalized JSON", aweme is not None,
         "HTTP %s, %d bytes" % (status, len(body)))

    # -- 4. comments (Phase 5 gate) -----------------------------------------
    print("")
    print("-- comments (Phase 5 gate)")
    comment_build = call("request.build", identity=identity, operation="comment_list",
                         params={"aweme_id": aweme_id, "count": 20})
    step("the comment request builds", comment_build.get("ok"), comment_build.get("error"))
    comment_plan = comment_build["result"]["plan"]
    step("the comment request carries the a_bogus parameter",
         "a_bogus=" in comment_plan["url"], comment_plan["url"][-90:])
    print("     GET %s" % comment_plan["url"][:150])

    status, body, _ = send(opener, comment_plan, "comments")
    print("     -> HTTP %s, %d bytes" % (status, len(body)))
    comment_page = None
    if status == 200 and body.strip().startswith("{"):
        classified = call("response.classify", httpStatus=status, body=body,
                          operation="comment_list", variant="comment.list.v1")
        if classified.get("ok"):
            parsed = call("response.commentPage", payload=classified["result"]["payload"],
                          operation="comment_list")
            if parsed.get("ok"):
                comment_page = parsed["result"]["page"]
                print("     comments=%d total=%d cursor=%s hasMore=%s" % (
                    len(comment_page["items"]), comment_page["total"],
                    comment_page["cursor"], comment_page["hasMore"]))
                for comment in comment_page["items"][:3]:
                    print("       %s | %s | %s | replies=%s" % (
                        comment["id"], (comment["author"]["nickname"] or "")[:12],
                        (comment["text"] or "")[:34], comment["replyCount"]))
        else:
            print("     classify said: %s" % classified["error"]["kind"])
    step("comments returned at least one item", bool(comment_page and comment_page["items"]),
         "HTTP %s, %d bytes" % (status, len(body)))

    # -- 5. replies (Phase 5, second half) ----------------------------------
    print("")
    print("-- comment replies (Phase 5 gate)")
    parent = None
    if comment_page:
        for comment in comment_page["items"]:
            if comment["replyCount"] > 0:
                parent = comment
                break
    if parent is None:
        print("  [SKIP] no comment on the first page has replies")
    else:
        reply_build = call("request.build", identity=identity, operation="comment_replies",
                           params={"item_id": aweme_id, "comment_id": parent["id"], "count": 10})
        reply_plan = reply_build["result"]["plan"]
        status, body, _ = send(opener, reply_plan, "replies")
        print("     GET %s" % reply_plan["url"][:150])
        print("     -> HTTP %s, %d bytes" % (status, len(body)))
        replies = None
        if status == 200 and body.strip().startswith("{"):
            classified = call("response.classify", httpStatus=status, body=body,
                              operation="comment_replies", variant="comment.reply.v1")
            if classified.get("ok"):
                parsed = call("response.commentPage", payload=classified["result"]["payload"],
                              operation="comment_replies")
                if parsed.get("ok"):
                    replies = parsed["result"]["page"]
                    print("     replies=%d" % len(replies["items"]))
                    for reply in replies["items"][:3]:
                        print("       %s | %s | %s" % (
                            reply["id"], (reply["author"]["nickname"] or "")[:12],
                            (reply["text"] or "")[:34]))
        step("replies for a comment with replyCount>0 returned at least one item",
             bool(replies and replies["items"]), "HTTP %s, %d bytes" % (status, len(body)))

    # -- 6. a playable source (Phase 3/4 prerequisite) ----------------------
    print("")
    print("-- playable source")
    if aweme and aweme["video"]["sources"]:
        supported = [s for s in aweme["video"]["sources"] if s["codec"] == "h264"]
        print("     h264 candidates: %d" % len(supported))
        step("the detail payload contains an H.264 candidate", bool(supported),
             [s["codec"] for s in aweme["video"]["sources"]])
        if supported:
            target = supported[0]["url"]
            print("     HEAD %s" % target[:120])
            request = urllib.request.Request(target, method="GET")
            request.add_header("Range", "bytes=0-2047")
            try:
                with opener.open(request, timeout=30) as response:
                    head = response.read(2048)
                    print("     -> HTTP %s, %d bytes, type=%s" % (
                        response.status, len(head), response.headers.get("Content-Type")))
                    print("     Content-Range: %s" % response.headers.get("Content-Range"))
                    step("the media URL answers a range request",
                         response.status in (200, 206) and len(head) > 0,
                         "HTTP %s" % response.status)
                    step("the bytes look like an MP4 (ftyp box present)",
                         b"ftyp" in head[:64], head[:16])
            except urllib.error.HTTPError as error:
                print("     -> HTTP %s" % error.code)
                step("the media URL answers a range request", False, "HTTP %s" % error.code)
            except Exception as error:  # noqa: BLE001
                print("     -> %s: %s" % (type(error).__name__, error))
                step("the media URL answers a range request", False, str(error))
    else:
        print("  [SKIP] no detail payload to read sources from")

    # -- summary ------------------------------------------------------------
    passed = sum(1 for _, ok, _ in _steps if ok)
    print("")
    print("=" * 74)
    print("gate checks: %d passed, %d failed, %d total" % (passed, len(_failures), len(_steps)))
    if _failures:
        print("")
        print("failed:")
        for name in _failures:
            print("  - %s" % name)
        print("")
        print("NOTE: a failure here is not necessarily a client bug. Read the raw HTTP")
        print("      status and body prefix printed above before concluding anything;")
        print("      risk control and a missing ttwid both look like a refusal.")
    print("=" * 74)
    return 1 if _failures else 0


if __name__ == "__main__":
    sys.exit(main())
