using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Models;
using DesultorySignals.Core.Networking;
using DesultorySignals.Core.Python;
using Newtonsoft.Json.Linq;

namespace DesultorySignals.Tests
{
    /// <summary>
    /// A protocol layer with no interpreter behind it.
    /// </summary>
    /// <remarks>
    /// The services call the Python bridge for four things: build a request,
    /// classify a response, parse the normalized payload, and manage the identity.
    /// This fake answers all four from canned data, which is what lets the *service*
    /// logic -- pagination, deduplication, retry, cookie adoption -- be tested on a
    /// desktop without a device, a transport or a CPython runtime.
    ///
    /// It deliberately records what it was asked, so a test can assert that the
    /// service re-signed rather than replayed a request.
    /// </remarks>
    public sealed class FakeProtocol : IDouyinProtocol
    {
        public readonly List<string> Calls = new List<string>();
        public readonly List<JObject> Requests = new List<JObject>();

        /// <summary>Canned body for the next classify call, per operation.</summary>
        public readonly Dictionary<string, string> Responses = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Canned classify failure, per operation.</summary>
        public readonly Dictionary<string, ApiError> Failures = new Dictionary<string, ApiError>(StringComparer.Ordinal);

        /// <summary>
        /// Canned *normalized* result for the response.* operations, keyed by
        /// operation name.
        /// </summary>
        /// <remarks>
        /// The Python layer owns the raw-to-normalized mapping and is tested
        /// separately (python/tests/run_tests.py). This fake stands in for that
        /// layer, so it returns already-normalized documents. Duplicating the
        /// normalizer here would test the duplicate, not the real one.
        /// </remarks>
        public readonly Dictionary<string, string> Normalized = new Dictionary<string, string>(StringComparer.Ordinal);

        public JObject Identity;
        public int BuildCount;

        public FakeProtocol()
        {
            Identity = new JObject
            {
                { "identityId", "1234567890123456789" },
                { "userAgent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/138.0.0.0 Safari/537.36" },
                { "webid", "1234567890123456789" },
                { "msToken", "token" },
                { "cookies", new JObject { { "identityId", "1234567890123456789" }, { "cookies", new JArray() } } }
            };
        }

        public bool IsReady
        {
            get { return true; }
        }

        public Task<JObject> CallAsync(string operation, JObject fields, CancellationToken cancellationToken)
        {
            Calls.Add(operation);
            Requests.Add(fields == null ? new JObject() : (JObject)fields.DeepClone());
            return Task.FromResult(Handle(operation, fields));
        }

        public Task<JObject> CallWithIdentityAsync(
            string operation, JObject identity, JObject fields, CancellationToken cancellationToken)
        {
            if (identity != null) Identity = identity;
            return CallAsync(operation, fields, cancellationToken);
        }

