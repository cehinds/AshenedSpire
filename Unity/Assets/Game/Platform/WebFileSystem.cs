using System.Runtime.InteropServices;
using Ashen.Generated;

namespace Ashen.Platform
{
    /// <summary>
    /// The WebGL save folder (persistentDataPath) lives in the browser's IndexedDB behind an in-memory file system:
    /// writes reach IndexedDB only on a sync. <c>AshenFsSync</c> (Plugins/WebGL/AshenFs.jslib) starts one; every
    /// call here is a no-op in the editor and on other platforms.
    /// </summary>
    public static class WebFileSystem
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        private static float _since;

        [DllImport(WebGlNames.Internal)]
        private static extern void AshenFsSync();
#endif

        /// <summary>Flush now: on pause and on losing focus.</summary>
        public static void Sync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            _since = 0f;
            AshenFsSync();
#endif
        }

        /// <summary>Called every frame; flushes every <see cref="WebGlTiming.SyncIntervalSeconds"/> so a closed tab loses at most that much.</summary>
        public static void Tick(float deltaSeconds)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            _since += deltaSeconds;
            if (_since >= WebGlTiming.SyncIntervalSeconds) Sync();
#endif
        }
    }
}
