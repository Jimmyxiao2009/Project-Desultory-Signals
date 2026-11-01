using System;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Media;
using DesultorySignals.Core.Models;

namespace DesultorySignals.Core.Services
{
    /// <summary>Where playback is in its lifecycle. Coarse on purpose: the UI needs
    /// to distinguish "working", "playing", "needs new sources" and "broken", not to
    /// model the decoder's internal state.</summary>
    public enum PlaybackState
    {
        Idle = 0,
        Resolving = 1,
        Downloading = 2,
        Preparing = 3,
        Playing = 4,
        Paused = 5,
        Failed = 6
    }

    /// <summary>
    /// The platform half of playback: owns the actual media surface.
    /// </summary>
    /// <remarks>
    /// Core decides *what* to play and manages the byte stream; the host owns the
    /// <c>MediaElement</c> and reports back. The split exists because
    /// <c>MediaElement</c> and <c>IRandomAccessStream</c> are WinRT types that the
    /// testable core cannot reference -- and because a decoder failure is platform
    /// information that only the host can observe.
    /// </remarks>
    public interface IMediaPlayerHost
    {
        /// <summary>Play from a random-access source. The host wraps it in an
        /// <c>IRandomAccessStream</c> and calls <c>MediaElement.SetSource</c>.</summary>
        Task PlayAsync(IRandomAccessByteSource source, string mimeType, CancellationToken cancellationToken);

        /// <summary>Play a file already in local storage, by absolute path.</summary>
        Task PlayLocalFileAsync(string absolutePath, string mimeType, CancellationToken cancellationToken);

        Task PauseAsync();
        Task ResumeAsync();
        Task StopAsync();

        /// <summary>True when the host believes the media surface is usable. Lets the
        /// service distinguish "no MediaElement" from "the file is broken".</summary>
        bool IsAvailable { get; }
    }

    /// <summary>
    /// Drives the video path: resolve a playable source, get bytes, hand them to the
    /// platform, recover from an expired URL.
    /// </summary>
    /// <remarks>
    /// The brief sequences this deliberately: first prove the whole chain by
    /// downloading the file and playing it locally, then upgrade to range streaming.
    /// Both paths exist here and share everything except the byte delivery, because
    /// the first one is the diagnostic that tells you whether a failure is the
    /// network, the TLS stack, the MP4 or the decoder -- and that distinction is
    /// lost the moment streaming is the only option.
    ///
    /// Recovery is the interesting part. A Douyin play URL is signed and short-lived,
    /// so a failure mid-playback is expected rather than exceptional:
    ///
    /// * the source is refreshed through <see cref="VideoService"/>,
    /// * the byte source is rebuilt against the new URL,
    /// * playback restarts at the last known position.
    ///
    /// A codec failure is *not* retried that way. When the selected source was
    /// labelled <c>unknown</c>, the next ranked candidate is tried instead, because
    /// the payload never told us the codec and a decode failure is the only evidence
    /// we will get.
    /// </remarks>
    public sealed class PlaybackService
    {
        private readonly VideoService _video;
        private readonly Networking.IHttpTransport _transport;
        private readonly Storage.IFileStore _store;
        private readonly IMediaPlayerHost _host;
        private readonly DiagLog _log;
        private readonly object _gate = new object();

        private IRandomAccessByteSource _currentSource;
        private Aweme _current;
        private PlaybackState _state = PlaybackState.Idle;
        private ApiError _lastError;

        public PlaybackService(
            VideoService video,
            Networking.IHttpTransport transport,
            Storage.IFileStore store,
            IMediaPlayerHost host,
            DiagLog log)
        {
            if (video == null) throw new ArgumentNullException("video");
            if (transport == null) throw new ArgumentNullException("transport");
            if (store == null) throw new ArgumentNullException("store");
            if (host == null) throw new ArgumentNullException("host");
            _video = video;
            _transport = transport;
            _store = store;
            _host = host;
            _log = log;
        }

        public PlaybackState State
        {
            get { lock (_gate) { return _state; } }
        }

        public Aweme Current
        {
            get { lock (_gate) { return _current; } }
        }

        public ApiError LastError
        {
            get { lock (_gate) { return _lastError; } }
        }

        public IRandomAccessByteSource CurrentSource
        {
            get { lock (_gate) { return _currentSource; } }
        }

