using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Douyin;
using DesultorySignals.Core.Models;
using Newtonsoft.Json.Linq;

namespace DesultorySignals.Core.Services
{
    /// <summary>What one feed operation produced.</summary>
    public sealed class FeedUpdate
    {
        public IList<Aweme> Added { get; set; }
        public int DuplicatesRemoved { get; set; }
        public bool HasMore { get; set; }
        public string Cursor { get; set; }
        public int SkippedByParser { get; set; }
        public int TotalItems { get; set; }

        public FeedUpdate()
        {
            Added = new List<Aweme>();
        }
    }

    /// <summary>
    /// The feed: initial load, refresh, pagination, duplicate removal.
    /// </summary>
    /// <remarks>
    /// The behaviour worth stating, because each item is a real failure the brief
    /// asks to avoid:
    ///
    /// * <b>Duplicates are removed at the source.</b> The recommend feed genuinely
    ///   repeats items across pages and across a refresh. Deduplication happens here,
    ///   before the list reaches the UI, so no page has to defend itself.
    /// * <b>Concurrent loads are serialized, not raced.</b> A user scrolling fast can
    ///   trigger two "load more" calls; a semaphore makes the second one wait and
    ///   then continue from the cursor the first one produced. Without this the two
    ///   requests share a cursor and the second page is lost.
    /// * <b>A refresh resets the cursor but not the identity.</b> Refreshing is a new
    ///   feed session; the guest cookies and the signature identity stay put, because
    ///   rotating those on refresh is exactly the fingerprint churn the brief forbids.
    /// * <b>Failure leaves the list intact.</b> A failed page load reports the error
    ///   and changes nothing, so the user keeps what they had.
    /// </remarks>
    public sealed class FeedService
    {
        private readonly DouyinApiClient _api;
        private readonly DiagLog _log;

        private readonly List<Aweme> _items = new List<Aweme>();
        private readonly HashSet<string> _seenIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private string _cursor;
        private int _refreshIndex;
        private bool _hasMore = true;

        /// <summary>Beyond this many remembered ids, the set is rebuilt from the tail
        /// of the list. Unbounded, it would be a slow leak over a long session.</summary>
        private const int SeenIdLimit = 4000;
        private const int SeenIdRebuildKeep = 1500;

        public FeedService(DouyinApiClient api, DiagLog log)
        {
            if (api == null) throw new ArgumentNullException("api");
            _api = api;
            _log = log;
        }

        public IList<Aweme> Items
        {
            get { lock (_items) { return new List<Aweme>(_items); } }
        }

        public int Count
        {
            get { lock (_items) { return _items.Count; } }
        }

        public bool HasMore
        {
            get { lock (_items) { return _hasMore; } }
        }

        public string Cursor
        {
            get { lock (_items) { return _cursor; } }
        }

        public event Action<FeedUpdate> Updated;

        /// <summary>First load. Equivalent to a refresh, but reports itself as such in
        /// the log so a "why did the feed reload" question has an answer.</summary>
        public Task<FeedUpdate> LoadInitialAsync(int count, CancellationToken cancellationToken)
        {
            _log.Info(DiagCategory.Feed, "initial feed load");
            return LoadPageAsync(count, true, cancellationToken);
        }

        /// <summary>Pull a fresh feed. Keeps the current items until the new page
        /// arrives, so a failed refresh does not blank the screen.</summary>
        public Task<FeedUpdate> RefreshAsync(int count, CancellationToken cancellationToken)
        {
            _refreshIndex++;
            _log.Info(DiagCategory.Feed, "feed refresh #" + _refreshIndex);
            return LoadPageAsync(count, true, cancellationToken);
        }

        public Task<FeedUpdate> LoadMoreAsync(int count, CancellationToken cancellationToken)
        {
            return LoadPageAsync(count, false, cancellationToken);
        }

        /// <summary>Forget everything and start over on the next load.</summary>
        public void Reset()
        {
            lock (_items)
            {
                _items.Clear();
                _seenIds.Clear();
                _cursor = null;
                _refreshIndex = 0;
                _hasMore = true;
            }
            _log.Info(DiagCategory.Feed, "feed state reset");
        }

        private async Task<FeedUpdate> LoadPageAsync(int count, bool replace, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string cursor = replace ? null : Cursor;

                FeedPage page = await _api.GetFeedAsync(cursor, count, _refreshIndex, cancellationToken)
                    .ConfigureAwait(false);

                var update = new FeedUpdate
                {
                    HasMore = page.HasMore,
                    Cursor = page.Cursor,
                    SkippedByParser = page.Skipped
                };

                lock (_items)
                {
                    if (replace)
                    {
                        _items.Clear();
                        _seenIds.Clear();
                    }

                    for (int index = 0; index < page.Items.Count; index++)
                    {
                        Aweme aweme = page.Items[index];
                        if (aweme == null || string.IsNullOrEmpty(aweme.Id)) continue;

                        if (!_seenIds.Add(aweme.Id))
                        {
                            update.DuplicatesRemoved++;
                            continue;
                        }

                        _items.Add(aweme);
                        update.Added.Add(aweme);
                    }

                    _cursor = page.Cursor;
                    _hasMore = page.HasMore;
                    update.TotalItems = _items.Count;

                    TrimSeenIdsLocked();
                }

                _log.Info(DiagCategory.Feed, string.Format(
                    "{0}: +{1} items, {2} duplicates dropped, total {3}, more={4}",
                    replace ? "refresh" : "load-more",
                    update.Added.Count,
                    update.DuplicatesRemoved,
                    update.TotalItems,
                    update.HasMore));

                if (update.SkippedByParser > 0)
                {
                    _log.Warn(DiagCategory.Feed, string.Format(
                        "{0} feed entries could not be normalized and were skipped", update.SkippedByParser));
                }

                Action<FeedUpdate> handler = Updated;
                if (handler != null) handler(update);
                return update;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Bound the dedupe set without losing the ids the user can still see.</summary>
        private void TrimSeenIdsLocked()
        {
            if (_seenIds.Count <= SeenIdLimit) return;

            _seenIds.Clear();
            int start = Math.Max(0, _items.Count - SeenIdRebuildKeep);
            for (int index = start; index < _items.Count; index++)
            {
                _seenIds.Add(_items[index].Id);
            }
            _log.Trace(DiagCategory.Feed, "dedupe set rebuilt to " + _seenIds.Count + " ids");
        }

        /// <summary>Diagnostics snapshot.</summary>
        public JObject Describe()
        {
            lock (_items)
            {
                return new JObject
                {
                    { "items", _items.Count },
                    { "seenIds", _seenIds.Count },
                    { "cursor", _cursor },
                    { "hasMore", _hasMore },
                    { "refreshIndex", _refreshIndex }
                };
            }
        }
    }
}