        private JObject Handle(string operation, JObject fields)
        {
            switch (operation)
            {
                case "identity.create":
                case "identity.load":
                case "identity.reset":
                case "identity.ensureFresh":
                case "login.logout":
                    return new JObject
                    {
                        { "identity", Identity },
                        { "problems", new JArray() },
                        { "warnings", new JArray() },
                        { "rotated", new JArray() }
                    };

                case "login.assess":
                    return new JObject
                    {
                        { "state", new JObject
                            {
                                { "state", "guest" },
                                { "sessionMarkers", new JArray() },
                                { "cookieCount", 0 }
                            }
                        }
                    };

                case "request.build":
                    BuildCount++;
                    return new JObject
                    {
                        { "plan", new JObject
                            {
                                { "method", "GET" },
                                { "url", "https://www.douyin.com/fake?attempt=" + BuildCount },
                                { "headers", new JArray { new JArray { "User-Agent", "test-agent" } } },
                                { "cookie", "msToken=token" },
                                { "body", null },
                                { "signatureState", "signed" },
                                { "variant", "fake" }
                            }
                        },
                        { "identity", Identity }
                    };

                case "request.buildPlain":
                    return new JObject
                    {
                        { "plan", new JObject
                            {
                                // Echo the requested method: the bootstrap chain depends on
                                // the registration call really being a POST, and a fake that
                                // always answered GET would hide that.
                                { "method", fields == null || fields["method"] == null
                                    ? "GET" : fields["method"].ToString() },
                                { "url", fields == null ? null : fields["url"] },
                                { "headers", new JArray { new JArray { "User-Agent", "test-agent" } } },
                                { "cookie", "" },
                                { "signatureState", "not-applicable" }
                            }
                        },
                        { "identity", Identity }
                    };

                case "response.classify":
                    {
                        string op = fields == null ? null : (fields["operation"] == null ? null : fields["operation"].ToString());
                        ApiError failure;
                        if (op != null && Failures.TryGetValue(op, out failure))
                        {
                            throw new DouyinApiException(failure);
                        }

                        string body = null;
                        if (op != null && Responses.TryGetValue(op, out body))
                        {
                            return new JObject
                            {
                                { "payload", JObject.Parse(body) },
                                { "identity", Identity },
                                { "cookiesChanged", new JArray() }
                            };
                        }

                        return new JObject
                        {
                            { "payload", new JObject { { "status_code", 0 } } },
                            { "identity", Identity },
                            { "cookiesChanged", new JArray() }
                        };
                    }

                case "response.awemeDetail":
                case "response.awemePage":
                case "response.commentPage":
                case "response.user":
                    {
                        string canned;
                        if (Normalized.TryGetValue(operation, out canned))
                        {
                            return new JObject { { KeyFor(operation), JToken.Parse(canned) } };
                        }
                        JToken payload = fields == null ? null : fields["payload"];
                        return new JObject { { KeyFor(operation), payload } };
                    }

                case "share.parse":
                    {
                        string input = fields == null ? null : (fields["input"] == null ? null : fields["input"].ToString());
                        if (!string.IsNullOrEmpty(input) && input.StartsWith("https://v.douyin.com", StringComparison.Ordinal))
                        {
                            return new JObject { { "share", new JObject
                                { { "kind", "redirect" }, { "nextUrl", input }, { "awemeId", null } } } };
                        }
                        return new JObject { { "share", new JObject
                            { { "kind", "aweme_id" }, { "awemeId", input }, { "source", "fake" } } } };
                    }

                case "share.parseRedirect":
                    return new JObject { { "share", new JObject
                        { { "kind", "aweme_id" }, { "awemeId", "7300000000000000000" }, { "source", "url-path" } } } };

                case "comment.guard":
                    return new JObject { { "key", "guard-key" }, { "guard", new JObject() } };

                case "comment.guardCommit":
                    return new JObject { { "guard", new JObject { { "lastKey", "guard-key" } } } };

                case "comment.buildPublish":
                    return new JObject { { "params", new JObject
                        { { "aweme_id", "1" }, { "text", "hi" }, { "text_extra", "[]" },
                          { "reply_id", "0" }, { "reply_to_reply_id", "0" } } } };

                case "comment.parsePublish":
                    return new JObject { { "comment", new JObject
                        { { "id", "new1" }, { "text", "hi" }, { "level", 1 } } } };

                case "identity.applyCookies":
                    {
                        // Mirrors the Python behaviour that matters to the services: the
                        // cookie lands in the jar, and the fingerprint fields that derive
                        // from it (ttwid, s_v_web_id) are updated on the identity. The
                        // real parsing rules are tested in python/tests/run_tests.py.
                        var changed = new JArray();
                        JArray incoming = fields == null ? null : fields["setCookie"] as JArray;
                        JArray jar = (JArray)Identity["cookies"]["cookies"];

                        for (int index = 0; incoming != null && index < incoming.Count; index++)
                        {
                            string raw = incoming[index].ToString();
                            string pair = raw.Split(';')[0].Trim();
                            int equals = pair.IndexOf('=');
                            if (equals <= 0) continue;
                            string name = pair.Substring(0, equals);
                            string value = pair.Substring(equals + 1);

                            var entry = new JObject
                            {
                                { "name", name },
                                { "value", value },
                                { "domain", "douyin.com" },
                                { "path", "/" },
                                { "hostOnly", false }
                            };
                            jar.Add(entry);
                            changed.Add(name);

                            if (name == "ttwid") Identity["ttwid"] = value;
                            if (name == "uifid") Identity["uifid"] = value;
                            if (name == "s_v_web_id")
                            {
                                Identity["sVWebId"] = value;
                                Identity["verifyFp"] = value;
                            }
                        }

                        return new JObject
                        {
                            { "identity", Identity },
                            { "changed", changed },
                            { "problems", new JArray() },
                            { "warnings", new JArray() }
                        };
                    }

                case "identity.redacted":
                    return new JObject
                    {
                        { "redacted", new JObject
                            {
                                { "identityId", "1234567890123456789" },
                                { "msTokenLength", 5 },
                                { "ttwid", Identity["ttwid"] == null ? "absent" : "present" }
                            }
                        }
                    };

                case "login.importCookie":
                    {
                        string text = fields == null ? null : (fields["cookieText"] == null ? null : fields["cookieText"].ToString());
                        if (string.IsNullOrEmpty(text) || text.IndexOf('=') < 0)
                        {
                            throw new DouyinApiException(ApiError.Local(
                                ErrorKinds.InvalidInput, "no name=value pairs found", false));
                        }

                        var names = new JArray();
                        var markers = new JArray();
                        foreach (string chunk in text.Split(';'))
                        {
                            string pair = chunk.Trim();
                            int equals = pair.IndexOf('=');
                            if (equals <= 0) continue;
                            string name = pair.Substring(0, equals).Trim();
                            string value = pair.Substring(equals + 1).Trim();
                            names.Add(name);
                            if (name == "sessionid" || name == "sid_tt") markers.Add(name);
                            if (name == "ttwid") Identity["ttwid"] = value;
                        }

                        return new JObject
                        {
                            { "report", new JObject
                                {
                                    { "state", markers.Count > 0 ? "logged_in" : "guest" },
                                    { "importedCount", names.Count },
                                    { "importedNames", names },
                                    { "sessionMarkers", markers },
                                    { "mirroredFingerprint", new JArray() }
                                }
                            },
                            { "identity", Identity },
                            { "state", new JObject
                                {
                                    { "state", markers.Count > 0 ? "logged_in" : "guest" },
                                    { "sessionMarkers", markers },
                                    { "cookieCount", names.Count }
                                }
                            }
                        };
                    }

                case "login.require":
                    return new JObject { { "ok", true } };

                default:
                    return new JObject();
            }
        }

