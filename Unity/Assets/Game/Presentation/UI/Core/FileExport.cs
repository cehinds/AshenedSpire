using System;
using System.IO;
using Ashen.Generated;
using Ashen.Platform;
using UnityEngine;

namespace Ashen.Presentation.UI
{
    /// <summary>
    /// Hands a text file to the player (web: a Blob behind a synthetic anchor, `a.download = name; a.click()`, profileArchive.js entryCard, profileNotice.js, about.js). In a WebGL build in the browser it is exactly that: a
    /// download the browser saves wherever the player's settings say (<see cref="WebFiles"/>, Plugins/WebGL/AshenFiles.jslib). Everywhere else (the editor, a desktop or mobile build, which have no download) the file is written into
    /// the exports folder beside the saves; the same name overwrites its own earlier export.
    /// </summary>
    public static class FileExport
    {
        /// <summary>Save a file; true when it was handed over. A full or read-only disk can refuse (a browser download cannot).</summary>
        public static bool Save(string fileName, string text, bool json = true)
        {
            if (WebFiles.Available)
            {
                WebFiles.Download(fileName, text, json ? WebGlNames.MimeJson : WebGlNames.MimeText);
                return true;
            }
            try
            {
                var folder = Path.Combine(Application.persistentDataPath, UiResources.ExportsFolder);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, fileName), text);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is NotSupportedException)
            {
                return false;
            }
        }
    }
}
