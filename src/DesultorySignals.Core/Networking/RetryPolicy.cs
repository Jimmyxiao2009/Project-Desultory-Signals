using System;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Models;

namespace DesultorySignals.Core.Networking
{
    /// <summary>
    /// Bounded retry with a rule set that differs between reads and writes.
    /// </summary>
    /// <remarks>
    /// The brief's rule is "writes get at most a safe, limited, decidable retry".
    /// The hard part is *decidable*: after a network error, was the comment posted
    /// or not? The answer for HTTP without a response is genuinely ambiguous, so
    /// this policy does not pretend otherwise:
    ///
    /// * Reads retry on any retryable classification, with exponential backoff and
    ///   jitter, up to <see cref="MaxAttempts"/>.
    /// * Writes retry only when the failure happened *before a response existed*
    ///   (a transport-level network error), at most once, and the caller is
    ///   expected to also hold a duplicate guard. A 5xx or a timeout after the
    ///   request was written is never retried, because the server may well have
    ///   applied it.
    ///
    /// The distinction is expressed by which factory the caller uses, not by a
    /// boolean the caller could get wrong.
    /// </remarks>
    public sealed class RetryPolicy
    {
        public int MaxAttempts { get; private set; }
        public int BaseDelayMs { get; private set; }
        public int MaxDelayMs { get; private set; }
        public bool WritesOnlyOnTransportFailure { get; private set; }
        private readonly Random _jitter;

        private RetryPolicy(int maxAttempts, int baseDelayMs, int maxDelayMs, bool writesOnlyOnTransportFailure, int? seed)
        {
            MaxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
            BaseDelayMs = baseDelayMs;
            MaxDelayMs = maxDelayMs;
            WritesOnlyOnTransportFailure = writesOnlyOnTransportFailure;
            _jitter = seed.HasValue ? new Random(seed.Value) : new Random();
        }

        /// <summary>Metadata, comments, feed pages: safe to repeat.</summary>
        public static RetryPolicy ForReads()
        {
            return new RetryPolicy(3, 400, 4000, false, null);
        }

        /// <summary>Feed refresh on a flaky phone radio: a couple more tries.</summary>
        public static RetryPolicy ForFeed()
        {
            return new RetryPolicy(4, 300, 5000, false, null);
        }

        /// <summary>Publishing: one retry, and only when nothing came back.</summary>
        public static RetryPolicy ForWrites()
        {
            return new RetryPolicy(2, 800, 8000, true, null);
        }

        /// <summary>Range reads during playback: fast, few, and quiet.</summary>
        public static RetryPolicy ForRangeReads()
        {
            return new RetryPolicy(3, 250, 2000, false, null);
        }

        /// <summary>Deterministic variant for tests.</summary>
        public static RetryPolicy ForTesting(int maxAttempts, int seed)
        {
            return new RetryPolicy(maxAttempts, 1, 1, false, seed);
        }

        /// <summary>
        /// Run <paramref name="action"/> until it succeeds, it fails
        /// non-retryably, or the attempt budget runs out. <paramref name="action"/>
        /// receives the 1-based attempt number so it can vary the request (a feed
        /// refresh can bump <c>refresh_index</c>).
        /// </summary>
        public async Task<T> ExecuteAsync<T>(
            Func<int, CancellationToken, Task<T>> action,
            DiagLog log,
            string category,
            string label,
            CancellationToken cancellationToken)
        {
            if (action == null) throw new ArgumentNullException("action");

            DouyinApiException lastFailure = null;

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await action(attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (DouyinApiException failure)
                {
                    lastFailure = failure;
                    if (!ShouldRetry(failure, attempt))
                    {
                        if (attempt > 1 && log != null)
                        {
                            log.Warn(category, string.Format(
                                "{0}: attempt {1} failed non-retryably ({2}), giving up",
                                label, attempt, failure.Kind));
                        }
                        throw;
                    }

                    int delay = DelayFor(attempt);
                    if (log != null)
                    {
                        log.Info(category, string.Format(
                            "{0}: attempt {1}/{2} failed ({3}), retrying in {4}ms",
                            label, attempt, MaxAttempts, failure.Kind, delay));
                    }
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception unexpected)
                {
                    // An unclassified exception is a bug in our own code; retrying
                    // it would only multiply the damage.
                    if (log != null)
                    {
                        log.Error(category, string.Format(
                            "{0}: attempt {1} threw {2}: {3}",
                            label, attempt, unexpected.GetType().Name, unexpected.Message));
                    }
                    throw;
                }
            }

            throw lastFailure ?? DouyinApiException.Network(label + ": exhausted retries", null);
        }

        private bool ShouldRetry(DouyinApiException failure, int attempt)
        {
            if (attempt >= MaxAttempts) return false;
            if (failure.Error == null) return false;

            if (WritesOnlyOnTransportFailure)
            {
                // Only a failure that provably produced no response is safe to
                // repeat for a write.
                return failure.Kind == ErrorKinds.Network;
            }

            return failure.Error.Retryable;
        }

        private int DelayFor(int attempt)
        {
            // Exponential backoff with jitter: the jitter matters because several
            // services (feed, comments, images) can be failing at the same moment
            // and synchronized retries would re-create the burst that caused it.
            long delay = (long)BaseDelayMs * (1L << (attempt - 1));
            if (delay > MaxDelayMs) delay = MaxDelayMs;
            int jittered = (int)delay + _jitter.Next(0, Math.Max(1, (int)(delay / 3)));
            return jittered;
        }
    }
}
