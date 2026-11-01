using System;

namespace DesultorySignals.Core.Media
{
    /// <summary>
    /// Chunk arithmetic for range reads. Pure functions, so the off-by-one bugs
    /// that make a video player stutter live in one testable place.
    /// </summary>
    /// <remarks>
    /// All ranges are inclusive on both ends, matching the HTTP <c>Range</c> header
    /// and the <c>Content-Range</c> response. Mixing inclusive and exclusive
    /// conventions is the classic source of a one-byte seam between chunks, so this
    /// type never uses an exclusive end.
    /// </remarks>
    public static class RangeMath
    {
        public const int DefaultChunkSize = 512 * 1024;

        /// <summary>Index of the chunk containing <paramref name="position"/>.</summary>
        public static int ChunkIndex(long position, int chunkSize)
        {
            if (position < 0) throw new ArgumentOutOfRangeException("position");
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException("chunkSize");
            return (int)(position / chunkSize);
        }

        /// <summary>First byte of a chunk.</summary>
        public static long ChunkStart(int chunkIndex, int chunkSize)
        {
            if (chunkIndex < 0) throw new ArgumentOutOfRangeException("chunkIndex");
            return (long)chunkIndex * chunkSize;
        }

        /// <summary>
        /// Last byte of a chunk, clamped to the resource. Returns -1 when the chunk
        /// starts at or past the end, which the caller must treat as "no data".
        /// </summary>
        public static long ChunkEnd(int chunkIndex, int chunkSize, long totalSize)
        {
            long start = ChunkStart(chunkIndex, chunkSize);
            if (totalSize >= 0 && start >= totalSize) return -1;

            long end = start + chunkSize - 1;
            if (totalSize >= 0 && end >= totalSize) end = totalSize - 1;
            return end;
        }

        public static int ChunkCount(long totalSize, int chunkSize)
        {
            if (totalSize <= 0) return 0;
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException("chunkSize");
            return (int)((totalSize + chunkSize - 1) / chunkSize);
        }

        /// <summary>The HTTP header value for an inclusive range.</summary>
        public static string RangeHeader(long start, long end)
        {
            return "bytes=" + start + "-" + end;
        }

        /// <summary>
        /// Parse a <c>Content-Range</c> header, e.g. <c>bytes 0-511/1048576</c>.
        /// Returns false when the header is absent or the shape is not one we
        /// understand -- notably <c>bytes */1234</c>, which is a 416 answer.
        /// </summary>
        public static bool TryParseContentRange(string header, out long start, out long end, out long total)
        {
            start = 0;
            end = 0;
            total = -1;
            if (string.IsNullOrEmpty(header)) return false;

            string text = header.Trim();
            const string prefix = "bytes ";
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            text = text.Substring(prefix.Length);

            int slash = text.IndexOf('/');
            if (slash < 0) return false;

            string rangePart = text.Substring(0, slash).Trim();
            string totalPart = text.Substring(slash + 1).Trim();

            if (rangePart != "*")
            {
                int dash = rangePart.IndexOf('-');
                if (dash <= 0) return false;
                if (!long.TryParse(rangePart.Substring(0, dash), out start)) return false;
                if (!long.TryParse(rangePart.Substring(dash + 1), out end)) return false;
            }

            if (totalPart != "*")
            {
                if (!long.TryParse(totalPart, out total)) return false;
            }
            return true;
        }

        /// <summary>Total size from a <c>Content-Range</c>, or null.</summary>
        public static long? TotalFromContentRange(string header)
        {
            long start, end, total;
            if (!TryParseContentRange(header, out start, out end, out total)) return null;
            return total < 0 ? (long?)null : total;
        }

        /// <summary>
        /// Copy the intersection of a source window and a destination window.
        /// </summary>
        /// <param name="source">The bytes fetched.</param>
        /// <param name="sourceStart">Absolute offset of <c>source[0]</c>.</param>
        /// <param name="sourceLength">Number of valid bytes in <paramref name="source"/>.</param>
        /// <param name="destination">The buffer to write into.</param>
        /// <param name="destinationOffset">
        /// Index in <paramref name="destination"/> that corresponds to
        /// <paramref name="targetStart"/>. Without this, a caller reading into the
        /// middle of a buffer would write every chunk at offset 0 and silently return
        /// the wrong bytes -- which is exactly the bug this parameter exists to make
        /// impossible to write by accident.
        /// </param>
        /// <param name="targetStart">Absolute offset of the destination window.</param>
        /// <param name="targetLength">Length of the destination window.</param>
        /// <returns>The number of bytes copied.</returns>
        public static int CopyOverlap(
            byte[] source, long sourceStart, int sourceLength,
            byte[] destination, int destinationOffset, long targetStart, int targetLength)
        {
            if (source == null || destination == null) return 0;
            if (destinationOffset < 0 || destinationOffset > destination.Length) return 0;

            long sourceEnd = sourceStart + sourceLength;      // exclusive
            long targetEnd = targetStart + targetLength;      // exclusive

            long overlapStart = Math.Max(sourceStart, targetStart);
            long overlapEnd = Math.Min(sourceEnd, targetEnd);
            if (overlapEnd <= overlapStart) return 0;

            int count = (int)(overlapEnd - overlapStart);
            int sourceOffset = (int)(overlapStart - sourceStart);
            int writeOffset = destinationOffset + (int)(overlapStart - targetStart);

            if (writeOffset < 0 || writeOffset + count > destination.Length) return 0;

            Buffer.BlockCopy(source, sourceOffset, destination, writeOffset, count);
            return count;
        }
    }
}
