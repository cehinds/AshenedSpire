using System.IO;
using Ashen.Content;
using Ashen.Generated;
using UnityEditor;
using UnityEngine;

namespace Ashen.EditorTools
{
    /// <summary>
    /// Batchmode entry points (docs/design/09 §6.2):
    ///   Unity -batchmode -projectPath Unity -executeMethod Ashen.EditorTools.Cli.ValidateContent -quit
    /// Exit code 0 = ok, 1 = failure (details in the log).
    /// </summary>
    public static partial class Cli
    {
        public static string ContentRoot => Path.Combine(Application.streamingAssetsPath, ContentLayout.ContentFolder);

        [MenuItem("Ashen/Validate Content")]
        public static void ValidateContentMenu() => RunValidation(false);

        public static void ValidateContent() => RunValidation(true);

        private static void RunValidation(bool exit)
        {
            var report = new ContentValidator(new DirectoryContentSource(ContentRoot)).Validate();
            if (report.IsValid) Debug.Log("Ashen content valid: " + report.FilesChecked + " files");
            else Debug.LogError("Ashen content INVALID (" + report.Issues.Count + ")\n" + report.Summary(200));
            if (exit) EditorApplication.Exit(report.IsValid ? 0 : 1);
        }

        /// <summary>Windows player build (PF-11). Filled in during F6; F0 keeps the entry point so CI resolves it.</summary>
        public static void BuildWindows()
        {
            RunValidation(false);
            Debug.LogWarning("BuildWindows: player build pipeline lands in F6 (PF-11).");
        }
    }
}
