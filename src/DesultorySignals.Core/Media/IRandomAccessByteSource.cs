using System;
using System.Threading;
using System.Threading.Tasks;

namespace DesultorySignals.Core.Media
{
    /// <summary>
    /// Random-access bytes, independent of WinRT.
    /// </summary>
    /// <remarks>
    /// This exists so that the entire range-read machinery -- chunking, caching,
    /// retry, URL-expiry recovery -- can live in the testable core, while the only
    /// platform-specific part is a thin adapter that turns this into an
    /// <c>IRandomAccessStream</c> for <c>MediaElement</c>.
    ///
    /// The contract is deliberately minimal and strictly random-access: the caller
    /// may seek anywhere, read any length, and repeat a read. It must not assume
    /// sequential access, because <c>MediaElement</c> does not: it reads the moov
    /// atom at the end of the file before it plays the first frame.
    /// </remarks>
    public interface IRandomAccessByteSource : IDisposable
    {
        /// <summary>Total length, or -1 when still unknown.</summary>
        long Size { get; }

        /// <summary>MIME type to hand to the media pipeline, e.g. "video/mp4".</summary>
        string ContentType { get; }

        /// <summary>
        /// Read up to <paramref name="count"/> bytes at <paramref name="position"/>.
        /// Returns the number of bytes actually read; 0 only at end of stream.
        /// </summary>
        Task<int> ReadAsync(long position, byte[] buffer, int offset, int count, CancellationToken cancellationToken);

        /// <summary>Drop cached bytes. Called when the underlying URL is replaced.</summary>
        void InvalidateCache(string reason);
    }

    /// <summary>Everything a range source needs, so its constructor stays honest.</summary>
    public sealed class RangeSourceOptions
    {
        public string Url { get; set; }

        /// <summary>Builds the request for a URL. Called per fetch so cookies and
        /// headers are current, and so a refreshed URL gets a fresh signature.</summary>
        public Func<string, Networking.HttpRequestSpec> SpecFactory { get; set; }

        /// <summary>Known total size, or -1. Taken from the source's declared
        /// <c>data_size</c> when available; the first response confirms or corrects it.</summary>
        public long TotalSize { get; set; }

        public int ChunkSize { get; set; }

        public int MaxChunks { get; set; }

        public Networking.RetryPolicy Retry { get; set; }

        /// <summary>
        /// Obtains a replacement URL when the current one is refused or has expired.
        /// Null when the caller cannot refresh; the source then reports the failure
        /// rather than guessing.
        /// </summary>
        public Func<CancellationToken, Task<string>> RefreshUrlAsync { get; set; }

        public string MimeType { get; set; }

        public string Tag { get; set; }

        public RangeSourceOptions()
        {
            TotalSize = -1;
            ChunkSize = RangeMath.DefaultChunkSize;
            MaxChunks = 48; // 24 MiB at the default 512 KiB chunk
            Retry = Networking.RetryPolicy.ForRangeReads();
            MimeType = "video/mp4";
            Tag = "range";
        }
    }
}
