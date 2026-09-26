using System;
using System.IO;
using Ashen.Content;

namespace Ashen.Tests
{
    /// <summary>
    /// Locates the committed content folder from either runner: Unity EditMode (cwd = Unity project) or
    /// dotnet test (cwd = Tests/Domain.Tests/bin/...). Shared, engine-free test support.
    /// </summary>
    public static class TestContent
    {
        private static readonly string[] Candidates =
        {
            Path.Combine("Assets", "StreamingAssets", "Content"),
            Path.Combine("Unity", "Assets", "StreamingAssets", "Content"),
        };

        private static string _root;

        public static string Root
        {
            get
            {
                if (_root != null) return _root;
                var dir = new DirectoryInfo(Environment.CurrentDirectory);
                while (dir != null)
                {
                    foreach (var candidate in Candidates)
                    {
                        var path = Path.Combine(dir.FullName, candidate);
                        if (File.Exists(Path.Combine(path, "manifest.json"))) return _root = path;
                    }
                    dir = dir.Parent;
                }
                throw new DirectoryNotFoundException("Content folder not found above " + Environment.CurrentDirectory);
            }
        }

        public static IContentSource Source => new DirectoryContentSource(Root);

        /// <summary>Repository root (parent of the Unity project).</summary>
        public static string RepoRoot => Path.GetFullPath(Path.Combine(Root, "..", "..", "..", ".."));
    }
}
