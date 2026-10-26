using System;
using System.Collections.Generic;
using System.IO;
using Ashen.Content;
using Newtonsoft.Json.Linq;

namespace Ashen.Tests
{
    /// <summary>
    /// The parsed oracle documents the parity tests read. Test discovery runs every TestCaseSource, and those read whole documents only to count
    /// the cases in them, so a cache that kept every document held gigabytes (the oracle is 134 MB of JSON, some twenty times that parsed) for the
    /// life of the test host. The cache holds the document read last (and any others the budget of source text allows) strongly, so a test that
    /// reads one document many times parses it once, and every other document weakly, so a collection frees it and a later read parses it again.
    /// </summary>
    public static class OracleDocuments
    {
        /// <summary>The source text of the documents kept (about 20 times that parsed).</summary>
        private const long BudgetBytes = 4L * 1024 * 1024;

        private sealed class Entry
        {
            public string Path;
            public long Bytes;
            public JObject Document;
        }

        private static readonly LinkedList<Entry> Latest = new LinkedList<Entry>();
        private static readonly Dictionary<string, WeakReference<JObject>> Others = new Dictionary<string, WeakReference<JObject>>(StringComparer.Ordinal);

        public static JObject Read(string path)
        {
            lock (Latest)
            {
                for (var node = Latest.First; node != null; node = node.Next)
                {
                    if (node.Value.Path != path) continue;
                    Latest.Remove(node);
                    Latest.AddFirst(node);
                    return node.Value.Document;
                }
                JObject parsed;
                if (!Others.TryGetValue(path, out var weak) || !weak.TryGetTarget(out parsed))
                {
                    parsed = (JObject)JsonContent.Parse(File.ReadAllText(path));
                    Others[path] = new WeakReference<JObject>(parsed);
                }
                var entry = new Entry { Path = path, Bytes = new FileInfo(path).Length, Document = parsed };
                Latest.AddFirst(entry);
                var total = 0L;
                for (var node = Latest.First; node != null;)
                {
                    var next = node.Next;
                    total += node.Value.Bytes;
                    if (total > BudgetBytes && node != Latest.First) Latest.Remove(node);
                    node = next;
                }
                return entry.Document;
            }
        }
    }
}
