using System;
using System.Text;

namespace DesultorySignals.Core
{
    /// <summary>Small shared helpers. No dependencies, so it is safe to call from
    /// anywhere including the diagnostics path.</summary>
    public static class Util
    {
        /// <summary>
        /// Stable 16-hex-character hash for cache keys.
        /// </summary>
        /// <remarks>
        /// FNV-1a rather than a cryptographic digest: this only names cache files, and
        /// keeping it dependency-free matters more than collision resistance for
        /// cover-image URLs. The value is stable across processes and platforms, which
        /// is what a cache key needs.
        /// </remarks>
        public static string ShortHash(string text)
        {
            if (text == null) text = string.Empty;

            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            ulong hash = offset;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            for (int index = 0; index < bytes.Length; index++)
            {
                hash ^= bytes[index];
                hash *= prime;
            }

            return hash.ToString("x16");
        }

        /// <summary>Chinese-style count abbreviation: 1234 -> "1234", 12345 -> "1.2万".</summary>
        public static string FormatCount(long value)
        {
            if (value < 0) return "0";
            if (value < 10000) return value.ToString();

            double scaled = value / 10000.0;
            if (scaled < 10000) return scaled.ToString("0.#") + "万";
            return (scaled / 10000.0).ToString("0.#") + "亿";
        }

        /// <summary>Milliseconds to "m:ss" or "h:mm:ss".</summary>
        public static string FormatDuration(int milliseconds)
        {
            if (milliseconds <= 0) return "0:00";
            TimeSpan span = TimeSpan.FromMilliseconds(milliseconds);
            if (span.TotalHours >= 1)
            {
                return string.Format("{0}:{1:00}:{2:00}", (int)span.TotalHours, span.Minutes, span.Seconds);
            }
            return string.Format("{0}:{1:00}", span.Minutes, span.Seconds);
        }

        /// <summary>Trim to a maximum length with an ellipsis, for list rows.</summary>
        public static string Truncate(string text, int maximum)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (text.Length <= maximum) return text;
            return text.Substring(0, maximum) + "…";
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KiB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.#") + " MiB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##") + " GiB";
        }
    }
}
