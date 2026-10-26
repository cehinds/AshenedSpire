using Ashen.Generated;
using SI = Ashen.Generated.SettingIds;

namespace Ashen.App.Settings
{
    /// <summary>
    /// The Rendering quality setting (the web's performanceMode, src/ui/performance.js resolvePerformanceMode and resolveCombatPacing): "lite" and "full" are chosen outright, "auto" is lite on a touch device
    /// (the web's coarse pointer) and full elsewhere. Lite leaves out the decorative glows, and "auto" combat pacing plays Fast in it, Normal otherwise. Presentation-only; it never reaches the rules.
    /// </summary>
    public static class RenderingQuality
    {
        /// <summary>True on a device whose primary pointer is a finger (the web's <c>(pointer: coarse)</c>); the boot sets it from the platform.</summary>
        public static bool Coarse;

        /// <summary>resolvePerformanceMode: the setting's value, else the device.</summary>
        public static bool IsLite(string mode, bool coarse) =>
            mode == Core2UiValues.RenderLite || (mode != Core2UiValues.RenderFull && coarse);

        public static bool IsLite(SettingsSession settings) => IsLite(settings.Text(SI.PerformanceMode), Coarse);

        /// <summary>resolveCombatPacing: a named pace stays, anything else (auto) is Fast in lite and Normal in full.</summary>
        public static string Pacing(string animSpeed, bool lite) =>
            animSpeed == Core2UiValues.AnimSlow || animSpeed == Core2UiValues.AnimNormal || animSpeed == Core2UiValues.AnimFast || animSpeed == Core2UiValues.AnimInstant
                ? animSpeed
                : lite ? Core2UiValues.AnimFast : Core2UiValues.AnimNormal;
    }
}
