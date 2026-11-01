using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DesultorySignals.Core.Storage
{
    /// <summary>
    /// File storage, abstracted away from <c>Windows.Storage</c>.
    /// </summary>
    /// <remarks>
    /// The Core layer must be runnable on a desktop interpreter for tests, so it
    /// cannot touch the application container's storage APIs directly. The device
    /// implementation is a thin wrapper over <c>ApplicationData.Current.LocalFolder</c>;
    /// the test implementation is <see cref="MemoryFileStore"/>.
    ///
    /// Paths are always relative to the store root, and always use forward slashes.
    /// That is enforced rather than trusted: a path that escapes the root would be a
    /// session store written outside the app's private directory, which the brief
    /// forbids outright.
    /// </remarks>
    public interface IFileStore
    {
        /// <summary>Absolute path of the store root, for diagnostics only.</summary>
        string RootPath { get; }

        Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken);

        /// <summary>Returns null when the file does not exist. Never throws for absence.</summary>
        Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken);

        Task WriteTextAsync(string relativePath, string content, CancellationToken cancellationToken);

        Task<byte[]> ReadBytesAsync(string relativePath, CancellationToken cancellationToken);

        Task WriteBytesAsync(string relativePath, byte[] content, CancellationToken cancellationToken);

        /// <summary>
        /// Open a file for writing and stream into it. Needed for the
        /// download-then-play path: a full video is tens of megabytes and must not be
        /// materialised in memory on a phone.
        /// </summary>
        Task<System.IO.Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken);

        /// <summary>Open a file for reading, or null when it does not exist.</summary>
        Task<System.IO.Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken);

        Task DeleteAsync(string relativePath, CancellationToken cancellationToken);

        /// <summary>File names (not paths) directly inside a folder. Empty when the
        /// folder does not exist.</summary>
        Task<IList<string>> ListAsync(string relativeFolder, CancellationToken cancellationToken);

        /// <summary>Size in bytes, or -1 when the file does not exist.</summary>
        Task<long> SizeAsync(string relativePath, CancellationToken cancellationToken);
    }

    /// <summary>Path rules shared by every store implementation.</summary>
    public static class StorePath
    {
        /// <summary>
        /// Normalize and validate a relative path. Throws on anything that could
        /// escape the store root, so a bug in a caller becomes an exception here
        /// rather than a file written outside the private directory.
        /// </summary>
        public static string Normalize(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                throw new ArgumentException("path is required", "relativePath");

            string path = relativePath.Replace('\\', '/').Trim();

            while (path.StartsWith("/", StringComparison.Ordinal)) path = path.Substring(1);
            if (path.Length == 0) throw new ArgumentException("path is empty", "relativePath");

            if (path.Length >= 2 && path[1] == ':')
                throw new ArgumentException("absolute paths are not allowed: " + relativePath, "relativePath");

            string[] segments = path.Split('/');
            var cleaned = new List<string>();
            for (int index = 0; index < segments.Length; index++)
            {
                string segment = segments[index];
                if (segment.Length == 0 || segment == ".") continue;
                if (segment == "..")
                {
                    if (cleaned.Count == 0)
                        throw new ArgumentException("path escapes the store root: " + relativePath, "relativePath");
                    cleaned.RemoveAt(cleaned.Count - 1);
                    continue;
                }
                if (segment.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                    throw new ArgumentException("path contains an illegal character: " + relativePath, "relativePath");
                cleaned.Add(segment);
            }

            if (cleaned.Count == 0)
                throw new ArgumentException("path is empty after normalization: " + relativePath, "relativePath");

            return string.Join("/", cleaned.ToArray());
        }

        /// <summary>Folder part of a normalized path, or "" for a root-level file.</summary>
        public static string FolderOf(string normalizedPath)
        {
            int slash = normalizedPath.LastIndexOf('/');
            return slash < 0 ? string.Empty : normalizedPath.Substring(0, slash);
        }

        public static string FileNameOf(string normalizedPath)
        {
            int slash = normalizedPath.LastIndexOf('/');
            return slash < 0 ? normalizedPath : normalizedPath.Substring(slash + 1);
        }
    }

    /// <summary>In-memory store for desktop tests.</summary>
    public sealed class MemoryFileStore : IFileStore
    {
        private readonly Dictionary<string, byte[]> _files =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public string RootPath
        {
            get { return "(memory)"; }
        }

        public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken)
        {
            string path = StorePath.Normalize(relativePath);
            lock (_gate)
            {
                return Task.FromResult(_files.ContainsKey(path));
            }
        }

        public Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken)
        {
            string path = StorePath.Normalize(relativePath);
            lock (_gate)
            {
                byte[] data;
                if (!_files.TryGetValue(path, out data)) return Task.FromResult<string>(null);
                return Task.FromResult(System.Text.Encoding.UTF8.GetString(data, 0, data.Length));
            }
        }

        public Task WriteTextAsync(string relativePath, string content, CancellationToken cancellationToken)
        {
            string path = StorePath.Normalize(relativePath);
            byte[] data = System.Text.Encoding.UTF8.GetBytes(content ?? string.Empty);
            lock (_gate)
            {
                _files[path] = data;
            }
            return Task.FromResult(0);
        }

        public Task<byte[]> ReadBytesAsync(string relativePath, CancellationToken cancellationToken)
        {
            string path = StorePath.Normalize(relativePath);
            lock (_gate)
            {
                byte[] data;
                if (!_files.TryGetValue(path, out data)) return Task.FromResult<byte[]>(null);
                var copy = new byte[data.Length];
                Buffer.BlockCopy(data, 0, copy, 0, data.Length);
                return Task.FromResult(copy);
            }
        }

        public Task<System.IO.Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken)
        {
            string path = StorePath.Normalize(relativePath);
            var stream = new MemoryWriteStream(delegate(byte[] data)
            {
                lock (_gate)
                {
                    _files[path] = data;
                }
            });
            return Task.FromResult<System.IO.Stream>(stream);
        }

        public Task<System.IO.Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
        {
            string path = StorePath.Normalize(relativePath);
            lock (_gate)
            {
                byte[] data;
                if (!_files.TryGetValue(path, out data)) return Task.FromResult<System.IO.Stream>(null);
                return Task.FromResult<System.IO.Stream>(new System.IO.MemoryStream(data, false));
            }
        }

        public Task WriteBytesAsync(string relativePath, byte[] content, CancellationToken cancellationToken)
        {
            string path = StorePath.Normalize(relativePath);
            lock (_gate)
            {
                _files[path] = content ?? new byte[0];
            }
            return Task.FromResult(0);
        }

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
        {
            string path = StorePath.Normalize(relativePath);
            lock (_gate)
            {
                _files.Remove(path);
            }
            return Task.FromResult(0);
        }

        public Task<IList<string>> ListAsync(string relativeFolder, CancellationToken cancellationToken)
        {
            var result = new List<string>();
            string folder = string.IsNullOrEmpty(relativeFolder)
                ? string.Empty
                : StorePath.Normalize(relativeFolder);
            string prefix = folder.Length == 0 ? string.Empty : folder + "/";

            lock (_gate)
            {
                foreach (string path in _files.Keys)
                {
                    if (prefix.Length > 0 && !path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    string remainder = path.Substring(prefix.Length);
                    if (remainder.IndexOf('/') >= 0) continue; // not directly inside
                    result.Add(remainder);
                }
            }
            result.Sort(StringComparer.Ordinal);
            return Task.FromResult<IList<string>>(result);
        }

        public Task<long> SizeAsync(string relativePath, CancellationToken cancellationToken)
        {
            string path = StorePath.Normalize(relativePath);
            lock (_gate)
            {
                byte[] data;
                if (!_files.TryGetValue(path, out data)) return Task.FromResult(-1L);
                return Task.FromResult((long)data.Length);
            }
        }

        /// <summary>Test helper: everything currently stored.</summary>
        public IList<string> AllPaths()
        {
            lock (_gate)
            {
                var paths = new List<string>(_files.Keys);
                paths.Sort(StringComparer.Ordinal);
                return paths;
            }
        }

        public long TotalBytes()
        {
            lock (_gate)
            {
                long total = 0;
                foreach (byte[] data in _files.Values) total += data.Length;
                return total;
            }
        }
    }

    /// <summary>
    /// A write-only stream that hands its bytes to a callback on dispose. Small
    /// enough to live here, and it keeps <see cref="MemoryFileStore"/> a faithful
    /// stand-in for a real file store (including "nothing is visible until the
    /// write completes").
    /// </summary>
    internal sealed class MemoryWriteStream : System.IO.Stream
    {
        private readonly Action<byte[]> _commit;
        private readonly System.IO.MemoryStream _buffer = new System.IO.MemoryStream();
        private bool _committed;

        public MemoryWriteStream(Action<byte[]> commit)
        {
            _commit = commit;
        }

        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { return _buffer.Length; } }

        public override long Position
        {
            get { return _buffer.Position; }
            set { throw new NotSupportedException(); }
        }

        public override void Flush()
        {
            Commit();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, System.IO.SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _buffer.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Commit();
                _buffer.Dispose();
            }
            base.Dispose(disposing);
        }

        private void Commit()
        {
            if (_committed) return;
            _committed = true;
            _commit(_buffer.ToArray());
        }
    }
}
