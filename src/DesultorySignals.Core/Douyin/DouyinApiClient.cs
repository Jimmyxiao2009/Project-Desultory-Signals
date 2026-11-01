using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Models;
using DesultorySignals.Core.Networking;
using DesultorySignals.Core.Python;
using Newtonsoft.Json.Linq;

namespace DesultorySignals.Core.Douyin
{
    /// <summary>Per-call knobs. Defaults are the safe choice for a read.</summary>
    public sealed class ApiCallOptions
    {
        public int VariantIndex { get; set; }

        /// <summary>Walk to the next endpoint variant when this one looks moved.</summary>
        public bool AllowVariantFallback { get; set; }

        /// <summary>Diagnosis only: send the request with no signature so a 403 can be
        /// attributed. Never enable for a real feature path.</summary>
        public bool AllowUnsigned { get; set; }

        public RetryPolicy Retry { get; set; }

        public bool FollowRedirects { get; set; }

        /// <summary>Skip the status_code check so a caller can read the server's own
        /// failure message. Used by the publish path.</summary>
        public bool SkipStatusCheck { get; set; }

        public ApiCallOptions()
        {
            Retry = RetryPolicy.ForReads();
            FollowRedirects = true;
        }

        public static ApiCallOptions Read()
        {
            return new ApiCallOptions { Retry = RetryPolicy.ForReads(), AllowVariantFallback = true };
        }

        public static ApiCallOptions Write()
        {
            return new ApiCallOptions
            {
                Retry = RetryPolicy.ForWrites(),
                AllowVariantFallback = false,
                SkipStatusCheck = true
            };
        }
    }

    /// <summary>
    /// The Douyin API adapter.
    /// </summary>
    /// <remarks>
    /// This is the only type in the project that knows a Douyin endpoint exists. It
    /// has two collaborators and no other state:
    ///
    /// * <see cref="PythonProtocolClient"/> decides *what* to send and interprets
    ///   *what came back* -- parameters, signature, schema.
    /// * <see cref="IHttpTransport"/> moves the bytes.
    ///
    /// Splitting it that way is what satisfies the brief's requirement that an API
    /// change touches only the Douyin layer: an endpoint rename is a Python edit, a
    /// transport change is one class in the app, and neither reaches the services.
    ///
    /// Every call re-signs. That is not wasteful, it is required: ``a_bogus`` covers
    /// the literal query string including a rotating ``msToken``, so a retry of a
    /// stored request would be a retry of a stale signature -- which the server
    /// answers with an empty 200 rather than an error.
    /// </remarks>
    public sealed class DouyinApiClient
    {
        private readonly IDouyinProtocol _python;
        private readonly IHttpTransport _transport;
        private readonly SessionManager _session;
        private readonly DiagLog _log;

        public DouyinApiClient(
            IDouyinProtocol python,
            IHttpTransport transport,
            SessionManager session,
            DiagLog log)
        {
            if (python == null) throw new ArgumentNullException("python");
            if (transport == null) throw new ArgumentNullException("transport");
            if (session == null) throw new ArgumentNullException("session");
            _python = python;
            _transport = transport;
            _session = session;
            _log = log;
        }

        public SessionManager Session
        {
            get { return _session; }
        }

        // ------------------------------------------------------------------
        // the primitive every method below is built on
        // ------------------------------------------------------------------

