using System;
using System.Collections.Generic;
using DesultorySignals.Core.Diagnostics;

namespace DesultorySignals.Core.Media
{
    /// <summary>
    /// A bounded, chunk-granular LRU cache for video bytes.
    /// </summary>
    /// <remarks>
    /// Three decisions worth recording, because each one is a bug avoided:
    ///
    /// 1. **Chunks, not a byte stream.** <c>MediaElement</c> is free to read in any
    ///    order and to seek; a cache keyed by chunk index answers a seek with one
    ///    lookup instead of a re-download.
    ///
    /// 2. **Bounded by chunk count, not by bytes requested.** The device has about a
    ///    gigabyte of RAM and playback can run for an hour. An unbounded cache is
    ///    the memory growth the completion criteria forbid, so eviction is part of
    ///    the type rather than an afterthought.
    ///
    /// 3. **The last chunk may be short.** The final chunk of a file is generally
    ///    not full, and the cache stores its true length rather than padding it.
    ///    Padding would feed the demuxer zeros past the end of the file, which
    ///    some decoders accept and some do not.
    ///
    /// Not thread safe by accident: every entry point takes the lock, because
    /// <c>MediaElement</c> reads on a different thread from the one that prefetches.
    /// </remarks>
    public sealed class RangeCache
    {
        private sealed class Chunk
        {
            public int Index;
            public byte[] Data;
            public long LastUsedTick;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<int, Chunk> _chunks;
        private readonly LinkedList<int> _lru; // front = most recently used
        private readonly Dictionary<int, LinkedListNode<int>> _lruNodes;
        private readonly DiagLog _log;

        private long _tick;
        private long _hits;
        private long _misses;
        private long _evictions;
        private long _bytesStored;

        public int ChunkSize { get; private set; }
        public int MaxChunks { get; private set; }

        /// <summary>Set when the whole cache had to be dropped because the URL that
        /// produced it expired. The playback layer uses this to decide it needs fresh
        /// sources rather than more range reads.</summary>
        public bool WasInvalidated { get; private set; }

        public RangeCache(int chunkSize, int maxChunks, DiagLog log)
        {
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException("chunkSize");
            if (maxChunks <= 0) throw new ArgumentOutOfRangeException("maxChunks");
            ChunkSize = chunkSize;
            MaxChunks = maxChunks;
            _chunks = new Dictionary<int, Chunk>();
            _lru = new LinkedList<int>();
            _lruNodes = new Dictionary<int, LinkedListNode<int>>();
            _log = log;
        }

        public int Count
        {
            get { lock (_gate) { return _chunks.Count; } }
        }

        public long TotalBytes
        {
            get { lock (_gate) { return _bytesStored; } }
        }

        public long Hits
        {
            get { lock (_gate) { return _hits; } }
        }

        public long Misses
        {
            get { lock (_gate) { return _misses; } }
        }

        public long Evictions
        {
            get { lock (_gate) { return _evictions; } }
        }

        public double HitRatio
        {
            get
            {
                lock (_gate)
                {
                    long total = _hits + _misses;
                    return total == 0 ? 0.0 : (double)_hits / total;
                }
            }
        }

        /// <summary>
        /// Look up a chunk. Returns null on a miss. The returned array must be
        /// treated as read-only by the caller: it is shared with every other reader.
        /// </summary>
        public byte[] TryGet(int chunkIndex)
        {
            lock (_gate)
            {
                Chunk chunk;
                if (!_chunks.TryGetValue(chunkIndex, out chunk))
                {
                    _misses++;
                    return null;
                }
                Touch(chunkIndex);
                _hits++;
                return chunk.Data;
            }
        }

