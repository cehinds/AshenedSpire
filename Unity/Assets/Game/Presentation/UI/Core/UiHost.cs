using System;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Platform;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI
{
    /// <summary>
    /// Owns the UIDocument, the two PanelSettings (wide and narrow, both Scale With Screen Size, from
    /// ui/layout.json; D-016) and the Navigator. The viewport is the screen, or a RenderTexture for captures.
    /// </summary>
    public sealed class UiHost : IDisposable
    {
        private PanelSettings _wide;
        private PanelSettings _narrow;
        private RenderTexture _target;
        private InputRouter _input;

        public UiContext Context { get; private set; }
        public UIDocument Document { get; private set; }
        public Navigator Navigator { get; private set; }

        public int ViewportWidth => _target != null ? _target.width : Screen.width;
        public int ViewportHeight => _target != null ? _target.height : Screen.height;

        public static UiHost Create(GameObject owner, UiContext context, RenderTexture target = null)
        {
            Loc.Table = context.Data.Strings;
            var host = new UiHost { Context = context, _target = target };
            var theme = Resources.Load<ThemeStyleSheet>(UiResources.Theme);
            var clear = context.TokenColor(TokenKeys.Bg);
            host._wide = Panel(context.Data.Layout.Wide, theme, target, clear);
            host._narrow = Panel(context.Data.Layout.Narrow, theme, target, clear);
            var document = owner.GetComponent<UIDocument>();
            host.Document = document != null ? document : owner.AddComponent<UIDocument>();
            var narrow = LayoutClassifier.Classify(host.ViewportWidth, host.ViewportHeight, context.Data.Layout).Narrow;
            host.Document.panelSettings = narrow ? host._narrow : host._wide;
            if (host.Document.rootVisualElement == null)
            {
                host.Document.enabled = false;
                host.Document.enabled = true;
            }
            var root = host.Document.rootVisualElement;
            root.AddToClassList(UiClasses.Screen);
            root.EnableInClassList(UiClasses.BuildDev, context.DevBuild);
            root.EnableInClassList(UiClasses.BuildPlayer, !context.DevBuild);
            host.Navigator = new Navigator(host, root);
            host.Navigator.ApplyLayout(host.ViewportWidth, host.ViewportHeight);
            host._input = new InputRouter(context.Data.Tokens.Duration(TokenKeys.NavRepeatDelay), context.Data.Tokens.Duration(TokenKeys.NavRepeatInterval));
            host._input.Move += host.Navigator.MoveFocus;
            host._input.Submit += host.Navigator.Submit;
            host._input.Cancel += host.Navigator.Back;
            return host;
        }

        private static PanelSettings Panel(PanelDef def, ThemeStyleSheet theme, RenderTexture target, Color clear)
        {
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.name = def.Id;
            ps.themeStyleSheet = theme;
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(def.Width, def.Height);
            ps.screenMatchMode = def.ScreenMatch == UiValues.MatchShrink ? PanelScreenMatchMode.Shrink
                : def.ScreenMatch == UiValues.MatchWidthOrHeight ? PanelScreenMatchMode.MatchWidthOrHeight
                : PanelScreenMatchMode.Expand;
            ps.match = (float)def.Match;
            ps.clearColor = target != null;
            ps.colorClearValue = clear;
            ps.targetTexture = target;
            return ps;
        }

        /// <summary>Swap PanelSettings by layout mode (the Navigator calls this when narrow/wide changes).</summary>
        public void UsePanel(bool narrow)
        {
            var wanted = narrow ? _narrow : _wide;
            if (Document != null && Document.panelSettings != wanted) Document.panelSettings = wanted;
        }

        /// <summary>Per-frame upkeep: follow viewport size changes.</summary>
        public void Tick()
        {
            Navigator?.ApplyLayout(ViewportWidth, ViewportHeight);
            _input?.Tick(Time.realtimeSinceStartup);
        }

        public void Dispose()
        {
            _input?.Dispose();
            _input = null;
            if (Document != null) Document.panelSettings = null;
            if (_wide != null) UnityEngine.Object.Destroy(_wide);
            if (_narrow != null) UnityEngine.Object.Destroy(_narrow);
        }
    }
}
