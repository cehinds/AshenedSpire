using System.IO;
using Ashen.Generated;
using UnityEngine;

namespace Ashen.Platform
{
    /// <summary>Where content and saves live on this platform (docs/design/08 §2, 09 §3.4).</summary>
    public static class PlatformPaths
    {
        /// <summary>StreamingAssets/Content: read through DirectoryContentSource on the editor and Windows players; the WebGL player fetches it over HTTP instead (WebContentLoader).</summary>
        public static string ContentRoot => Path.Combine(Application.streamingAssetsPath, ContentLayout.ContentFolder);

        /// <summary>Run slots, the profile and their mirrors (persistentDataPath).</summary>
        public static string SaveRoot => Path.Combine(Application.persistentDataPath, UiResources.SavesFolder);
    }
}
