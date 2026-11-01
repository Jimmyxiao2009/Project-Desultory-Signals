using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Models;
using DesultorySignals.Core.Python;

namespace DesultorySignals.Platform
{
    /// <summary>
    /// The one thread that owns the embedded CPython interpreter.
    /// </summary>
    /// <remarks>
    /// The native driver is thread-affine: the interpreter is created on a thread and
    /// every later call must come from that same thread, otherwise the ABI answers
    /// <c>PYUWP_E_WRONG_THREAD (-2)</c> instead of running anything. A UWP UI thread
    /// cannot be that thread -- it is not ours to own and it must never block -- so
    /// this class creates its own background thread and funnels every request through
    /// a single-producer/consumer queue.
    ///
    /// The queue (rather than a lock) is what gives the ordering guarantee the Core
    /// layer depends on: <see cref="PythonProtocolClient"/> may be called from any
    /// number of tasks and the operations still reach the interpreter one at a time,
    /// in the order they were queued.
    ///
    /// Cancellation cannot interrupt a Python call in flight -- the ABI has no
    /// interruption point -- so a cancelled token only prevents work that has not
    /// started yet from running. That is stated here rather than silently pretended,
    /// because the alternative (abandoning the interpreter mid-call) would corrupt it.
    /// </remarks>
    public sealed class EngineThread : IEngineExecutor, IDisposable
    {
        /// <summary>True only on the thread running the engine loop. A
        /// <see cref="ThreadStaticAttribute"/> is the honest way to ask this question:
        /// it is exactly "which thread am I on", with no id bookkeeping to drift.</summary>
        [ThreadStatic]
        private static bool _isEngineLoop;

        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly DiagLog _log;
        private readonly object _gate = new object();

        private Task _loop;
        private volatile bool _running;
        private bool _disposed;

        public EngineThread(DiagLog log)
        {
            _log = log;
        }

        /// <summary>True once the thread has been started. Says nothing about the
        /// interpreter: <see cref="PythonUwpRuntime.IsReady"/> reports that.</summary>
        public bool IsAvailable
        {
            get { return _running; }
        }

        /// <summary>Managed id of the engine loop thread, or 0 when the caller is not
        /// on it. Diagnostics only: the authoritative test is
        /// <see cref="IsEngineThread"/>.</summary>
        public int ThreadId
        {
            get { return _isEngineLoop ? Environment.CurrentManagedThreadId : 0; }
        }

        /// <summary>True when the caller is already on the engine thread. Used to let
        /// the runtime bypass the queue instead of deadlocking on itself.</summary>
        public bool IsEngineThread
        {
            get { return _isEngineLoop; }
        }

        /// <summary>
        /// Create the engine loop. Idempotent.
        /// </summary>
        /// <remarks>
        /// <c>TaskCreationOptions.LongRunning</c> rather than
        /// <c>System.Threading.Thread</c>: the UWP reference set this project compiles
        /// against does not expose the <c>Thread</c> type at all (nor is the
        /// <c>System.Threading.Thread</c> package available offline), while a
        /// long-running task is the documented way to ask for a dedicated thread --
        /// which is what thread affinity requires. A pooled work item would be a
        /// gamble that the pool never migrates it.
        /// </remarks>
        public void Start()
        {
            lock (_gate)
            {
                if (_running) return;
                _running = true;
                _loop = Task.Factory.StartNew(
                    new Action(Run),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                _log.Info(DiagCategory.Python, "engine loop started");
            }
        }

        public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken)
        {
            if (work == null) throw new ArgumentNullException("work");

            if (cancellationToken.IsCancellationRequested)
            {
                var cancelled = new TaskCompletionSource<T>();
                cancelled.SetCanceled();
                return cancelled.Task;
            }

            var completion = new TaskCompletionSource<T>();
            CancellationTokenRegistration registration = default(CancellationTokenRegistration);
            if (cancellationToken.CanBeCanceled)
            {
                registration = cancellationToken.Register(
                    delegate { completion.TrySetCanceled(); });
            }

            if (_disposed || !_running || _queue.IsAddingCompleted)
            {
                registration.Dispose();
                completion.TrySetException(new DouyinApiException(ApiError.Local(
                    ErrorKinds.Internal, "the Python engine thread is not running", false)));
                return completion.Task;
            }

            try
            {
                _queue.Add(delegate
                {
                    try
                    {
                        if (completion.Task.IsCompleted) return;
                        completion.TrySetResult(work());
                    }
                    catch (OperationCanceledException)
                    {
                        completion.TrySetCanceled();
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                    finally
                    {
                        registration.Dispose();
                    }
                });
            }
            catch (InvalidOperationException)
            {
                // Raced with shutdown between the check above and the Add.
                registration.Dispose();
                completion.TrySetException(new DouyinApiException(ApiError.Local(
                    ErrorKinds.Internal, "the Python engine thread stopped before the work was queued", false)));
            }

            return completion.Task;
        }

        public Task InvokeAsync(Action work, CancellationToken cancellationToken)
        {
            if (work == null) throw new ArgumentNullException("work");
            return InvokeAsync<bool>(
                delegate
                {
                    work();
                    return true;
                },
                cancellationToken);
        }

        /// <summary>
        /// Stop accepting work and wait briefly for the queue to drain. Called from
        /// suspension; the interpreter is finalized by the runtime, on this thread,
        /// before this returns.
        /// </summary>
        /// <remarks>
        /// A work item that is queued after this point is neither run nor completed, and
        /// a caller awaiting it will wait until the process goes away. That is the
        /// deliberate choice: running it would mean calling the native ABI from the
        /// wrong thread (which answers <c>-2</c> and, worse, pretends to have worked),
        /// and this method is only ever called while the app is being suspended.
        /// </remarks>
        public void Shutdown(int timeoutMs)
        {
            Task loop;
            lock (_gate)
            {
                if (!_running) return;
                _running = false;
                loop = _loop;
            }

            try
            {
                _queue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
            }

            if (loop != null)
            {
                try
                {
                    if (!loop.Wait(Math.Max(timeoutMs, 0)))
                        _log.Warn(DiagCategory.Python, "the engine loop did not stop within the timeout");
                }
                catch (Exception exception)
                {
                    _log.Warn(DiagCategory.Python, "waiting for the engine loop failed: " + exception.Message);
                }
            }

            _log.Info(DiagCategory.Python, "engine loop stopped");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Shutdown(4000);
            try
            {
                _queue.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private void Run()
        {
            // Marked before the first item is consumed: IsEngineThread is only allowed
            // to answer "yes" for this loop.
            _isEngineLoop = true;
            _log.Info(DiagCategory.Python,
                "engine loop running (managed id " + Environment.CurrentManagedThreadId + ")");

            try
            {
                foreach (Action item in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        item();
                    }
                    catch (Exception exception)
                    {
                        // A work item must never kill the loop: the interpreter is still
                        // healthy and the next request has to be servable.
                        _log.Error(DiagCategory.Python, "engine work item threw: " + exception.Message);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                _log.Error(DiagCategory.Python, "engine loop ended: " + exception.Message);
            }
            finally
            {
                _isEngineLoop = false;
            }
        }
    }
}
