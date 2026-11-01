using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using DesultorySignals.Core.Diagnostics;
using DesultorySignals.Core.Storage;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace DesultorySignals.Platform
{
    /// <summary>
    /// <see cref="IFileStore"/> over <c>ApplicationData.Current.LocalFolder</c>.
    /// </summary>
    /// <remarks>
    /// The whole point of this class is that nothing above it ever sees an absolute
    /// path. <see cref="StorePath.Normalize"/> has already rejected traversal and
    /// illegal characters, so all that remains here is to walk the relative folders
    /// one segment at a time -- the API has no "create this nested path" call -- and
    /// then hand back a <c>System.IO.Stream</c> the Core layer can write to.
    ///
    /// The write path truncates deliberately (<c>ReplaceExisting</c>): every writer in
    /// the Core layer writes a whole document, and a partial overwrite of a JSON file
    /// would leave a store that fails to parse on the next launch. As with the test
    /// store's <c>MemoryWriteStream</c>, the file becomes the new content when the
    /// returned stream is disposed, and not before.
    /// </remarks>
    public sealed class LocalFileStore : IFileStore
    {
        private readonly StorageFolder _root;
        private readonly DiagLog _log;

        public LocalFileStore(StorageFolder root, DiagLog log)
        {
            if (root == null) throw new ArgumentNullException("root");
            _root = root;
            _log = log;
            RootPath = root.Path;
        }

        public string RootPath { get; private set; }

        public async Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken)
        {
            StorageFile file = await TryGetFileAsync(relativePath, cancellationToken).ConfigureAwait(false);
            return file != null;
        }

        public async Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken)
        {
            StorageFile file = await TryGetFileAsync(relativePath, cancellationToken).ConfigureAwait(false);
            if (file == null) return null;
            try
            {
                return await FileIO.ReadTextAsync(file).AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A corrupt store must behave like a missing one: the caller falls back
                // to a fresh session instead of failing to start.
                _log.Warn(DiagCategory.Storage,
                    "reading " + relativePath + " failed, treating it as absent: " + exception.Message);
                return null;
            }
        }

        public async Task WriteTextAsync(string relativePath, string content, CancellationToken cancellationToken)
        {
            StorageFile file = await EnsureFileAsync(relativePath, cancellationToken).ConfigureAwait(false);
            await FileIO.WriteTextAsync(file, content ?? string.Empty).AsTask(cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<byte[]> ReadBytesAsync(string relativePath, CancellationToken cancellationToken)
        {
            StorageFile file = await TryGetFileAsync(relativePath, cancellationToken).ConfigureAwait(false);
            if (file == null) return null;
            IBuffer buffer = await FileIO.ReadBufferAsync(file).AsTask(cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }

        public async Task WriteBytesAsync(string relativePath, byte[] content, CancellationToken cancellationToken)
        {
            StorageFile file = await EnsureFileAsync(relativePath, cancellationToken).ConfigureAwait(false);
            IBuffer buffer = Windows.Security.Cryptography.CryptographicBuffer
                .CreateFromByteArray(content ?? new byte[0]);
            await FileIO.WriteBufferAsync(file, buffer).AsTask(cancellationToken).ConfigureAwait(false);
        }

        public async Task<System.IO.Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken)
        {
            StorageFile file = await EnsureFileAsync(relativePath, cancellationToken).ConfigureAwait(false);
            IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite)
                .AsTask(cancellationToken).ConfigureAwait(false);
            return stream.AsStreamForWrite();
        }

        public async Task<System.IO.Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
        {
            StorageFile file = await TryGetFileAsync(relativePath, cancellationToken).ConfigureAwait(false);
            if (file == null) return null;
            IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read)
                .AsTask(cancellationToken).ConfigureAwait(false);
            return stream.AsStreamForRead();
        }

        public async Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
        {
            StorageFile file = await TryGetFileAsync(relativePath, cancellationToken).ConfigureAwait(false);
            if (file == null) return;
            try
            {
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete).AsTask(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log.Warn(DiagCategory.Storage,
                    "deleting " + relativePath + " failed: " + exception.Message);
            }
        }

        public async Task<IList<string>> ListAsync(string relativeFolder, CancellationToken cancellationToken)
        {
            var names = new List<string>();
            StorageFolder folder = await TryGetFolderAsync(relativeFolder, cancellationToken).ConfigureAwait(false);
            if (folder == null) return names;

            IReadOnlyList<IStorageItem> items = await folder.GetItemsAsync()
                .AsTask(cancellationToken).ConfigureAwait(false);
            for (int index = 0; index < items.Count; index++)
            {
                var file = items[index] as StorageFile;
                if (file != null) names.Add(file.Name);
            }
            return names;
        }

        public async Task<long> SizeAsync(string relativePath, CancellationToken cancellationToken)
        {
            StorageFile file = await TryGetFileAsync(relativePath, cancellationToken).ConfigureAwait(false);
            if (file == null) return -1;
            BasicProperties properties = await file.GetBasicPropertiesAsync()
                .AsTask(cancellationToken).ConfigureAwait(false);
            return (long)properties.Size;
        }

        /// <summary>The absolute path behind a store-relative one, for the two places
        /// that need it: <c>MediaElement</c> file playback and the diagnostics export.</summary>
        public string AbsolutePathOf(string relativePath)
        {
            string normalized = StorePath.Normalize(relativePath);
            string[] segments = normalized.Split('/');
            string path = RootPath;
            for (int index = 0; index < segments.Length; index++)
                path = System.IO.Path.Combine(path, segments[index]);
            return path;
        }

        private async Task<StorageFile> TryGetFileAsync(string relativePath, CancellationToken cancellationToken)
        {
            string normalized = StorePath.Normalize(relativePath);
            string folderPath = StorePath.FolderOf(normalized);
            string name = StorePath.FileNameOf(normalized);

            StorageFolder folder = await TryGetFolderAsync(folderPath, cancellationToken).ConfigureAwait(false);
            if (folder == null) return null;

            try
            {
                return await folder.GetFileAsync(name).AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (System.IO.FileNotFoundException)
            {
                return null;
            }
            catch (Exception)
            {
                // WinRT throws COMException for "not found" in some paths rather than
                // the projected FileNotFoundException.
                return null;
            }
        }

        private async Task<StorageFolder> TryGetFolderAsync(string relativeFolder, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(relativeFolder)) return _root;

            StorageFolder current = _root;
            string[] segments = relativeFolder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < segments.Length; index++)
            {
                try
                {
                    current = await current.GetFolderAsync(segments[index])
                        .AsTask(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return null;
                }
            }
            return current;
        }

        private async Task<StorageFile> EnsureFileAsync(string relativePath, CancellationToken cancellationToken)
        {
            string normalized = StorePath.Normalize(relativePath);
            string folderPath = StorePath.FolderOf(normalized);
            string name = StorePath.FileNameOf(normalized);

            StorageFolder folder = await EnsureFolderAsync(folderPath, cancellationToken).ConfigureAwait(false);
            return await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting)
                .AsTask(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Walk the relative folder path, creating each segment as needed.
        /// <c>OpenIfExists</c> makes concurrent creation of the same folder benign.</summary>
        private async Task<StorageFolder> EnsureFolderAsync(string relativeFolder, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(relativeFolder)) return _root;

            StorageFolder current = _root;
            string[] segments = relativeFolder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < segments.Length; index++)
            {
                current = await current
                    .CreateFolderAsync(segments[index], CreationCollisionOption.OpenIfExists)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
            }
            return current;
        }
    }
}
