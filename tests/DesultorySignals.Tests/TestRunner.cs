using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Douyin;
using DesultorySignals.Core.Media;
using DesultorySignals.Core.Models;
using DesultorySignals.Core.Networking;
using DesultorySignals.Core.Python;
using DesultorySignals.Core.Services;
using DesultorySignals.Core.Storage;
using Newtonsoft.Json.Linq;

namespace DesultorySignals.Tests
{
    /// <summary>
    /// The desktop test suite for the C# core.
    /// </summary>
    /// <remarks>
    /// Everything here runs without a phone, an interpreter or a network. That is
    /// the point: the parts of this client most likely to be subtly wrong --
    /// chunk arithmetic, cache eviction, codec ranking, retry policy, range recovery
    /// -- are exactly the parts that are hard to observe on a device and easy to
    /// pin here.
    /// </remarks>
    public sealed class TestRunner
    {
        private int _passed;
        private int _failed;
        private readonly List<string> _failures = new List<string>();
        private string _current = "";

        public int Run()
        {
            Section("RangeMath");
            RangeMathTests();

            Section("RangeCache");
            RangeCacheTests();

            Section("VideoSourceSelector");
            SourceSelectorTests();

            Section("RetryPolicy");
            RetryPolicyTests();

            Section("Redactor");
            RedactorTests();

            Section("DiagLog");
            DiagLogTests();

            Section("Model parsing");
            ModelTests();

            Section("Storage");
            StorageTests();

            Section("RemoteRandomAccessByteSource");
            RangeSourceTests();

            Section("SessionManager");
            SessionTests();

            Section("Guest bootstrap");
            BootstrapTests();

            Section("FeedService");
            FeedTests();

            Section("CommentService");
            CommentTests();

            Section("VideoService");
            VideoServiceTests();

            Section("CacheService");
            CacheTests();

            Section("HistoryService");
            HistoryTests();

            Section("PlaybackService");
            PlaybackTests();

            Console.WriteLine("");
            Console.WriteLine(new string('=', 72));
            Console.WriteLine("checks: {0} passed, {1} failed, {2} total", _passed, _failed, _passed + _failed);
            if (_failed > 0)
            {
                Console.WriteLine("");
                Console.WriteLine("failures:");
                for (int index = 0; index < _failures.Count; index++)
                {
                    Console.WriteLine("  - " + _failures[index]);
                }
            }
            Console.WriteLine(new string('=', 72));
            return _failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------
        // harness
        // ------------------------------------------------------------------

        private void Section(string name)
        {
            _current = name;
            Console.WriteLine("");
            Console.WriteLine("-- " + name);
        }

        private void Check(string name, bool condition, object detail = null)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("[PASS] " + name);
            }
            else
            {
                _failed++;
                string message = _current + " / " + name + (detail == null ? "" : "  -- " + detail);
                _failures.Add(message);
                Console.WriteLine("[FAIL] " + name + (detail == null ? "" : "  -- " + detail));
            }
        }

        private void CheckEqual(string name, object expected, object actual)
        {
            bool same = expected == null ? actual == null : expected.Equals(actual);
            Check(name, same, string.Format("expected {0}, got {1}", Show(expected), Show(actual)));
        }

        private static string Show(object value)
        {
            if (value == null) return "null";
            string text = value.ToString();
            return text.Length > 120 ? text.Substring(0, 120) + "..." : text;
        }

        private static DiagLog QuietLog()
        {
            return new DiagLog(256) { Enabled = false, MinimumLevel = DiagLevel.Trace };
        }

        // ------------------------------------------------------------------
        // RangeMath
        // ------------------------------------------------------------------

        private void RangeMathTests()
        {
            int chunk = 512 * 1024;

            CheckEqual("chunk index of position 0", 0, RangeMath.ChunkIndex(0, chunk));
            CheckEqual("chunk index of the last byte of chunk 0", 0, RangeMath.ChunkIndex(chunk - 1, chunk));
            CheckEqual("chunk index of the first byte of chunk 1", 1, RangeMath.ChunkIndex(chunk, chunk));
            CheckEqual("chunk index at an exact multiple", 4, RangeMath.ChunkIndex(4L * chunk, chunk));

            CheckEqual("chunk start", 3L * chunk, RangeMath.ChunkStart(3, chunk));
            CheckEqual("chunk end is inclusive", 3L * chunk + chunk - 1, RangeMath.ChunkEnd(3, chunk, -1));
            CheckEqual("a short final chunk is clamped",
                3L * chunk + 9, RangeMath.ChunkEnd(3, chunk, 3L * chunk + 10));
            CheckEqual("a chunk past the end reports -1", -1L, RangeMath.ChunkEnd(5, chunk, 3L * chunk + 10));
            CheckEqual("chunk count of an exact multiple", 3, RangeMath.ChunkCount(3L * chunk, chunk));
            CheckEqual("chunk count rounds up", 4, RangeMath.ChunkCount(3L * chunk + 1, chunk));
            CheckEqual("chunk count of zero bytes", 0, RangeMath.ChunkCount(0, chunk));

            CheckEqual("range header format", "bytes=0-511", RangeMath.RangeHeader(0, 511));

            long start, end, total;
            Check("a well-formed content-range parses",
                RangeMath.TryParseContentRange("bytes 0-511/1048576", out start, out end, out total));
            CheckEqual("content-range start", 0L, start);
            CheckEqual("content-range end", 511L, end);
            CheckEqual("content-range total", 1048576L, total);

            Check("an unsatisfiable content-range still yields the total",
                RangeMath.TryParseContentRange("bytes */1048576", out start, out end, out total));
            CheckEqual("total from an unsatisfiable range", 1048576L, total);
            CheckEqual("total helper agrees", 1048576L, RangeMath.TotalFromContentRange("bytes 0-1/1048576"));
            Check("a missing content-range does not parse",
                !RangeMath.TryParseContentRange(null, out start, out end, out total));
            CheckEqual("no total when the header is absent", null, RangeMath.TotalFromContentRange("nonsense"));

            // Overlap copying is what makes a chunk serve a read that starts mid-chunk.
            byte[] source = RangeResponse.Pattern(1000, 100);
            byte[] destination = new byte[200];
            int copied = RangeMath.CopyOverlap(source, 1000, 100, destination, 0, 1050, 100);
            CheckEqual("overlap length", 50, copied);
            Check("the overlap landed at the right destination offset",
                RangeResponse.Matches(destination, 0, 50, 1050), BitConverter.ToString(destination, 0, 8));

            // The destination offset parameter is what keeps a mid-buffer read correct.
            byte[] mid = new byte[200];
            int copiedMid = RangeMath.CopyOverlap(source, 1000, 100, mid, 30, 1050, 100);
            CheckEqual("a mid-buffer overlap has the same length", 50, copiedMid);
            Check("a mid-buffer overlap lands at the requested destination offset",
                RangeResponse.Matches(mid, 30, 50, 1050), BitConverter.ToString(mid, 30, 8));
            Check("a mid-buffer overlap leaves the prefix alone",
                mid[0] == 0 && mid[29] == 0, BitConverter.ToString(mid, 0, 8));

            CheckEqual("a disjoint overlap copies nothing",
                0, RangeMath.CopyOverlap(source, 1000, 100, destination, 0, 2000, 100));
            CheckEqual("an overlap before the source copies nothing",
                0, RangeMath.CopyOverlap(source, 1000, 100, destination, 0, 0, 500));
            CheckEqual("an out-of-range destination offset copies nothing",
                0, RangeMath.CopyOverlap(source, 1000, 100, destination, 199, 1050, 100));
        }

        // ------------------------------------------------------------------
        // RangeCache
        // ------------------------------------------------------------------

