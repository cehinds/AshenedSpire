using System.Collections;
using System.Globalization;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using Ashen.Platform;
using Ashen.Presentation.UI;
using UnityEngine;

namespace Ashen.Presentation
{
    /// <summary>
    /// The Boot scene's only component: reads content through DirectoryContentSource on StreamingAssets (the WebGL
    /// player fetches it into a MemoryContentSource first, D-1251), builds the UI host and starts the Navigator at
    /// ui/screens.json#initial (W-24 boot → title gate → W-02 title).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AshenBoot : MonoBehaviour
    {
        /// <summary>Tests point saves at a scratch folder before the scene loads.</summary>
        public static string SaveDirectoryOverride { get; set; }

        public static AshenBoot Current { get; private set; }

        public UiHost Host { get; private set; }

        private void Awake()
        {
            Current = this;
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
#if UNITY_WEBGL && !UNITY_EDITOR
            StartCoroutine(BootFromWeb());
#else
            Build(new DirectoryContentSource(PlatformPaths.ContentRoot));
#endif
        }

        private IEnumerator BootFromWeb()
        {
            var loader = new WebContentLoader();
            yield return loader.Run();
            if (loader.Failed)
            {
                Debug.LogError(loader.Error);
                yield break;
            }
            Debug.LogFormat(WebGlNames.FetchDone, loader.Loaded);
            Build(loader.Result);
        }

        private void Build(IContentSource source)
        {
            var data = UiData.Load(source);
            var context = UiContext.Create(source, data, SaveDirectoryOverride ?? PlatformPaths.SaveRoot);
            var camera = Camera.main;
            if (camera != null) camera.backgroundColor = context.TokenColor(Ashen.Generated.TokenKeys.Bg);
            Host = UiHost.Create(gameObject, context);
            Host.Applier = new SettingsApplier(Host);
            Host.Applier.Apply(new Ashen.App.Settings.SettingsSession(context.RunContent, Ashen.App.Run.ProfileStore.Load(context.Saves), core2: true));
            gameObject.AddComponent<Audio.MusicPlayer>().Init(Host, source);
            gameObject.AddComponent<Audio.SfxPlayer>().Init(Host, source);
            Host.Navigator.Go(data.Screens.Initial);
        }

        private void Update()
        {
            Host?.Tick();
            WebFileSystem.Tick(Time.unscaledDeltaTime);
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) WebFileSystem.Sync();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) WebFileSystem.Sync();
        }

        private void OnDestroy()
        {
            Host?.Dispose();
            if (Current == this) Current = null;
        }
    }
}
