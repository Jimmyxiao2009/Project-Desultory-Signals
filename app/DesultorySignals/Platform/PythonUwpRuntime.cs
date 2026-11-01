using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Models;
using DesultorySignals.Core.Python;
using Newtonsoft.Json.Linq;

namespace DesultorySignals.Platform
{
    /// <summary>
    /// P/Invoke surface of <c>native/pythonhost/PyUwpDriver.h</c>.
    /// </summary>
    /// <remarks>
    /// Two marshalling decisions are deliberate and load-bearing:
    ///
    /// * The JSON request goes in as a <c>byte[]</c> built by this file from UTF-8,
    ///   not as a <c>string</c> with <c>CharSet.Ansi</c>. <c>LPStr</c> means "the
    ///   process ANSI code page", so a Chinese comment or an emoji in a request would
    ///   arrive at the interpreter as mojibake before Python ever saw it.
    /// * The response comes back as a pointer plus a byte length and is copied with
    ///   <c>Marshal.Copy</c> + <c>Encoding.UTF8.GetString</c>, never
    ///   <c>PtrToStringAnsi</c> -- same reason, in the other direction.
    /// </remarks>
    internal static class NativeRuntime
    {
        internal const string Dll = "python-uwp.dll";

        internal const int Ok = 0;
        internal const int InvalidArgument = -1;
        internal const int WrongThread = -2;
        internal const int Initialization = -1000;
        internal const int BridgeUnavailable = -1100;
        internal const int Execution = -2000;
        internal const int ResponseTooLarge = -2100;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int PyUwpRuntime_Initialize(
            [MarshalAs(UnmanagedType.LPWStr)] string runtimeRoot,
            [MarshalAs(UnmanagedType.LPWStr)] string localSite);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int PyUwpRuntime_CallJsonEx(
            byte[] requestUtf8,
            out IntPtr responseUtf8,
            out int responseLength);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern IntPtr PyUwpRuntime_LastError();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int PyUwpRuntime_BridgeReady();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int PyUwpRuntime_Finalize();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int PyUwpRuntime_RunUtf8(byte[] sourceUtf8);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern IntPtr PyUwpRuntime_LastOutput();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int PyUwpSelfTest();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern IntPtr PyUwpLastError();

        /// <summary>UTF-8 with a NUL terminator, as the C ABI expects.</summary>
        internal static byte[] Encode(string text)
        {
            if (string.IsNullOrEmpty(text)) return new byte[1];
            int length = Encoding.UTF8.GetByteCount(text);
            var payload = new byte[length + 1];
            Encoding.UTF8.GetBytes(text, 0, text.Length, payload, 0);
            return payload;
        }

        /// <summary>Read a NUL-terminated UTF-8 C string. Bounded so a missing
        /// terminator cannot turn into an unbounded walk through memory.</summary>
        internal static string ReadUtf8(IntPtr pointer, int maximum = 65536)
        {
            if (pointer == IntPtr.Zero) return null;

            var scratch = new byte[256];
            var result = new StringBuilder();
            int offset = 0;
            while (offset < maximum)
            {
                int chunk = Math.Min(scratch.Length, maximum - offset);
                Marshal.Copy(new IntPtr(pointer.ToInt64() + offset), scratch, 0, chunk);
                int terminator = Array.IndexOf(scratch, (byte)0, 0, chunk);
                if (terminator >= 0)
                {
                    result.Append(Encoding.UTF8.GetString(scratch, 0, terminator));
                    return result.ToString();
                }
                result.Append(Encoding.UTF8.GetString(scratch, 0, chunk));
                offset += chunk;
            }
            return result.ToString();
        }

        /// <summary>Copy exactly <paramref name="length"/> UTF-8 bytes out of the
        /// runtime-owned response buffer. The buffer is only valid until the next ABI
        /// call, so this copy is what makes the string safe to hand upwards.</summary>
        internal static string CopyUtf8(IntPtr pointer, int length)
        {
            if (pointer == IntPtr.Zero || length <= 0) return null;
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes, 0, length);
        }

