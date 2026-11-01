using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Douyin;
using DesultorySignals.Core.Media;
using DesultorySignals.Core.Models;
using DesultorySignals.Core.Networking;
using DesultorySignals.Core.Python;
using Newtonsoft.Json.Linq;

namespace DesultorySignals.Core.Services
{
    /// <summary>
    /// A playable source together with everything needed to fetch it and the
    /// freshness of the URL that produced it.
    /// </summary>
    /// <remarks>
    /// The freshness fields are not decoration. A Douyin play URL is signed and
    /// short-lived, so "when was this resolved" is the difference between a player
    /// that recovers and one that reports a mystery 403 five minutes into a session.
    /// </remarks>
    public sealed class ResolvedVideoSource
    {
        public string AwemeId { get; set; }
        public VideoSource Source { get; set; }
        public IList<VideoSource> Ranked { get; set; }

        /// <summary>The payload never stated the codec; a decode failure should be
        /// attributed to the codec, not to the network.</summary>
        public bool NeedsDecodeProbe { get; set; }

        /// <summary>Builds a request for any URL with the current identity's headers
        /// and cookies. Used per range fetch, so a refreshed URL is signed afresh.</summary>
        public Func<string, HttpRequestSpec> SpecFactory { get; set; }

        /// <summary>Headers for the bulk download path.</summary>
        public IList<KeyValuePair<string, string>> Headers { get; set; }

        public long ResolvedAtMs { get; set; }

        /// <summary>Set when the payload told us, or when we infer it from the signed
        /// URL's own expiry parameters. Null means "unknown, assume short".</summary>
        public long? ExpiresAtMs { get; set; }

        public int ChunkSize { get; set; }
        public int MaxCachedChunks { get; set; }

        public ResolvedVideoSource()
        {
            Ranked = new List<VideoSource>();
            Headers = new List<KeyValuePair<string, string>>();
            ChunkSize = RangeMath.DefaultChunkSize;
            MaxCachedChunks = 48;
        }

        public bool IsExpired(long nowMs)
        {
            return ExpiresAtMs.HasValue && nowMs >= ExpiresAtMs.Value;
        }
    }

    /// <summary>
    /// Video detail and source resolution.
    /// </summary>
    public sealed class VideoService
    {
        private readonly DouyinApiClient _api;
        private readonly IDouyinProtocol _python;
        private readonly SessionManager _session;
        private readonly CacheService _cache;
        private readonly DiagLog _log;

        private readonly Dictionary<string, ResolvedVideoSource> _resolved =
            new Dictionary<string, ResolvedVideoSource>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _candidateCursor =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public VideoService(
            DouyinApiClient api,
            IDouyinProtocol python,
            SessionManager session,
            CacheService cache,
            DiagLog log)
        {
            if (api == null) throw new ArgumentNullException("api");
            if (python == null) throw new ArgumentNullException("python");
            if (session == null) throw new ArgumentNullException("session");
            if (cache == null) throw new ArgumentNullException("cache");
            _api = api;
            _python = python;
            _session = session;
            _cache = cache;
            _log = log;
        }

        /// <summary>Detail for one aweme, from cache when it is fresh.</summary>
        public async Task<Aweme> GetDetailAsync(string awemeId, bool bypassCache, CancellationToken cancellationToken)
        {
            if (!bypassCache)
            {
                Aweme cached = await _cache.GetAwemeAsync(awemeId, cancellationToken).ConfigureAwait(false);
                if (cached != null)
                {
                    _log.Trace(DiagCategory.Cache, "aweme " + awemeId + " served from the metadata cache");
                    return cached;
                }
            }

            Aweme aweme = await _api.GetAwemeDetailAsync(awemeId, cancellationToken).ConfigureAwait(false);
            if (aweme != null)
            {
                await _cache.PutAwemeAsync(aweme, cancellationToken).ConfigureAwait(false);
            }
            return aweme;
        }

        /// <summary>Resolve a playable source for an already-fetched aweme.</summary>
        public Task<ResolvedVideoSource> ResolveAsync(Aweme aweme, CancellationToken cancellationToken)
        {
            return ResolveCoreAsync(aweme, false, cancellationToken);
        }

