using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Models;
using DesultorySignals.Core.Storage;
using Newtonsoft.Json.Linq;

namespace DesultorySignals.Core.Services
{
    public sealed class HistoryEntry
    {
        public string AwemeId { get; set; }
        public string Desc { get; set; }
        public string AuthorName { get; set; }
        public string CoverUrl { get; set; }
        public long WatchedAtMs { get; set; }
        public int PositionMs { get; set; }
        public int DurationMs { get; set; }

        public static HistoryEntry FromJson(JObject source)
        {
            if (source == null) return null;
            string id = JsonValue.GetString(source, "awemeId");
            if (string.IsNullOrEmpty(id)) return null;
            return new HistoryEntry
            {
                AwemeId = id,
                Desc = JsonValue.GetString(source, "desc", string.Empty),
                AuthorName = JsonValue.GetString(source, "authorName"),
                CoverUrl = JsonValue.GetString(source, "coverUrl"),
                WatchedAtMs = JsonValue.GetLong(source, "watchedAtMs") ?? 0,
                PositionMs = JsonValue.GetInt(source, "positionMs", 0),
                DurationMs = JsonValue.GetInt(source, "durationMs", 0)
            };
        }

        public JObject ToJson()
        {
            return new JObject
            {
                { "awemeId", AwemeId },
                { "desc", Desc },
                { "authorName", AuthorName },
                { "coverUrl", CoverUrl },
                { "watchedAtMs", WatchedAtMs },
                { "positionMs", PositionMs },
                { "durationMs", DurationMs }
            };
        }
    }

    /// <summary>
    /// Local watch history.
    /// </summary>
    /// <remarks>
    /// A single small index file, unlike the metadata cache. The distinction is
    /// deliberate: history is one ordered list whose order *is* the data, so
    /// splitting it per entry would mean reading all of them to render the list. It
    /// is bounded, and it holds no credential -- only ids, titles and a resume
    /// position.
    ///
    /// Writes are debounced. Playback progress updates arrive continuously, and
    /// writing the file on each one would hammer the phone's flash for no benefit.
    /// </remarks>
    public sealed class HistoryService
    {
        public const string HistoryPath = "history/history.json";

        private readonly IFileStore _store;
        private readonly DiagLog _log;
        private readonly object _gate = new object();

        private List<HistoryEntry> _entries;
        private bool _dirty;
        private long _lastWriteMs;

        public int MaxEntries { get; set; }
        public int MinWriteIntervalMs { get; set; }

        public HistoryService(IFileStore store, DiagLog log)
        {
            if (store == null) throw new ArgumentNullException("store");
            _store = store;
            _log = log;
            MaxEntries = 200;
            MinWriteIntervalMs = 5000;
        }

        public async Task<IList<HistoryEntry>> ListAsync(CancellationToken cancellationToken)
        {
            List<HistoryEntry> entries = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                return new List<HistoryEntry>(entries);
            }
        }

        public async Task<HistoryEntry> GetAsync(string awemeId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(awemeId)) return null;
            List<HistoryEntry> entries = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                for (int index = 0; index < entries.Count; index++)
                {
                    if (entries[index].AwemeId == awemeId) return entries[index];
                }
            }
            return null;
        }

        /// <summary>
        /// Record or update a view. The entry moves to the front, which is what makes
        /// the list "most recent first" without a sort.
        /// </summary>
        public async Task RecordAsync(
            Aweme aweme, int positionMs, bool forceFlush, CancellationToken cancellationToken)
        {
            if (aweme == null || string.IsNullOrEmpty(aweme.Id)) return;

            List<HistoryEntry> entries = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                int existing = -1;
                for (int index = 0; index < entries.Count; index++)
                {
                    if (entries[index].AwemeId == aweme.Id)
                    {
                        existing = index;
                        break;
                    }
                }

                HistoryEntry entry = existing >= 0 ? entries[existing] : new HistoryEntry { AwemeId = aweme.Id };
                if (existing >= 0) entries.RemoveAt(existing);

                entry.Desc = aweme.Desc;
                entry.AuthorName = aweme.Author == null ? null : aweme.Author.DisplayName;
                entry.CoverUrl = aweme.BestCoverUrl;
                entry.DurationMs = aweme.DurationMs;
                entry.WatchedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (positionMs >= 0) entry.PositionMs = positionMs;

                entries.Insert(0, entry);

                while (entries.Count > MaxEntries)
                {
                    entries.RemoveAt(entries.Count - 1);
                }

                _dirty = true;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool due = forceFlush || (now - _lastWriteMs) >= MinWriteIntervalMs;
            if (due)
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<bool> RemoveAsync(string awemeId, CancellationToken cancellationToken)
        {
            List<HistoryEntry> entries = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            bool removed;
            lock (_gate)
            {
                int before = entries.Count;
                for (int index = entries.Count - 1; index >= 0; index--)
                {
                    if (entries[index].AwemeId == awemeId) entries.RemoveAt(index);
                }
                removed = entries.Count != before;
                _dirty = _dirty || removed;
            }
            if (removed) await FlushAsync(cancellationToken).ConfigureAwait(false);
            return removed;
        }

        public async Task ClearAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_entries != null) _entries.Clear();
                _dirty = true;
            }
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            _log.Info(DiagCategory.History, "history cleared");
        }

        /// <summary>Write the index if anything changed.</summary>
        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            string payload = null;
            int count = 0;

            lock (_gate)
            {
                if (!_dirty || _entries == null) return;

                var items = new JArray();
                for (int index = 0; index < _entries.Count; index++)
                {
                    items.Add(_entries[index].ToJson());
                }

                var root = new JObject
                {
                    { "schema", 1 },
                    { "savedAtMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    { "entries", items }
                };

                payload = root.ToString(Newtonsoft.Json.Formatting.None);
                count = _entries.Count;
                _dirty = false;
                _lastWriteMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            try
            {
                await _store.WriteTextAsync(HistoryPath, payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    _dirty = true; // retry on the next opportunity
                }
                _log.Warn(DiagCategory.History, "could not write history: " + exception.Message);
                return;
            }

            _log.Trace(DiagCategory.History, "history saved (" + count + " entries)");
        }

        private async Task<List<HistoryEntry>> EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_entries != null) return _entries;
            }

            var loaded = new List<HistoryEntry>();
            try
            {
                string text = await _store.ReadTextAsync(HistoryPath, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(text))
                {
                    JObject root = JObject.Parse(text);
                    JArray items = root["entries"] as JArray;
                    if (items != null)
                    {
                        for (int index = 0; index < items.Count; index++)
                        {
                            HistoryEntry entry = HistoryEntry.FromJson(items[index] as JObject);
                            if (entry != null) loaded.Add(entry);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                // Unreadable history is discarded rather than fatal: it is a
                // convenience list, and refusing to start over it would be absurd.
                _log.Warn(DiagCategory.History, "history was unreadable and was reset: " + exception.Message);
                loaded = new List<HistoryEntry>();
            }

            lock (_gate)
            {
                if (_entries == null)
                {
                    _entries = loaded;
                }
                return _entries;
            }
        }
    }
}