        /// <summary>
        /// Build, send and classify one request, with variant fallback.
        /// </summary>
        private async Task<JObject> ExecuteAsync(
            string operation,
            JObject operationParameters,
            ApiCallOptions options,
            CancellationToken cancellationToken)
        {
            if (options == null) options = ApiCallOptions.Read();

            int variantIndex = options.VariantIndex;
            int firstVariant = variantIndex;
            int variantLimit = options.AllowVariantFallback ? 4 : 1;
            DouyinApiException lastFailure = null;

            for (int attemptIndex = 0; attemptIndex < variantLimit; attemptIndex++)
            {
                string label = operation + "#" + variantIndex;
                try
                {
                    return await options.Retry.ExecuteAsync(
                        async (attempt, token) => await SendOnceAsync(
                            operation, operationParameters, variantIndex, options, attempt, label, token,
                            count => { if (count > 0) variantLimit = Math.Min(variantLimit, count - firstVariant); })
                            .ConfigureAwait(false),
                        _log,
                        DiagCategory.Protocol,
                        label,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (DouyinApiException failure)
                {
                    lastFailure = failure;
                    if (!options.AllowVariantFallback || attemptIndex + 1 >= variantLimit || !ShouldTryNextVariant(failure))
                    {
                        throw;
                    }

                    _log.Warn(DiagCategory.Protocol, string.Format(
                        "{0}: endpoint variant looks moved ({1}); trying the next variant",
                        label, failure.Kind));

                    variantIndex++;
                    // Fallback is for "this endpoint moved", not for "the network is
                    // unhappy", so the attempt budget restarts for the new variant.
                    options.Retry = RetryPolicy.ForReads();
                }
            }

            throw lastFailure ?? new DouyinApiException(ApiError.Local(
                ErrorKinds.Internal, "no endpoint variant succeeded for " + operation, false));
        }

        private async Task<JObject> SendOnceAsync(
            string operation,
            JObject operationParameters,
            int variantIndex,
            ApiCallOptions options,
            int attempt,
            string label,
            CancellationToken cancellationToken,
            Action<int> observeVariantCount)
        {
            long startedAt = Environment.TickCount;

            JObject identity = _session.Identity;
            if (identity == null)
            {
                throw new DouyinApiException(ApiError.Local(
                    ErrorKinds.SessionExpired, "the session has not been initialized", false));
            }

            // 1. Build. Done per attempt on purpose: this is where a fresh msToken and
            //    therefore a fresh signature come from.
            JObject buildResult = await _python.CallWithIdentityAsync(
                "request.build",
                identity,
                PythonProtocolClient.Fields(
                    "operation", operation,
                    "params", operationParameters ?? new JObject(),
                    "variantIndex", variantIndex,
                    "allowUnsigned", options.AllowUnsigned),
                cancellationToken).ConfigureAwait(false);

            await _session.AdoptIdentityAsync(buildResult["identity"] as JObject, cancellationToken)
                .ConfigureAwait(false);

            observeVariantCount(JsonValue.GetInt(buildResult, "variantCount", 0));
            JObject plan = buildResult["plan"] as JObject;
            if (plan == null)
            {
                throw new DouyinApiException(ApiError.Local(
                    ErrorKinds.Internal, "request.build returned no plan", false));
            }

            string signatureState = JsonValue.GetString(plan, "signatureState", "unknown");
            string url = JsonValue.GetString(plan, "url");

            // 2. Send.
            HttpResponseData response;
            try
            {
                response = await _transport.SendAsync(
                    ToRequestSpec(plan, options, label), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Transport failures are the one class where a retry is unambiguously
                // safe for a read and the only case a write is allowed to repeat.
                _log.Warn(DiagCategory.Transport, string.Format(
                    "{0}: transport failed: {1}", label, exception.Message));
                throw DouyinApiException.Network(
                    label + ": " + exception.Message, exception);
            }

            if (response == null)
            {
                throw DouyinApiException.Network(label + ": the transport returned nothing", null);
            }

            long elapsed = Environment.TickCount - startedAt;

            // Cookie rotation also matters on a refused response. Adopt it before
            // classification, since a thrown error cannot return the updated identity.
            if (response.SetCookies != null && response.SetCookies.Count > 0)
            {
                JObject adopted = await _python.CallWithIdentityAsync(
                    "identity.applyCookies", _session.Identity,
                    PythonProtocolClient.Fields("setCookie", new JArray(ToArray(response.SetCookies)), "requestUrl", url),
                    cancellationToken).ConfigureAwait(false);
                await _session.AdoptIdentityAsync(adopted["identity"] as JObject, cancellationToken).ConfigureAwait(false);
            }
            identity = _session.Identity;

            // 3. Classify.
            JObject classifyFields = PythonProtocolClient.Fields(
                "httpStatus", response.StatusCode,
                "body", response.BodyText,
                "operation", operation,
                "variant", JsonValue.GetString(plan, "variant"),
                "requestUrl", url,
                "setCookie", new JArray(ToArray(response.SetCookies)),
                "identity", identity,
                "skipStatusCheck", options.SkipStatusCheck);

            JObject classifyResult;
            try
            {
                classifyResult = await _python.CallAsync(
                    "response.classify", classifyFields, cancellationToken).ConfigureAwait(false);
            }
            catch (DouyinApiException failure)
            {
                LogExchange(label, attempt, response, elapsed, signatureState, failure.Kind);
                throw;
            }

            await _session.AdoptIdentityAsync(classifyResult["identity"] as JObject, cancellationToken)
                .ConfigureAwait(false);

            LogExchange(label, attempt, response, elapsed, signatureState, null);

            // A cookie rotation is worth surfacing even on success: it is the only
            // visible sign that the session is alive.
            var changed = JsonValue.GetStrings(classifyResult, "cookiesChanged");
            if (changed != null && changed.Count > 0)
            {
                _log.Info(DiagCategory.Session, label + ": server rotated cookies",
                    new JObject { { "names", new JArray(changed) } });
            }

            return classifyResult["payload"] as JObject ?? new JObject();
        }

        private void LogExchange(
            string label, int attempt, HttpResponseData response,
            long elapsedMs, string signatureState, string failureKind)
        {
            var data = new JObject
            {
                { "attempt", attempt },
                { "status", response.StatusCode },
                { "ms", elapsedMs },
                { "bytes", response.BodyLength },
                { "signature", signatureState }
            };
            if (failureKind != null) data["failure"] = failureKind;

            if (failureKind != null)
            {
                _log.Warn(DiagCategory.Protocol, label + " failed", data);
            }
            else
            {
                _log.Info(DiagCategory.Protocol, label + " ok", data);
            }
        }

        private static HttpRequestSpec ToRequestSpec(JObject plan, ApiCallOptions options, string label)
        {
            var spec = new HttpRequestSpec
            {
                Method = JsonValue.GetString(plan, "method", "GET"),
                Url = JsonValue.GetString(plan, "url"),
                Tag = label,
                FollowRedirects = options.FollowRedirects,
                StreamResponse = false
            };

            JArray headers = plan["headers"] as JArray;
            if (headers != null)
            {
                for (int index = 0; index < headers.Count; index++)
                {
                    JArray pair = headers[index] as JArray;
                    if (pair == null || pair.Count < 2) continue;
                    spec.AddHeader(pair[0].ToString(), pair[1].ToString());
                }
            }

            string cookie = JsonValue.GetString(plan, "cookie");
            if (!string.IsNullOrEmpty(cookie)) spec.SetHeader("Cookie", cookie);

            string body = JsonValue.GetString(plan, "body");
            if (!string.IsNullOrEmpty(body)) spec.Body = Encoding.UTF8.GetBytes(body);

            return spec;
        }

        private static bool ShouldTryNextVariant(DouyinApiException failure)
        {
            if (failure.Error == null) return false;
            switch (failure.Kind)
            {
                case ErrorKinds.SchemaChanged:
                case ErrorKinds.EmptyBody:
                    return true;
                case ErrorKinds.HttpError:
                    return failure.Error.HttpStatus == 404 || failure.Error.HttpStatus == 405;
                default:
                    return false;
            }
        }

        private static string[] ToArray(IList<string> values)
        {
            if (values == null) return new string[0];
            var array = new string[values.Count];
            for (int index = 0; index < values.Count; index++) array[index] = values[index];
            return array;
        }

        // ------------------------------------------------------------------
        // reads
        // ------------------------------------------------------------------

        public async Task<Aweme> GetAwemeDetailAsync(string awemeId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(awemeId))
                throw new DouyinApiException(ApiError.Local(ErrorKinds.InvalidInput, "aweme id is required", false));

            JObject payload = await ExecuteAsync(
                "aweme_detail",
                PythonProtocolClient.Fields("aweme_id", awemeId),
                ApiCallOptions.Read(),
                cancellationToken).ConfigureAwait(false);

            JObject result = await _python.CallAsync(
                "response.awemeDetail",
                PythonProtocolClient.Fields("payload", payload),
                cancellationToken).ConfigureAwait(false);

            Aweme aweme = Aweme.FromJson(result["aweme"] as JObject);
            if (aweme == null)
            {
                throw new DouyinApiException(ApiError.Local(
                    ErrorKinds.SchemaChanged, "the detail response produced no usable aweme", false));
            }
            return aweme;
        }

        /// <summary>
        /// One page of the recommend feed. Pass the previous page's
        /// <see cref="FeedPage.Cursor"/> verbatim to continue.
        /// </summary>
        public async Task<FeedPage> GetFeedAsync(
            string cursor, int count, int refreshIndex, CancellationToken cancellationToken)
        {
            var parameters = new JObject
            {
                { "count", count <= 0 ? 20 : count },
                { "refresh_index", refreshIndex },
                { "volume", 0.0 }
            };
            // cursor is the web client's refresh index in disguise; the API ignores
            // max_cursor on this endpoint, so it is only sent when non-empty.
            if (!string.IsNullOrEmpty(cursor)) parameters["refer_share_id"] = cursor;

            JObject payload = await ExecuteAsync(
                "feed_recommend",
                parameters,
                new ApiCallOptions { Retry = RetryPolicy.ForFeed(), AllowVariantFallback = true },
                cancellationToken).ConfigureAwait(false);

            JObject result = await _python.CallAsync(
                "response.awemePage",
                PythonProtocolClient.Fields("payload", payload, "operation", "feed_recommend"),
                cancellationToken).ConfigureAwait(false);

            return FeedPage.FromJson(result["page"] as JObject);
        }

        public async Task<CommentPage> GetCommentsAsync(
            string awemeId, string cursor, int count, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(awemeId))
                throw new DouyinApiException(ApiError.Local(ErrorKinds.InvalidInput, "aweme id is required", false));

            var parameters = new JObject
            {
                { "aweme_id", awemeId },
                { "cursor", ParseCursor(cursor) },
                { "count", count <= 0 ? 20 : count }
            };

            JObject payload = await ExecuteAsync(
                "comment_list", parameters, ApiCallOptions.Read(), cancellationToken).ConfigureAwait(false);

            JObject result = await _python.CallAsync(
                "response.commentPage",
                PythonProtocolClient.Fields("payload", payload, "operation", "comment_list"),
                cancellationToken).ConfigureAwait(false);

            return CommentPage.FromJson(result["page"] as JObject);
        }

        /// <summary>Replies to one top-level comment.</summary>
        public async Task<CommentPage> GetCommentRepliesAsync(
            string awemeId, string commentId, string cursor, int count, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(awemeId) || string.IsNullOrEmpty(commentId))
                throw new DouyinApiException(ApiError.Local(
                    ErrorKinds.InvalidInput, "aweme id and comment id are required", false));

            var parameters = new JObject
            {
                { "item_id", awemeId },
                { "comment_id", commentId },
                { "cursor", ParseCursor(cursor) },
                { "count", count <= 0 ? 20 : count }
            };

            JObject payload = await ExecuteAsync(
                "comment_replies", parameters, ApiCallOptions.Read(), cancellationToken).ConfigureAwait(false);

            JObject result = await _python.CallAsync(
                "response.commentPage",
                PythonProtocolClient.Fields("payload", payload, "operation", "comment_replies"),
                cancellationToken).ConfigureAwait(false);

            return CommentPage.FromJson(result["page"] as JObject);
        }