        private static string KeyFor(string operation)
        {
            switch (operation)
            {
                case "response.awemeDetail": return "aweme";
                case "response.awemePage": return "page";
                case "response.commentPage": return "page";
                case "response.user": return "user";
                default: return "result";
            }
        }
    }

    /// <summary>
    /// A transport that answers from a queue, so a test can script a sequence of
    /// responses (a 403 followed by a 206, for instance) and assert how the caller
    /// reacted.
    /// </summary>
    public sealed class FakeTransport : IHttpTransport
    {
        private readonly Queue<Func<HttpRequestSpec, HttpResponseData>> _scripted =
            new Queue<Func<HttpRequestSpec, HttpResponseData>>();

        public readonly List<HttpRequestSpec> Requests = new List<HttpRequestSpec>();
        public Func<HttpRequestSpec, HttpResponseData> Default;
        public Func<HttpRequestSpec, Exception> ThrowOnNext;

        public void Enqueue(Func<HttpRequestSpec, HttpResponseData> responder)
        {
            _scripted.Enqueue(responder);
        }

        public void EnqueueJson(int status, string body, params string[] setCookies)
        {
            Enqueue(delegate(HttpRequestSpec spec)
            {
                var response = new HttpResponseData
                {
                    StatusCode = status,
                    Body = Encoding.UTF8.GetBytes(body ?? string.Empty),
                    FinalUrl = spec.Url
                };
                for (int index = 0; index < setCookies.Length; index++)
                {
                    response.AddHeader("Set-Cookie", setCookies[index]);
                }
                return response;
            });
        }

        public Task<HttpResponseData> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (ThrowOnNext != null)
            {
                Func<HttpRequestSpec, Exception> thrower = ThrowOnNext;
                ThrowOnNext = null;
                throw thrower(request);
            }

            if (_scripted.Count > 0)
            {
                return Task.FromResult(_scripted.Dequeue()(request));
            }

            if (Default != null)
            {
                return Task.FromResult(Default(request));
            }

            return Task.FromResult(new HttpResponseData
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes("{\"status_code\":0}"),
                FinalUrl = request.Url
            });
        }
    }

    /// <summary>Helpers for building range responses in tests.</summary>
    public static class RangeResponse
    {
        public static HttpResponseData Partial(string contentRange, int length, byte fill)
        {
            var body = new byte[length];
            for (int index = 0; index < length; index++) body[index] = fill;
            var response = new HttpResponseData { StatusCode = 206, Body = body };
            response.AddHeader("Content-Range", contentRange);
            return response;
        }

        public static HttpResponseData Whole(byte[] body, bool withLength = true)
        {
            var response = new HttpResponseData { StatusCode = 200, Body = body };
            if (withLength) response.ContentLength = body.Length;
            return response;
        }

        public static HttpResponseData Status(int status, string contentRange = null)
        {
            var response = new HttpResponseData { StatusCode = status, Body = new byte[0] };
            if (contentRange != null) response.AddHeader("Content-Range", contentRange);
            return response;
        }

        /// <summary>A body whose bytes encode their own absolute offset, so a test can
        /// prove the right window was returned rather than merely the right length.</summary>
        public static byte[] Pattern(long start, int length)
        {
            var body = new byte[length];
            for (int index = 0; index < length; index++)
            {
                body[index] = (byte)((start + index) & 0xFF);
            }
            return body;
        }

        public static bool Matches(byte[] buffer, int offset, int count, long expectedStart)
        {
            for (int index = 0; index < count; index++)
            {
                if (buffer[offset + index] != (byte)((expectedStart + index) & 0xFF)) return false;
            }
            return true;
        }
    }
}