        public event Action<PlaybackState> StateChanged;

        public event Action<ApiError> Failed;

        /// <summary>
        /// Phase 3 path: fetch the whole file into local storage, then play it.
        /// </summary>
        /// <remarks>
        /// Worth keeping even after streaming works: it isolates variables. If this
        /// path plays and streaming does not, the problem is range handling; if
        /// neither plays, the problem is the decoder, the container or the bytes.
        /// </remarks>
        public async Task<string> DownloadToLocalAsync(Aweme aweme, CancellationToken cancellationToken)
        {
            if (aweme == null) throw new ArgumentNullException("aweme");

            SetState(PlaybackState.Resolving);
            ResolvedVideoSource resolved = await _video
                .ResolveAsync(aweme, cancellationToken).ConfigureAwait(false);

            string relativePath = "media/" + aweme.Id + ".mp4";            SetState(PlaybackState.Downloading);

            var request = new Networking.HttpRequestSpec
            {
                Method = "GET",
                Url = resolved.Source.Url,
                Tag = "download:" + aweme.Id,
                TimeoutMs = 120000,
                StreamResponse = true
            };
            foreach (var header in resolved.Headers)
            {
                request.AddHeader(header.Key, header.Value);
            }

            long startedAt = Environment.TickCount;
            long written = 0;

            using (Networking.HttpResponseData response =
                await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (response == null || !response.IsSuccess || response.BodyStream == null)
                {
                    throw Fail(new ApiError(
                        response != null && response.StatusCode == 403
                            ? ErrorKinds.NotFound
                            : ErrorKinds.HttpError,
                        string.Format("the download failed with HTTP {0}",
                            response == null ? 0 : response.StatusCode),
                        response == null ? (int?)null : response.StatusCode,
                        false,
                        null));
                }

                using (System.IO.Stream target = await _store
                    .OpenWriteAsync(relativePath, cancellationToken).ConfigureAwait(false))
                {
                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int read = await response.BodyStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                            .ConfigureAwait(false);
                        if (read <= 0) break;
                        await target.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                        written += read;
                    }
                }
            }

            _log.Info(DiagCategory.Playback, "video downloaded",
                new Newtonsoft.Json.Linq.JObject
                {
                    { "awemeId", aweme.Id },
                    { "bytes", written },
                    { "ms", Environment.TickCount - startedAt }
                });

            return relativePath;
        }

        /// <summary>Phase 3 playback: download to local storage, then hand it to the host.</summary>
        public async Task PlayDownloadedAsync(
            Aweme aweme, string absoluteMediaRoot, CancellationToken cancellationToken)
        {
            string relativePath = await DownloadToLocalAsync(aweme, cancellationToken).ConfigureAwait(false);
            string absolutePath = CombineAbsolute(absoluteMediaRoot, relativePath);

            SetState(PlaybackState.Preparing);
            await _host.PlayLocalFileAsync(absolutePath, "video/mp4", cancellationToken).ConfigureAwait(false);
            SetState(PlaybackState.Playing);
        }

        /// <summary>
        /// Phase 4 playback: stream from the CDN with range requests.
        /// </summary>
        /// <remarks>
        /// The URL-refresh callback is the piece that makes this survivable: the byte
        /// source calls it when the CDN refuses a range, and it asks
        /// <see cref="VideoService"/> for fresh sources for the same aweme, which
        /// re-signs the request.
        /// </remarks>
        public async Task PlayStreamingAsync(Aweme aweme, CancellationToken cancellationToken)
        {
            if (aweme == null) throw new ArgumentNullException("aweme");

            SetState(PlaybackState.Resolving);
            ResolvedVideoSource resolved = await _video
                .ResolveAsync(aweme, cancellationToken).ConfigureAwait(false);

            await PlayResolvedAsync(aweme, resolved, cancellationToken).ConfigureAwait(false);
        }

        private async Task PlayResolvedAsync(
            Aweme aweme, ResolvedVideoSource resolved, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _current = aweme;
                if (_currentSource != null)
                {
                    try
                    {
                        _currentSource.Dispose();
                    }
                    catch (Exception)
                    {
                        // Nothing useful to do; the replacement is what matters.
                    }
                    _currentSource = null;
                }
            }

            string awemeId = aweme.Id;

