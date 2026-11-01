using System;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Media;
using DesultorySignals.Core.Models;
using DesultorySignals.Core.Services;
using Newtonsoft.Json.Linq;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace DesultorySignals.Platform
{
    /// <summary>
    /// The device's <see cref="IMediaPlayerHost"/>: a thin, threaded wrapper around one
    /// <c>MediaElement</c>.
    /// </summary>
    /// <remarks>
    /// Everything the Core layer can ask of playback is expressed here in terms of
    /// three XAML calls (<c>SetSource</c>, <c>Play</c>, <c>Stop</c>), and every one of
    /// them is marshalled onto the UI thread: <c>MediaElement</c> is a dependency
    /// object and touching it from a worker thread is an immediate, unrecoverable
    /// crash rather than an exception one can catch.
    ///
    /// The element is attached after construction. The composition root
    /// (<see cref="App"/>) has to build <c>ServiceHost</c> before any page exists, but
    /// the <c>MediaElement</c> lives in a page; <see cref="Attach"/> closes that gap,
    /// and <see cref="IsAvailable"/> tells the truth in the window before a page has
    /// navigated.
    ///
    /// <c>MediaFailed</c> is the only place a decode failure is observable, so it is
    /// logged with the source label and raised as <see cref="PlaybackError"/> -- the
    /// Core layer's decode-recovery decision depends on it, and a silently swallowed
    /// failure would look like "the video just did not play".
    /// </remarks>
    public sealed class MediaElementHost : IMediaPlayerHost
    {
        private readonly DiagLog _log;
        private readonly object _gate = new object();

        private MediaElement _element;
        private CoreDispatcher _dispatcher;
        private IRandomAccessStream _currentStream;
        private string _currentLabel = "(nothing)";
        private string _lastMediaError;
        private TaskCompletionSource<bool> _pendingOpen;

        public MediaElementHost(DiagLog log)
        {
            _log = log;
        }

        /// <summary>Raised with the platform's message when the media pipeline fails.
        /// Separate from the Core layer's own failure reporting so a decode error can
        /// be turned into a source-candidate retry.</summary>
        public event Action<string> PlaybackError;

        public bool IsAvailable
        {
            get
            {
                lock (_gate) { return _element != null; }
            }
        }

        /// <summary>Current media label, for diagnostics only.</summary>
        public string CurrentLabel
        {
            get
            {
                lock (_gate) { return _currentLabel; }
            }
        }

        /// <summary>Hand the host its media surface. Called by the debug page when it
        /// loads, and with null when it unloads.</summary>
        public void Attach(MediaElement element)
        {
            lock (_gate)
            {
                if (_element != null)
                {
                    _element.MediaFailed -= OnMediaFailed;
                    _element.MediaOpened -= OnMediaOpened;
                }

                _element = element;
                _dispatcher = element == null ? null : element.Dispatcher;

                if (_element != null)
                {
                    _element.MediaFailed += OnMediaFailed;
                    _element.MediaOpened += OnMediaOpened;
                    _log.Info(DiagCategory.Playback, "MediaElement attached to the media host");
                }
                else
                {
                    _element = null;

                    // The page that owned the surface is going away; the media stream
                    // it was playing has no reader left, so it is released here rather
                    // than left to the GC of an interface that wraps an OS handle.
                    IRandomAccessStream previous = _currentStream;
                    _currentStream = null;
                    _currentLabel = "(no surface)";
                    if (previous != null)
                    {
                        try
                        {
                            previous.Dispose();
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
        }

        public async Task PlayAsync(
            IRandomAccessByteSource source, string mimeType, CancellationToken cancellationToken)
        {
            if (source == null) throw new ArgumentNullException("source");
            EnsureAttached();

            string type = string.IsNullOrEmpty(mimeType) ? "video/mp4" : mimeType;

            // The stream cannot report a size it does not know, and MediaElement needs
            // one before it will even open the container. RangeSourceOptions gets the
            // size from the payload when it is offered; when it is not, one byte read
            // is what teaches both the source and this stream.
            if (source.Size <= 0)
            {
                try
                {
                    var probe = new byte[1];
                    int read = await source
                        .ReadAsync(0, probe, 0, 1, cancellationToken)
                        .ConfigureAwait(false);
                    _log.Info(DiagCategory.Playback, string.Format(
                        "probed the byte source: {0} byte(s), size now {1}", read, source.Size));
                }
                catch (Exception exception)
                {
                    // Not fatal by itself: if the size is still unknown, MediaElement
                    // gets 0 and reports its own failure, which is diagnosable.
                    _log.Warn(DiagCategory.Playback, "size probe failed: " + exception.Message);
                }
            }

            var stream = new RangeRandomAccessStream(source);
            string label = string.Format("range stream, {0} bytes",
                source.Size > 0 ? source.Size.ToString() : "unknown");

            var opened = new TaskCompletionSource<bool>();
            await RunOnUiAsync(delegate
            {
                cancellationToken.ThrowIfCancellationRequested();
                SwapStream(stream, label);
                _pendingOpen = opened;
                _element.SetSource(stream, type);
                _element.Play();
            }).ConfigureAwait(false);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(30000);
                using (deadline.Token.Register(delegate { opened.TrySetCanceled(); }))
                    await opened.Task.ConfigureAwait(false);
            }

            _log.Info(DiagCategory.Playback, "playback started (" + label + ", " + type + ")");
        }

        public async Task PlayLocalFileAsync(
            string absolutePath, string mimeType, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(absolutePath))
                throw new DouyinApiException(ApiError.Local(
                    ErrorKinds.InvalidInput, "PlayLocalFileAsync needs an absolute path", false));
            EnsureAttached();

            string type = string.IsNullOrEmpty(mimeType) ? "video/mp4" : mimeType;

            StorageFile file = await StorageFile.GetFileFromPathAsync(absolutePath)
                .AsTask(cancellationToken).ConfigureAwait(false);
            IRandomAccessStream stream = await file.OpenReadAsync()
                .AsTask(cancellationToken).ConfigureAwait(false);

            string label = "file " + file.Name;

            var opened = new TaskCompletionSource<bool>();
            await RunOnUiAsync(delegate
            {
                cancellationToken.ThrowIfCancellationRequested();
                SwapStream(stream, label);
                _pendingOpen = opened;
                _element.SetSource(stream, type);
                _element.Play();
            }).ConfigureAwait(false);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(30000);
                using (deadline.Token.Register(delegate { opened.TrySetCanceled(); }))
                    await opened.Task.ConfigureAwait(false);
            }

            _log.Info(DiagCategory.Playback, "playback started from " + label);
        }

        public Task PauseAsync()
        {
            return RunOnUiAsync(delegate
            {
                if (_element == null) return;
                _element.Pause();
                _log.Info(DiagCategory.Playback, "paused");
            });
        }

        public Task ResumeAsync()
        {
            return RunOnUiAsync(delegate
            {
                if (_element == null) return;
                _element.Play();
                _log.Info(DiagCategory.Playback, "resumed");
            });
        }

        public Task StopAsync()
        {
            return RunOnUiAsync(delegate
            {
                if (_element == null) return;
                _element.Stop();
                _element.Source = null;
                SwapStream(null, "(stopped)");
                _log.Info(DiagCategory.Playback, "stopped");
            });
        }

        public async Task<JObject> DescribeAsync()
        {
            JObject result = null;
            await RunOnUiAsync(delegate
            {
                result = new JObject
                {
                    { "state", _element == null ? "Detached" : _element.CurrentState.ToString() },
                    { "positionMs", _element == null ? 0 : _element.Position.TotalMilliseconds },
                    { "durationMs", _element != null && _element.NaturalDuration.HasTimeSpan ? _element.NaturalDuration.TimeSpan.TotalMilliseconds : 0 },
                    { "videoWidth", _element == null ? 0 : _element.NaturalVideoWidth },
                    { "videoHeight", _element == null ? 0 : _element.NaturalVideoHeight },
                    { "error", _lastMediaError }
                };
            });
            return result;
        }

        public Task SeekAsync(TimeSpan position)
        {
            return RunOnUiAsync(delegate
            {
                if (_element == null) throw new InvalidOperationException("media surface detached");
                _element.Position = position;
            });
        }

        private void EnsureAttached()
        {
            lock (_gate)
            {
                if (_element != null) return;
            }

            throw new DouyinApiException(ApiError.Local(
                ErrorKinds.Internal,
                "no MediaElement is attached yet; open the debug page first",
                false));
        }

        /// <summary>Release the previous media stream and adopt a new one. Must run on
        /// the UI thread; the caller guarantees that.</summary>
        private void SwapStream(IRandomAccessStream stream, string label)
        {
            _lastMediaError = null;
            if (_pendingOpen != null) { _pendingOpen.TrySetCanceled(); _pendingOpen = null; }
            IRandomAccessStream previous;
            lock (_gate)
            {
                previous = _currentStream;
                _currentStream = stream;
                _currentLabel = label ?? "(nothing)";
            }

            if (previous == null || previous == stream) return;
            try
            {
                previous.Dispose();
            }
            catch (Exception exception)
            {
                _log.Warn(DiagCategory.Playback,
                    "releasing the previous media stream failed: " + exception.Message);
            }
        }

        private void OnMediaFailed(object sender, ExceptionRoutedEventArgs args)
        {
            _lastMediaError = args == null || string.IsNullOrEmpty(args.ErrorMessage) ? "media failed" : args.ErrorMessage;
            string message = args == null || string.IsNullOrEmpty(args.ErrorMessage)
                ? "the media pipeline failed without a message"
                : args.ErrorMessage;

            string label;
            lock (_gate) { label = _currentLabel; }

            if (_pendingOpen != null) { _pendingOpen.TrySetException(new InvalidOperationException(message)); _pendingOpen = null; }
            _log.Error(DiagCategory.Playback, "MediaElement failed: " + message,
                new JObject { { "source", label } });

            Action<string> handler = PlaybackError;
            if (handler != null)
            {
                try
                {
                    handler(message);
                }
                catch (Exception exception)
                {
                    _log.Warn(DiagCategory.Playback,
                        "a PlaybackError subscriber threw: " + exception.Message);
                }
            }
        }

        private void OnMediaOpened(object sender, RoutedEventArgs args)
        {
            if (_pendingOpen != null) { _pendingOpen.TrySetResult(true); _pendingOpen = null; }
            string label;
            lock (_gate) { label = _currentLabel; }
            _log.Info(DiagCategory.Playback, "MediaElement opened " + label);
        }

        private Task RunOnUiAsync(Action action)
        {
            var completion = new TaskCompletionSource<bool>();

            CoreDispatcher dispatcher;
            lock (_gate) { dispatcher = _dispatcher; }

            if (dispatcher == null)
            {
                completion.TrySetException(new DouyinApiException(ApiError.Local(
                    ErrorKinds.Internal, "the media surface has no dispatcher yet", false)));
                return completion.Task;
            }

            if (dispatcher.HasThreadAccess)
            {
                try
                {
                    action();
                    completion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                return completion.Task;
            }

            // Deliberately not awaited: the caller wants the result of the action, and
            // the completion source below carries it. Assigning the operation is what
            // keeps that intent explicit rather than looking like a forgotten await.
            IAsyncAction dispatch = dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                try
                {
                    action();
                    completion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            _ = dispatch;

            return completion.Task;
        }
    }
}