        internal static string Describe(int code)
        {
            switch (code)
            {
                case Ok: return "ok";
                case InvalidArgument: return "invalid argument";
                case WrongThread: return "wrong thread";
                case Initialization: return "initialization failed";
                case BridgeUnavailable: return "protocol layer unavailable";
                case Execution: return "execution failed";
                case ResponseTooLarge: return "response too large";
                default: return "unknown code " + code;
            }
        }
    }

    /// <summary>
    /// The device's <see cref="IPythonRuntimeBackend"/>: CPython 3.15 behind the
    /// native driver, reachable only from the engine thread.
    /// </summary>
    /// <remarks>
    /// <see cref="CallJson"/> is invoked by <see cref="PythonProtocolClient"/> inside
    /// an <see cref="EngineThread"/> work item, i.e. it is already on the engine
    /// thread. The off-thread branch below exists only so that a mistake surfaces as
    /// a logged warning plus a correct marshalled call rather than as the native
    /// <c>-2</c> the ABI would otherwise return (which reads as a mysterious signer
    /// failure rather than as a threading bug).
    /// </remarks>
    public sealed class PythonUwpRuntime : IPythonRuntimeBackend
    {
        private readonly EngineThread _thread;
        private readonly DiagLog _log;

        private volatile bool _ready;
        private int _initializeResult = int.MinValue;
        private int _bridgeReadyResult = -1;
        private string _describe = "not initialized";

        public PythonUwpRuntime(EngineThread thread, DiagLog log)
        {
            if (thread == null) throw new ArgumentNullException("thread");
            _thread = thread;
            _log = log;
        }

        public bool IsReady
        {
            get { return _ready; }
        }

        /// <summary>The raw return code of <c>PyUwpRuntime_Initialize</c>.</summary>
        public int InitializeResult
        {
            get { return _initializeResult; }
        }

        /// <summary>1 when <c>douyin.protocol</c> imported, 0 when not, -1 when never probed.</summary>
        public int BridgeReadyResult
        {
            get { return _bridgeReadyResult; }
        }

        public string Describe()
        {
            return _describe;
        }

        /// <summary>UTF-8 detail for the last failed ABI call.</summary>
        public string LastError()
        {
            return NativeRuntime.ReadUtf8(NativeRuntime.PyUwpRuntime_LastError());
        }

        /// <summary>Captured stdout/stderr of the last <see cref="RunUtf8"/>.</summary>
        public string LastOutput()
        {
            return NativeRuntime.ReadUtf8(NativeRuntime.PyUwpRuntime_LastOutput());
        }

        public bool BridgeReady()
        {
            try
            {
                return NativeRuntime.PyUwpRuntime_BridgeReady() == 1;
            }
            catch (Exception exception)
            {
                _log.Error(DiagCategory.Python, "BridgeReady probe failed: " + exception.Message);
                return false;
            }
        }

        /// <summary>
        /// Bring the interpreter up. Must run on the engine thread; call it through
        /// <see cref="EngineThread.InvokeAsync{T}"/>.
        /// </summary>
        /// <param name="runtimeRoot">Installed package folder, which holds
        /// <c>Lib\</c> and <c>AppLib\</c>.</param>
        /// <param name="localSite">Writable site-packages directory inside LocalState.
        /// The native driver requires it to exist.</param>
        public bool Initialize(string runtimeRoot, string localSite)
        {
            if (string.IsNullOrEmpty(runtimeRoot))
                throw new ArgumentException("runtimeRoot is required", "runtimeRoot");
            if (string.IsNullOrEmpty(localSite))
                throw new ArgumentException("localSite is required", "localSite");

            if (!_thread.IsEngineThread)
            {
                _log.Warn(DiagCategory.Python,
                    "Initialize was called off the engine thread; marshalling onto it");
                return _thread.InvokeAsync(
                    delegate { return Initialize(runtimeRoot, localSite); },
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            try
            {
                _initializeResult = NativeRuntime.PyUwpRuntime_Initialize(runtimeRoot, localSite);
            }
            catch (Exception exception)
            {
                _initializeResult = NativeRuntime.Initialization;
                _describe = "python-uwp.dll could not be loaded: " + exception.Message;
                _log.Error(DiagCategory.Python, _describe);
                return false;
            }

            if (_initializeResult != NativeRuntime.Ok)
            {
                _describe = string.Format(
                    "initialize returned {0} ({1}): {2}",
                    _initializeResult,
                    NativeRuntime.Describe(_initializeResult),
                    LastError() ?? "(no detail)");
                _log.Error(DiagCategory.Python, _describe);
                return false;
            }

            _bridgeReadyResult = NativeRuntime.PyUwpRuntime_BridgeReady();
            _ready = _bridgeReadyResult == 1;

            _describe = string.Format(
                "CPython attached from {0} (initialize=ok, bridgeReady={1})",
                runtimeRoot, _bridgeReadyResult);

            if (_ready)
            {
                _log.Info(DiagCategory.Python, _describe);
            }
            else
            {
                _log.Error(DiagCategory.Python, _describe + " | import failure: "
                    + (LastError() ?? "(no detail)"));
            }

            return true;
        }

        /// <summary>
        /// The ABI probe the parent build exposes: 1 when the first package smoke test
        /// passes. Kept available for the diagnostics page.
        /// </summary>
        public int SelfTest()
        {
            try
            {
                return NativeRuntime.PyUwpSelfTest();
            }
            catch (Exception exception)
            {
                _log.Error(DiagCategory.Python, "SelfTest probe failed: " + exception.Message);
                return -1;
            }
        }

        public string CallJson(string requestJson)
        {
            if (!_thread.IsEngineThread)
            {
                // Not the intended path -- the Core layer always goes through the
                // executor -- but a wrong-thread call must not silently become a -2.
                _log.Warn(DiagCategory.Python,
                    "CallJson arrived off the engine thread; marshalling onto it");
                return _thread.InvokeAsync(
                    delegate { return CallJsonOnEngineThread(requestJson); },
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            return CallJsonOnEngineThread(requestJson);
        }

        /// <summary>Run a statement in <c>__main__</c> and return the ABI code. Only
        /// meaningful on the engine thread; used by the diagnostics page.</summary>
        public int RunUtf8(string source)
        {
            if (!_thread.IsEngineThread)
            {
                return _thread.InvokeAsync(
                    delegate { return RunUtf8(source); },
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            return NativeRuntime.PyUwpRuntime_RunUtf8(NativeRuntime.Encode(source));
        }

        /// <summary>Finalize the interpreter. Must be the engine thread, and must be
        /// the last thing that thread does with the runtime.</summary>
        public int Shutdown()
        {
            if (!_thread.IsEngineThread)
            {
                return _thread.InvokeAsync(
                    delegate { return Shutdown(); },
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            try
            {
                int code = NativeRuntime.PyUwpRuntime_Finalize();
                _ready = false;
                _describe = "finalized (code " + code + ")";
                _log.Info(DiagCategory.Python, _describe);
                return code;
            }
            catch (Exception exception)
            {
                _log.Error(DiagCategory.Python, "finalize failed: " + exception.Message);
                return -1;
            }
        }

        private string CallJsonOnEngineThread(string requestJson)
        {
            byte[] request = NativeRuntime.Encode(requestJson);
            IntPtr responsePointer;
            int responseLength;

            int code;
            try
            {
                code = NativeRuntime.PyUwpRuntime_CallJsonEx(request, out responsePointer, out responseLength);
            }
            catch (Exception exception)
            {
                _log.Error(DiagCategory.Python,
                    "the bridge call threw before reaching the interpreter: " + exception.Message);
                throw new DouyinApiException(ApiError.Local(
                    ErrorKinds.SignerUnavailable,
                    "python-uwp.dll call failed: " + exception.Message,
                    false));
            }

            string responseText = NativeRuntime.CopyUtf8(responsePointer, responseLength);

            if (code != NativeRuntime.Ok)
            {
                // A negative code still often carries a structured {"ok":false,...}
                // envelope, which the Core layer can classify. Only when there is
                // nothing at all is this a transport-level mystery.
                _log.Warn(DiagCategory.Python, string.Format(
                    "CallJsonEx returned {0} ({1}); payload {2} bytes",
                    code,
                    NativeRuntime.Describe(code),
                    responseLength));

                if (string.IsNullOrEmpty(responseText))
                {
                    string detail = LastError();
                    throw new DouyinApiException(ApiError.Local(
                        ErrorKinds.SignerUnavailable,
                        string.Format("the bridge returned {0} ({1}) with no payload: {2}",
                            code, NativeRuntime.Describe(code), detail ?? "(no detail)"),
                        false));
                }
            }

            return responseText;
        }
    }
}
