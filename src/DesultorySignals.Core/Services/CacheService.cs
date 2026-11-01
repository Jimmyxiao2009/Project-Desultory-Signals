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
    /// <summary>
    /// Metadata and image caching.
    /// </summary>
    /// <remarks>
    /// Four separate concerns, kept separate on purpose (the brief forbids one giant
    /// JSON blob, and it is right to):
    ///
    /// * <b>Aweme metadata</b> -- one file per aweme, so a refresh of one entry never
    ///   rewrites the others and a corrupt entry costs one item, not the cache.
    /// * <b>Images</b> -- one file per URL hash, with an LRU index and both an entry
    ///   and a byte budget. Covers are the largest thing the client stores.
    /// * <b>Freshness</b> -- metadata carries its own timestamp and expires. Statistics
    ///   move, so a cached aweme is a hint, not a fact, and callers can bypass it.
    /// * <b>Eviction</b> -- bounded, because a phone's storage is not.
    ///
    /// The image index is written lazily rather than on every insert: the phone's
    /// flash is slow, and an index write per cover thumbnail would be visible.
    /// </remarks>
    public sealed class CacheService
    {
        public const string AwemeFolder = "metadata/aweme";
        public const string ImageFolder = "image";
        public const string ImageIndexPath = "image/index.json";

        private static readonly TimeSpan MetadataLifetime = TimeSpan.FromHours(6);

        private readonly IFileStore _store;
        private readonly DiagLog _log;
        private readonly object _gate = new object();

        private ImageIndex _imageIndex;
        private int _putsSinceIndexFlush;

        private const int IndexFlushInterval = 25;

        public int MetadataMaxEntries { get; set; }
        public int ImageMaxEntries { get; set; }
        public long ImageMaxBytes { get; set; }

        public CacheService(IFileStore store, DiagLog log)
        {
            if (store == null) throw new ArgumentNullException("store");
            _store = store;
            _log = log;
            MetadataMaxEntries = 400;
            ImageMaxEntries = 500;
            ImageMaxBytes = 48L * 1024 * 1024;
        }

        // ------------------------------------------------------------------
        // aweme metadata
        // ------------------------------------------------------------------

        private static string AwemePath(string awemeId)
        {
            return AwemeFolder + "/" + awemeId + ".json";
        }

        /// <summary>Cached aweme, or null when absent or stale.</summary>
        public async Task<Aweme> GetAwemeAsync(string awemeId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(awemeId)) return null;

            try
            {
                string text = await _store.ReadTextAsync(AwemePath(awemeId), cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrEmpty(text)) return null;

                JObject envelope = JObject.Parse(text);
                long cachedAtMs = JsonValue.GetLong(envelope, "cachedAtMs") ?? 0;
                long ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cachedAtMs;
                if (ageMs > MetadataLifetime.TotalMilliseconds) return null;

                return Aweme.FromJson(envelope["aweme"] as JObject);
            }
            catch (Exception exception)
            {
                // A corrupt cache entry is a cache miss, never a failure.
                _log.Trace(DiagCategory.Cache, "metadata cache read failed for " + awemeId + ": " + exception.Message);
                return null;
            }
        }

        public async Task PutAwemeAsync(Aweme aweme, CancellationToken cancellationToken)
        {
            if (aweme == null || string.IsNullOrEmpty(aweme.Id)) return;

            var envelope = new JObject
            {
                { "cachedAtMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                { "schema", 1 },
                { "aweme", ToCacheJson(aweme) }
            };

            try
            {
                await _store.WriteTextAsync(AwemePath(aweme.Id), envelope.ToString(Newtonsoft.Json.Formatting.None),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log.Warn(DiagCategory.Cache, "metadata cache write failed for " + aweme.Id + ": " + exception.Message);
            }
        }

        /// <summary>
        /// Serialize the fields the cache needs.
        /// </summary>
        /// <remarks>
        /// The source list is trimmed to the top few candidates. A cache entry that
        /// carried every bitrate variant would be several times larger for no benefit:
        /// the URLs expire within minutes anyway, and a stale entry is refreshed
        /// before playback.
        /// </remarks>
        private static JObject ToCacheJson(Aweme aweme)
        {
            var video = new JObject
            {
                { "width", aweme.Video == null ? 0 : aweme.Video.Width },
                { "height", aweme.Video == null ? 0 : aweme.Video.Height },
                { "durationMs", aweme.DurationMs },
                { "coverUrl", aweme.Video == null ? null : aweme.Video.CoverUrl },
                { "format", aweme.Video == null ? null : aweme.Video.Format }
            };

            var sources = new JArray();
            if (aweme.Video != null && aweme.Video.Sources != null)
            {
                int limit = Math.Min(aweme.Video.Sources.Count, 4);
                for (int index = 0; index < limit; index++)
                {
                    VideoSource source = aweme.Video.Sources[index];
                    sources.Add(new JObject
                    {
                        { "url", source.Url },
                        { "originalUrl", source.OriginalUrl },
                        { "codec", source.Codec },
                        { "codecEvidence", source.CodecEvidence },
                        { "container", source.Container },
                        { "quality", source.Quality },
                        { "width", source.Width },
                        { "height", source.Height },
                        { "bitrate", source.Bitrate },
                        { "sizeBytes", source.SizeBytes },
                        { "gearName", source.GearName }
                    });
                }
            }
            video["sources"] = sources;

            return new JObject
            {
                { "id", aweme.Id },
                { "desc", aweme.Desc },
                { "createTime", aweme.CreateTime },
                { "durationMs", aweme.DurationMs },
                { "isImage", aweme.IsImage },
                { "shareUrl", aweme.ShareUrl },
                { "author", new JObject
                    {
                        { "id", aweme.Author == null ? null : aweme.Author.Id },
                        { "secUid", aweme.Author == null ? null : aweme.Author.SecUid },
                        { "nickname", aweme.Author == null ? null : aweme.Author.Nickname },
                        { "avatarUrl", aweme.Author == null ? null : aweme.Author.AvatarUrl },
                        { "uniqueId", aweme.Author == null ? null : aweme.Author.UniqueId }
                    }
                },
                { "video", video },
                { "statistics", new JObject
                    {
                        { "diggCount", aweme.Statistics == null ? 0 : aweme.Statistics.DiggCount },
                        { "commentCount", aweme.Statistics == null ? 0 : aweme.Statistics.CommentCount },
                        { "shareCount", aweme.Statistics == null ? 0 : aweme.Statistics.ShareCount },
                        { "collectCount", aweme.Statistics == null ? 0 : aweme.Statistics.CollectCount }
                    }
                }
            };
        }

        /// <summary>Drop the oldest metadata entries until the budget is met.</summary>
        public async Task EnforceMetadataBudgetAsync(CancellationToken cancellationToken)
        {
            IList<string> files = await _store.ListAsync(AwemeFolder, cancellationToken).ConfigureAwait(false);
            if (files.Count <= MetadataMaxEntries) return;

            var entries = new List<CacheEntry>();
            for (int index = 0; index < files.Count; index++)
            {
                string path = AwemeFolder + "/" + files[index];
                long size = await _store.SizeAsync(path, cancellationToken).ConfigureAwait(false);
                if (size < 0) continue;
                entries.Add(new CacheEntry { Path = path, Size = size });
            }
            entries.Sort(delegate(CacheEntry left, CacheEntry right) { return left.Size.CompareTo(right.Size); });

            int removeCount = files.Count - MetadataMaxEntries;
            for (int index = 0; index < removeCount && index < entries.Count; index++)
            {
                await _store.DeleteAsync(entries[index].Path, cancellationToken).ConfigureAwait(false);
            }

            _log.Info(DiagCategory.Cache, "metadata cache trimmed",
                new JObject { { "removed", removeCount }, { "remaining", files.Count - removeCount } });
        }

        // ------------------------------------------------------------------
        // images
        // ------------------------------------------------------------------

        public async Task<byte[]> GetImageAsync(string url, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string key = ImageKey(url);

            try
            {
                byte[] data = await _store.ReadBytesAsync(ImageFolder + "/" + key + ".bin", cancellationToken)
                    .ConfigureAwait(false);
                if (data == null) return null;

                await TouchImageAsync(key, data.Length, cancellationToken).ConfigureAwait(false);
                return data;
            }
            catch (Exception exception)
            {
                _log.Trace(DiagCategory.Cache, "image cache read failed: " + exception.Message);
                return null;
            }
        }

        public async Task PutImageAsync(string url, byte[] data, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(url) || data == null || data.Length == 0) return;
            if (data.Length > ImageMaxBytes) return; // one image must not blow the whole budget

            string key = ImageKey(url);
            try
            {
                await _store.WriteBytesAsync(ImageFolder + "/" + key + ".bin", data, cancellationToken)
                    .ConfigureAwait(false);
                await TouchImageAsync(key, data.Length, cancellationToken).ConfigureAwait(false);
                await EvictImagesIfNeededAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log.Warn(DiagCategory.Cache, "image cache write failed: " + exception.Message);
            }
        }

        private static string ImageKey(string url)
        {
            // The URL contains query parameters that rotate; the path identifies the
            // resource. Hashing the path keeps a cover cached across URL refreshes.
            string key = url;
            int question = key.IndexOf('?');
            if (question > 0) key = key.Substring(0, question);
            return Util.ShortHash(key);
        }

        private async Task TouchImageAsync(string key, long size, CancellationToken cancellationToken)
        {
            ImageIndex index = await LoadImageIndexAsync(cancellationToken).ConfigureAwait(false);
            index.Entries[key] = new ImageIndexEntry
            {
                Size = size,
                LastUsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            bool flush;
            lock (_gate)
            {
                _putsSinceIndexFlush++;
                flush = _putsSinceIndexFlush >= IndexFlushInterval;
                if (flush) _putsSinceIndexFlush = 0;
            }
            if (flush)
            {
                await SaveImageIndexAsync(index, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task EvictImagesIfNeededAsync(CancellationToken cancellationToken)
        {
            ImageIndex index = await LoadImageIndexAsync(cancellationToken).ConfigureAwait(false);

            long totalBytes = 0;
            foreach (KeyValuePair<string, ImageIndexEntry> pair in index.Entries)
            {
                totalBytes += pair.Value.Size;
            }

            if (index.Entries.Count <= ImageMaxEntries && totalBytes <= ImageMaxBytes) return;

            var ordered = new List<KeyValuePair<string, ImageIndexEntry>>(index.Entries);
            ordered.Sort(delegate(
                KeyValuePair<string, ImageIndexEntry> left,
                KeyValuePair<string, ImageIndexEntry> right)
            {
                return left.Value.LastUsedMs.CompareTo(right.Value.LastUsedMs);
            });

            int removed = 0;
            for (int position = 0; position < ordered.Count; position++)
            {
                if (index.Entries.Count <= ImageMaxEntries && totalBytes <= ImageMaxBytes) break;

                string key = ordered[position].Key;
                long size = ordered[position].Value.Size;
                try
                {
                    await _store.DeleteAsync(ImageFolder + "/" + key + ".bin", cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Keep going: one undeletable file must not stop the sweep.
                }
                index.Entries.Remove(key);
                totalBytes -= size;
                removed++;
            }

            await SaveImageIndexAsync(index, cancellationToken).ConfigureAwait(false);
            _log.Info(DiagCategory.Cache, "image cache trimmed",
                new JObject { { "removed", removed }, { "bytes", totalBytes } });
        }

        private async Task<ImageIndex> LoadImageIndexAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_imageIndex != null) return _imageIndex;
            }

            var index = new ImageIndex();
            try
            {
                string text = await _store.ReadTextAsync(ImageIndexPath, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(text))
                {
                    JObject parsed = JObject.Parse(text);
                    JObject entries = parsed["entries"] as JObject;
                    if (entries != null)
                    {
                        foreach (KeyValuePair<string, JToken> pair in entries)
                        {
                            JObject entry = pair.Value as JObject;
                            if (entry == null) continue;
                            index.Entries[pair.Key] = new ImageIndexEntry
                            {
                                Size = JsonValue.GetLong(entry, "size") ?? 0,
                                LastUsedMs = JsonValue.GetLong(entry, "lastUsedMs") ?? 0
                            };
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                // A corrupt index means we cannot evict precisely; starting empty is
                // recoverable, and the orphaned files are swept on the next sweep.
                _log.Warn(DiagCategory.Cache, "the image index was unreadable and was reset: " + exception.Message);
                index = new ImageIndex();
            }

            lock (_gate)
            {
                _imageIndex = index;
            }
            return index;
        }

        private async Task SaveImageIndexAsync(ImageIndex index, CancellationToken cancellationToken)
        {
            var entries = new JObject();
            foreach (KeyValuePair<string, ImageIndexEntry> pair in index.Entries)
            {
                entries[pair.Key] = new JObject
                {
                    { "size", pair.Value.Size },
                    { "lastUsedMs", pair.Value.LastUsedMs }
                };
            }

            var root = new JObject
            {
                { "schema", 1 },
                { "savedAtMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                { "entries", entries }
            };

            try
            {
                await _store.WriteTextAsync(ImageIndexPath, root.ToString(Newtonsoft.Json.Formatting.None),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log.Warn(DiagCategory.Cache, "could not persist the image index: " + exception.Message);
            }
        }

        // ------------------------------------------------------------------
        // maintenance
        // ------------------------------------------------------------------

        public async Task<JObject> DescribeAsync(CancellationToken cancellationToken)
        {
            IList<string> metadataFiles = await _store.ListAsync(AwemeFolder, cancellationToken).ConfigureAwait(false);
            IList<string> imageFiles = await _store.ListAsync(ImageFolder, cancellationToken).ConfigureAwait(false);

            long imageBytes = 0;
            for (int index = 0; index < imageFiles.Count; index++)
            {
                long size = await _store.SizeAsync(ImageFolder + "/" + imageFiles[index], cancellationToken)
                    .ConfigureAwait(false);
                if (size > 0) imageBytes += size;
            }

            return new JObject
            {
                { "metadataEntries", metadataFiles.Count },
                { "metadataMaxEntries", MetadataMaxEntries },
                { "imageEntries", imageFiles.Count },
                { "imageMaxEntries", ImageMaxEntries },
                { "imageBytes", imageBytes },
                { "imageMaxBytes", ImageMaxBytes }
            };
        }

        /// <summary>Clear one kind of cache. "all" clears both.</summary>
        public async Task<int> ClearAsync(string kind, CancellationToken cancellationToken)
        {
            int removed = 0;

            if (kind == "metadata" || kind == "all")
            {
                IList<string> files = await _store.ListAsync(AwemeFolder, cancellationToken).ConfigureAwait(false);
                for (int index = 0; index < files.Count; index++)
                {
                    await _store.DeleteAsync(AwemeFolder + "/" + files[index], cancellationToken).ConfigureAwait(false);
                    removed++;
                }
            }

            if (kind == "images" || kind == "all")
            {
                IList<string> files = await _store.ListAsync(ImageFolder, cancellationToken).ConfigureAwait(false);
                for (int index = 0; index < files.Count; index++)
                {
                    await _store.DeleteAsync(ImageFolder + "/" + files[index], cancellationToken).ConfigureAwait(false);
                    removed++;
                }
                lock (_gate)
                {
                    _imageIndex = null;
                }
            }

            _log.Info(DiagCategory.Cache, "cache cleared",
                new JObject { { "kind", kind }, { "removed", removed } });
            return removed;
        }

        private sealed class CacheEntry
        {
            public string Path;
            public long Size;
        }

        private sealed class ImageIndexEntry
        {
            public long Size;
            public long LastUsedMs;
        }

        private sealed class ImageIndex
        {
            public readonly Dictionary<string, ImageIndexEntry> Entries =
                new Dictionary<string, ImageIndexEntry>(StringComparer.Ordinal);
        }
    }
}
