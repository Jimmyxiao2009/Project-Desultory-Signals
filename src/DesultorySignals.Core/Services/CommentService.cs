using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Douyin;
using DesultorySignals.Core.Models;

namespace DesultorySignals.Core.Services
{
    /// <summary>
    /// One paginated comment thread, with duplicate removal.
    /// </summary>
    /// <remarks>
    /// Comments repeat across pages far more often than feed items do: a new comment
    /// posted while the user is reading shifts every subsequent page by one, so page
    /// two contains the last item of page one. Deduplicating by id is what keeps the
    /// list from visibly duplicating as the user scrolls.
    /// </remarks>
    public sealed class CommentThread
    {
        private readonly List<Comment> _items = new List<Comment>();
        private readonly HashSet<string> _seenIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public string AwemeId { get; private set; }
        public string Cursor { get; private set; }
        public bool HasMore { get; private set; }
        public int Total { get; private set; }

        public CommentThread(string awemeId)
        {
            AwemeId = awemeId;
            HasMore = true;
        }

        public IList<Comment> Items
        {
            get { lock (_gate) { return new List<Comment>(_items); } }
        }

        public int Count
        {
            get { lock (_gate) { return _items.Count; } }
        }

        /// <summary>Merge a page, returning only the comments that were actually new.</summary>
        public IList<Comment> Merge(CommentPage page, bool replace)
        {
            var added = new List<Comment>();
            if (page == null) return added;

            lock (_gate)
            {
                if (replace)
                {
                    _items.Clear();
                    _seenIds.Clear();
                }

                for (int index = 0; index < page.Items.Count; index++)
                {
                    Comment comment = page.Items[index];
                    if (comment == null || string.IsNullOrEmpty(comment.Id)) continue;
                    if (!_seenIds.Add(comment.Id)) continue;
                    _items.Add(comment);
                    added.Add(comment);
                }

                Cursor = page.Cursor;
                HasMore = page.HasMore;
                if (page.Total > 0) Total = page.Total;
            }

            return added;
        }

        /// <summary>
        /// Put a just-published comment at the top of the list.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Merge"/> because a published comment belongs at the
        /// front while a fetched page belongs at the back. Folding both into Merge would
        /// make the position depend on an argument nobody reads.
        /// </remarks>
        public bool Prepend(Comment comment)
        {
            if (comment == null || string.IsNullOrEmpty(comment.Id)) return false;

            lock (_gate)
            {
                if (!_seenIds.Add(comment.Id)) return false;
                _items.Insert(0, comment);
                return true;
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _items.Clear();
                _seenIds.Clear();
                Cursor = null;
                HasMore = true;
                Total = 0;
            }
        }
    }

    /// <summary>
    /// Comment reads and writes. Deliberately free of any UI concern: it returns
    /// models and classified failures, and nothing else.
    /// </summary>
    public sealed class CommentService
    {
        private readonly DouyinApiClient _api;
        private readonly DiagLog _log;
        private readonly Dictionary<string, CommentThread> _threads =
            new Dictionary<string, CommentThread>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public CommentService(DouyinApiClient api, DiagLog log)
        {
            if (api == null) throw new ArgumentNullException("api");
            _api = api;
            _log = log;
        }

        /// <summary>The thread for one aweme, created on first use.</summary>
        public CommentThread Thread(string awemeId)
        {
            lock (_gate)
            {
                CommentThread thread;
                if (!_threads.TryGetValue(awemeId, out thread))
                {
                    thread = new CommentThread(awemeId);
                    _threads[awemeId] = thread;
                }
                return thread;
            }
        }

        /// <summary>Load the first page of top-level comments.</summary>
        public async Task<IList<Comment>> LoadFirstPageAsync(
            string awemeId, int count, CancellationToken cancellationToken)
        {
            CommentThread thread = Thread(awemeId);
            thread.Reset();
            CommentPage page = await _api.GetCommentsAsync(awemeId, null, count, cancellationToken)
                .ConfigureAwait(false);
            IList<Comment> added = thread.Merge(page, true);
            _log.Info(DiagCategory.Comments, string.Format(
                "{0}: first page, {1} comments (total {2}, more={3})",
                awemeId, added.Count, page.Total, page.HasMore));
            return added;
        }

        public async Task<IList<Comment>> LoadNextPageAsync(
            string awemeId, int count, CancellationToken cancellationToken)
        {
            CommentThread thread = Thread(awemeId);
            if (!thread.HasMore) return new List<Comment>();

            CommentPage page = await _api.GetCommentsAsync(awemeId, thread.Cursor, count, cancellationToken)
                .ConfigureAwait(false);
            IList<Comment> added = thread.Merge(page, false);
            _log.Info(DiagCategory.Comments, string.Format(
                "{0}: next page, {1} new comments (cursor {2}, more={3})",
                awemeId, added.Count, thread.Cursor, page.HasMore));
            return added;
        }

        /// <summary>Load one page of replies to a top-level comment.</summary>
        public async Task<CommentPage> LoadRepliesAsync(
            string awemeId, string commentId, string cursor, int count, CancellationToken cancellationToken)
        {
            CommentPage page = await _api.GetCommentRepliesAsync(
                awemeId, commentId, cursor, count, cancellationToken).ConfigureAwait(false);
            _log.Info(DiagCategory.Comments, string.Format(
                "{0}/{1}: {2} replies (cursor {3}, more={4})",
                awemeId, commentId, page.Items.Count, page.Cursor, page.HasMore));
            return page;
        }

        /// <summary>
        /// Post a top-level comment, then splice it into the thread so the user sees
        /// it without a refresh.
        /// </summary>
        public async Task<Comment> PublishAsync(
            string awemeId, string text, CancellationToken cancellationToken)
        {
            Comment comment = await _api.PublishCommentAsync(awemeId, text, cancellationToken)
                .ConfigureAwait(false);
            InsertPublished(awemeId, comment, false);
            return comment;
        }

        public async Task<Comment> ReplyAsync(
            string awemeId, string commentId, string text, string replyToReplyId,
            CancellationToken cancellationToken)
        {
            Comment comment = await _api.ReplyCommentAsync(
                awemeId, commentId, text, replyToReplyId, cancellationToken).ConfigureAwait(false);
            InsertPublished(awemeId, comment, true);
            return comment;
        }

        /// <summary>
        /// Splice a just-published comment in at the top.
        /// </summary>
        /// <remarks>
        /// A reply is intentionally not spliced into the top-level thread: it belongs
        /// under its parent, and showing it at the top would be a lie about the
        /// conversation. The caller refreshes the parent's replies instead.
        /// </remarks>
        private void InsertPublished(string awemeId, Comment comment, bool isReply)
        {
            if (isReply || comment == null || string.IsNullOrEmpty(comment.Id)) return;
            Thread(awemeId).Prepend(comment);
        }

        /// <summary>Forget cached threads. Called on logout and on low memory.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _threads.Clear();
            }
        }
    }
}
