using System;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Models;
using DesultorySignals.Core.Python;
using DesultorySignals.Core.Storage;
using Newtonsoft.Json.Linq;

namespace DesultorySignals.Core.Douyin
{
    /// <summary>
    /// Owns the virtual browser identity, its cookie jar and their persistence.
    /// </summary>
    /// <remarks>
    /// The brief's rule is that no service may maintain cookies of its own, and
    /// this is the type that makes that enforceable: every service reaches the
    /// session through here, and this is the only place that reads or writes the
    /// session files.
    ///
    /// The identity itself lives in Python (it owns the generation rules and the
    /// consistency checks); C# holds it as an opaque JSON document and hands it
    /// back on every call. That has a useful consequence: a rotated <c>msToken</c>
    /// or an ingested <c>Set-Cookie</c> is persisted because Python returned a
    /// changed document, not because a service remembered to save.
    ///
    /// Writes are change-gated. The phone's storage is slow and the identity is
    /// rewritten whenever a cookie rotates, so writing unconditionally would mean a
    /// file write on most requests.
    /// </remarks>
    public sealed class SessionManager
    {
        public const string IdentityPath = "session/identity.json";
        public const string GuardPath = "session/publish-guard.json";

        private readonly IFileStore _store;
        private readonly IDouyinProtocol _python;
        private readonly DiagLog _log;
        private readonly object _gate = new object();

        private JObject _identity;
        private string _lastPersistedJson;
        private SessionAssessment _assessment;

        public SessionManager(IFileStore store, IDouyinProtocol python, DiagLog log)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (python == null) throw new ArgumentNullException("python");
            _store = store;
            _python = python;
            _log = log;
            _assessment = new SessionAssessment { State = "guest", SessionMarkers = new System.Collections.Generic.List<string>() };
        }

        /// <summary>The live identity document. Never null once initialized.</summary>
        public JObject Identity
        {
            get { lock (_gate) { return _identity; } }
        }

        public SessionAssessment Assessment
        {
            get { lock (_gate) { return _assessment; } }
        }

        public bool IsLoggedIn
        {
            get { lock (_gate) { return _assessment != null && _assessment.IsLoggedIn; } }
        }

        /// <summary>Raised when the guest/logged-in state or the marker set changes.</summary>
        public event Action<SessionAssessment> SessionStateChanged;

