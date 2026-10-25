using System;
using System.IO;
using System.Linq;
using Ashen.Content;
using Ashen.Generated;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
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
            BuildUiArt();
            Debug.LogWarning("BuildWindows: player build pipeline lands in F6 (PF-11).");
        }

        // ------------------------------------------------------------------ WebGL (D-1250)

        private const string WebGlOutputFolder = "Builds/WebGL";
        private const string CustomBuildPathArg = "-customBuildPath";
        private const string WebGlTemplate = "APPLICATION:Default";
        private const int WebGlInitialMemoryMb = 256;
        private const int WebGlMaximumMemoryMb = 2048;

        /// <summary>
        /// WebGL player build:
        ///   Tools/unity.sh webgl -executeMethod Ashen.EditorTools.Cli.BuildWebGL -quit
        /// Writes Builds/WebGL (or the folder given by -customBuildPath, which game-ci passes). Brotli with the JS
        /// decompression fallback, so a host that sends no Content-Encoding header (GitHub Pages) still serves it.
        /// </summary>
        public static void BuildWebGL()
        {
            var report = new ContentValidator(new DirectoryContentSource(ContentRoot)).Validate();
            if (!report.IsValid)
            {
                Debug.LogError("Ashen content INVALID (" + report.Issues.Count + ")\n" + report.Summary(200));
                EditorApplication.Exit(1);
                return;
            }
            // CI restores the gitignored images just before the build (Tools/ci/fetch-art.sh, D-1300): rebuild the catalogs from them.
            BuildUiArt();
            ApplyWebGlSettings();
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            var output = WebGlOutputPath();
            Directory.CreateDirectory(output);
            var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.WebGL,
                targetGroup = BuildTargetGroup.WebGL,
                options = BuildOptions.None,
            });
            var ok = result.summary.result == BuildResult.Succeeded;
            if (ok) Debug.Log("Ashen WebGL build ok: " + output + " (" + result.summary.totalSize + " bytes, " + result.summary.totalTime + ")");
            else Debug.LogError("Ashen WebGL build FAILED: " + result.summary.result + ", " + result.summary.totalErrors + " errors");
            EditorApplication.Exit(ok ? 0 : 1);
        }

        private static string WebGlOutputPath()
        {
            var args = Environment.GetCommandLineArgs();
            var at = Array.IndexOf(args, CustomBuildPathArg);
            if (at >= 0 && at + 1 < args.Length) return Path.GetFullPath(args[at + 1]);
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", WebGlOutputFolder));
        }

        /// <summary>The WebGL PlayerSettings, set in code so the build does not depend on hand-edited ProjectSettings YAML.</summary>
        private static void ApplyWebGlSettings()
        {
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.threadsSupport = false;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.nameFilesAsHashes = false;
            PlayerSettings.WebGL.template = WebGlTemplate;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.FullWithoutStacktrace;
            PlayerSettings.WebGL.initialMemorySize = WebGlInitialMemoryMb;
            PlayerSettings.WebGL.maximumMemorySize = WebGlMaximumMemoryMb;
            PlayerSettings.WebGL.memoryGrowthMode = WebGLMemoryGrowthMode.Geometric;
            // Newtonsoft.Json reads members by reflection; Low keeps them without a link.xml for every Domain type.
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.Low);
        }
    }
}