        public async Task<SearchPage> SearchAsync(string keyword, bool users, string cursor, string searchId, int count, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                throw new DouyinApiException(ApiError.Local(ErrorKinds.InvalidInput, "请输入搜索关键词", false));
            var parameters = new JObject { { "keyword", keyword.Trim() }, { "offset", string.IsNullOrEmpty(cursor) ? "0" : cursor }, { "count", count <= 0 ? 10 : count } };
            if (!string.IsNullOrEmpty(searchId)) parameters["search_id"] = searchId;
            JObject payload = await ExecuteAsync(users ? "search_user" : "search_single", parameters, ApiCallOptions.Read(), cancellationToken).ConfigureAwait(false);
            JObject result = await _python.CallAsync("response.searchPage", PythonProtocolClient.Fields("payload", payload), cancellationToken).ConfigureAwait(false);
            return SearchPage.FromJson(result["page"] as JObject);
        }

        public async Task<FeedPage> GetUserPostsAsync(string secUid, string cursor, int count, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(secUid))
                throw new DouyinApiException(ApiError.Local(ErrorKinds.InvalidInput, "作者 sec_uid 不能为空", false));
            JObject payload = await ExecuteAsync("aweme_post",
                PythonProtocolClient.Fields("sec_user_id", secUid, "max_cursor", string.IsNullOrEmpty(cursor) ? "0" : cursor, "count", count <= 0 ? 10 : count),
                ApiCallOptions.Read(), cancellationToken).ConfigureAwait(false);
            JObject result = await _python.CallAsync("response.awemePage", PythonProtocolClient.Fields("payload", payload, "operation", "aweme_post"), cancellationToken).ConfigureAwait(false);
            return FeedPage.FromJson(result["page"] as JObject);
        }