        /// <summary>
        /// Load the stored identity, or create and persist one on first run.
        /// </summary>
        /// <remarks>
        /// A stored identity that fails its own consistency check is *replaced*
        /// rather than repaired. A mismatched identity (a cookie jar from another
        /// browser, say) produces requests that look logged in and are not, and the
        /// cost of a fresh guest identity is one request, whereas the cost of a
        /// silently inconsistent one is hours of confusing diagnosis.
        /// </remarks>
        public async Task<SessionAssessment> InitializeAsync(CancellationToken cancellationToken)
        {
            string stored = null;
            try
            {
                stored = await _store.ReadTextAsync(IdentityPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log.Warn(DiagCategory.Session, "could not read the stored identity: " + exception.Message);
            }

            JObject identity = null;
            if (!string.IsNullOrEmpty(stored))
            {
                JObject parsed = TryParse(stored);
                if (parsed != null)
                {
                    try
                    {
                        JObject result = await _python.CallAsync(
                            "identity.load",
                            PythonProtocolClient.Fields("identity", parsed),
                            cancellationToken).ConfigureAwait(false);

                        var problems = JsonValue.GetStrings(result, "problems");
                        if (problems != null && problems.Count > 0)
                        {
                            _log.Warn(DiagCategory.Session,
                                "the stored identity is inconsistent and will be replaced",
                                new JObject { { "problems", new JArray(problems) } });
                        }
                        else
                        {
                            identity = result["identity"] as JObject;
                            LogWarnings(result);
                        }
                    }
                    catch (DouyinApiException exception)
                    {
                        _log.Warn(DiagCategory.Session,
                            "the stored identity could not be loaded (" + exception.Kind + "), creating a new one");
                    }
                }
                else
                {
                    _log.Warn(DiagCategory.Session, "the stored identity file is not valid JSON; creating a new one");
                }
            }

            if (identity == null)
            {
                JObject created = await _python.CallAsync(
                    "identity.create", new JObject(), cancellationToken).ConfigureAwait(false);
                identity = created["identity"] as JObject;
                LogWarnings(created);
            }

            if (identity == null)
            {
                throw new DouyinApiException(ApiError.Local(
                    ErrorKinds.Internal, "the protocol layer produced no identity", false));
            }

            lock (_gate)
            {
                _identity = identity;
            }

            await PersistIfChangedAsync(true, cancellationToken).ConfigureAwait(false);
            return await RefreshAssessmentAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Adopt an identity document returned by a protocol call. Persists only if
        /// something actually changed.
        /// </summary>
        public async Task AdoptIdentityAsync(JObject identity, CancellationToken cancellationToken)
        {
            if (identity == null) return;
            lock (_gate)
            {
                _identity = identity;
            }
            await PersistIfChangedAsync(false, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Synchronous adoption for callers that already hold the lock-free
        /// result and do not want to await persistence on the hot path.</summary>
        public void AdoptIdentity(JObject identity)
        {
            if (identity == null) return;
            lock (_gate)
            {
                _identity = identity;
            }
        }

        /// <summary>Recompute guest/logged-in from the jar (never from a cached flag).</summary>
        public async Task<SessionAssessment> RefreshAssessmentAsync(CancellationToken cancellationToken)
        {
            JObject identity;
            lock (_gate)
            {
                identity = _identity;
            }
            if (identity == null) return _assessment;

            JObject result = await _python.CallWithIdentityAsync(
                "login.assess", identity, new JObject(), cancellationToken).ConfigureAwait(false);

            var assessment = SessionAssessment.FromJson(result["state"] as JObject);
            bool changed;
            lock (_gate)
            {
                changed = _assessment == null
                    || _assessment.State != assessment.State
                    || _assessment.SessionMarkers.Count != assessment.SessionMarkers.Count;
                _assessment = assessment;
            }

            if (changed)
            {
                _log.Info(DiagCategory.Session, "session state is now " + assessment.State,
                    new JObject
                    {
                        { "state", assessment.State },
                        { "markers", new JArray(assessment.SessionMarkers) },
                        { "cookieCount", assessment.CookieCount }
                    });
                Action<SessionAssessment> handler = SessionStateChanged;
                if (handler != null) handler(assessment);
            }
            return assessment;
        }

        /// <summary>Import a pasted cookie blob. The dependable login path.</summary>
        public async Task<JObject> ImportCookiesAsync(string cookieText, CancellationToken cancellationToken)
        {
            JObject identity;
            lock (_gate)
            {
                identity = _identity;
            }

            JObject result = await _python.CallWithIdentityAsync(
                "login.importCookie",
                identity,
                PythonProtocolClient.Fields("cookieText", cookieText),
                cancellationToken).ConfigureAwait(false);

            await AdoptIdentityAsync(result["identity"] as JObject, cancellationToken).ConfigureAwait(false);
            await PersistIfChangedAsync(true, cancellationToken).ConfigureAwait(false);

            JObject report = result["report"] as JObject;
            _log.Info(DiagCategory.Session, "cookie import finished",
                new JObject
                {
                    { "state", JsonValue.GetString(report, "state", "unknown") },
                    { "imported", JsonValue.GetInt(report, "importedCount", 0) },
                    { "markers", new JArray(JsonValue.GetStrings(report, "sessionMarkers")) }
                });

            await RefreshAssessmentAsync(cancellationToken).ConfigureAwait(false);
            return report;
        }

        /// <summary>Drop every session cookie, keep the fingerprint identity.</summary>
        public async Task<JObject> LogoutAsync(CancellationToken cancellationToken)
        {
            JObject identity;
            lock (_gate)
            {
                identity = _identity;
            }

            JObject result = await _python.CallWithIdentityAsync(
                "login.logout", identity, new JObject(), cancellationToken).ConfigureAwait(false);

            await AdoptIdentityAsync(result["identity"] as JObject, cancellationToken).ConfigureAwait(false);
            await PersistIfChangedAsync(true, cancellationToken).ConfigureAwait(false);
            await RefreshAssessmentAsync(cancellationToken).ConfigureAwait(false);
            _log.Info(DiagCategory.Session, "signed out; the virtual browser identity was kept");
            return result["report"] as JObject;
        }

        /// <summary>Reset the whole virtual browser: a new identity with no session.</summary>
        public async Task ResetIdentityAsync(CancellationToken cancellationToken)
        {
            JObject identity;
            lock (_gate)
            {
                identity = _identity;
            }

            JObject result = await _python.CallWithIdentityAsync(
                "identity.reset", identity, new JObject(), cancellationToken).ConfigureAwait(false);

            await AdoptIdentityAsync(result["identity"] as JObject, cancellationToken).ConfigureAwait(false);
            await PersistIfChangedAsync(true, cancellationToken).ConfigureAwait(false);
            await RefreshAssessmentAsync(cancellationToken).ConfigureAwait(false);
            _log.Info(DiagCategory.Session, "the virtual browser identity was reset");
        }

        /// <summary>Log-safe identity view for the diagnostics page.</summary>
        public async Task<JObject> DescribeRedactedAsync(CancellationToken cancellationToken)
        {
            JObject identity;
            lock (_gate)
            {
                identity = _identity;
            }
            if (identity == null) return new JObject();
            JObject result = await _python.CallWithIdentityAsync(
                "identity.redacted", identity, new JObject(), cancellationToken).ConfigureAwait(false);
            return result["redacted"] as JObject;
        }

        /// <summary>Force a session write. Used on shutdown and after logout.</summary>
        public Task FlushAsync(CancellationToken cancellationToken)
        {
            return PersistIfChangedAsync(true, cancellationToken);
        }

        // -- publish guard -----------------------------------------------------

        public async Task<JObject> LoadPublishGuardAsync(CancellationToken cancellationToken)
        {
            try
            {
                string text = await _store.ReadTextAsync(GuardPath, cancellationToken).ConfigureAwait(false);
                JObject parsed = TryParse(text);
                return parsed ?? new JObject();
            }
            catch (Exception)
            {
                return new JObject();
            }
        }

        public async Task SavePublishGuardAsync(JObject guard, CancellationToken cancellationToken)
        {
            if (guard == null) return;
            try
            {
                await _store.WriteTextAsync(GuardPath, guard.ToString(Newtonsoft.Json.Formatting.None), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Losing the guard is survivable: the worst case is one duplicate
                // comment after a restart, which is better than failing a publish.
                _log.Warn(DiagCategory.Publish, "could not persist the duplicate guard: " + exception.Message);
            }
        }

        // -- persistence -------------------------------------------------------

        private async Task PersistIfChangedAsync(bool force, CancellationToken cancellationToken)
        {
            JObject identity;
            lock (_gate)
            {
                identity = _identity;
            }
            if (identity == null) return;

            string json = identity.ToString(Newtonsoft.Json.Formatting.None);
            if (!force && json == _lastPersistedJson) return;

            try
            {
                await _store.WriteTextAsync(IdentityPath, json, cancellationToken).ConfigureAwait(false);
                _lastPersistedJson = json;
            }
            catch (Exception exception)
            {
                // Reported, not thrown: a session that could not be saved is worth
                // knowing about, but it must not fail the operation that triggered it.
                _log.Error(DiagCategory.Storage,
                    "could not persist the session identity: " + exception.Message);
            }
        }

        private static JObject TryParse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                return JObject.Parse(text);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }

        private void LogWarnings(JObject result)
        {
            var warnings = JsonValue.GetStrings(result, "warnings");
            if (warnings != null && warnings.Count > 0 && _log != null)
            {
                _log.Info(DiagCategory.Session, "identity notes",
                    new JObject { { "warnings", new JArray(warnings) } });
            }
        }
    }
}
