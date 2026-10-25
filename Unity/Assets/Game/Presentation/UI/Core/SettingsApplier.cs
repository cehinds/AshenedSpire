using System;
using System.Collections.Generic;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;
using SV = Ashen.Generated.SettingsValues;

namespace Ashen.Presentation.UI
{
    /// <summary>
    /// Dresses the running UI in the player's settings, as the web's applyDisplaySettings does (src/main.js:905-1060): the accent colour, high contrast off and colorblind-friendly (palette
    /// classes on the UI root, <c>Styles/Accessibility.uss</c>), readable headings, the HUD parts the player hides, the control-hints bar, fullscreen, the View mode and UI size (the layout
    /// classifier) and the hover explanations (<see cref="TooltipPolicy"/>). It listens for <see cref="SettingsSession.Changed"/>; a key of null (a reset, an Undo) applies everything.
    /// </summary>
    public sealed class SettingsApplier : IDisposable
    {
        private readonly UiHost _host;

        private static readonly KeyValuePair<string, string>[] HudParts =
        {
            new KeyValuePair<string, string>(SettingIds.HudShowVitality, UiClasses.HudHideVitality),
            new KeyValuePair<string, string>(SettingIds.HudShowRelics, UiClasses.HudHideRelics),
            new KeyValuePair<string, string>(SettingIds.HudShowCurrency, UiClasses.HudHideCurrency),
            new KeyValuePair<string, string>(SettingIds.HudShowPosition, UiClasses.HudHidePosition),
            new KeyValuePair<string, string>(SettingIds.HudShowPotions, UiClasses.HudHidePotions),
        };

        public SettingsApplier(UiHost host)
        {
            _host = host;
            SettingsSession.Changed += OnChanged;
        }

        public void Dispose() => SettingsSession.Changed -= OnChanged;

        private void OnChanged(SettingsSession settings, string key) => Apply(settings, key);

        /// <summary>Applies one setting, or all of them when the key is null.</summary>
        public void Apply(SettingsSession settings, string key = null)
        {
            var root = _host.Document?.rootVisualElement;
            if (root == null || settings == null) return;
            if (key == null || key == SettingIds.Accent) ApplyAccent(root, settings);
            if (key == null || key == SettingIds.WalkedFade) ApplyChoice(root, settings, SettingIds.WalkedFade, UiClasses.WalkedPrefix);
            if (key == null || key == SettingIds.HighContrast) root.EnableInClassList(UiClasses.PaletteSoft, !settings.On(SettingIds.HighContrast));
            if (key == null || key == SettingIds.ColorblindSafe) root.EnableInClassList(UiClasses.PaletteColourblind, settings.On(SettingIds.ColorblindSafe));
            if (key == null || key == SettingIds.ReadableHeadings) root.EnableInClassList(UiClasses.ReadableHeadings, settings.On(SettingIds.ReadableHeadings));
            if (key == null || key == SettingIds.ControlHints) root.EnableInClassList(UiClasses.KeysHidden, !settings.On(SettingIds.ControlHints));
            foreach (var part in HudParts)
                if (key == null || key == part.Key) root.EnableInClassList(part.Value, !settings.On(part.Key));
            if (key == null || key == SettingIds.Fullscreen) ApplyFullscreen(settings);
            if (key == null || key == SettingIds.ViewPreset || key == SettingIds.UiScale) ApplyLayout(settings);
            if (key == null || key == SettingIds.HoverTooltips || key == SettingIds.TooltipDelay || key == SettingIds.TooltipCloseDelay) ApplyTooltips(settings);
        }

        private void ApplyAccent(VisualElement root, SettingsSession settings) => ApplyChoice(root, settings, SettingIds.Accent, UiClasses.AccentPrefix);

        /// <summary>A choice shows as a class on the root, one per option, except the default's (its look is the style sheet's own).</summary>
        private static void ApplyChoice(VisualElement root, SettingsSession settings, string key, string prefix)
        {
            var def = settings.Defs.Find(key);
            if (def == null) return;
            var chosen = settings.Text(key);
            foreach (var option in def.Options) root.EnableInClassList(prefix + option, option == chosen && option != def.Def.ToString());
        }

        /// <summary>Fullscreen is a switch for the window (not for a capture's render texture).</summary>
        private void ApplyFullscreen(SettingsSession settings)
        {
            if (_host.Document == null || _host.Document.panelSettings == null || _host.Document.panelSettings.targetTexture != null) return;
            var on = settings.On(SettingIds.Fullscreen);
            if (Screen.fullScreen != on) Screen.fullScreen = on;
        }

        private void ApplyLayout(SettingsSession settings)
        {
            LayoutClassifier.Preset = settings.Text(SettingIds.ViewPreset);
            LayoutClassifier.Size = settings.Text(SettingIds.UiScale);
            _host.Navigator?.Relayout();
        }

        private static void ApplyTooltips(SettingsSession settings)
        {
            TooltipPolicy.HoverEnabled = settings.On(SettingIds.HoverTooltips);
            TooltipPolicy.OpenMs = TooltipPolicy.Millis(settings.Text(SettingIds.TooltipDelay), TooltipPolicy.OpenMs);
            TooltipPolicy.CloseMs = TooltipPolicy.Millis(settings.Text(SettingIds.TooltipCloseDelay), TooltipPolicy.CloseMs);
            TooltipPolicy.Applied = true;
        }
    }
}
