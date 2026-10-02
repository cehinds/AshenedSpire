using System.Globalization;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Platform;
using Ashen.Presentation.UI;
using UnityEngine;

namespace Ashen.Presentation
{
    /// <summary>
    /// The Boot scene's only component: reads content through DirectoryContentSource on StreamingAssets, builds
    /// the UI host and starts the Navigator at ui/screens.json#initial (W-24 boot → title gate → W-02 title).
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
            var source = new DirectoryContentSource(PlatformPaths.ContentRoot);
            var data = UiData.Load(source);
            var context = UiContext.Create(source, data, SaveDirectoryOverride ?? PlatformPaths.SaveRoot);
            var camera = Camera.main;
            if (camera != null) camera.backgroundColor = context.TokenColor(Ashen.Generated.TokenKeys.Bg);
            Host = UiHost.Create(gameObject, context);
            gameObject.AddComponent<Audio.MusicPlayer>().Init(Host, source);
            gameObject.AddComponent<Audio.SfxPlayer>().Init(Host, source);
            Host.Navigator.Go(data.Screens.Initial);
        }

        private void Update() => Host?.Tick();

        private void OnDestroy()
        {
            Host?.Dispose();
            if (Current == this) Current = null;
        }
    }
}
