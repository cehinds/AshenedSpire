using System;
using System.IO;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using Ashen.Presentation;
using Ashen.Presentation.UI;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ashen.EditorTools
{
    /// <summary>
    /// F1 UI tooling (docs/design/09 §6.2): the UI art catalog, the Boot scene and the screenshot capture.
    ///   Unity -batchmode -projectPath Unity -executeMethod Ashen.EditorTools.Cli.BuildUiArt -quit
    ///   Unity -batchmode -projectPath Unity -executeMethod Ashen.EditorTools.Cli.CreateBootScene -quit
    ///   Unity -batchmode -projectPath Unity -executeMethod Ashen.EditorTools.Cli.CaptureScreens      (no -quit, no -nographics)
    /// </summary>
    public static partial class Cli
    {
        public const string BootScenePath = "Assets/Scenes/Boot.unity";
        private const string PresentationResources = "Assets/Game/Presentation/UI/Resources/";
        private const string CaptureVariable = "ASHEN_CAPTURE";
        private const string CaptureTest = "Ashen.Tests.Play.ScreenCaptureTests.CaptureScreens";
        private const string UnityPathKey = "unityPath";

        [MenuItem("Ashen/UI/Build UI Art Catalog")]
        public static void BuildUiArt()
        {
            var source = new DirectoryContentSource(ContentRoot);
            var data = UiData.Load(source);
            var registry = (JObject)JsonContent.Parse(source.ReadText(ContentFiles.AssetsRegistry));
            var path = PresentationResources + UiResources.ArtCatalog + ".asset";
            var catalog = AssetDatabase.LoadAssetAtPath<UiArtCatalog>(path);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<UiArtCatalog>();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                AssetDatabase.CreateAsset(catalog, path);
            }
            catalog.entries.Clear();
            var missing = 0;
            foreach (var p in registry.Properties().Where(p => data.Components.ArtIncludes(p.Name)))
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>((string)p.Value[UnityPathKey]);
                if (texture == null) { missing++; continue; }
                catalog.entries.Add(new UiArtCatalog.Entry { id = p.Name, texture = texture });
            }
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            Debug.Log("UI art catalog: " + catalog.entries.Count + " textures, " + missing + " registry ids without an imported texture");
        }

        [MenuItem("Ashen/UI/Create Boot Scene")]
        public static void CreateBootScene()
        {
            BuildUiArt();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.orthographic = true;
            new GameObject("Ashen").AddComponent<AshenBoot>();
            Directory.CreateDirectory(Path.GetDirectoryName(BootScenePath));
            EditorSceneManager.SaveScene(scene, BootScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(BootScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("Boot scene written to " + BootScenePath + " and set as the only build scene");
        }

        /// <summary>Runs the capture PlayMode test (ASHEN_CAPTURE=1) and exits with its result. Do not pass -quit.</summary>
        public static void CaptureScreens()
        {
            Environment.SetEnvironmentVariable(CaptureVariable, "1");
            BuildUiArt();
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new CaptureCallbacks());
            api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.PlayMode, testNames = new[] { CaptureTest } }));
        }

        [InitializeOnLoad]
        private static class CaptureHook
        {
            static CaptureHook()
            {
                if (Environment.GetEnvironmentVariable(CaptureVariable) != "1" || !Application.isBatchMode) return;
                ScriptableObject.CreateInstance<TestRunnerApi>().RegisterCallbacks(new CaptureCallbacks());
            }
        }

        private sealed class CaptureCallbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                Debug.Log("CaptureScreens finished: " + result.PassCount + " passed, " + result.FailCount + " failed, " + result.SkipCount + " skipped");
                if (Application.isBatchMode) EditorApplication.Exit(result.FailCount > 0 || result.PassCount == 0 ? 1 : 0);
            }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (!result.HasChildren && result.FailCount > 0) Debug.LogError(result.Name + ": " + result.Message);
            }
        }
    }
}
