using System;
using System.IO;
using System.Text;
using Ashen.Generated;

namespace Ashen.Content
{
    /// <summary>File-system content source rooted at the Content folder. Normalises CRLF to LF so hashes are stable.</summary>
    public sealed class DirectoryContentSource : IContentSource
    {
        private readonly string _root;

        public DirectoryContentSource(string root)
        {
            if (string.IsNullOrEmpty(root)) throw new ArgumentException(nameof(root));
            _root = Path.GetFullPath(root);
        }

        public string Root => _root;

        public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

        public string ReadText(string relativePath)
        {
            return File.ReadAllText(Resolve(relativePath), Encoding.UTF8).Replace(ContentLayout.CrLf, ContentLayout.Lf);
        }

        private string Resolve(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(ContentLayout.ParentDirectory))
                throw new ArgumentException(relativePath, nameof(relativePath));
            var parts = relativePath.Split(ContentLayout.PathSeparator[0]);
            return Path.Combine(_root, Path.Combine(parts));
        }
    }
}