        /// <summary>Store a chunk, evicting the least recently used entries as needed.</summary>
        public void Put(int chunkIndex, byte[] data)
        {
            if (data == null) return;

            lock (_gate)
            {
                Chunk existing;
                if (_chunks.TryGetValue(chunkIndex, out existing))
                {
                    _bytesStored -= existing.Data.Length;
                    existing.Data = data;
                    _bytesStored += data.Length;
                    Touch(chunkIndex);
                    return;
                }

                var chunk = new Chunk { Index = chunkIndex, Data = data, LastUsedTick = ++_tick };
                _chunks[chunkIndex] = chunk;
                _lruNodes[chunkIndex] = _lru.AddFirst(chunkIndex);
                _bytesStored += data.Length;

                while (_chunks.Count > MaxChunks)
                {
                    LinkedListNode<int> oldest = _lru.Last;
                    if (oldest == null) break;
                    int victimIndex = oldest.Value;
                    Chunk victim;
                    if (_chunks.TryGetValue(victimIndex, out victim))
                    {
                        _bytesStored -= victim.Data.Length;
                        _chunks.Remove(victimIndex);
                    }
                    _lru.RemoveLast();
                    _lruNodes.Remove(victimIndex);
                    _evictions++;
                }
            }
        }

        private void Touch(int chunkIndex)
        {
            LinkedListNode<int> node;
            if (!_lruNodes.TryGetValue(chunkIndex, out node)) return;
            _lru.Remove(node);
            _lru.AddFirst(node);
        }

        /// <summary>Drop everything. Called when the source URL changes or expires.</summary>
        public void InvalidateAll(string reason)
        {
            lock (_gate)
            {
                int dropped = _chunks.Count;
                _chunks.Clear();
                _lru.Clear();
                _lruNodes.Clear();
                _bytesStored = 0;
                WasInvalidated = true;
                if (_log != null && dropped > 0)
                {
                    _log.Info(DiagCategory.Cache, string.Format(
                        "range cache cleared ({0} chunks) because {1}", dropped, reason));
                }
            }
        }

        /// <summary>
        /// Drop chunks at or after <paramref name="chunkIndex"/>. Used when a read
        /// fails mid-stream: bytes past the failure point may have come from a
        /// partially applied write and cannot be trusted.
        /// </summary>
        public void InvalidateFrom(int chunkIndex, string reason)
        {
            lock (_gate)
            {
                var doomed = new List<int>();
                foreach (KeyValuePair<int, Chunk> pair in _chunks)
                {
                    if (pair.Key >= chunkIndex) doomed.Add(pair.Key);
                }
                for (int index = 0; index < doomed.Count; index++)
                {
                    int key = doomed[index];
                    _bytesStored -= _chunks[key].Data.Length;
                    _chunks.Remove(key);
                    LinkedListNode<int> node;
                    if (_lruNodes.TryGetValue(key, out node))
                    {
                        _lru.Remove(node);
                        _lruNodes.Remove(key);
                    }
                }
                if (doomed.Count > 0 && _log != null)
                {
                    _log.Info(DiagCategory.Cache, string.Format(
                        "range cache dropped {0} chunks from #{1} because {2}",
                        doomed.Count, chunkIndex, reason));
                }
            }
        }

        public RangeCacheStats Snapshot()
        {
            lock (_gate)
            {
                return new RangeCacheStats
                {
                    ChunkSize = ChunkSize,
                    MaxChunks = MaxChunks,
                    ChunkCount = _chunks.Count,
                    TotalBytes = _bytesStored,
                    Hits = _hits,
                    Misses = _misses,
                    Evictions = _evictions,
                    HitRatio = (_hits + _misses) == 0 ? 0.0 : (double)_hits / (_hits + _misses)
                };
            }
        }
    }

    public struct RangeCacheStats
    {
        public int ChunkSize;
        public int MaxChunks;
        public int ChunkCount;
        public long TotalBytes;
        public long Hits;
        public long Misses;
        public long Evictions;
        public double HitRatio;

        public override string ToString()
        {
            return string.Format(
                "chunks={0}/{1} bytes={2} hit={3:P0} ({4} hit / {5} miss / {6} evict)",
                ChunkCount, MaxChunks, TotalBytes, HitRatio, Hits, Misses, Evictions);
        }
    }
}
