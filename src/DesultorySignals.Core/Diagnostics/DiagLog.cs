using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace DesultorySignals.Core.Diagnostics
{
    public enum DiagLevel
    {
        Trace = 0,
        Info = 1,
        Warn = 2,
        Error = 3
    }

    /// <summary>Named channels, so a log dump can be filtered by subsystem.</summary>
    public static class DiagCategory
    {
        public const string Lifecycle = "lifecycle";
        public const string Python = "python";
        public const string Transport = "transport";
        public const string Protocol = "protocol";
        public const string Parser = "parser";
        public const string Feed = "feed";
        public const string Comments = "comments";
        public const string Session = "session";
        public const string Login = "login";
        public const string Playback = "playback";
        public const string Range = "range";
        public const string Cache = "cache";
        public const string History = "history";
        public const string Storage = "storage";
        public const string Publish = "publish";
    }

    public sealed class DiagEntry
    {
        public long TimestampMs { get; set; }
        public DiagLevel Level { get; set; }
        public string Category { get; set; }
        public string Message { get; set; }
        public JObject Data { get; set; }

        public override string ToString()
        {
            string text = string.Format("{0} [{1}] {2}: {3}",
                Level.ToString().ToUpperInvariant(), Category, TimestampMs, Message);
            if (Data != null && Data.Count > 0) text += " " + Data.ToString(Newtonsoft.Json.Formatting.None);
            return text;
        }
    }

    /// <summary>
    /// In-memory diagnostics with a bounded ring buffer.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. The target device has about a gigabyte of RAM and a
    /// session can run for hours; an unbounded log would be the memory leak the
    /// completion criteria explicitly forbid. Once the buffer is full the oldest
    /// entry is dropped, and the drop count is tracked so a truncated history is
    /// visible rather than silently missing.
    ///
    /// Every message goes through <see cref="Redactor"/> before it is stored, so a
    /// caller cannot leak a credential by forgetting to think about it.
    /// </remarks>
    public sealed class DiagLog
    {
        private readonly object _gate = new object();
        private readonly DiagEntry[] _entries;
        private int _next;
        private int _count;
        private long _dropped;

        public DiagLog(int capacity)
        {
            if (capacity < 64) capacity = 64;
            _entries = new DiagEntry[capacity];
        }

        /// <summary>Master switch. Off still counts entries so the UI can say how many
        /// were suppressed, which is different from "nothing happened".</summary>
        public bool Enabled { get; set; }

        public DiagLevel MinimumLevel { get; set; }

        public int Capacity
        {
            get { return _entries.Length; }
        }

        public long DroppedCount
        {
            get { lock (_gate) { return _dropped; } }
        }

        public event Action<DiagEntry> EntryWritten;

        public void Trace(string category, string message, JObject data = null)
        {
            Write(DiagLevel.Trace, category, message, data);
        }

        public void Info(string category, string message, JObject data = null)
        {
            Write(DiagLevel.Info, category, message, data);
        }

        public void Warn(string category, string message, JObject data = null)
        {
            Write(DiagLevel.Warn, category, message, data);
        }

        public void Error(string category, string message, JObject data = null)
        {
            Write(DiagLevel.Error, category, message, data);
        }

        public void Write(DiagLevel level, string category, string message, JObject data = null)
        {
            if (level < MinimumLevel) return;

            var entry = new DiagEntry
            {
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Level = level,
                Category = category,
                // Scrubbed here, once, rather than trusting every call site.
                Message = Redactor.Text(message),
                Data = data == null ? null : (JObject)ScrubToken(data)
            };

            Action<DiagEntry> handler = null;
            lock (_gate)
            {
                if (!Enabled)
                {
                    // Still counted: "0 entries" and "everything was dropped" are
                    // different answers to "why is the log empty?".
                    _dropped++;
                    return;
                }

                _entries[_next] = entry;
                _next = (_next + 1) % _entries.Length;
                if (_count < _entries.Length) _count++;
                handler = EntryWritten;
            }

            if (handler != null)
            {
                try
                {
                    handler(entry);
                }
                catch (Exception)
                {
                    // A subscriber must never be able to break logging.
                }
            }
        }

        /// <summary>Oldest-to-newest snapshot.</summary>
        public IList<DiagEntry> Snapshot()
        {
            var result = new List<DiagEntry>();
            lock (_gate)
            {
                int start = _count == _entries.Length ? _next : 0;
                for (int index = 0; index < _count; index++)
                {
                    DiagEntry entry = _entries[(start + index) % _entries.Length];
                    if (entry != null) result.Add(entry);
                }
            }
            return result;
        }

        public IList<DiagEntry> Snapshot(string category, int maximum)
        {
            var result = new List<DiagEntry>();
            IList<DiagEntry> all = Snapshot();
            for (int index = all.Count - 1; index >= 0 && result.Count < maximum; index--)
            {
                if (string.Equals(all[index].Category, category, StringComparison.Ordinal))
                    result.Insert(0, all[index]);
            }
            return result;
        }

        public void Clear()
        {
            lock (_gate)
            {
                Array.Clear(_entries, 0, _entries.Length);
                _next = 0;
                _count = 0;
                _dropped = 0;
            }
        }

        /// <summary>Render the whole buffer as text for the export button.</summary>
        public string Dump()
        {
            var builder = new System.Text.StringBuilder();
            IList<DiagEntry> all = Snapshot();
            builder.AppendLine("# Desultory Signals diagnostics");
            builder.AppendLine("# entries=" + all.Count + " capacity=" + Capacity + " dropped=" + DroppedCount);
            for (int index = 0; index < all.Count; index++)
            {
                builder.AppendLine(all[index].ToString());
            }
            return builder.ToString();
        }

        /// <summary>
        /// Recursively scrub string values inside a structured payload. Data objects
        /// are how request/response summaries travel, and those summaries include
        /// URLs and header shapes.
        /// </summary>
        private static JToken ScrubToken(JToken token)
        {
            if (token == null) return null;
            switch (token.Type)
            {
                case JTokenType.String:
                    return new JValue(Redactor.Text((string)token));

                case JTokenType.Object:
                    var source = (JObject)token;
                    var copy = new JObject();
                    foreach (KeyValuePair<string, JToken> pair in source)
                    {
                        string name = pair.Key;
                        JToken value = pair.Value;
                        if (value != null && value.Type == JTokenType.String)
                        {
                            string text = (string)value;
                            if (string.Equals(name, "cookie", StringComparison.OrdinalIgnoreCase))
                                copy[name] = new JValue(Redactor.CookieHeader(text));
                            else if (IsSecretish(name))
                                copy[name] = new JValue("<redacted:" + text.Length + ">");
                            else
                                copy[name] = new JValue(Redactor.Text(text));
                        }
                        else
                        {
                            copy[name] = ScrubToken(value);
                        }
                    }
                    return copy;

                case JTokenType.Array:
                    var array = new JArray();
                    foreach (JToken item in (JArray)token)
                    {
                        array.Add(ScrubToken(item));
                    }
                    return array;

                default:
                    return token.DeepClone();
            }
        }

        private static bool IsSecretish(string name)
        {
            return name.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("cookie", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("webid", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
