using System;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Models;
using DesultorySignals.Core.Networking;

namespace DesultorySignals.Core.Media
{
    /// <summary>
    /// Random-access video bytes fetched from a Douyin CDN with HTTP range
    /// requests, backed by a bounded chunk cache.
    /// </summary>
    /// <remarks>
    /// The design answers four specific hazards of playing a CDN video on a phone:
    ///
    /// * **The player seeks.** <c>MediaElement</c> reads the trailing moov atom
    ///   before the first frame, so the very first access pattern is a seek to the
    ///   end. Each chunk is fetched on demand and cached, so that costs one request
    ///   rather than a full download.
    ///
    /// * **The URL expires.** Douyin's play URLs are signed and short-lived. A 403 or
    ///   404 mid-playback is not a network error; it means the URL is stale. When a
    ///   refresh callback was supplied the source swaps the URL, drops the cache and
    ///   retries once. Without a callback it reports the failure instead of looping.
    ///
    /// * **The server ignores Range.** Some CDN responses answer 200 with the whole
    ///   file rather than 206. That is handled: the body is treated as a chunk at
    ///   offset 0 and the real size is taken from it, so playback still works
    ///   instead of silently reading misaligned bytes.
    ///
    /// * **The connection stalls.** Each fetch has its own short timeout and its own
    ///   retry budget, so one bad chunk cannot wedge the player for the duration of
    ///   a long download.
    ///
    /// Not thread safe for the *position*, but safe for concurrent reads: the cache
    /// and the size discovery are guarded, which is what matters, since the media
    /// pipeline may issue overlapping reads.
    /// </remarks>
    public sealed class RemoteRandomAccessByteSource : IRandomAccessByteSource
    {
        private readonly RangeSourceOptions _options;
        private readonly IHttpTransport _transport;
        private readonly RangeCache _cache;
        private readonly DiagLog _log;
        private readonly object _gate = new object();

        private string _url;
        private long _size = -1;
        private bool _sizeConfirmed;
        private int _refreshes;
        private bool _disposed;

        /// <summary>Give up after this many URL refreshes: a CDN that keeps refusing
        /// fresh URLs is not going to be fixed by asking again.</summary>
        private const int MaxRefreshes = 2;

        public RemoteRandomAccessByteSource(
            RangeSourceOptions options,
            IHttpTransport transport,
            DiagLog log)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (transport == null) throw new ArgumentNullException("transport");
            if (string.IsNullOrEmpty(options.Url)) throw new ArgumentException("options.Url is required", "options");

            _options = options;
            _transport = transport;
            _log = log;
            _url = options.Url;
            _size = options.TotalSize;
            _cache = new RangeCache(options.ChunkSize, options.MaxChunks, log);
        }

        public long Size
        {
            get { lock (_gate) { return _size; } }
        }

        public string ContentType
        {
            get { return _options.MimeType ?? "video/mp4"; }
        }

        public int ChunkSize
        {
            get { return _options.ChunkSize; }
        }

        public RangeCacheStats CacheStats
        {
            get { return _cache.Snapshot(); }
        }

        public int RefreshCount
        {
            get { lock (_gate) { return _refreshes; } }
        }