        public async Task<UserProfile> GetUserAsync(string secUid, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(secUid))
                throw new DouyinApiException(ApiError.Local(ErrorKinds.InvalidInput, "sec_user_id is required", false));

            JObject payload = await ExecuteAsync(
                "user_detail",
                PythonProtocolClient.Fields("sec_user_id", secUid),
                ApiCallOptions.Read(),
                cancellationToken).ConfigureAwait(false);

            JObject result = await _python.CallAsync(
                "response.user", PythonProtocolClient.Fields("payload", payload), cancellationToken)
                .ConfigureAwait(false);

            return UserProfile.FromJson(result["user"] as JObject);
        }

        // ------------------------------------------------------------------
        // share links
        // ------------------------------------------------------------------

        /// <summary>
        /// Turn any accepted share input into an aweme id.
        /// </summary>
        /// <remarks>
        /// Short links are resolved by *following the redirect* and re-parsing the
        /// final URL, not by scraping the page. That is both cheaper and more
        /// durable: the redirect target is the canonical video URL, and it does not
        /// change when the share page's markup does.
        /// </remarks>
        public async Task<ShareParseResult> ResolveShareAsync(string input, CancellationToken cancellationToken)
        {
            JObject parseResult = await _python.CallAsync(
                "share.parse", PythonProtocolClient.Fields("input", input), cancellationToken)
                .ConfigureAwait(false);

            ShareParseResult parsed = ShareParseResult.FromJson(parseResult["share"] as JObject);
            if (parsed == null)
            {
                throw new DouyinApiException(ApiError.Local(
                    ErrorKinds.Internal, "share.parse returned nothing", false));
            }

            if (!parsed.NeedsRedirect) return parsed;

            _log.Info(DiagCategory.Protocol, "resolving a short share link through its redirect");

            JObject identity = _session.Identity;
            JObject buildResult = await _python.CallWithIdentityAsync(
                "request.buildPlain",
                identity,
                PythonProtocolClient.Fields("url", parsed.NextUrl, "method", "GET"),
                cancellationToken).ConfigureAwait(false);

            await _session.AdoptIdentityAsync(buildResult["identity"] as JObject, cancellationToken)
                .ConfigureAwait(false);

            JObject plan = buildResult["plan"] as JObject;
            var options = new ApiCallOptions { FollowRedirects = true, Retry = RetryPolicy.ForReads() };

            HttpResponseData response = await _transport.SendAsync(
                ToRequestSpec(plan, options, "share-resolve"), cancellationToken).ConfigureAwait(false);

            string finalUrl = response != null ? response.FinalUrl : null;
            if (string.IsNullOrEmpty(finalUrl))
            {
                throw new DouyinApiException(ApiError.Local(
                    ErrorKinds.NotFound,
                    "the share link did not redirect anywhere usable (HTTP "
                        + (response == null ? 0 : response.StatusCode) + ")",
                    false));
            }

            JObject redirectResult = await _python.CallAsync(
                "share.parseRedirect", PythonProtocolClient.Fields("url", finalUrl), cancellationToken)
                .ConfigureAwait(false);

            return ShareParseResult.FromJson(redirectResult["share"] as JObject);
        }

        // ------------------------------------------------------------------
        // session
        // ------------------------------------------------------------------

        /// <summary>
        /// Acquire the server-issued guest credentials, in the order that works.
        /// </summary>
        /// <remarks>
        /// This is not a scrape and not a login: no markup is read, only the
        /// <c>Set-Cookie</c> headers are kept. The brief forbids inventing these
        /// values, and the measurement agrees -- the gate is a server-issued
        /// <c>ttwid</c>, and a fabricated one does not pass.
        ///
        /// Three steps, because no single one is reliable:
        ///
        /// 1. <c>GET https://www.douyin.com/</c>. This is the canonical source, and
        ///    it is also where the site sets its own anti-bot nonce. On some network
        ///    paths it answers with a challenge page instead, which is why the status
        ///    is not treated as failure: a challenge page still sets cookies.
        /// 2. If step 1 produced no <c>ttwid</c>, <c>POST</c> the union registration
        ///    endpoint. It answers with a <c>ttwid</c> and, importantly, with a
        ///    <c>redirect_url</c> callback on the douyin.com origin.
        /// 3. Follow that callback. Measured 2026-10-07: doing so cleared the Argus
        ///    gate that had been refusing the detail endpoint, so it is a real part of
        ///    the handshake rather than a formality.
        ///
        /// Returns true when at least one credential was adopted.
        /// </remarks>
        public async Task<bool> BootstrapGuestSessionAsync(CancellationToken cancellationToken)
        {
            bool anyChange = false;

            // -- step 1: the home page -------------------------------------------
            anyChange |= await FetchPlainAsync(
                "https://www.douyin.com/", "GET", null, "guest-home", cancellationToken)
                .ConfigureAwait(false);

            // -- step 2: the union registration -----------------------------------
            string ttwid = GetIdentityString("ttwid");
            string callback = null;
            if (string.IsNullOrEmpty(ttwid))
            {
                string payload =
                    "{\"region\":\"cn\",\"aid\":1768,\"needFid\":false,\"service\":\"www.douyin.com\","
                    + "\"migrate_info\":{\"ticket\":\"\",\"source\":\"node\"},"
                    + "\"cbUrlProtocol\":\"https\",\"union\":true}";

                HttpResponseData register = await SendPlainAsync(
                    "https://ttwid.bytedance.com/ttwid/union/register/",
                    "POST",
                    payload,
                    "application/json",
                    "ttwid-register",
                    cancellationToken).ConfigureAwait(false);

                if (register != null)
                {
                    anyChange |= await AdoptCookiesAsync(
                        register, "https://ttwid.bytedance.com/", cancellationToken).ConfigureAwait(false);

                    string body = register.BodyText;
                    if (!string.IsNullOrEmpty(body) && body.TrimStart().StartsWith("{"))
                    {
                        try
                        {
                            JObject parsed = JObject.Parse(body);
                            callback = JsonValue.GetString(parsed, "redirect_url");
                        }
                        catch (Newtonsoft.Json.JsonException)
                        {
                            // A non-JSON answer is not fatal; the cookie is what matters.
                        }
                    }
                }
            }

            // -- step 3: the callback --------------------------------------------
            if (!string.IsNullOrEmpty(callback))
            {
                anyChange |= await FetchPlainAsync(callback, "GET", null, "ttwid-callback", cancellationToken)
                    .ConfigureAwait(false);
            }

            await _session.RefreshAssessmentAsync(cancellationToken).ConfigureAwait(false);

            bool hasTtwid = !string.IsNullOrEmpty(GetIdentityString("ttwid"));
            bool hasUifid = !string.IsNullOrEmpty(GetIdentityString("uifid"));

            _log.Info(DiagCategory.Session, "guest session bootstrap finished",
                new JObject
                {
                    { "changed", anyChange },
                    { "hasTtwid", hasTtwid },
                    { "hasUifid", hasUifid }
                });

            if (!hasTtwid)
            {
                // Loud on purpose. The single most likely cause is a transport that
                // does not surface Set-Cookie at all -- which is silent otherwise, and
                // presents later as an unexplained 403 on every API call. Saying it
                // here turns a two-hour diagnosis into a one-line read.
                _log.Warn(DiagCategory.Session,
                    "no ttwid was obtained from any bootstrap step. Every API call will be "
                    + "refused until this is fixed. Check that the transport surfaces "
                    + "Set-Cookie (it must not run with a cookie jar of its own), and see "
                    + "docs/API-SURFACE.md section 2.7 for the three-step handshake.",
                    new JObject { { "changed", anyChange } });
            }

            return anyChange;
        }

        /// <summary>Build, send and adopt cookies for one unauthenticated request.</summary>
        private async Task<bool> FetchPlainAsync(
            string url, string method, string body, string tag, CancellationToken cancellationToken)
        {
            HttpResponseData response = await SendPlainAsync(
                url, method, body, null, tag, cancellationToken).ConfigureAwait(false);
            if (response == null) return false;
            return await AdoptCookiesAsync(response, url, cancellationToken).ConfigureAwait(false);
        }

        private async Task<HttpResponseData> SendPlainAsync(
            string url, string method, string body, string contentType, string tag,
            CancellationToken cancellationToken)
        {
            JObject identity = _session.Identity;
            JObject buildResult = await _python.CallWithIdentityAsync(
                "request.buildPlain",
                identity,
                PythonProtocolClient.Fields("url", url, "method", method, "body", body),
                cancellationToken).ConfigureAwait(false);

            await _session.AdoptIdentityAsync(buildResult["identity"] as JObject, cancellationToken)
                .ConfigureAwait(false);

            JObject plan = buildResult["plan"] as JObject;
            var options = new ApiCallOptions { FollowRedirects = true };
            HttpRequestSpec spec = ToRequestSpec(plan, options, tag);
            if (!string.IsNullOrEmpty(contentType))
            {
                spec.SetHeader("Content-Type", contentType);
                spec.Body = Encoding.UTF8.GetBytes(body ?? string.Empty);
            }

            try
            {
                return await _transport.SendAsync(spec, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log.Warn(DiagCategory.Session, tag + " failed: " + exception.Message);
                return null;
            }
        }

        private async Task<bool> AdoptCookiesAsync(
            HttpResponseData response, string requestUrl, CancellationToken cancellationToken)
        {
            IList<string> setCookies = response.SetCookies;
            if (setCookies == null || setCookies.Count == 0) return false;

            JObject applied = await _python.CallWithIdentityAsync(
                "identity.applyCookies",
                _session.Identity,
                PythonProtocolClient.Fields(
                    "setCookie", new JArray(ToArray(setCookies)),
                    "requestUrl", requestUrl),
                cancellationToken).ConfigureAwait(false);

            await _session.AdoptIdentityAsync(applied["identity"] as JObject, cancellationToken)
                .ConfigureAwait(false);

            var changed = JsonValue.GetStrings(applied, "changed");
            bool any = changed != null && changed.Count > 0;
            if (any)
            {
                _log.Info(DiagCategory.Session, "guest credentials adopted",
                    new JObject
                    {
                        { "url", requestUrl },
                        { "status", response.StatusCode },
                        { "changed", new JArray(changed) }
                    });
            }
            return any;
        }

        private string GetIdentityString(string name)
        {
            JObject identity = _session.Identity;
            return identity == null ? null : JsonValue.GetString(identity, name);
        }

        /// <summary>Probe whether the session is still accepted by the server.</summary>
        public async Task<SessionAssessment> CheckSessionAsync(CancellationToken cancellationToken)
        {
            // An anonymous guest has no authenticated account endpoint to validate.
            // Preserve that supported state instead of reporting a rejected user/self
            // request as risk control every time the guest app resumes.
            if (!_session.IsLoggedIn)
                return await _session.RefreshAssessmentAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ExecuteAsync("user_self", new JObject(), ApiCallOptions.Read(), cancellationToken)
                    .ConfigureAwait(false);
                return await _session.RefreshAssessmentAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DouyinApiException failure)
            {
                if (failure.Error != null && failure.Error.IsSessionProblem)
                {
                    _log.Warn(DiagCategory.Session, "the server rejected the session; it is no longer valid");
                }
                throw;
            }
        }

        // ------------------------------------------------------------------
        // login
        // ------------------------------------------------------------------

        /// <summary>
        /// Start a QR login. The returned session carries the QR payload; the caller
        /// polls with <see cref="PollLoginSessionAsync"/>.
        /// </summary>
        public async Task<LoginSessionModel> CreateLoginSessionAsync(CancellationToken cancellationToken)
        {
            JObject identity = _session.Identity;
            JObject result = await _python.CallWithIdentityAsync(
                "login.qrCreate", identity, new JObject(), cancellationToken).ConfigureAwait(false);

            await _session.AdoptIdentityAsync(result["identity"] as JObject, cancellationToken)
                .ConfigureAwait(false);

            JObject plan = result["plan"] as JObject;
            HttpResponseData response = await _transport.SendAsync(
                ToRequestSpec(plan, new ApiCallOptions(), "login-qr-create"), cancellationToken)
                .ConfigureAwait(false);

            await AdoptCookiesAsync(response, JsonValue.GetString(plan, "url"), cancellationToken).ConfigureAwait(false);

            JObject applied = await _python.CallWithIdentityAsync(
                "login.qrApplyCreate",
                _session.Identity,
                PythonProtocolClient.Fields(
                    "session", result["session"],
                    "httpStatus", response == null ? 0 : response.StatusCode,
                    "body", response == null ? null : response.BodyText),
                cancellationToken).ConfigureAwait(false);

            LoginSessionModel session = LoginSessionModel.FromJson(applied["session"] as JObject);
            _log.Info(DiagCategory.Login, "QR login session created",
                new JObject { { "status", session == null ? "?" : session.Status } });
            return session;
        }

        /// <summary>One poll of a QR login session.</summary>
        public async Task<LoginSessionModel> PollLoginSessionAsync(
            LoginSessionModel session, CancellationToken cancellationToken)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (session.IsTerminal) return session;

            JObject identity = _session.Identity;
            JObject planResult = await _python.CallWithIdentityAsync(
                "login.qrCheckPlan",
                identity,
                PythonProtocolClient.Fields("session", ToSessionJson(session)),
                cancellationToken).ConfigureAwait(false);

            await _session.AdoptIdentityAsync(planResult["identity"] as JObject, cancellationToken)
                .ConfigureAwait(false);

            JObject plan = planResult["plan"] as JObject;
            HttpResponseData response = await _transport.SendAsync(
                ToRequestSpec(plan, new ApiCallOptions(), "login-qr-check"), cancellationToken)
                .ConfigureAwait(false);

            await AdoptCookiesAsync(response, JsonValue.GetString(plan, "url"), cancellationToken).ConfigureAwait(false);
            JObject diagnosticResult = await _python.CallAsync("login.qrDiagnostic",
                PythonProtocolClient.Fields("httpStatus", response.StatusCode, "body", response.BodyText),
                cancellationToken).ConfigureAwait(false);
            JObject diagnostic = diagnosticResult["diagnostic"] as JObject;
            _log.Info(DiagCategory.Login, "QR poll response", diagnostic);
            JObject redirect = await _python.CallAsync("login.qrRedirect",
                PythonProtocolClient.Fields("httpStatus", response.StatusCode, "body", response.BodyText),
                cancellationToken).ConfigureAwait(false);
            string redirectUrl = JsonValue.GetString(redirect, "url");
            if (!string.IsNullOrEmpty(redirectUrl))
                await CompleteQrRedirectAsync(redirectUrl, cancellationToken).ConfigureAwait(false);

            JObject applied = await _python.CallWithIdentityAsync(
                "login.qrCheckApply",
                _session.Identity,
                PythonProtocolClient.Fields(
                    "session", ToSessionJson(session),
                    "httpStatus", response == null ? 0 : response.StatusCode,
                    "body", response == null ? null : response.BodyText,
                    "setCookie", new JArray(ToArray(response == null ? null : response.SetCookies)),
                    "requestUrl", JsonValue.GetString(plan, "url")),
                cancellationToken).ConfigureAwait(false);

            await _session.AdoptIdentityAsync(applied["identity"] as JObject, cancellationToken)
                .ConfigureAwait(false);

            LoginSessionModel updated = LoginSessionModel.FromJson(applied["session"] as JObject);
            if (updated != null) updated.ResponseDiagnostic = diagnostic;
            if (updated != null && updated.IsTerminal)
            {
                _log.Info(DiagCategory.Login, "QR login finished with status " + updated.Status);
                await _session.RefreshAssessmentAsync(cancellationToken).ConfigureAwait(false);
            }
            return updated;
        }

        private async Task CompleteQrRedirectAsync(string url, CancellationToken cancellationToken)
        {
            for (int hop = 0; hop < 10; hop++)
            {
                Uri target;
                if (!Uri.TryCreate(url, UriKind.Absolute, out target) || target.Scheme != "https"
                    || (target.Host != "douyin.com" && !target.Host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase))
                    || !string.IsNullOrEmpty(target.UserInfo))
                    throw new DouyinApiException(ApiError.Local(ErrorKinds.RiskControl, "登录跳转地址不属于抖音", false));
                JObject built = await _python.CallWithIdentityAsync("request.buildPlain", _session.Identity,
                    PythonProtocolClient.Fields("url", url, "method", "GET"), cancellationToken).ConfigureAwait(false);
                var options = ApiCallOptions.Read(); options.FollowRedirects = false;
                using (var reply = await _transport.SendAsync(ToRequestSpec(built["plan"] as JObject, options,
                    "login-qr-redirect"), cancellationToken).ConfigureAwait(false))
                {
                    await AdoptCookiesAsync(reply, url, cancellationToken).ConfigureAwait(false);
                    if (reply.StatusCode >= 300 && reply.StatusCode < 400 && !string.IsNullOrEmpty(reply.Location))
                    { url = new Uri(target, reply.Location).AbsoluteUri; continue; }
                    if (reply.StatusCode < 200 || reply.StatusCode >= 300)
                        throw new DouyinApiException(ApiError.Local(ErrorKinds.Network, "登录跳转未完成", false));
                    return;
                }
            }
            throw new DouyinApiException(ApiError.Local(ErrorKinds.RiskControl, "登录跳转次数过多", false));
        }

        // ------------------------------------------------------------------
        // writes
        // ------------------------------------------------------------------

        public Task<Comment> PublishCommentAsync(string awemeId, string text, CancellationToken cancellationToken)
        {
            return PublishAsync(awemeId, text, null, null, cancellationToken);
        }

        public Task<Comment> ReplyCommentAsync(
            string awemeId, string commentId, string text, string replyToReplyId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(commentId))
                throw new DouyinApiException(ApiError.Local(
                    ErrorKinds.InvalidInput, "the comment being replied to is required", false));
            return PublishAsync(awemeId, text, commentId, replyToReplyId, cancellationToken);
        }

        private async Task<Comment> PublishAsync(
            string awemeId, string text, string replyCommentId, string replyToReplyId,
            CancellationToken cancellationToken)
        {
            // 1. Refuse early if there is no session. Cheaper and clearer than a 403.
            await _python.CallWithIdentityAsync(
                "login.require",
                _session.Identity,
                PythonProtocolClient.Fields("operation", "publishing a comment"),
                cancellationToken).ConfigureAwait(false);

            // 2. Duplicate guard, before anything is sent.
            JObject guard = await _session.LoadPublishGuardAsync(cancellationToken).ConfigureAwait(false);
            JObject guardResult = await _python.CallAsync(
                "comment.guard",
                PythonProtocolClient.Fields(
                    "guard", guard,
                    "awemeId", awemeId,
                    "text", text,
                    "replyCommentId", replyCommentId,
                    "replyToReplyId", replyToReplyId),
                cancellationToken).ConfigureAwait(false);

            string guardKey = JsonValue.GetString(guardResult, "key");

            // 3. Build the form body in Python, then send it as a POST.
            JObject built = await _python.CallAsync(
                "comment.buildPublish",
                PythonProtocolClient.Fields(
                    "awemeId", awemeId,
                    "text", text,
                    "replyCommentId", replyCommentId,
                    "replyToReplyId", replyToReplyId),
                cancellationToken).ConfigureAwait(false);

            JObject parameters = built["params"] as JObject;

            JObject payload = await ExecuteAsync(
                "comment_publish", parameters, ApiCallOptions.Write(), cancellationToken)
                .ConfigureAwait(false);

            // 4. Interpret the write response ourselves so a review refusal keeps its
            //    own wording instead of collapsing into a generic error.
            JObject parsed = await _python.CallAsync(
                "comment.parsePublish",
                PythonProtocolClient.Fields(
                    "body", payload.ToString(Newtonsoft.Json.Formatting.None),
                    "httpStatus", 200),
                cancellationToken).ConfigureAwait(false);

            Comment comment = Comment.FromJson(parsed["comment"] as JObject);

            // 5. Only now is the write known to have landed, so only now does the
            //    guard advance. Committing earlier would suppress a legitimate retry
            //    after a failure that never reached the server.
            JObject committed = await _python.CallAsync(
                "comment.guardCommit",
                PythonProtocolClient.Fields("guard", guardResult["guard"], "key", guardKey),
                cancellationToken).ConfigureAwait(false);

            await _session.SavePublishGuardAsync(committed["guard"] as JObject, cancellationToken)
                .ConfigureAwait(false);

            _log.Info(DiagCategory.Publish, "comment published",
                new JObject
                {
                    { "awemeId", awemeId },
                    { "isReply", !string.IsNullOrEmpty(replyCommentId) },
                    { "commentId", comment == null ? null : comment.Id }
                });

            return comment;
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        private static JObject ToSessionJson(LoginSessionModel session)
        {
            return new JObject
            {
                { "id", session.Id },
                { "status", session.Status },
                { "token", session.Token },
                { "qrUrl", session.QrUrl },
                { "qrImageBase64", session.QrImageBase64 },
                { "expiresAtMs", session.ExpiresAtMs },
                { "pollIntervalMs", session.PollIntervalMs },
                { "statusMessage", session.StatusMessage },
                { "errorKind", session.ErrorKind }
            };
        }

        /// <summary>Cursors are opaque strings from the server; the API wants a number.</summary>
        private static long ParseCursor(string cursor)
        {
            long parsed;
            if (!string.IsNullOrEmpty(cursor) && long.TryParse(cursor, out parsed)) return parsed;
            return 0;
        }
    }
}