            var options = new RangeSourceOptions
            {
                Url = resolved.Source.Url,
                SpecFactory = resolved.SpecFactory,
                TotalSize = resolved.Source.SizeBytes ?? -1,
                ChunkSize = resolved.ChunkSize,
                MaxChunks = resolved.MaxCachedChunks,
                Retry = Networking.RetryPolicy.ForRangeReads(),
                MimeType = "video/mp4",
                Tag = "play:" + awemeId,
                RefreshUrlAsync = async token =>
                {
                    _log.Warn(DiagCategory.Playback, "media URL refused; refreshing sources for " + awemeId);
                    ResolvedVideoSource fresh = await _video
                        .RefreshSourcesAsync(aweme, token).ConfigureAwait(false);
                    return fresh == null ? null : fresh.Source.Url;
                }
            };

            var source = new RemoteRandomAccessByteSource(options, _transport, _log);

            lock (_gate)
            {
                _currentSource = source;
            }

            SetState(PlaybackState.Preparing);
            try
            {
                await _host.PlayAsync(source, "video/mp4", cancellationToken).ConfigureAwait(false);
                SetState(PlaybackState.Playing);
            }
            catch (Exception exception)
            {
                throw Fail(new ApiError(
                    ErrorKinds.Internal,
                    "the media host could not start playback: " + exception.Message,
                    null,
                    false,
                    null));
            }
        }

        /// <summary>
        /// Report a decoder failure observed by the host, and decide whether another
        /// candidate is worth trying.
        /// </summary>
        /// <remarks>
        /// Only a source whose codec was never verified earns a second attempt. If the
        /// payload said H.264 and the decoder still refused, the problem is not the
        /// codec and trying the next one would just waste bandwidth.
        /// </remarks>
        public async Task<bool> RecoverFromDecodeFailureAsync(
            Aweme aweme, bool codecWasUnverified, CancellationToken cancellationToken)
        {
            if (aweme == null) return false;

            if (!codecWasUnverified)
            {
                Fail(ApiError.Local(
                    ErrorKinds.UnsupportedCodec,
                    "the decoder refused a source that the payload labelled H.264; "
                        + "the stream is not what it claimed",
                    false));
                return false;
            }

            _log.Warn(DiagCategory.Playback,
                "the unverified-codec source failed to decode; trying the next ranked candidate");

            ResolvedVideoSource next = await _video
                .ResolveNextCandidateAsync(aweme, cancellationToken).ConfigureAwait(false);
            if (next == null)
            {
                Fail(ApiError.Local(
                    ErrorKinds.UnsupportedCodec,
                    "every candidate source failed to decode on this device",
                    false));
                return false;
            }

            await PlayResolvedAsync(aweme, next, cancellationToken).ConfigureAwait(false);
            return true;
        }

        public async Task StopAsync()
        {
            IRandomAccessByteSource source;
            lock (_gate)
            {
                source = _currentSource;
                _currentSource = null;
                _current = null;
            }

            try
            {
                await _host.StopAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Stopping must be idempotent and must never throw.
            }

            if (source != null)
            {
                try
                {
                    source.Dispose();
                }
                catch (Exception)
                {
                }
            }

            SetState(PlaybackState.Idle);
        }

        /// <summary>Cache statistics for the diagnostics page.</summary>
        public string DescribeCache()
        {
            lock (_gate)
            {
                var source = _currentSource as RemoteRandomAccessByteSource;
                return source == null ? "(no active source)" : source.CacheStats.ToString();
            }
        }

        private void SetState(PlaybackState state)
        {
            lock (_gate)
            {
                if (_state == state) return;
                _state = state;
            }
            Action<PlaybackState> handler = StateChanged;
            if (handler != null) handler(state);
        }

        private DouyinApiException Fail(ApiError error)
        {
            lock (_gate)
            {
                _lastError = error;
                _state = PlaybackState.Failed;
            }
            Action<ApiError> handler = Failed;
            if (handler != null) handler(error);
            _log.Error(DiagCategory.Playback, error.Describe());
            return new DouyinApiException(error);
        }

        private static string CombineAbsolute(string root, string relative)
        {
            if (string.IsNullOrEmpty(root)) return relative;
            string separator = root.EndsWith("\\", StringComparison.Ordinal) ? string.Empty : "\\";
            return root + separator + relative.Replace('/', '\\');
        }
    }
}