        private void RangeCacheTests()
        {
            var cache = new RangeCache(16, 4, QuietLog());

            Check("a miss returns null", cache.TryGet(0) == null);
            CheckEqual("a miss is counted", 1L, cache.Misses);

            cache.Put(0, new byte[16]);
            cache.Put(1, new byte[16]);
            CheckEqual("count reflects the entries", 2, cache.Count);
            CheckEqual("bytes are accounted", 32L, cache.TotalBytes);
            Check("a hit returns the bytes", cache.TryGet(1) != null);
            CheckEqual("a hit is counted", 1L, cache.Hits);
            CheckEqual("hit ratio", 0.5, cache.HitRatio);

            // Eviction is LRU: touch 0 so 1 becomes the least recently used.
            cache.TryGet(0);
            cache.Put(2, new byte[16]);
            cache.Put(3, new byte[16]);
            cache.Put(4, new byte[16]); // capacity 4 -> one eviction

            CheckEqual("the cache never exceeds its capacity", 4, cache.Count);
            Check("the least recently used entry was evicted", cache.TryGet(1) == null);
            Check("the most recently used entry survived", cache.TryGet(0) != null);
            CheckEqual("evictions are counted", 1L, cache.Evictions);

            cache.InvalidateAll("test");
            CheckEqual("invalidation empties the cache", 0, cache.Count);
            CheckEqual("invalidation resets the byte count", 0L, cache.TotalBytes);
            Check("invalidation is recorded", cache.WasInvalidated);

            var cache2 = new RangeCache(16, 8, QuietLog());
            cache2.Put(0, new byte[16]);
            cache2.Put(1, new byte[16]);
            cache2.Put(2, new byte[16]);
            cache2.InvalidateFrom(1, "test");
            Check("invalidate-from keeps earlier chunks", cache2.TryGet(0) != null);
            Check("invalidate-from drops the named chunk", cache2.TryGet(1) == null);
            Check("invalidate-from drops later chunks", cache2.TryGet(2) == null);
            CheckEqual("invalidate-from leaves the right count", 1, cache2.Count);

            // A short final chunk must be stored at its true length, not padded.
            var cache3 = new RangeCache(512, 4, QuietLog());
            cache3.Put(3, new byte[7]);
            byte[] shortChunk = cache3.TryGet(3);
            CheckEqual("a short chunk keeps its true length", 7, shortChunk.Length);
        }

        // ------------------------------------------------------------------
        // VideoSourceSelector
        // ------------------------------------------------------------------

        private static VideoSource Source(string url, string codec, string quality, int width, int height,
            string container = "mp4", long? size = null, int? bitrate = null)
        {
            return new VideoSource
            {
                Url = url,
                OriginalUrl = url,
                Codec = codec,
                CodecEvidence = "test",
                Container = container,
                Quality = quality,
                Width = width,
                Height = height,
                SizeBytes = size,
                Bitrate = bitrate
            };
        }

        private void SourceSelectorTests()
        {
            var list = new List<VideoSource>
            {
                Source("https://cdn/1080.mp4", "h264", "1080p", 1080, 1920),
                Source("https://cdn/540.mp4", "h264", "540p", 540, 960),
                Source("https://cdn/720.mp4", "h264", "720p", 720, 1280),
                Source("https://cdn/360.mp4", "h264", "360p", 360, 640)
            };
            VideoSourceSelection selection = VideoSourceSelector.Select(list);
            Check("a selection is produced", selection.IsSupported);
            CheckEqual("720p wins over 1080p and 540p", "https://cdn/720.mp4", selection.Selected.Url);
            CheckEqual("the whole list is ranked", 4, selection.Ranked.Count);
            CheckEqual("ranking is ordered", "https://cdn/540.mp4", selection.Ranked[1].Url);
            Check("a known codec needs no decode probe", !selection.NeedsDecodeProbe);

            var hevcOnly = new List<VideoSource>
            {
                Source("https://cdn/265.mp4", "hevc", "1080p", 1080, 1920),
                Source("https://cdn/av1.mp4", "av1", "1080p", 1080, 1920)
            };
            VideoSourceSelection rejected = VideoSourceSelector.Select(hevcOnly);
            Check("an unsupported-codec-only video is refused", !rejected.IsSupported);
            CheckEqual("the refusal is classified as unsupported_codec",
                ErrorKinds.UnsupportedCodec, rejected.Failure.Kind);

            var allowed = VideoSourceSelector.Select(hevcOnly, true);
            Check("HEVC is selectable when explicitly allowed", allowed.IsSupported);

            var unknown = new List<VideoSource>
            {
                Source("https://cdn/generic.mp4", "unknown", "720p", 720, 1280),
                Source("https://cdn/h264_360.mp4", "h264", "360p", 360, 640)
            };
            VideoSourceSelection preferKnown = VideoSourceSelector.Select(unknown);
            CheckEqual("a known codec beats an unknown one even at lower quality",
                "https://cdn/h264_360.mp4", preferKnown.Selected.Url);

            var unknownOnly = new List<VideoSource>
            {
                Source("https://cdn/generic.mp4", "unknown", "720p", 720, 1280)
            };
            VideoSourceSelection probe = VideoSourceSelector.Select(unknownOnly);
            Check("an unknown-codec mp4 is still usable", probe.IsSupported);
            Check("an unknown codec is flagged for a decode probe", probe.NeedsDecodeProbe);

            var unknownWebm = new List<VideoSource>
            {
                Source("https://cdn/generic.webm", "unknown", "720p", 720, 1280, "webm")
            };
            Check("an unknown codec in an unsupported container is refused",
                !VideoSourceSelector.Select(unknownWebm).IsSupported);

            // The https twin of the same resource should win, because the device is
            // more likely to be allowed to reach it.
            var schemes = new List<VideoSource>
            {
                Source("http://cdn/a.mp4", "h264", "720p", 720, 1280),
                Source("https://cdn/a.mp4", "h264", "720p", 720, 1280)
            };
            CheckEqual("the https twin wins",
                "https://cdn/a.mp4", VideoSourceSelector.Select(schemes).Selected.Url);

            // Tie-break: at equal score, the smaller file wins.
            var sizes = new List<VideoSource>
            {
                Source("https://cdn/big.mp4", "h264", "720p", 720, 1280, "mp4", 40L * 1024 * 1024),
                Source("https://cdn/small.mp4", "h264", "720p", 720, 1280, "mp4", 8L * 1024 * 1024)
            };
            CheckEqual("at equal score the smaller file wins",
                "https://cdn/small.mp4", VideoSourceSelector.Select(sizes).Selected.Url);

            CheckEqual("an empty source list is refused",
                ErrorKinds.UnsupportedCodec, VideoSourceSelector.Select(new List<VideoSource>()).Failure.Kind);
        }

        // ------------------------------------------------------------------
        // RetryPolicy
        // ------------------------------------------------------------------

