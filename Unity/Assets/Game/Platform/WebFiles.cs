using System.Runtime.InteropServices;
using Ashen.Generated;

namespace Ashen.Platform
{
    /// <summary>
    /// The browser's file doors for a WebGL build (Plugins/WebGL/AshenFiles.jslib): a download (the web's `Blob`, `a.download = name`, `a.click()`) and a file the player picks (the web's hidden `input type=file`). Every call is
    /// a no-op in the editor and on other platforms, where <see cref="Available"/> is false and the callers use the exports folder instead.
    /// </summary>
    public static class WebFiles
    {
        /// <summary>The state of a file pick: nothing asked, waiting for the player, a file is ready or the player chose none.</summary>
        public enum PickState
        {
            Idle,
            Waiting,
            Ready,
            Cancelled,
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport(WebGlNames.Internal)]
        private static extern void AshenDownloadText(string fileName, string text, string mime);

        [DllImport(WebGlNames.Internal)]
        private static extern void AshenPickFileOpen(string accept);

        [DllImport(WebGlNames.Internal)]
        private static extern int AshenPickFileState();

        [DllImport(WebGlNames.Internal)]
        private static extern string AshenPickFileText();
#endif

        /// <summary>True in a browser build, where a file is downloaded and picked rather than written to and read from the exports folder.</summary>
        public static bool Available =>
#if UNITY_WEBGL && !UNITY_EDITOR
            true;
#else
            false;
#endif

        /// <summary>Hand the browser a text file to save.</summary>
        public static void Download(string fileName, string text, string mime)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            AshenDownloadText(fileName, text, mime);
#endif
        }

        /// <summary>Open the browser's file chooser; poll <see cref="Poll"/> for the answer. Must be called from the player's own click.</summary>
        public static void Pick(string accept)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            AshenPickFileOpen(accept);
#endif
        }

        /// <summary>The chooser's state; when Ready, <paramref name="text"/> is the chosen file's text (read once, then the state returns to Idle).</summary>
        public static PickState Poll(out string text)
        {
            text = null;
#if UNITY_WEBGL && !UNITY_EDITOR
            var state = (PickState)AshenPickFileState();
            if (state == PickState.Ready) text = AshenPickFileText();
            return state;
#else
            return PickState.Idle;
#endif
        }
    }
}
