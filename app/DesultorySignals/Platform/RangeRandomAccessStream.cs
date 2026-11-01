using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace DesultorySignals.Platform
{
    /// <summary>
    /// Presents an <see cref="DesultorySignals.Core.Media.IRandomAccessByteSource"/> to
    /// <c>MediaElement</c> as a WinRT <see cref="IRandomAccessStream"/>.
    /// </summary>
    /// <remarks>
    /// <b>Buffer access: this implementation writes into the caller's
    /// <see cref="IBuffer"/> with <c>WindowsRuntimeBufferExtensions.CopyTo(byte[], int,
    /// IBuffer, uint, int)</c>, and reads a response buffer out with
    /// <c>ToArray()</c>.</b> The alternatives were rejected for concrete reasons:
    /// <c>buffer.AsStream()</c> gives a stream whose initial position depends on the
    /// buffer's state rather than on the interface contract, and the
    /// <c>IBufferByteAccess</c> COM interop route needs <c>unsafe</c> plus a hand-rolled
    /// vtable cast for no benefit here. The two extension methods used are the
    /// symmetrical, position-explicit pair.
    ///
    /// Every read goes through <see cref="DesultorySignals.Core.Media.IRandomAccessByteSource.ReadAsync"/>
    /// with the *current* <see cref="Position"/>. Nothing is assumed about ordering:
    /// <c>MediaElement</c> reads the moov atom at the end of an MP4 before it asks for
    /// the first byte, so a stream that only worked forwards would fail on every real
    /// file.
    ///
    /// The byte source is *not* owned here. <c>PlaybackService</c> keeps a reference to
    /// it and disposes it when the video is replaced; disposing this stream only
    /// cancels in-flight reads, which is exactly what happens when
    /// <c>MediaElement</c> switches to the next video.
    /// </remarks>
    public sealed class RangeRandomAccessStream : IRandomAccessStream
    {
        /// <summary>Reads are chunked by the media pipeline; this is only a guard
        /// against a hostile request turning into a hostile allocation.</summary>
        private const int MaximumReadSize = 8 * 1024 * 1024;

        private readonly Core.Media.IRandomAccessByteSource _source;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly object _gate = new object();

        private byte[] _scratch = new byte[64 * 1024];
        private readonly SemaphoreSlim _readGate = new SemaphoreSlim(1, 1);
        private int _seekGeneration;
        private ulong _position;
        private bool _disposed;

        public RangeRandomAccessStream(Core.Media.IRandomAccessByteSource source)
        {
            if (source == null) throw new ArgumentNullException("source");
            _source = source;
        }

        /// <summary>
        /// Total length. Zero means "not known yet": the media pipeline is told the
        /// size after <c>MediaElementHost</c> has probed the source once, because a
        /// blocking probe inside a property getter would stall the UI thread.
        /// </summary>
        public ulong Size
        {
            get { return _source.Size > 0 ? (ulong)_source.Size : 0UL; }
            set
            {
                // A read-only source cannot be resized. MediaElement does not do this;
                // ignoring it keeps playback alive instead of failing the whole load.
            }
        }

        public ulong Position
        {
            get { lock (_gate) { return _position; } }
            set { Seek(value); }
        }

        public bool CanRead
        {
            get { return !_disposed; }
        }

        public bool CanWrite
        {
            get { return false; }
        }

        public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(
            IBuffer buffer, uint count, InputStreamOptions options)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (_disposed) throw new ObjectDisposedException("RangeRandomAccessStream");

            // The two-type-parameter overload is the one that produces the
            // IAsyncOperationWithProgress the media pipeline expects; its delegate takes
            // the progress sink as well as the cancellation token.
            return AsyncInfo.Run<IBuffer, uint>(
                new Func<CancellationToken, IProgress<uint>, Task<IBuffer>>(
                    (cancellationToken, progress) => ReadCoreAsync(cancellationToken, buffer, count)));
        }

        private async Task<IBuffer> ReadCoreAsync(CancellationToken cancellationToken, IBuffer buffer, uint count)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token))
            {
                await _readGate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    int wanted = (int)Math.Min(Math.Min(count, buffer.Capacity), (uint)MaximumReadSize);
                    byte[] scratch;
                    long position;
                    int generation;
                    lock (_gate)
                    {
                        position = (long)_position;
                        generation = _seekGeneration;
                        if (_scratch.Length < wanted) _scratch = new byte[wanted];
                        scratch = _scratch;
                    }
                    int read = await _source.ReadAsync(position, scratch, 0, wanted, linked.Token).ConfigureAwait(false);
                    read = Math.Max(0, Math.Min(read, wanted));
                    if (read > 0) WindowsRuntimeBufferExtensions.CopyTo(scratch, 0, buffer, 0, read);
                    buffer.Length = (uint)read;
                    lock (_gate)
                    {
                        if (_seekGeneration == generation) _position = (ulong)(position + read);
                    }
                    return buffer;
                }
                finally { _readGate.Release(); }
            }
        }

        public IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer)
        {
            throw new NotSupportedException("the video source is read-only");
        }

        public IAsyncOperation<bool> FlushAsync()
        {
            return Task.FromResult(true).AsAsyncOperation();
        }

        /// <summary>Move the read cursor. WinRT's <c>IRandomAccessStream.Seek</c>
        /// returns void -- unlike <c>Stream.Seek</c>, there is no new position to hand
        /// back.</summary>
        public void Seek(ulong position)
        {
            // Under the lock because the media pipeline seeks from the UI thread while a
            // read may be running elsewhere, and a torn 64-bit write on ARM32 would turn
            // into a read of the wrong byte range.
            lock (_gate) { _position = position; _seekGeneration++; }
        }

        /// <summary>A second, independently positioned view of the same bytes.</summary>
        public IInputStream GetInputStreamAt(ulong position)
        {
            return new RangeRandomAccessStream(_source) { Position = position };
        }

        public IOutputStream GetOutputStreamAt(ulong position)
        {
            throw new NotSupportedException("the video source is read-only");
        }

        public IRandomAccessStream CloneStream()
        {
            return new RangeRandomAccessStream(_source);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Cancel rather than dispose: the byte source belongs to PlaybackService,
            // which keeps serving it while a new video is being prepared.
            //
            // The token source itself is deliberately not disposed. A read that is
            // already in flight calls _lifetime.Token when it links its token, and
            // CancellationTokenSource.Token throws once the source has been disposed --
            // which would turn "MediaElement changed video" into a crash. An undisposed
            // CTS with no timer is a tiny amount of garbage, not a leak.
            try
            {
                _lifetime.Cancel();
            }
            catch (Exception)
            {
            }
        }
    }
}