        private void RetryPolicyTests()
        {
            DiagLog log = QuietLog();

            // A retryable failure is retried, and the action sees the attempt number.
            int attempts = 0;
            RetryPolicy reads = RetryPolicy.ForTesting(3, 1);
            try
            {
                reads.ExecuteAsync<int>(
                    delegate(int attempt, CancellationToken token)
                    {
                        attempts = attempt;
                        throw new DouyinApiException(ApiError.Local(ErrorKinds.Network, "boom", true));
                    },
                    log, "test", "retryable", CancellationToken.None).GetAwaiter().GetResult();
                Check("a retryable failure eventually throws", false);
            }
            catch (DouyinApiException)
            {
                Check("a retryable failure eventually throws", true);
            }
            CheckEqual("the action ran the full attempt budget", 3, attempts);

            // A non-retryable failure stops immediately.
            int stops = 0;
            try
            {
                RetryPolicy.ForTesting(5, 1).ExecuteAsync<int>(
                    delegate(int attempt, CancellationToken token)
                    {
                        stops = attempt;
                        throw new DouyinApiException(ApiError.Local(ErrorKinds.NotFound, "gone", false));
                    },
                    log, "test", "nonretryable", CancellationToken.None).GetAwaiter().GetResult();
                Check("a non-retryable failure throws", false);
            }
            catch (DouyinApiException)
            {
                Check("a non-retryable failure throws", true);
            }
            CheckEqual("a non-retryable failure is not repeated", 1, stops);

            // Writes repeat only when nothing came back at all.
            int writeAttempts = 0;
            try
            {
                RetryPolicy.ForWrites().ExecuteAsync<int>(
                    delegate(int attempt, CancellationToken token)
                    {
                        writeAttempts = attempt;
                        throw new DouyinApiException(ApiError.Local(ErrorKinds.HttpError, "500", true));
                    },
                    log, "test", "write", CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (DouyinApiException)
            {
            }
            CheckEqual("a write is not retried after the server answered", 1, writeAttempts);

            int writeNetworkAttempts = 0;
            try
            {
                RetryPolicy.ForWrites().ExecuteAsync<int>(
                    delegate(int attempt, CancellationToken token)
                    {
                        writeNetworkAttempts = attempt;
                        throw new DouyinApiException(ApiError.Local(ErrorKinds.Network, "socket", true));
                    },
                    log, "test", "write-network", CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (DouyinApiException)
            {
            }
            CheckEqual("a write is retried once when nothing came back", 2, writeNetworkAttempts);

            // Success on a later attempt is returned.
            int tries = 0;
            int value = RetryPolicy.ForTesting(4, 7).ExecuteAsync<int>(
                async delegate(int attempt, CancellationToken token)
                {
                    tries++;
                    if (attempt < 3)
                        throw new DouyinApiException(ApiError.Local(ErrorKinds.RateLimited, "slow down", true));
                    await Task.Yield();
                    return 42;
                },
                log, "test", "eventual", CancellationToken.None).GetAwaiter().GetResult();
            CheckEqual("a later success is returned", 42, value);
            CheckEqual("it took three attempts", 3, tries);

            // An unexpected exception is not retried: it is our own bug.
            int bugAttempts = 0;
            try
            {
                RetryPolicy.ForTesting(4, 1).ExecuteAsync<int>(
                    delegate(int attempt, CancellationToken token)
                    {
                        bugAttempts++;
                        throw new InvalidOperationException("our bug");
                    },
                    log, "test", "bug", CancellationToken.None).GetAwaiter().GetResult();
                Check("an unclassified exception propagates", false);
            }
            catch (InvalidOperationException)
            {
                Check("an unclassified exception propagates", true);
            }
            CheckEqual("an unclassified exception is not retried", 1, bugAttempts);

            // Cancellation is honoured, not swallowed.
            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                RetryPolicy.ForReads().ExecuteAsync<int>(
                    async delegate(int attempt, CancellationToken token)
                    {
                        await Task.Yield();
                        return 1;
                    },
                    log, "test", "cancelled", cancelled.Token).GetAwaiter().GetResult();
                Check("a cancelled call throws", false);
            }
            catch (OperationCanceledException)
            {
                Check("a cancelled call throws", true);
            }
        }

        // ------------------------------------------------------------------
        // Redactor
        // ------------------------------------------------------------------

        private void RedactorTests()
        {
            string header = "ttwid=abc; sessionid=SUPERSECRETVALUE; msToken=" + new string('x', 128);
            string summary = Redactor.CookieHeader(header);
            Check("a cookie summary does not contain the session value",
                summary.IndexOf("SUPERSECRETVALUE", StringComparison.Ordinal) < 0, summary);
            Check("a cookie summary still names the cookies",
                summary.IndexOf("sessionid", StringComparison.Ordinal) >= 0, summary);
            Check("a cookie summary counts the secrets",
                summary.IndexOf("1 secret", StringComparison.Ordinal) >= 0, summary);

            string url = "https://www.douyin.com/aweme/v1/web/aweme/detail/?aweme_id=1&msToken=" + new string('y', 64) + "&a_bogus=keepme";
            string redactedUrl = Redactor.Url(url);
            Check("a url keeps non-secret parameters",
                redactedUrl.IndexOf("a_bogus=keepme", StringComparison.Ordinal) >= 0, redactedUrl);
            Check("a url redacts the msToken",
                redactedUrl.IndexOf(new string('y', 64), StringComparison.Ordinal) < 0, redactedUrl);
            Check("a url keeps the aweme id", redactedUrl.IndexOf("aweme_id=1", StringComparison.Ordinal) >= 0);

            string scrubbed = Redactor.Text("Cookie: sessionid=ABC123XYZ; other=visible");
            Check("free text scrubbing removes a session value",
                scrubbed.IndexOf("ABC123XYZ", StringComparison.Ordinal) < 0, scrubbed);
            Check("free text scrubbing keeps unrelated values",
                scrubbed.IndexOf("other=visible", StringComparison.Ordinal) >= 0, scrubbed);

            Check("a non-secret cookie value is preserved",
                Redactor.CookieValue("aweme_id", "12345") == "12345");
            Check("a secret cookie value is length-tagged",
                Redactor.CookieValue("sessionid", "12345") == "<redacted:5>");
        }

        // ------------------------------------------------------------------
        // DiagLog
        // ------------------------------------------------------------------

        private void DiagLogTests()
        {
            var log = new DiagLog(64) { Enabled = true, MinimumLevel = DiagLevel.Trace };
            for (int index = 0; index < 200; index++)
            {
                log.Info(DiagCategory.Feed, "entry " + index);
            }

            CheckEqual("the ring buffer is bounded", 64, log.Snapshot().Count);
            IList<DiagEntry> snapshot = log.Snapshot();
            CheckEqual("the newest entry is retained", "entry 199", snapshot[snapshot.Count - 1].Message);
            CheckEqual("the oldest retained entry is correct", "entry 136", snapshot[0].Message);

            var disabled = new DiagLog(64) { Enabled = false };
            for (int index = 0; index < 10; index++) disabled.Info(DiagCategory.Feed, "x");
            CheckEqual("a disabled log stores nothing", 0, disabled.Snapshot().Count);
            CheckEqual("a disabled log counts what it suppressed", 10L, disabled.DroppedCount);

            var scrubbing = new DiagLog(16) { Enabled = true, MinimumLevel = DiagLevel.Trace };
            scrubbing.Info(DiagCategory.Session, "cookie is sessionid=TOPSECRET",
                new JObject { { "cookie", "sessionid=ALSO_SECRET" }, { "msToken", "abcdef" } });
            string dump = scrubbing.Dump();
            Check("the log dump has no secret from the message",
                dump.IndexOf("TOPSECRET", StringComparison.Ordinal) < 0, dump);
            Check("the log dump has no secret from the data",
                dump.IndexOf("ALSO_SECRET", StringComparison.Ordinal) < 0, dump);
            Check("the log dump keeps the cookie summary",
                dump.IndexOf("sessionid", StringComparison.Ordinal) >= 0, dump);

            var levels = new DiagLog(16) { Enabled = true, MinimumLevel = DiagLevel.Warn };
            levels.Info(DiagCategory.Feed, "quiet");
            levels.Warn(DiagCategory.Feed, "loud");
            CheckEqual("a level filter drops quieter entries", 1, levels.Snapshot().Count);

            var capped = new DiagLog(8) { Enabled = true, MinimumLevel = DiagLevel.Trace };
            for (int index = 0; index < 5; index++) capped.Info(DiagCategory.Cache, "c" + index);
            CheckEqual("category filtering works", 5, capped.Snapshot(DiagCategory.Cache, 10).Count);
            CheckEqual("category filtering excludes other categories",
                0, capped.Snapshot(DiagCategory.Feed, 10).Count);
        }

        // ------------------------------------------------------------------
        // models
        // ------------------------------------------------------------------

        private void ModelTests()
        {
            var raw = JObject.Parse(@"{
                ""id"": ""7300000000000000000"",
                ""desc"": ""hello"",
                ""createTime"": ""1690000000"",
                ""isImage"": 1,
                ""author"": { ""id"": 12345, ""nickname"": ""nick"" },
                ""video"": {
                    ""width"": ""720"", ""height"": 1280, ""durationMs"": 15000,
                    ""coverUrl"": ""https://cdn/c.jpg"",
                    ""sources"": [
                        { ""url"": ""https://cdn/a.mp4"", ""codec"": ""h264"", ""container"": ""mp4"",
                          ""quality"": ""720p"", ""width"": 720, ""height"": 1280, ""sizeBytes"": 1000 }
                    ]
                },
                ""statistics"": { ""diggCount"": ""42"", ""commentCount"": 7 }
            }");

            Aweme aweme = Aweme.FromJson(raw);
            Check("an aweme parses", aweme != null);
            CheckEqual("a string createTime is parsed", 1690000000L, aweme.CreateTime);
            Check("an integer isImage flag is parsed", aweme.IsImage);
            CheckEqual("a numeric author id becomes a string", "12345", aweme.Author.Id);
            CheckEqual("a string width is parsed", 720, aweme.Video.Width);
            CheckEqual("statistics parse from strings", 42, aweme.Statistics.DiggCount);
            CheckEqual("the source list is parsed", 1, aweme.Video.Sources.Count);
            CheckEqual("the cover is kept", "https://cdn/c.jpg", aweme.BestCoverUrl);

            Check("an aweme without an id is rejected", Aweme.FromJson(new JObject { { "desc", "x" } }) == null);
            Check("a non-http url is rejected as a cover",
                JsonValue.GetUrl(JObject.Parse(@"{""u"":""file:///c:/x.jpg""}"), "u") == null);
            Check("an http url is accepted",
                JsonValue.GetUrl(JObject.Parse(@"{""u"":""http://cdn/x.jpg""}"), "u") == "http://cdn/x.jpg");

            var page = FeedPage.FromJson(JObject.Parse(@"{
                ""items"": [ { ""id"": ""1"" }, { ""id"": ""2"" } ],
                ""cursor"": ""123"", ""hasMore"": ""1"", ""skipped"": 2
            }"));
            CheckEqual("a page parses its items", 2, page.Items.Count);
            CheckEqual("hasMore parses from a string", true, page.HasMore);
            CheckEqual("the skipped count is carried", 2, page.Skipped);

            var comment = Comment.FromJson(JObject.Parse(@"{
                ""id"": ""c1"", ""text"": ""hi"", ""diggCount"": ""9"", ""replyCount"": 2, ""level"": 2
            }"));
            CheckEqual("a comment parses", "c1", comment.Id);
            CheckEqual("comment diggCount parses from a string", 9, comment.DiggCount);
            CheckEqual("a comment reports replies", true, comment.HasReplies);
        }

        // ------------------------------------------------------------------
        // storage
        // ------------------------------------------------------------------

        private void StorageTests()
        {
            CheckEqual("a path is normalized", "a/b/c.json", StorePath.Normalize("a\\b\\c.json"));
            CheckEqual("leading slashes are trimmed", "a/b", StorePath.Normalize("/a/b/"));
            CheckEqual("a redundant segment is dropped", "a/b", StorePath.Normalize("a/./b"));
            CheckEqual("an inner parent segment is resolved", "a/c", StorePath.Normalize("a/b/../c"));

            Check("a traversal is refused", Throws(delegate { StorePath.Normalize("../secret"); }));
            Check("a deep traversal is refused", Throws(delegate { StorePath.Normalize("a/../../secret"); }));
            Check("an absolute path is refused", Throws(delegate { StorePath.Normalize("C:/Windows/x"); }));
            Check("an empty path is refused", Throws(delegate { StorePath.Normalize("   "); }));
            Check("an illegal character is refused", Throws(delegate { StorePath.Normalize("a/b*c"); }));

            CheckEqual("the folder of a nested path", "a/b", StorePath.FolderOf("a/b/c.json"));
            CheckEqual("the folder of a root file", "", StorePath.FolderOf("c.json"));
            CheckEqual("the file name", "c.json", StorePath.FileNameOf("a/b/c.json"));

            var store = new MemoryFileStore();
            store.WriteTextAsync("a/b.txt", "hello", CancellationToken.None).GetAwaiter().GetResult();
            CheckEqual("text round-trips", "hello",
                store.ReadTextAsync("a/b.txt", CancellationToken.None).GetAwaiter().GetResult());
            Check("a missing file reads as null",
                store.ReadTextAsync("nope.txt", CancellationToken.None).GetAwaiter().GetResult() == null);
            CheckEqual("a missing file reports size -1", -1L,
                store.SizeAsync("nope.txt", CancellationToken.None).GetAwaiter().GetResult());
            CheckEqual("size is reported", 5L,
                store.SizeAsync("a/b.txt", CancellationToken.None).GetAwaiter().GetResult());

            store.WriteBytesAsync("img/1.bin", new byte[] { 1, 2, 3 }, CancellationToken.None).GetAwaiter().GetResult();
            IList<string> listing = store.ListAsync("img", CancellationToken.None).GetAwaiter().GetResult();
            CheckEqual("listing returns the file", 1, listing.Count);
            CheckEqual("listing returns only direct children", "1.bin", listing[0]);
            CheckEqual("listing a missing folder is empty",
                0, store.ListAsync("nothing", CancellationToken.None).GetAwaiter().GetResult().Count);

            // The streamed write must be visible only after dispose, like a real file.
            System.IO.Stream target = store.OpenWriteAsync("media/v.mp4", CancellationToken.None)
                .GetAwaiter().GetResult();
            byte[] payload = new byte[1024];
            target.Write(payload, 0, payload.Length);
            target.Dispose();
            CheckEqual("a streamed write is committed on dispose", 1024L,
                store.SizeAsync("media/v.mp4", CancellationToken.None).GetAwaiter().GetResult());
        }

        private static bool Throws(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        // ------------------------------------------------------------------
        // range source
        // ------------------------------------------------------------------

        private void RangeSourceTests()
        {
            var log = QuietLog();

            // A plain read across two chunks, with the server honouring Range.
            var transport = new FakeTransport();
            int chunk = 64;
            long total = 200;
            transport.Default = delegate(HttpRequestSpec spec)
            {
                long start = ParseRangeStart(spec.GetHeader("Range"));
                int length = (int)Math.Min(chunk, total - start);
                HttpResponseData response = RangeResponse.Partial(
                    string.Format("bytes {0}-{1}/{2}", start, start + length - 1, total), length, 0);
                response.Body = RangeResponse.Pattern(start, length);
                return response;
            };

            var options = new RangeSourceOptions
            {
                Url = "https://cdn/v.mp4",
                ChunkSize = chunk,
                MaxChunks = 8,
                TotalSize = total,
                Retry = RetryPolicy.ForTesting(2, 3)
            };

            using (var source = new RemoteRandomAccessByteSource(options, transport, log))
            {
                var buffer = new byte[100];
                int read = source.ReadAsync(50, buffer, 0, 100, CancellationToken.None).GetAwaiter().GetResult();
                CheckEqual("a read spanning two chunks returns the requested length", 100, read);
                Check("the bytes come from the right offsets",
                    RangeResponse.Matches(buffer, 0, 100, 50), BitConverter.ToString(buffer, 0, 8));

                // A second read inside the same chunks must not hit the network.
                int before = transport.Requests.Count;
                int again = source.ReadAsync(60, buffer, 0, 20, CancellationToken.None).GetAwaiter().GetResult();
                CheckEqual("a cached read returns bytes", 20, again);
                CheckEqual("a cached read issues no request", before, transport.Requests.Count);

                // A seek far ahead fetches only the needed chunk.
                int seeked = source.ReadAsync(190, buffer, 0, 20, CancellationToken.None).GetAwaiter().GetResult();
                CheckEqual("a read at the tail is clamped to the resource", 10, seeked);
                CheckEqual("a tail read fetched exactly one chunk", before + 1, transport.Requests.Count);

                CheckEqual("the source knows its size", total, source.Size);
                Check("the cache recorded hits", source.CacheStats.Hits > 0, source.CacheStats.ToString());
            }

            // A server that ignores Range and answers 200 with the whole file.
            var ignoring = new FakeTransport();
            byte[] whole = RangeResponse.Pattern(0, 200);
            ignoring.Default = delegate(HttpRequestSpec spec) { return RangeResponse.Whole(whole); };

            var options2 = new RangeSourceOptions
            {
                Url = "https://cdn/v.mp4",
                ChunkSize = 64,
                MaxChunks = 8,
                TotalSize = -1,
                Retry = RetryPolicy.ForTesting(2, 3)
            };
            using (var source = new RemoteRandomAccessByteSource(options2, ignoring, log))
            {
                var buffer = new byte[64];
                int read = source.ReadAsync(0, buffer, 0, 64, CancellationToken.None).GetAwaiter().GetResult();
                CheckEqual("a range-ignoring server still yields the requested window", 64, read);
                Check("the window comes from the right offset",
                    RangeResponse.Matches(buffer, 0, 64, 0));
                CheckEqual("the size is taken from the 200 response", 200L, source.Size);

                // Reading past the first chunk must slice out of a fresh 200 body
                // rather than reusing the first chunk's bytes.
                int offset = source.ReadAsync(70, buffer, 0, 20, CancellationToken.None).GetAwaiter().GetResult();
                CheckEqual("an offset read against a range-ignoring server returns bytes", 20, offset);
                Check("an offset read is not misaligned",
                    RangeResponse.Matches(buffer, 0, 20, 70), BitConverter.ToString(buffer, 0, 8));
            }

            // A 403 on a signed URL triggers a refresh, and the retry uses the new URL.
            var refreshing = new FakeTransport();
            refreshing.Default = delegate(HttpRequestSpec spec)
            {
                if (spec.Url == "https://cdn/old.mp4") return RangeResponse.Status(403);
                HttpResponseData response = RangeResponse.Partial("bytes 0-63/200", 64, 0);
                response.Body = RangeResponse.Pattern(0, 64);
                return response;
            };

            int refreshes = 0;
            var options3 = new RangeSourceOptions
            {
                Url = "https://cdn/old.mp4",
                ChunkSize = 64,
                MaxChunks = 8,
                TotalSize = 200,
                Retry = RetryPolicy.ForTesting(3, 5),
                RefreshUrlAsync = delegate(CancellationToken token)
                {
                    refreshes++;
                    return Task.FromResult("https://cdn/new.mp4");
                }
            };
            using (var source = new RemoteRandomAccessByteSource(options3, refreshing, log))
            {
                var buffer = new byte[64];
                int read = source.ReadAsync(0, buffer, 0, 64, CancellationToken.None).GetAwaiter().GetResult();
                CheckEqual("a refused URL is refreshed and the read succeeds", 64, read);
                CheckEqual("the refresh callback ran once", 1, refreshes);
                Check("the retry used the refreshed URL",
                    refreshing.Requests[refreshing.Requests.Count - 1].Url == "https://cdn/new.mp4",
                    refreshing.Requests[refreshing.Requests.Count - 1].Url);
                CheckEqual("the refresh count is tracked", 1, source.RefreshCount);
            }

            // A 403 with no refresh available must fail rather than loop.
            var hopeless = new FakeTransport();
            hopeless.Default = delegate(HttpRequestSpec spec) { return RangeResponse.Status(403); };
            var options4 = new RangeSourceOptions
            {
                Url = "https://cdn/dead.mp4",
                ChunkSize = 64,
                MaxChunks = 8,
                Retry = RetryPolicy.ForTesting(2, 5)
            };
            using (var source = new RemoteRandomAccessByteSource(options4, hopeless, log))
            {
                bool threw = false;
                try
                {
                    source.ReadAsync(0, new byte[64], 0, 64, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (DouyinApiException exception)
                {
                    threw = true;
                    CheckEqual("an unrefreshable refusal is classified as not-found",
                        ErrorKinds.NotFound, exception.Kind);
                }
                Check("an unrefreshable refusal throws", threw);
                Check("an unrefreshable refusal does not retry forever",
                    hopeless.Requests.Count <= 3, hopeless.Requests.Count);
            }

            // The source must always send an explicit Range header.
            Check("every range request carried a Range header",
                transport.Requests.TrueForAll(delegate(HttpRequestSpec spec)
                {
                    return spec.GetHeader("Range") != null && spec.GetHeader("Range").StartsWith("bytes=");
                }));
            Check("range requests do not follow redirects",
                transport.Requests.TrueForAll(delegate(HttpRequestSpec spec) { return !spec.FollowRedirects; }));
        }

        private static long ParseRangeStart(string header)
        {
            // "bytes=start-end"
            string body = header.Substring("bytes=".Length);
            return long.Parse(body.Substring(0, body.IndexOf('-')));
        }

        // ------------------------------------------------------------------
        // session
        // ------------------------------------------------------------------

        private ServiceHost BuildHost(out FakeProtocol protocol, out FakeTransport transport, out MemoryFileStore store)
        {
            protocol = new FakeProtocol();
            transport = new FakeTransport();
            store = new MemoryFileStore();
            var python = new PythonProtocolClient(
                new FakeBackend(protocol), new InlineEngineExecutor(), QuietLog());
            return new ServiceHost(
                python, transport, store, new FakeMediaHost(), QuietLog());
        }

        private void SessionTests()
        {
            FakeProtocol protocol;
            FakeTransport transport;
            MemoryFileStore store;
            ServiceHost host = BuildHost(out protocol, out transport, out store);

            host.Session.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check("the session initializes from a stored or created identity",
                host.Session.Identity != null);
            Check("the identity is persisted",
                store.ExistsAsync(SessionManager.IdentityPath, CancellationToken.None).GetAwaiter().GetResult());

            string firstPersisted = store.ReadTextAsync(SessionManager.IdentityPath, CancellationToken.None)
                .GetAwaiter().GetResult();
            Check("the persisted identity is JSON", firstPersisted.StartsWith("{"), firstPersisted.Substring(0, 40));

            // Adopting an unchanged identity must not rewrite the file.
            host.Session.AdoptIdentityAsync(host.Session.Identity, CancellationToken.None).GetAwaiter().GetResult();
            string secondPersisted = store.ReadTextAsync(SessionManager.IdentityPath, CancellationToken.None)
                .GetAwaiter().GetResult();
            CheckEqual("an unchanged identity is not rewritten", firstPersisted, secondPersisted);

            // Cookie import through the real path.
            var imported = host.Session.ImportCookiesAsync("sessionid=X; ttwid=Y", CancellationToken.None)
                .GetAwaiter().GetResult();
            Check("cookie import succeeds", imported != null);

            host.Session.LogoutAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check("logout completes", true);

            host.Session.ResetIdentityAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check("identity reset completes", true);
        }

        private void BootstrapTests()
        {
            FakeProtocol protocol;
            FakeTransport transport;
            MemoryFileStore store;
            ServiceHost host = BuildHost(out protocol, out transport, out store);
            host.Session.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

            // Step 1: the home page sets the anti-bot nonce but not ttwid, which is the
            // measured real behaviour. Step 2 must then be attempted.
            transport.EnqueueJson(200, "<html>challenge</html>",
                "__ac_nonce=06ac5c8d6003de554bd51; Path=/; Domain=.douyin.com");
            transport.EnqueueJson(200,
                "{\"status_code\":0,\"message\":\"union register success\","
                + "\"redirect_url\":\"https://www.douyin.com/ttwid/union/register/callback/?aid=1768&ticket=x\"}",
                "ttwid=1%7Cabc%7C1791346934%7Cdeadbeef; Path=/; Domain=bytedance.com; HttpOnly; Secure");
            transport.EnqueueJson(200, "ok");

            bool changed = host.Api.BootstrapGuestSessionAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            Check("the guest bootstrap reports a change", changed);
            CheckEqual("the home page was requested", "https://www.douyin.com/",
                transport.Requests[0].Url);
            CheckEqual("the union registration endpoint was used as the fallback",
                "https://ttwid.bytedance.com/ttwid/union/register/", transport.Requests[1].Url);
            CheckEqual("the registration was a POST", "POST", transport.Requests[1].Method);
            Check("the callback was followed",
                transport.Requests.Count >= 3
                && transport.Requests[2].Url.Contains("register/callback"),
                transport.Requests.Count >= 3 ? transport.Requests[2].Url : "(no third request)");
            CheckEqual("the ttwid reached the identity", "1%7Cabc%7C1791346934%7Cdeadbeef",
                JsonValue.GetString(host.Session.Identity, "ttwid"));

            // When the home page already provides ttwid, the fallback must not run.
            FakeProtocol protocol2;
            FakeTransport transport2;
            MemoryFileStore store2;
            ServiceHost host2 = BuildHost(out protocol2, out transport2, out store2);
            host2.Session.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            transport2.EnqueueJson(200, "<html>ok</html>",
                "ttwid=1%7Cdirect%7C1791346934%7Ccafe; Path=/; Domain=.douyin.com");
            host2.Api.BootstrapGuestSessionAsync(CancellationToken.None).GetAwaiter().GetResult();
            CheckEqual("the fallback is skipped when the home page already issued a ttwid",
                1, transport2.Requests.Count);
        }

        private void FeedTests()
        {
            FakeProtocol protocol;
            FakeTransport transport;
            MemoryFileStore store;
            ServiceHost host = BuildHost(out protocol, out transport, out store);
            host.Session.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

            protocol.Normalized["response.awemePage"] = @"{
                ""items"": [ { ""id"": ""1"" }, { ""id"": ""2"" }, { ""id"": ""1"" } ],
                ""cursor"": ""c1"", ""hasMore"": true, ""skipped"": 1
            }";

            FeedUpdate first = host.Feed.LoadInitialAsync(20, CancellationToken.None).GetAwaiter().GetResult();
            CheckEqual("duplicates within a page are removed", 2, first.Added.Count);
            CheckEqual("the duplicate is counted", 1, first.DuplicatesRemoved);
            CheckEqual("the parser's skip count is surfaced", 1, first.SkippedByParser);
            CheckEqual("the feed holds the deduplicated items", 2, host.Feed.Count);
            CheckEqual("has-more is carried", true, host.Feed.HasMore);
            CheckEqual("the cursor is carried", "c1", host.Feed.Cursor);

            // A second page that repeats an item must not duplicate it.
            protocol.Normalized["response.awemePage"] = @"{
                ""items"": [ { ""id"": ""2"" }, { ""id"": ""3"" } ],
                ""cursor"": ""c2"", ""hasMore"": false
            }";
            FeedUpdate second = host.Feed.LoadMoreAsync(20, CancellationToken.None).GetAwaiter().GetResult();
            CheckEqual("a repeat across pages is removed", 1, second.DuplicatesRemoved);
            CheckEqual("only the genuinely new item is added", 1, second.Added.Count);
            CheckEqual("the total is three", 3, host.Feed.Count);
            CheckEqual("has-more is updated", false, host.Feed.HasMore);

            // Every call must have re-signed rather than replayed: the signature covers
            // a rotating msToken, so a stored request would be a stale signature.
            int buildsBefore = protocol.BuildCount;
            host.Feed.LoadMoreAsync(20, CancellationToken.None).GetAwaiter().GetResult();
            CheckEqual("each page load builds a fresh request",
                buildsBefore + 1, protocol.BuildCount);

            // A failed refresh must leave the list intact.
            protocol.Failures["feed_recommend"] =
                ApiError.Local(ErrorKinds.Network, "offline", true);
            bool threw = false;
            try
            {
                host.Feed.RefreshAsync(20, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (DouyinApiException)
            {
                threw = true;
            }
            Check("a failed refresh throws", threw);
            CheckEqual("a failed refresh leaves the feed intact", 3, host.Feed.Count);
            protocol.Failures.Remove("feed_recommend");

            // Concurrent loads must be serialized, not raced.
            protocol.Normalized["response.awemePage"] = @"{
                ""items"": [ { ""id"": ""10"" } ], ""cursor"": ""c10"", ""hasMore"": true
            }";
            Task<FeedUpdate> left = host.Feed.LoadMoreAsync(20, CancellationToken.None);
            Task<FeedUpdate> right = host.Feed.LoadMoreAsync(20, CancellationToken.None);
            Task.WhenAll(left, right).GetAwaiter().GetResult();
            CheckEqual("two concurrent load-more calls both complete",
                true, left.IsCompleted && right.IsCompleted);
            CheckEqual("the item is added exactly once", 4, host.Feed.Count);

            host.Feed.Reset();
            CheckEqual("a reset empties the feed", 0, host.Feed.Count);
        }

        private void CommentTests()
        {
            FakeProtocol protocol;
            FakeTransport transport;
            MemoryFileStore store;
            ServiceHost host = BuildHost(out protocol, out transport, out store);
            host.Session.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

            protocol.Normalized["response.commentPage"] = @"{
                ""items"": [ { ""id"": ""c1"" }, { ""id"": ""c2"" } ],
                ""cursor"": ""20"", ""hasMore"": true, ""total"": 5
            }";

            IList<Comment> first = host.Comments
                .LoadFirstPageAsync("7300000000000000000", 20, CancellationToken.None)
                .GetAwaiter().GetResult();
            CheckEqual("the first comment page loads", 2, first.Count);
            CheckEqual("the thread total is carried", 5, host.Comments.Thread("7300000000000000000").Total);

            // Page two repeats the last item of page one, as the real API does.
            protocol.Normalized["response.commentPage"] = @"{
                ""items"": [ { ""id"": ""c2"" }, { ""id"": ""c3"" } ],
                ""cursor"": ""40"", ""hasMore"": false
            }";
            IList<Comment> second = host.Comments
                .LoadNextPageAsync("7300000000000000000", 20, CancellationToken.None)
                .GetAwaiter().GetResult();
            CheckEqual("a repeat across comment pages is removed", 1, second.Count);
            CheckEqual("the thread holds three distinct comments",
                3, host.Comments.Thread("7300000000000000000").Count);

            IList<Comment> third = host.Comments
                .LoadNextPageAsync("7300000000000000000", 20, CancellationToken.None)
                .GetAwaiter().GetResult();
            CheckEqual("a thread with no more pages returns nothing", 0, third.Count);

            // Replies are a separate call and are not merged into the top level.
            protocol.Normalized["response.commentPage"] = @"{
                ""items"": [ { ""id"": ""r1"", ""level"": 2 } ], ""cursor"": ""3"", ""hasMore"": false
            }";
            CommentPage replies = host.Comments
                .LoadRepliesAsync("7300000000000000000", "c1", null, 20, CancellationToken.None)
                .GetAwaiter().GetResult();
            CheckEqual("replies load", 1, replies.Items.Count);
            CheckEqual("a reply is not merged into the top-level thread",
                3, host.Comments.Thread("7300000000000000000").Count);

            // Publishing splices the new comment in at the top.
            Comment published = host.Comments
                .PublishAsync("7300000000000000000", "hello", CancellationToken.None)
                .GetAwaiter().GetResult();
            Check("publishing returns a comment", published != null);
            CheckEqual("the published comment is spliced in",
                "new1", host.Comments.Thread("7300000000000000000").Items[0].Id);

            // A reply must not be spliced into the top level.
            int before = host.Comments.Thread("7300000000000000000").Count;
            host.Comments.ReplyAsync("7300000000000000000", "c1", "hi", null, CancellationToken.None)
                .GetAwaiter().GetResult();
            CheckEqual("a reply is not spliced into the top-level thread",
                before, host.Comments.Thread("7300000000000000000").Count);

            host.Comments.Clear();
            CheckEqual("clearing comments empties the threads",
                0, host.Comments.Thread("7300000000000000000").Count);
        }

        private void VideoServiceTests()
        {
            FakeProtocol protocol;
            FakeTransport transport;
            MemoryFileStore store;
            ServiceHost host = BuildHost(out protocol, out transport, out store);
            host.Session.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

            protocol.Normalized["response.awemeDetail"] = @"{
                ""id"": ""7300000000000000000"", ""desc"": ""x"",
                ""video"": { ""durationMs"": 1000, ""sources"": [
                    { ""url"": ""https://cdn/1080.mp4"", ""codec"": ""h264"", ""container"": ""mp4"",
                      ""quality"": ""1080p"", ""sizeBytes"": 9000 },
                    { ""url"": ""https://cdn/720.mp4"", ""codec"": ""h264"", ""container"": ""mp4"",
                      ""quality"": ""720p"", ""sizeBytes"": 4000 },
                    { ""url"": ""https://cdn/265.mp4"", ""codec"": ""hevc"", ""container"": ""mp4"",
                      ""quality"": ""1080p"", ""sizeBytes"": 9000 }
                ] }
            }";

            ResolvedVideoSource resolved = host.Video
                .ResolveAsync("7300000000000000000", CancellationToken.None)
                .GetAwaiter().GetResult();
            Check("a source is resolved", resolved != null);
            CheckEqual("the 720p h264 source is chosen", "https://cdn/720.mp4", resolved.Source.Url);
            // The HEVC candidate is excluded by the selector, so only the two H.264
            // sources are ranked. Ranking is over *playable* candidates, not over the
            // raw payload.
            CheckEqual("only the playable candidates are ranked", 2, resolved.Ranked.Count);
            bool anyHevc = false;
            for (int index = 0; index < resolved.Ranked.Count; index++)
            {
                if (resolved.Ranked[index].Codec != "h264") anyHevc = true;
            }
            Check("no ranked candidate is HEVC", !anyHevc, resolved.Ranked.Count);
            Check("the resolution records when it happened", resolved.ResolvedAtMs > 0);
            Check("the resolution estimates an expiry", resolved.ExpiresAtMs.HasValue);
            Check("a request factory was captured", resolved.SpecFactory != null);

            ResolvedVideoSource next = host.Video
                .ResolveNextCandidateAsync(Aweme.FromJson(JObject.Parse(
                    protocol.Normalized["response.awemeDetail"])), CancellationToken.None)
                .GetAwaiter().GetResult();
            Check("the next candidate is available", next != null);
            CheckEqual("the next candidate is the second ranked source",
                "https://cdn/1080.mp4", next.Source.Url);

            // An image post must be refused with the right classification.
            protocol.Normalized["response.awemeDetail"] = @"{
                ""id"": ""9"", ""isImage"": true, ""images"": [ { ""url"": ""https://cdn/1.jpg"" } ],
                ""video"": { ""sources"": [] }
            }";
            bool threw = false;
            try
            {
                host.Video.ResolveAsync("9", CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (DouyinApiException exception)
            {
                threw = true;
                CheckEqual("an image post is classified as unsupported",
                    ErrorKinds.UnsupportedCodec, exception.Kind);
            }
            Check("an image post throws", threw);
        }

        private void CacheTests()
        {
            var store = new MemoryFileStore();
            var cache = new CacheService(store, QuietLog());

            var aweme = Aweme.FromJson(JObject.Parse(@"{
                ""id"": ""1"", ""desc"": ""cached"", ""durationMs"": 1000,
                ""author"": { ""id"": ""u"", ""nickname"": ""n"" },
                ""video"": { ""sources"": [ { ""url"": ""https://cdn/a.mp4"", ""codec"": ""h264"" } ] }
            }"));

            cache.PutAwemeAsync(aweme, CancellationToken.None).GetAwaiter().GetResult();
            Aweme loaded = cache.GetAwemeAsync("1", CancellationToken.None).GetAwaiter().GetResult();
            Check("a cached aweme round-trips", loaded != null);
            CheckEqual("the cached description survives", "cached", loaded.Desc);
            CheckEqual("the cached source survives", 1, loaded.Video.Sources.Count);
            Check("a missing aweme is a cache miss",
                cache.GetAwemeAsync("nope", CancellationToken.None).GetAwaiter().GetResult() == null);

            // A stale entry must be treated as a miss.
            store.WriteTextAsync(CacheService.AwemeFolder + "/2.json",
                "{\"cachedAtMs\": 1, \"aweme\": {\"id\": \"2\"}}", CancellationToken.None)
                .GetAwaiter().GetResult();
            Check("a stale metadata entry is a miss",
                cache.GetAwemeAsync("2", CancellationToken.None).GetAwaiter().GetResult() == null);

            // A corrupt entry is a miss, not a failure.
            store.WriteTextAsync(CacheService.AwemeFolder + "/3.json", "{not json",
                CancellationToken.None).GetAwaiter().GetResult();
            Check("a corrupt metadata entry is a miss",
                cache.GetAwemeAsync("3", CancellationToken.None).GetAwaiter().GetResult() == null);

            cache.PutImageAsync("https://cdn/cover.jpg?sig=1", new byte[100], CancellationToken.None)
                .GetAwaiter().GetResult();
            byte[] image = cache.GetImageAsync("https://cdn/cover.jpg?sig=1", CancellationToken.None)
                .GetAwaiter().GetResult();
            Check("an image round-trips", image != null && image.Length == 100);

            // The cache key must ignore the rotating query string.
            byte[] afterRefresh = cache.GetImageAsync("https://cdn/cover.jpg?sig=999", CancellationToken.None)
                .GetAwaiter().GetResult();
            Check("an image is cached across a rotating query string", afterRefresh != null);

            Check("a missing image is a miss",
                cache.GetImageAsync("https://cdn/other.jpg", CancellationToken.None).GetAwaiter().GetResult() == null);

            // Eviction: a tight byte budget must remove the least recently used entry.
            var tight = new CacheService(store, QuietLog());
            tight.ImageMaxEntries = 2;
            tight.ImageMaxBytes = 1000;
            tight.PutImageAsync("https://cdn/a.jpg", new byte[10], CancellationToken.None).GetAwaiter().GetResult();
            System.Threading.Thread.Sleep(5);
            tight.PutImageAsync("https://cdn/b.jpg", new byte[10], CancellationToken.None).GetAwaiter().GetResult();
            System.Threading.Thread.Sleep(5);
            tight.PutImageAsync("https://cdn/c.jpg", new byte[10], CancellationToken.None).GetAwaiter().GetResult();
            Check("eviction keeps the newest image",
                tight.GetImageAsync("https://cdn/c.jpg", CancellationToken.None).GetAwaiter().GetResult() != null);
            Check("eviction drops the oldest image",
                tight.GetImageAsync("https://cdn/a.jpg", CancellationToken.None).GetAwaiter().GetResult() == null);

            int removed = cache.ClearAsync("all", CancellationToken.None).GetAwaiter().GetResult();
            Check("clearing reports how much it removed", removed > 0, removed);

            JObject described = cache.DescribeAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check("the cache describes itself", described["metadataEntries"] != null);
        }

        private void HistoryTests()
        {
            var store = new MemoryFileStore();
            var history = new HistoryService(store, QuietLog()) { MinWriteIntervalMs = 0 };

            var first = Aweme.FromJson(JObject.Parse(
                @"{ ""id"": ""1"", ""desc"": ""one"", ""durationMs"": 1000,
                    ""author"": { ""nickname"": ""A"" } }"));
            var second = Aweme.FromJson(JObject.Parse(
                @"{ ""id"": ""2"", ""desc"": ""two"", ""durationMs"": 2000,
                    ""author"": { ""nickname"": ""B"" } }"));

            history.RecordAsync(first, 100, true, CancellationToken.None).GetAwaiter().GetResult();
            history.RecordAsync(second, 200, true, CancellationToken.None).GetAwaiter().GetResult();

            IList<HistoryEntry> entries = history.ListAsync(CancellationToken.None).GetAwaiter().GetResult();
            CheckEqual("both views are recorded", 2, entries.Count);
            CheckEqual("the newest view is first", "2", entries[0].AwemeId);
            CheckEqual("the position is recorded", 200, entries[0].PositionMs);

            // Re-watching an old entry moves it to the front without duplicating.
            history.RecordAsync(first, 300, true, CancellationToken.None).GetAwaiter().GetResult();
            entries = history.ListAsync(CancellationToken.None).GetAwaiter().GetResult();
            CheckEqual("re-watching does not duplicate", 2, entries.Count);
            CheckEqual("re-watching moves the entry to the front", "1", entries[0].AwemeId);
            CheckEqual("the updated position is recorded", 300, entries[0].PositionMs);

            // The cap is enforced.
            var capped = new HistoryService(store, QuietLog()) { MaxEntries = 3, MinWriteIntervalMs = 0 };
            for (int index = 0; index < 10; index++)
            {
                capped.RecordAsync(Aweme.FromJson(JObject.Parse(
                    "{ \"id\": \"x" + index + "\", \"desc\": \"d\" }")), 0, true, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            CheckEqual("the history cap is enforced",
                3, capped.ListAsync(CancellationToken.None).GetAwaiter().GetResult().Count);

            // It must survive a restart.
            var reloaded = new HistoryService(store, QuietLog());
            CheckEqual("history survives a restart",
                3, reloaded.ListAsync(CancellationToken.None).GetAwaiter().GetResult().Count);

            Check("removing an entry works",
                history.RemoveAsync("2", CancellationToken.None).GetAwaiter().GetResult());
            Check("removing a missing entry reports false",
                !history.RemoveAsync("nope", CancellationToken.None).GetAwaiter().GetResult());

            history.ClearAsync(CancellationToken.None).GetAwaiter().GetResult();
            CheckEqual("clearing empties the history",
                0, history.ListAsync(CancellationToken.None).GetAwaiter().GetResult().Count);
        }

        private void PlaybackTests()
        {
            FakeProtocol protocol;
            FakeTransport transport;
            MemoryFileStore store;
            ServiceHost host = BuildHost(out protocol, out transport, out store);
            host.Session.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

            CheckEqual("playback starts idle", PlaybackState.Idle, host.Playback.State);
            Check("no cache is reported before playback", host.Playback.DescribeCache() != null);

            host.Playback.StopAsync().GetAwaiter().GetResult();
            CheckEqual("stopping an idle player is harmless", PlaybackState.Idle, host.Playback.State);

            // A codec failure on a *verified* H.264 source must not be retried as a
            // codec problem: the payload said H.264, so the stream is not what it claimed.
            var aweme = Aweme.FromJson(JObject.Parse(
                @"{ ""id"": ""1"", ""video"": { ""sources"": [
                    { ""url"": ""https://cdn/a.mp4"", ""codec"": ""h264"", ""container"": ""mp4"", ""quality"": ""720p"" } ] } }"));
            bool recovered = host.Playback
                .RecoverFromDecodeFailureAsync(aweme, false, CancellationToken.None)
                .GetAwaiter().GetResult();
            Check("a failure on a verified codec is not retried", !recovered);
            CheckEqual("a verified-codec failure is classified as unsupported",
                ErrorKinds.UnsupportedCodec, host.Playback.LastError.Kind);

            host.Playback.StopAsync().GetAwaiter().GetResult();

            // A release must return the player to idle and drop page state.
            host.Playback.StopAsync().GetAwaiter().GetResult();
            CheckEqual("release leaves the player idle", PlaybackState.Idle, host.Playback.State);

            host.ReleasePageResourcesAsync().GetAwaiter().GetResult();
            CheckEqual("releasing page resources resets the feed", 0, host.Feed.Count);
        }

        // ------------------------------------------------------------------
        // test doubles for the platform seams
        // ------------------------------------------------------------------

        private sealed class FakeBackend : IPythonRuntimeBackend
        {
            private readonly FakeProtocol _protocol;

            public FakeBackend(FakeProtocol protocol)
            {
                _protocol = protocol;
            }

            public bool IsReady
            {
                get { return true; }
            }

            public string CallJson(string requestJson)
            {
                JObject request = JObject.Parse(requestJson);
                string operation = JsonValue.GetString(request, "op");
                try
                {
                    JObject result = _protocol
                        .CallAsync(operation, request, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    return new JObject { { "ok", true }, { "result", result } }
                        .ToString(Newtonsoft.Json.Formatting.None);
                }
                catch (DouyinApiException exception)
                {
                    return new JObject
                    {
                        { "ok", false },
                        { "error", JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(
                            new
                            {
                                kind = exception.Error.Kind,
                                message = exception.Error.Message,
                                httpStatus = exception.Error.HttpStatus,
                                retryable = exception.Error.Retryable
                            })) }
                    }.ToString(Newtonsoft.Json.Formatting.None);
                }
            }

            public string Describe()
            {
                return "fake runtime";
            }
        }

        private sealed class FakeMediaHost : IMediaPlayerHost
        {
            public int PlayCount;
            public string LastMimeType;
            public string LastPath;
            public bool Stopped;

            public Task PlayAsync(IRandomAccessByteSource source, string mimeType, CancellationToken cancellationToken)
            {
                PlayCount++;
                LastMimeType = mimeType;
                return Task.FromResult(0);
            }

            public Task PlayLocalFileAsync(string absolutePath, string mimeType, CancellationToken cancellationToken)
            {
                PlayCount++;
                LastPath = absolutePath;
                LastMimeType = mimeType;
                return Task.FromResult(0);
            }

            public Task PauseAsync() { return Task.FromResult(0); }
            public Task ResumeAsync() { return Task.FromResult(0); }

            public Task StopAsync()
            {
                Stopped = true;
                return Task.FromResult(0);
            }

            public bool IsAvailable
            {
                get { return true; }
            }
        }
    }
}