        public async Task<int> ReadAsync(
            long position, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (position < 0) throw new ArgumentOutOfRangeException("position");
            if (count <= 0) return 0;
            if (offset < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException("offset");

            long total;
            lock (_gate) { total = _size; }

            if (total >= 0 && position >= total) return 0;
            if (total >= 0 && position + count > total) count = (int)(total - position);

            int written = 0;
            while (written < count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long current = position + written;
                int chunkIndex = RangeMath.ChunkIndex(current, _options.ChunkSize);

                byte[] chunk = _cache.TryGet(chunkIndex);
                if (chunk == null)
                {
                    chunk = await FetchChunkAsync(chunkIndex, cancellationToken).ConfigureAwait(false);
                    if (chunk == null || chunk.Length == 0) break;
                }

                long chunkStart = RangeMath.ChunkStart(chunkIndex, _options.ChunkSize);
                // The destination window is exactly buffer[offset .. offset+count),
                // which maps to absolute [position, position+count). Both the offset
                // and the absolute start are needed: `written` is already accounted for
                // because the overlap start advances with it, so passing
                // `offset + written` here would double-count and write past the end of
                // the caller's buffer on the third chunk.
                int copied = RangeMath.CopyOverlap(
                    chunk, chunkStart, chunk.Length,
                    buffer, offset, position, count);

                if (copied == 0) break; // chunk did not cover this offset; stop rather than loop

                written += copied;

                // A short chunk means end of stream.
                if (chunk.Length < _options.ChunkSize) break;
            }

            return written;
        }

        private async Task<byte[]> FetchChunkAsync(int chunkIndex, CancellationToken cancellationToken)
        {
            long chunkStart = RangeMath.ChunkStart(chunkIndex, _options.ChunkSize);
            long total;
            lock (_gate) { total = _size; }

            long chunkEnd = RangeMath.ChunkEnd(chunkIndex, _options.ChunkSize, total);
            if (chunkEnd < 0) return null;

            byte[] data = await _options.Retry.ExecuteAsync(
                async (attempt, token) =>
                {
                    // The URL is read inside the retry lambda, not captured: a 403
                    // triggers a refresh, and the retry must use the *new* URL.
                    string current;
                    lock (_gate) { current = _url; }
                    return await FetchOnceAsync(current, chunkStart, chunkEnd, token).ConfigureAwait(false);
                },
                _log,
                DiagCategory.Range,
                string.Format("{0}:chunk{1}", _options.Tag, chunkIndex),
                cancellationToken).ConfigureAwait(false);

            if (data != null && data.Length > 0)
            {
                _cache.Put(chunkIndex, data);
                if (_log != null && _log.MinimumLevel <= DiagLevel.Trace)
                {
                    _log.Trace(DiagCategory.Range, string.Format(
                        "{0}: chunk {1} fetched ({2} bytes)", _options.Tag, chunkIndex, data.Length));
                }
            }
            return data;
        }

        private async Task<byte[]> FetchOnceAsync(string url, long start, long end, CancellationToken cancellationToken)
        {
            HttpResponseData response = null;
            string currentUrl = url;
            for (int hop = 0; hop < 5; hop++)
            {
                HttpRequestSpec spec = _options.SpecFactory != null
                    ? _options.SpecFactory(currentUrl)
                    : new HttpRequestSpec { Url = currentUrl, Method = "GET" };
                spec.Method = "GET";
                spec.Url = currentUrl;
                spec.FollowRedirects = false;
                spec.StreamResponse = false;
                spec.Tag = _options.Tag;
                spec.SetRange(start, end);
                response = await _transport.SendAsync(spec, cancellationToken).ConfigureAwait(false);
                if (response == null)
                    throw DouyinApiException.Network(_options.Tag + ": the transport returned nothing", null);
                if (!response.IsRedirect || string.IsNullOrEmpty(response.Location)) break;
                Uri next;
                if (!Uri.TryCreate(new Uri(currentUrl), response.Location, out next)
                    || (next.Scheme != "https" && next.Scheme != "http"))
                    throw new DouyinApiException(ApiError.Local(Models.ErrorKinds.HttpError, "invalid media redirect", false));
                currentUrl = next.AbsoluteUri;
                response.Dispose();
                response = null;
            }
            if (response == null || response.IsRedirect)
                throw new DouyinApiException(ApiError.Local(Models.ErrorKinds.HttpError, "too many media redirects", false));
            if (response.IsSuccess && currentUrl != url)
            {
                lock (_gate) { if (_url == url) _url = currentUrl; }
                if (_log != null) _log.Info(DiagCategory.Range, "resolved media redirect; Range preserved");
            }

            if (response.IsSuccess)
            {
                byte[] body = response.Body ?? new byte[0];

                if (response.StatusCode == 206)
                {
                    long actualStart, actualEnd, actualTotal;
                    if (!RangeMath.TryParseContentRange(response.ContentRange, out actualStart, out actualEnd, out actualTotal)
                        || actualStart != start || actualEnd - actualStart + 1 != body.Length)
                        throw new DouyinApiException(ApiError.Local(Models.ErrorKinds.HttpError, "the media response did not match the requested Range", false));
                    ConfirmSize(response.ContentRange, start, body.Length);
                    return body;
                }

                // 200 to a range request: the server ignored the range and sent the
                // whole resource. Take the requested window out of it rather than
                // returning bytes from the wrong offset.
                if (response.StatusCode == 200)
                {
                    long size;
                    if (response.ContentLength.HasValue && response.ContentLength.Value > 0)
                        size = response.ContentLength.Value;
                    else
                        size = body.Length;

                    SetSize(size);

                    if (start == 0 && body.Length <= _options.ChunkSize)
                    {
                        // Common for small files: the whole file is one chunk.
                        return body;
                    }

                    int wanted = (int)Math.Min(_options.ChunkSize, Math.Max(0, size - start));
                    var slice = new byte[wanted];
                    int copied = RangeMath.CopyOverlap(body, 0, body.Length, slice, 0, start, wanted);
                    if (copied == 0 && start > 0)
                    {
                        throw new DouyinApiException(ApiError.Local(
                            Models.ErrorKinds.HttpError,
                            string.Format(
                                "{0}: the server ignored Range and sent {1} bytes, which does not cover offset {2}",
                                _options.Tag, body.Length, start),
                            false));
                    }
                    if (copied < slice.Length)
                    {
                        var trimmed = new byte[copied];
                        Buffer.BlockCopy(slice, 0, trimmed, 0, copied);
                        return trimmed;
                    }
                    return slice;
                }
            }

            // 403 / 404 / 410 on a signed CDN URL almost always means the signature
            // expired rather than that the object is gone.
            if (response.StatusCode == 403 || response.StatusCode == 404 || response.StatusCode == 410
                || response.IsRedirect)
            {
                bool refreshed = await TryRefreshUrlAsync(cancellationToken).ConfigureAwait(false);
                if (refreshed)
                {
                    throw new DouyinApiException(ApiError.Local(
                        Models.ErrorKinds.EmptyBody,
                        _options.Tag + ": URL refreshed, retry the range read",
                        true));
                }
                throw new DouyinApiException(ApiError.Local(
                    Models.ErrorKinds.NotFound,
                    string.Format("{0}: the media URL was refused (HTTP {1}) and could not be refreshed",
                        _options.Tag, response.StatusCode),
                    false));
            }

            if (response.StatusCode == 416)
            {
                // Requested range not satisfiable: our size is wrong or we are past
                // the end. Re-derive the size from the response and let the caller stop.
                long? total = RangeMath.TotalFromContentRange(response.ContentRange);
                if (total.HasValue && total.Value > 0)
                {
                    SetSize(total.Value);
                    _cache.InvalidateFrom(RangeMath.ChunkIndex(start, _options.ChunkSize),
                        "the server reported the range as unsatisfiable");
                    return null;
                }
            }

            throw new DouyinApiException(ApiError.Local(
                response.StatusCode == 429 ? Models.ErrorKinds.RateLimited : Models.ErrorKinds.HttpError,
                string.Format("{0}: HTTP {1} while reading a range", _options.Tag, response.StatusCode),
                response.StatusCode == 429 || response.StatusCode >= 500));
        }

        private void ConfirmSize(string contentRange, long requestedStart, int received)
        {
            long? total = RangeMath.TotalFromContentRange(contentRange);
            if (total.HasValue && total.Value > 0)
            {
                SetSize(total.Value);
                return;
            }
            // No usable Content-Range: infer from where we are, which is still better
            // than leaving the size unknown and letting the player guess.
            if (!_sizeConfirmed)
            {
                SetSize(requestedStart + received);
            }
        }

        private void SetSize(long size)
        {
            lock (_gate)
            {
                if (_sizeConfirmed && _size == size) return;
                if (_size != size && _size > 0)
                {
                    if (_log != null)
                    {
                        _log.Warn(DiagCategory.Range, string.Format(
                            "{0}: size corrected from {1} to {2}", _options.Tag, _size, size));
                    }
                }
                _size = size;
                _sizeConfirmed = true;
            }
        }

        private async Task<bool> TryRefreshUrlAsync(CancellationToken cancellationToken)
        {
            if (_options.RefreshUrlAsync == null) return false;

            lock (_gate)
            {
                if (_refreshes >= MaxRefreshes) return false;
                _refreshes++;
            }

            string fresh;
            try
            {
                fresh = await _options.RefreshUrlAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (_log != null)
                {
                    _log.Warn(DiagCategory.Range,
                        _options.Tag + ": refreshing the media URL failed: " + exception.Message);
                }
                return false;
            }

            if (string.IsNullOrEmpty(fresh)) return false;

            lock (_gate)
            {
                _url = fresh;
            }
            // Every cached byte belongs to the old URL. Keeping them would be a
            // correctness bug the moment the CDN serves different content.
            _cache.InvalidateAll("the media URL was refreshed");
            if (_log != null)
            {
                _log.Info(DiagCategory.Range, _options.Tag + ": media URL refreshed, cache dropped");
            }
            return true;
        }

        public void InvalidateCache(string reason)
        {
            _cache.InvalidateAll(reason ?? "requested");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cache.InvalidateAll("the source was disposed");
        }
    }
}
