using System;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Models;
using DesultorySignals.Core.Python;
using DesultorySignals.Core.Services;
using DesultorySignals.Platform;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace DesultorySignals
{
    /// <summary>
    /// Composition root for the debug shell, and nothing else.
    /// </summary>
    /// <remarks>
    /// This file builds the six objects the device has to supply and then gets out of
    /// the way. It contains no protocol logic, no HTTP, no storage decisions -- every
    /// one of those lives in <c>DesultorySignals.Core</c> behind an interface, which is
    /// what keeps the debug shell from slowly becoming a second implementation of the
    /// client.
    ///
    /// The startup order is a hard constraint, not a preference:
    ///
    /// 1. <see cref="EngineThread"/> -- the interpreter's thread must exist first.
    /// 2. <see cref="PythonUwpRuntime.Initialize"/> on that thread -- creates the
    ///    interpreter and puts <c>Lib\</c> and <c>AppLib\</c> on the module path.
    /// 3. The bridge probe and one <c>diagnostics.selfTest</c> round trip -- proves the
    ///    Python layer imports and answers before any request needs it. A failure here
    ///    is reported with the native error text rather than surfacing later as an
    ///    unexplained protocol error.
    /// 4. <c>ServiceHost.InitializeAsync</c> -- loads the session and, when no identity
    ///    is stored yet, fetches the guest cookies.
    ///
    /// Every step records its outcome in <see cref="BootstrapReport"/> so the page can
    /// show exactly which rung of that ladder failed.
    /// </remarks>
    public sealed partial class App : Application
    {
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private Task _bootstrapTask;
        private DeviceDiagnostics _deviceDiagnostics;

        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
            Resuming += OnResuming;
            UnhandledException += OnUnhandledException;
        }

        // -- the platform pieces, once each -------------------------------

        public static DiagLog Log { get; private set; }
        public static EngineThread Engine { get; private set; }
        public static PythonUwpRuntime Runtime { get; private set; }
        public static PythonProtocolClient Python { get; private set; }
        public static UwpHttpTransport Transport { get; private set; }
        public static LocalFileStore Files { get; private set; }
        public static MediaElementHost Media { get; private set; }
        public static ServiceHost Services { get; private set; }

        /// <summary>Installed package folder: the appx root that holds Lib and AppLib.</summary>
        public static string RuntimeRoot { get; private set; }

        /// <summary>Writable site-packages directory inside LocalState.</summary>
        public static string LocalSitePath { get; private set; }

        public static BootstrapReport Bootstrap { get; private set; }

        /// <summary>Raised when the startup sequence finishes, successfully or not. The
        /// page may also read <see cref="Bootstrap"/> if it loaded after the fact.</summary>
        public static event Action<BootstrapReport> BootstrapCompleted;

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            // Re-activation of a suspended app keeps its page and interpreter.
            // Replacing the static services here would orphan both their owners.
            if (Services == null) BuildCompositionRoot();

            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }

            if (frame.Content == null)
                frame.Navigate(typeof(Views.MainPage));

            Window.Current.Activate();

            if (_bootstrapTask == null) _bootstrapTask = BootstrapAsync();
            await _bootstrapTask.ConfigureAwait(true);
        }

        private void BuildCompositionRoot()
        {
            Log = new DiagLog(4096) { Enabled = true, MinimumLevel = DiagLevel.Trace };

            Engine = new EngineThread(Log);
            Runtime = new PythonUwpRuntime(Engine, Log);
            Python = new PythonProtocolClient(Runtime, Engine, Log);
            Transport = new UwpHttpTransport(Log);
            Files = new LocalFileStore(ApplicationData.Current.LocalFolder, Log);
            Media = new MediaElementHost(Log);
            Services = new ServiceHost(Python, Transport, Files, Media, Log);

            RuntimeRoot = Package.Current.InstalledLocation.Path;
            LocalSitePath = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "site-packages");

            try
            {
                // The native driver expects the directory to exist; it does not create
                // it, and a missing local site is an initialization failure rather than
                // a warning.
                System.IO.Directory.CreateDirectory(LocalSitePath);
            }
            catch (Exception exception)
            {
                Log.Warn(DiagCategory.Python,
                    "could not create the local site-packages directory: " + exception.Message);
            }

            _deviceDiagnostics = new DeviceDiagnostics();

            Log.Info(DiagCategory.Lifecycle, "composition root built",
                new JObject
                {
                    { "runtimeRoot", RuntimeRoot },
                    { "localSite", LocalSitePath }
                });
        }

        private async Task BootstrapAsync()
        {
            var report = new BootstrapReport();
            Bootstrap = report;
            CancellationToken token = _lifetime.Token;

            // Diagnostic kill switch, read from storage rather than compiled in so that
            // one build can do both halves of an A/B test:
            //
            //   present  -> the shell runs with no Python at all
            //   absent   -> normal startup
            //
            // It exists because a native fault inside the embedded interpreter cannot be
            // caught by managed code: the process simply disappears. Being able to remove
            // the interpreter from the picture without a rebuild is what separates "the
            // XAML shell is broken" from "the CPython bridge is broken", and those two
            // have nothing in common as problems.
            if (IsPythonDisabled())
            {
                report.EngineStarted = false;
                report.RuntimeInitialized = false;
                report.BridgeReady = false;
                report.SelfTestOk = false;
                report.Error = "Python is disabled by " + DisablePythonFile + " (diagnostic mode)";
                Log.Warn(DiagCategory.Python, report.Error);
                await WriteStartupReportAsync(report).ConfigureAwait(true);
                RaiseBootstrapCompleted(report);
                return;
            }

            try
            {
                Engine.Start();
                report.EngineStarted = Engine.IsAvailable;

                report.RuntimeInitialized = await Engine
                    .InvokeAsync(delegate { return Runtime.Initialize(RuntimeRoot, LocalSitePath); }, token)
                    .ConfigureAwait(true);
                report.RuntimeDescribe = Runtime.Describe();
                report.LastNativeError = Runtime.LastError();

                if (report.RuntimeInitialized)
                {
                    report.BridgeReady = await Engine
                        .InvokeAsync(delegate { return Runtime.BridgeReady(); }, token)
                        .ConfigureAwait(true);
                }

                if (report.BridgeReady)
                {
                    // One round trip through the bridge with the deterministic clock, so
                    // "the interpreter answers" is established before any real request.
                    var fields = new JObject
                    {
                        {
                            "clock", new JObject
                            {
                                { "seed", 1 },
                                { "fixedNowMs", 1700000000000L }
                            }
                        }
                    };

                    JObject envelope = await Python
                        .CallEnvelopeAsync("diagnostics.selfTest", fields, token)
                        .ConfigureAwait(true);

                    report.SelfTestOk = JsonValue.GetBool(envelope, "ok", false);
                    report.SelfTestJson = envelope.ToString(Formatting.None);

                    if (!report.SelfTestOk)
                    {
                        JObject error = envelope["error"] as JObject;
                        report.SelfTestError = error == null
                            ? "the bridge reported failure without an error object"
                            : ApiError.FromJson(error).Describe();
                    }

                    Log.Info(DiagCategory.Python, "diagnostics.selfTest ok=" + report.SelfTestOk);
                }
                else if (string.IsNullOrEmpty(report.SelfTestError))
                {
                    report.SelfTestError = report.LastNativeError ?? "the protocol layer did not import";
                }
            }
            catch (Exception exception)
            {
                report.Error = Describe(exception);
                Log.Error(DiagCategory.Lifecycle, "the Python runtime did not come up: " + report.Error);
            }

            try
            {
                SessionAssessment assessment = await Services
                    .InitializeAsync(true, token)
                    .ConfigureAwait(true);

                report.SessionState = assessment == null ? "unknown" : assessment.State;
                report.CookieCount = assessment == null ? 0 : assessment.CookieCount;
            }
            catch (Exception exception)
            {
                // Not fatal: every later call reports its own classified failure, and the
                // diagnostics page shows why the session is missing.
                report.SessionError = Describe(exception);
                Log.Warn(DiagCategory.Session, "session initialization failed: " + report.SessionError);
            }

            report.CompletedAtMs = Environment.TickCount;
            Log.Info(DiagCategory.Lifecycle, "startup finished",
                new JObject
                {
                    { "engine", report.EngineStarted },
                    { "runtime", report.RuntimeInitialized },
                    { "bridge", report.BridgeReady },
                    { "selfTest", report.SelfTestOk },
                    { "session", report.SessionState }
                });

            await WriteStartupReportAsync(report).ConfigureAwait(true);

            RaiseBootstrapCompleted(report);
            if (report.IsHealthy)
            {
                try { await _deviceDiagnostics.RunIfRequestedAsync(token); }
                catch (Exception ex) { Log.Error(DiagCategory.Lifecycle, "device checks interrupted: " + Describe(ex)); }
            }
        }

        /// <summary>
        /// Persist the startup result to LocalState so it can be read over Device Portal.
        /// </summary>
        /// <remarks>
        /// This exists because the device is often only reachable over the network, and
        /// "did CPython actually initialize, and did the guest session get its ttwid?" is
        /// exactly the question that cannot be answered by looking at a screen from here.
        /// Writing it to a file makes the answer retrievable:
        ///
        ///     scripts\deploy-device.ps1 -DeviceIp &lt;ip&gt; -SkipDeploy -PullDiagnostics
        ///
        /// The ttwid and uifid fields are the important ones. Every API call is refused
        /// until the server issues a ttwid, so its presence is the single fact that
        /// decides whether the rest of the client can work at all. The values themselves
        /// are never written -- only "present"/"absent" and a length -- so the report can
        /// be pasted anywhere.
        /// </remarks>
        private async Task WriteStartupReportAsync(BootstrapReport report)
        {
            try
            {
                var document = new JObject
                {
                    { "writtenAtUtc", DateTimeOffset.UtcNow.ToString("o") },
                    { "healthy", report.IsHealthy },
                    { "engineStarted", report.EngineStarted },
                    { "runtimeInitialized", report.RuntimeInitialized },
                    { "bridgeReady", report.BridgeReady },
                    { "selfTestOk", report.SelfTestOk },
                    { "runtimeDescribe", report.RuntimeDescribe },
                    { "lastNativeError", report.LastNativeError },
                    { "selfTestError", report.SelfTestError },
                    { "bootstrapError", report.Error },
                    { "sessionState", report.SessionState },
                    { "cookieCount", report.CookieCount },
                    { "sessionError", report.SessionError },
                    { "selfTest", report.SelfTestJson },
                    { "summary", report.Summary() }
                };

                // The guest credentials. Only presence and length are recorded.
                try
                {
                    JObject identity = await Services.Session
                        .DescribeRedactedAsync(_lifetime.Token)
                        .ConfigureAwait(true);

                    document["identity"] = new JObject
                    {
                        { "identityId", JsonValue.GetString(identity, "identityId") },
                        { "userAgent", JsonValue.GetString(identity, "userAgent") },
                        { "webid", JsonValue.GetString(identity, "webid") },
                        { "ttwid", JsonValue.GetString(identity, "ttwid", "absent") },
                        { "uifid", JsonValue.GetString(identity, "uifid", "absent") },
                        { "msTokenLength", JsonValue.GetInt(identity, "msTokenLength", 0) },
                        { "cookieCount", JsonValue.GetInt(identity, "cookieCount", 0) }
                    };

                    // A live probe, so the report says whether the server accepted us
                    // rather than only what we hold locally. This is the real gate check:
                    // a 200 here means ttwid worked, and a classified failure here names
                    // the reason.
                    try
                    {
                        FeedUpdate update = await Services.Feed
                            .LoadInitialAsync(6, _lifetime.Token)
                            .ConfigureAwait(true);

                        document["feedProbe"] = new JObject
                        {
                            { "items", update.Added.Count },
                            { "total", update.TotalItems },
                            { "hasMore", update.HasMore },
                            { "cursor", update.Cursor },
                            { "skipped", update.SkippedByParser }
                        };
                    }
                    catch (Exception probe)
                    {
                        document["feedProbeError"] = Describe(probe);
                    }
                }
                catch (Exception identityError)
                {
                    document["identityError"] = Describe(identityError);
                }

                document["log"] = Services.Log.Dump();

                string path = System.IO.Path.Combine(
                    ApplicationData.Current.LocalFolder.Path, StartupReportFile);
                System.IO.File.WriteAllText(path, document.ToString(Formatting.Indented));
                Log.Info(DiagCategory.Lifecycle, "startup report written to " + StartupReportFile);
            }
            catch (Exception exception)
            {
                // Diagnostics must never be the reason startup fails.
                Log.Warn(DiagCategory.Lifecycle, "could not write the startup report: " + exception.Message);
            }
        }

        /// <summary>The file Device Portal pulls. Name is referenced by the deploy script.</summary>
        public const string StartupReportFile = "startup-report.txt";

        /// <summary>Presence of this file in LocalState skips the Python bootstrap.</summary>
        public const string DisablePythonFile = "disable-python.txt";

        private bool IsPythonDisabled()
        {
            try
            {
                string path = System.IO.Path.Combine(
                    ApplicationData.Current.LocalFolder.Path, DisablePythonFile);
                return System.IO.File.Exists(path);
            }
            catch (Exception)
            {
                // If the check itself fails, behave normally: a diagnostic switch must
                // never be the reason the app misbehaves.
                return false;
            }
        }

        private static void RaiseBootstrapCompleted(BootstrapReport report)
        {
            Action<BootstrapReport> handler = BootstrapCompleted;
            if (handler == null) return;
            try
            {
                handler(report);
            }
            catch (Exception exception)
            {
                Log.Warn(DiagCategory.Lifecycle,
                    "a BootstrapCompleted subscriber threw: " + exception.Message);
            }
        }

        /// <summary>Classified text for the status bar. A protocol failure is shown with
        /// its taxonomy, because that is what says whether a retry would help.</summary>
        public static string Describe(Exception exception)
        {
            var api = exception as DouyinApiException;
            if (api != null) return api.Error.Describe();
            return exception.GetType().Name + ": " + exception.Message;
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            SuspendingDeferral deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                if (Services != null)
                {
                    using (var shutdown = new CancellationTokenSource(3000))
                    {
                        await Services.Playback.StopAsync().ConfigureAwait(false);
                        await Services.History.FlushAsync(shutdown.Token).ConfigureAwait(false);
                        await Services.Session.FlushAsync(shutdown.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception)
            {
                if (Log != null)
                    Log.Warn(DiagCategory.Lifecycle, "shutdown was not clean: " + exception.Message);
            }
            finally
            {
                // Suspension freezes and later resumes this same process. Keep the
                // interpreter and its owner thread together; finalizing here leaves
                // the existing page backed by a dead engine when reopened.
                // Process termination releases the native runtime automatically.
                if (_deviceDiagnostics != null) _deviceDiagnostics.FlushLog();
                deferral.Complete();
            }
        }

        private async void OnResuming(object sender, object e)
        {
            try
            {
                if (_bootstrapTask != null) await _bootstrapTask.ConfigureAwait(false);
                if (Services == null || Bootstrap == null || !Bootstrap.IsHealthy) return;
                await Services.Api.CheckSessionAsync(_lifetime.Token).ConfigureAwait(false);
                Log.Info(DiagCategory.Lifecycle, "resumed; session checked");
            }
            catch (Exception exception)
            {
                if (Log != null)
                    Log.Warn(DiagCategory.Session, "resume session check: " + Describe(exception));
            }
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // Logged, then swallowed: on a device-only debug shell a crashed UI thread
            // costs the session and the log with it, and the message is far more useful
            // on the status bar than in a Watson report.
            string text = e.Exception == null ? "(no exception)" : Describe(e.Exception);
            if (Log != null) Log.Error(DiagCategory.Lifecycle, "unhandled: " + text);
            if (_deviceDiagnostics != null) _deviceDiagnostics.FlushLog();
            e.Handled = true;
        }
    }

    /// <summary>
    /// What the startup ladder did, rung by rung.
    /// </summary>
    /// <remarks>
    /// Plain fields rather than properties: this is read by code, never bound. The
    /// point is that "the runtime is broken" becomes a specific sentence -- which of
    /// the five steps failed, with the native error text attached.
    /// </remarks>
    public sealed class BootstrapReport
    {
        public bool EngineStarted;
        public bool RuntimeInitialized;
        public bool BridgeReady;
        public bool SelfTestOk;

        public string RuntimeDescribe;
        public string SelfTestJson;
        public string SelfTestError;
        public string LastNativeError;
        public string SessionState;
        public int CookieCount;
        public string SessionError;
        public string Error;
        public long CompletedAtMs;

        public bool IsHealthy
        {
            get { return EngineStarted && RuntimeInitialized && BridgeReady && SelfTestOk; }
        }

        public string Summary()
        {
            if (!EngineStarted) return "启动失败：引擎线程未启动";
            if (!RuntimeInitialized)
                return "启动失败：CPython 初始化未完成" + Suffix(RuntimeDescribe);

            string state = "运行时 " + (RuntimeDescribe ?? "(unknown)");
            state += "；桥接 " + (BridgeReady ? "已导入" : "未就绪");
            state += "；自检 " + (SelfTestOk ? "通过" : "失败");

            if (!BridgeReady || !SelfTestOk)
            {
                string detail = SelfTestError;
                if (string.IsNullOrEmpty(detail)) detail = LastNativeError;
                if (!string.IsNullOrEmpty(detail)) state += "（" + detail + "）";
            }

            if (!string.IsNullOrEmpty(Error)) state += "；异常 " + Error;
            if (!string.IsNullOrEmpty(SessionError)) state += "；会话 " + SessionError;

            return state;
        }

        private static string Suffix(string text)
        {
            return string.IsNullOrEmpty(text) ? string.Empty : "（" + text + "）";
        }
    }
}
