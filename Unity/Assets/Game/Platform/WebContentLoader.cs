using System.Collections;
using System.Collections.Generic;
using Ashen.Content;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.Networking;

namespace Ashen.Platform
{
    /// <summary>
    /// Fetches every manifest file of StreamingAssets/Content over HTTP into a <see cref="MemoryContentSource"/>
    /// (the WebGL player cannot use System.IO on StreamingAssets). Run it as a coroutine; when it ends,
    /// <see cref="Failed"/> says whether <see cref="Result"/> is usable. The manifest is the only file list (no directory listing).
    /// </summary>
    public sealed class WebContentLoader
    {
        public MemoryContentSource Result { get; private set; }

        public string Error { get; private set; }

        public bool Failed => Error != null;

        public int Loaded { get; private set; }

        public int Total { get; private set; }

        private static string UrlOf(string relativePath) =>
            Application.streamingAssetsPath + ContentLayout.PathSeparator + ContentLayout.ContentFolder + ContentLayout.PathSeparator + relativePath;

        public IEnumerator Run()
        {
            var memory = new MemoryContentSource();
            using (var manifest = UnityWebRequest.Get(UrlOf(ContentLayout.Manifest)))
            {
                yield return manifest.SendWebRequest();
                if (manifest.result != UnityWebRequest.Result.Success)
                {
                    Error = string.Format(WebGlNames.FetchFailed, ContentLayout.Manifest, manifest.error);
                    yield break;
                }
                memory.Add(ContentLayout.Manifest, manifest.downloadHandler.text);
            }

            var pending = new Queue<string>();
            foreach (var entry in ContentManifest.Load(memory).Files) pending.Enqueue(entry.Path);
            Total = pending.Count;

            var inFlight = new List<KeyValuePair<string, UnityWebRequest>>();
            while (pending.Count > 0 || inFlight.Count > 0)
            {
                while (pending.Count > 0 && inFlight.Count < WebGlLimits.FetchParallel)
                {
                    var next = pending.Dequeue();
                    var started = UnityWebRequest.Get(UrlOf(next));
                    started.SendWebRequest();
                    inFlight.Add(new KeyValuePair<string, UnityWebRequest>(next, started));
                }
                yield return null;
                for (var i = inFlight.Count - 1; i >= 0; i--)
                {
                    var path = inFlight[i].Key;
                    var request = inFlight[i].Value;
                    if (!request.isDone) continue;
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Error = string.Format(WebGlNames.FetchFailed, path, request.error);
                        foreach (var other in inFlight) other.Value.Dispose();
                        yield break;
                    }
                    memory.Add(path, request.downloadHandler.text);
                    request.Dispose();
                    inFlight.RemoveAt(i);
                    Loaded++;
                }
            }
            Result = memory;
        }
    }
}
