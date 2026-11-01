using System;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Douyin;
using DesultorySignals.Core.Models;
using DesultorySignals.Core.Networking;
using DesultorySignals.Core.Python;
using DesultorySignals.Core.Storage;
using Newtonsoft.Json.Linq;

namespace DesultorySignals.Core.Services
{
    /// <summary>
    /// Composition root: builds every service from the four platform pieces and owns
    /// their lifetime.
    /// </summary>
    /// <remarks>
    /// The CodeBehind talks to this and to nothing else. That is what keeps the
    /// second phase's UI work from touching business logic: a page needs a feed, a
    /// video and comments, and it gets them from here without knowing that a Python
    /// interpreter, a cookie jar or a range cache exist.
    ///
    /// The four platform dependencies are the complete list of what the device must
    /// supply:
    ///
    /// * <see cref="PythonProtocolClient"/> -- the CPython engine thread and ABI.
    /// * <see cref="IHttpTransport"/> -- TLS and sockets.
    /// * <see cref="IFileStore"/> -- private app storage.
    /// * <see cref="IMediaPlayerHost"/> -- <c>MediaElement</c>.
    /// </remarks>
    public sealed class ServiceHost
    {
        private readonly IMediaPlayerHost _media;
        private readonly IFileStore _store;

        private bool _initialized;

        public ServiceHost(
            PythonProtocolClient python,
            IHttpTransport transport,
            IFileStore store,
            IMediaPlayerHost media,
            DiagLog log)
        {
            if (python == null) throw new ArgumentNullException("python");
            if (transport == null) throw new ArgumentNullException("transport");
            if (store == null) throw new ArgumentNullException("store");
            if (media == null) throw new ArgumentNullException("media");

            Log = log ?? new DiagLog(2048) { Enabled = true, MinimumLevel = DiagLevel.Info };
            _media = media;
            _store = store;

            Session = new SessionManager(store, python, Log);
            Api = new DouyinApiClient(python, transport, Session, Log);
            Cache = new CacheService(store, Log);
            Feed = new FeedService(Api, Log);
            Comments = new CommentService(Api, Log);
            Video = new VideoService(Api, python, Session, Cache, Log);
            Playback = new PlaybackService(Video, transport, store, media, Log);
            History = new HistoryService(store, Log);
        }

        public DiagLog Log { get; private set; }
        public SessionManager Session { get; private set; }
        public DouyinApiClient Api { get; private set; }
        public CacheService Cache { get; private set; }
        public FeedService Feed { get; private set; }
        public CommentService Comments { get; private set; }
        public VideoService Video { get; private set; }
        public PlaybackService Playback { get; private set; }
        public HistoryService History { get; private set; }

        public bool IsInitialized
        {
            get { return _initialized; }
        }

        /// <summary>
        /// Load the session and, when asked, acquire the server-issued guest cookies.
        /// </summary>
        /// <remarks>
        /// The guest bootstrap is a separate step rather than part of the session load
        /// because it needs the network, and a client that cannot reach Douyin should
        /// still start and say so clearly rather than failing at construction.
        /// </remarks>
        public async Task<SessionAssessment> InitializeAsync(
            bool bootstrapGuestSession, CancellationToken cancellationToken)
        {
            Log.Info(DiagCategory.Lifecycle, "initializing services");

            SessionAssessment assessment = await Session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;

            if (bootstrapGuestSession && !assessment.IsLoggedIn)
            {
                try
                {
                    await Api.BootstrapGuestSessionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DouyinApiException failure)
                {
                    // Not fatal: every subsequent call will report its own failure, and
                    // the diagnostics page will show why.
                    Log.Warn(DiagCategory.Session,
                        "guest session bootstrap failed: " + failure.Error.Describe());
                }
                catch (Exception exception)
                {
                    Log.Warn(DiagCategory.Session, "guest session bootstrap failed: " + exception.Message);
                }
            }

            Log.Info(DiagCategory.Lifecycle, "services ready (session: " + Session.Assessment.State + ")");
            return Session.Assessment;
        }

        /// <summary>
        /// Everything the diagnostics page shows, in one call.
        /// </summary>
        /// <remarks>
        /// Contains no credential: the identity is the redacted view, and the log
        /// scrubs on write. That is what makes it safe to display and to export.
        /// </remarks>
        public async Task<JObject> DiagnosticsAsync(CancellationToken cancellationToken)
        {
            var result = new JObject();

            try
            {
                result["runtime"] = Session != null ? JToken.FromObject("(see log)") : JValue.CreateNull();
                result["session"] = await Session.DescribeRedactedAsync(cancellationToken).ConfigureAwait(false);
                result["feed"] = Feed.Describe();
                result["cache"] = await Cache.DescribeAsync(cancellationToken).ConfigureAwait(false);
                result["playbackCache"] = Playback.DescribeCache();
                result["historyCount"] = (await History.ListAsync(cancellationToken).ConfigureAwait(false)).Count;
                result["log"] = new JObject
                {
                    { "entries", Log.Snapshot().Count },
                    { "capacity", Log.Capacity },
                    { "dropped", Log.DroppedCount },
                    { "enabled", Log.Enabled }
                };
            }
            catch (Exception exception)
            {
                result["diagnosticsError"] = exception.Message;
            }

            return result;
        }

        /// <summary>Flush everything that is debounced, then stop playback.</summary>
        public async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            Log.Info(DiagCategory.Lifecycle, "shutting down services");
            try
            {
                await Playback.StopAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            try
            {
                await History.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            try
            {
                await Session.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            _initialized = false;
        }

        /// <summary>
        /// Release what a page can release without ending the session: playback and
        /// per-page caches. Called from the page's suspension handler.
        /// </summary>
        /// <remarks>
        /// The completion criteria require that continuous scrolling and playback do
        /// not grow memory. The bound is spread across three owners, and this is the
        /// one the page controls: stop the media pipeline, drop the comment threads
        /// (which hold every loaded comment), and let the range cache go with the
        /// source.
        /// </remarks>
        public async Task ReleasePageResourcesAsync()
        {
            await Playback.StopAsync().ConfigureAwait(false);
            Comments.Clear();
            Feed.Reset();
            Log.Info(DiagCategory.Lifecycle, "page resources released");
        }
    }
}