        public async Task<ResolvedVideoSource> ResolveAsync(string awemeId, CancellationToken cancellationToken)
        {
            Aweme aweme = await GetDetailAsync(awemeId, false, cancellationToken).ConfigureAwait(false);
            return await ResolveCoreAsync(aweme, false, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Re-fetch detail and pick a source again. This is the recovery entry point
        /// for an expired URL.
        /// </summary>
        public async Task<ResolvedVideoSource> RefreshSourcesAsync(Aweme aweme, CancellationToken cancellationToken)
        {
            if (aweme == null) return null;
            _log.Info(DiagCategory.Playback, "refreshing video sources for " + aweme.Id);
            return await ResolveCoreAsync(aweme, true, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The next ranked candidate after the one currently in use.</summary>
        public Task<ResolvedVideoSource> ResolveNextCandidateAsync(Aweme aweme, CancellationToken cancellationToken)
        {
            if (aweme == null) return Task.FromResult<ResolvedVideoSource>(null);

            int cursor;
            lock (_gate)
            {
                _candidateCursor.TryGetValue(aweme.Id, out cursor);
                cursor++;
                _candidateCursor[aweme.Id] = cursor;
            }

            return ResolveCoreAsync(aweme, false, cancellationToken, cursor);
        }

        private async Task<ResolvedVideoSource> ResolveCoreAsync(
            Aweme aweme, bool forceRefresh, CancellationToken cancellationToken, int candidateIndex = 0)
        {
            if (aweme == null)
                throw new DouyinApiException(ApiError.Local(ErrorKinds.InvalidInput, "aweme is required", false));

            if (aweme.IsImage)
            {
                throw new DouyinApiException(ApiError.Local(
                    ErrorKinds.UnsupportedCodec, "this post is an image set, not a video", false));
            }

            if (forceRefresh)
            {
                aweme = await GetDetailAsync(aweme.Id, true, cancellationToken).ConfigureAwait(false);
            }

            VideoInfo video = aweme.Video;
            if (video == null || !video.HasAnySource)
            {
                throw new DouyinApiException(ApiError.Local(
                    ErrorKinds.UnsupportedCodec, "the video has no source list", false));
            }

            VideoSourceSelection selection = VideoSourceSelector.Select(video.Sources);
            if (!selection.IsSupported)
            {
                throw new DouyinApiException(selection.Failure);
            }

            if (candidateIndex >= selection.Ranked.Count)
            {
                return null;
            }

            VideoSource chosen = selection.Ranked[candidateIndex];

            var resolved = new ResolvedVideoSource
            {
                AwemeId = aweme.Id,
                Source = chosen,
                Ranked = selection.Ranked,
                NeedsDecodeProbe = string.Equals(chosen.Codec, "unknown", StringComparison.OrdinalIgnoreCase)
                    && candidateIndex == 0,
                ResolvedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ExpiresAtMs = EstimateExpiry(chosen.Url)
            };

            await BuildFetchMaterialAsync(resolved, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                _resolved[aweme.Id] = resolved;
            }

            _log.Info(DiagCategory.Playback, string.Format(
                "source for {0}: {1}", aweme.Id, selection.Reason));

            return resolved;
        }

        /// <summary>
        /// Capture the headers and cookies to use for CDN fetches.
        /// </summary>
        /// <remarks>
        /// Built once per resolution through the Python layer so the CDN request
        /// carries the same identity as everything else -- and, crucially, so the
        /// cookie jar is consulted by the same code that owns it rather than by a
        /// copy made here.
        /// </remarks>
        private async Task BuildFetchMaterialAsync(
            ResolvedVideoSource resolved, CancellationToken cancellationToken)
        {
            JObject identity = _session.Identity;
            JObject built = await _python.CallWithIdentityAsync(
                "request.buildPlain",
                identity,
                PythonProtocolClient.Fields("url", resolved.Source.Url, "method", "GET"),
                cancellationToken).ConfigureAwait(false);

            await _session.AdoptIdentityAsync(built["identity"] as JObject, cancellationToken).ConfigureAwait(false);

            JObject plan = built["plan"] as JObject;
            var headers = new List<KeyValuePair<string, string>>();
            JArray rawHeaders = plan == null ? null : plan["headers"] as JArray;
            if (rawHeaders != null)
            {
                for (int index = 0; index < rawHeaders.Count; index++)
                {
                    JArray pair = rawHeaders[index] as JArray;
                    if (pair == null || pair.Count < 2) continue;
                    string name = pair[0].ToString();
                    // Range is set per request by the byte source; carrying a stale one
                    // here would silently corrupt every read.
                    if (string.Equals(name, "Range", StringComparison.OrdinalIgnoreCase)) continue;
                    headers.Add(new KeyValuePair<string, string>(name, pair[1].ToString()));
                }
            }

            string cookie = plan == null ? null : JsonValue.GetString(plan, "cookie");
            if (!string.IsNullOrEmpty(cookie))
            {
                headers.Add(new KeyValuePair<string, string>("Cookie", cookie));
            }

            resolved.Headers = headers;
            resolved.SpecFactory = delegate(string url)
            {
                var spec = new HttpRequestSpec { Method = "GET", Url = url, FollowRedirects = false };
                for (int index = 0; index < headers.Count; index++)
                {
                    // Rebuilding a range request after a redirect must not send the
                    // first-party cookies to a CDN outside that cookie scope.
                    if (headers[index].Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                        && !new Uri(url).Host.Equals(new Uri(resolved.Source.Url).Host, StringComparison.OrdinalIgnoreCase)) continue;
                    spec.AddHeader(headers[index].Key, headers[index].Value);
                }
                return spec;
            };
        }

        /// <summary>
        /// Best-effort expiry from the signed URL's own parameters.
        /// </summary>
        /// <remarks>
        /// Douyin CDN URLs carry a signed deadline. Reading it is a heuristic and is
        /// treated as one: a wrong guess only costs an unnecessary refresh, whereas
        /// assuming "never expires" costs a playback failure with no recovery.
        /// </remarks>
        private static long? EstimateExpiry(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            int queryStart = url.IndexOf('?');
            if (queryStart < 0) return now + 10 * 60 * 1000L;

            string query = url.Substring(queryStart + 1);
            string[] pairs = query.Split('&');
            for (int index = 0; index < pairs.Length; index++)
            {
                int equals = pairs[index].IndexOf('=');
                if (equals <= 0) continue;
                string name = pairs[index].Substring(0, equals);
                if (!name.Equals("expire", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("expires", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("x-expires", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("deadline", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long value;
                if (!long.TryParse(pairs[index].Substring(equals + 1), out value)) continue;
                // Could be seconds or milliseconds; both are distinguishable by width.
                if (value > 100000000000L) return value;
                return value * 1000L;
            }

            return now + 10 * 60 * 1000L;
        }
    }
}
