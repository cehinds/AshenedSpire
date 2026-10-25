using System;
using System.Collections.Generic;
using System.IO;
using Ashen.Generated;

namespace Ashen.Content
{
    /// <summary>
    /// Content files held in memory, keyed by manifest-relative path. The WebGL player fills it at boot from
    /// StreamingAssets (a browser has no file system to read them from); tests fill it by hand. Like
    /// <see cref="DirectoryContentSource"/> it normalises CRLF to LF so hashes are stable.
    /// </summary>
    public sealed class MemoryContentSource : IContentSource
    {
        private readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.Ordinal);

        public int Count => _files.Count;

        public IEnumerable<string> Paths => _files.Keys;

        public MemoryContentSource Add(string relativePath, string text)
        {
            if (string.IsNullOrEmpty(relativePath) || relativePath.StartsWith(ContentLayout.PathSeparator, StringComparison.Ordinal) || relativePath.Contains(ContentLayout.ParentDirectory))
                throw new ArgumentException(relativePath, nameof(relativePath));
            if (text == null) throw new ArgumentNullException(nameof(text));
            _files[relativePath] = text.Replace(ContentLayout.CrLf, ContentLayout.Lf);
            return this;
        }

        public bool Exists(string relativePath) => relativePath != null && _files.ContainsKey(relativePath);

        public string ReadText(string relativePath)
        {
            if (relativePath == null || !_files.TryGetValue(relativePath, out var text)) throw new FileNotFoundException(relativePath);
            return text;
        }
    }
}
