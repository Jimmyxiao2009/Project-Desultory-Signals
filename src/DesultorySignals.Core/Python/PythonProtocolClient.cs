using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DesultorySignals.Core.Python
{
    /// <summary>
    /// Runs work on the single thread that owns the embedded CPython interpreter.
    /// </summary>
    /// <remarks>
    /// CPython's embedding ABI here is thread-affine: the interpreter is created on
    /// one thread and every subsequent call must come from that same thread. The
    /// Core layer therefore never touches the runtime directly -- it hands a closure
    /// to this interface, and the platform implementation queues it onto the engine
    /// thread.
    ///
    /// Keeping this as an interface (rather than, say, a lock) is what makes the
    /// Core unit-testable on a desktop with no interpreter at all: the test executor
    /// runs the closure inline.
    /// </remarks>
    public interface IEngineExecutor
    {
        /// <summary>True when the engine thread exists and the interpreter initialized.</summary>
        bool IsAvailable { get; }

        Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken);
    }

    /// <summary>The raw, synchronous bridge to the native host ABI.</summary>
    public interface IPythonRuntimeBackend
    {
        bool IsReady { get; }

        /// <summary>Send one JSON request, get one JSON response. Runs on the engine thread.</summary>
        string CallJson(string requestJson);

        /// <summary>A short human-readable description of the runtime, for diagnostics.</summary>
        string Describe();
    }

    /// <summary>
    /// The protocol surface every service depends on.
    /// </summary>
    /// <remarks>
    /// An interface rather than the concrete client so that the services can be
    /// tested on a desktop with no interpreter at all: the test suite supplies canned
    /// normalized payloads and asserts the service's behaviour. That is what makes
    /// "feed pagination", "duplicate removal" and "comment pagination" testable
    /// requirements rather than aspirations.
    /// </remarks>
    public interface IDouyinProtocol
    {
        bool IsReady { get; }

        Task<JObject> CallAsync(string operation, JObject fields, CancellationToken cancellationToken);

        Task<JObject> CallWithIdentityAsync(
            string operation, JObject identity, JObject fields, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Typed access to the Python protocol layer.
    /// </summary>
    /// <remarks>
    /// This is the only place in the C# code that knows the bridge protocol exists.
    /// Everything above it calls methods like <c>BuildRequestAsync</c> and gets
    /// either a result or a classified <see cref="DouyinApiException"/>. Everything
    /// below it is a string in and a string out.
    /// </remarks>
    public sealed class PythonProtocolClient : IDouyinProtocol
    {
        private readonly IPythonRuntimeBackend _backend;
        private readonly IEngineExecutor _executor;
        private readonly DiagLog _log;

        public PythonProtocolClient(IPythonRuntimeBackend backend, IEngineExecutor executor, DiagLog log)
        {
            if (backend == null) throw new ArgumentNullException("backend");
            if (executor == null) throw new ArgumentNullException("executor");
            _backend = backend;
            _executor = executor;
            _log = log;
        }

        public bool IsReady
        {
            get { return _backend.IsReady; }
        }

        public string DescribeRuntime()
        {
            return _backend.Describe();
        }

        /// <summary>
        /// Invoke one protocol operation and return its <c>result</c> object.
        /// </summary>
        /// <param name="operation">e.g. "request.build". Must exist in
        /// <c>python/douyin/protocol.py</c>.</param>
        /// <param name="fields">Operation fields, merged at the top level of the request.</param>
        public async Task<JObject> CallAsync(string operation, JObject fields, CancellationToken cancellationToken)
        {
            JObject envelope = await CallEnvelopeAsync(operation, fields, cancellationToken)
                .ConfigureAwait(false);

            JObject error = envelope["error"] as JObject;
            if (error != null)
            {
                ApiError parsed = ApiError.FromJson(error);
                _log.Warn(DiagCategory.Protocol,
                    string.Format("op {0} failed: {1}", operation, parsed.Kind),
                    new JObject { { "kind", parsed.Kind }, { "httpStatus", parsed.HttpStatus } });
                throw new DouyinApiException(parsed);
            }

            return envelope["result"] as JObject ?? new JObject();
        }

        /// <summary>The full envelope, including <c>ok</c> and <c>error</c>.</summary>
        public Task<JObject> CallEnvelopeAsync(string operation, JObject fields, CancellationToken cancellationToken)
        {
            var request = fields == null ? new JObject() : (JObject)fields.DeepClone();
            request["op"] = operation;

            string requestJson = request.ToString(Formatting.None);

            return _executor.InvokeAsync(
                delegate
                {
                    string responseJson = _backend.CallJson(requestJson);
                    if (string.IsNullOrEmpty(responseJson))
                    {
                        throw new DouyinApiException(ApiError.Local(
                            ErrorKinds.Internal,
                            "the Python runtime returned nothing for " + operation,
                            false));
                    }

                    JObject envelope;
                    try
                    {
                        envelope = JObject.Parse(responseJson);
                    }
                    catch (JsonException exception)
                    {
                        // A parse failure here means the bridge itself is broken, not
                        // the remote API. Report a bounded prefix so it is debuggable
                        // without dumping an arbitrary amount of text into the log.
                        throw new DouyinApiException(ApiError.Local(
                            ErrorKinds.Internal,
                            "the bridge response was not JSON: " + exception.Message
                                + " | prefix=" + Truncate(responseJson, 200),
                            false));
                    }

                    if (!JsonValue.GetBool(envelope, "ok", false) && envelope["error"] == null)
                    {
                        throw new DouyinApiException(ApiError.Local(
                            ErrorKinds.Internal,
                            "the bridge reported failure without an error object for " + operation,
                            false));
                    }
                    return envelope;
                },
                cancellationToken);
        }

        /// <summary>Convenience for the many operations that take an identity.</summary>
        public Task<JObject> CallWithIdentityAsync(
            string operation, JObject identity, JObject fields, CancellationToken cancellationToken)
        {
            var request = fields == null ? new JObject() : (JObject)fields.DeepClone();
            request["identity"] = identity;
            return CallAsync(operation, request, cancellationToken);
        }

        private static string Truncate(string text, int limit)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= limit) return text;
            return text.Substring(0, limit) + "...";
        }

        /// <summary>Shared request-building helper so every caller produces the same shape.</summary>
        public static JObject Fields(params object[] nameValuePairs)
        {
            if (nameValuePairs == null || nameValuePairs.Length == 0) return new JObject();
            if (nameValuePairs.Length % 2 != 0)
                throw new ArgumentException("expected an even number of name/value arguments");

            var result = new JObject();
            for (int index = 0; index < nameValuePairs.Length; index += 2)
            {
                string name = (string)nameValuePairs[index];
                object value = nameValuePairs[index + 1];
                result[name] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
            }
            return result;
        }
    }

    /// <summary>
    /// An executor that runs work inline. Used by desktop tests and by the
    /// diagnostics path when no engine thread has been started yet, so a caller gets
    /// a clear "runtime unavailable" error instead of a deadlock.
    /// </summary>
    public sealed class InlineEngineExecutor : IEngineExecutor
    {
        public bool IsAvailable
        {
            get { return true; }
        }

        public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(work());
        }
    }

    /// <summary>
    /// Placeholder backend for a host that has no interpreter. Every call fails with
    /// <see cref="ErrorKinds.SignerUnavailable"/>, which is the honest answer and
    /// keeps a build without the runtime diagnosable rather than mysterious.
    /// </summary>
    public sealed class UnavailablePythonRuntime : IPythonRuntimeBackend
    {
        private readonly string _reason;

        public UnavailablePythonRuntime(string reason)
        {
            _reason = reason ?? "no CPython runtime is attached";
        }

        public bool IsReady
        {
            get { return false; }
        }

        public string CallJson(string requestJson)
        {
            return "{\"ok\":false,\"error\":{\"kind\":\"" + ErrorKinds.SignerUnavailable
                + "\",\"message\":\"" + _reason.Replace("\"", "'")
                + "\",\"httpStatus\":null,\"retryable\":false,\"detail\":{}}}";
        }

        public string Describe()
        {
            return "unavailable: " + _reason;
        }
    }
}
