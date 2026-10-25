using SI = Ashen.Generated.SettingIds;

namespace Ashen.App.Settings
{
    /// <summary>
    /// The combat pacing and automation dials, read from the player's settings (FIXCORE D11, D12): the pause between the
    /// stages of an action, the animation step, the speed multiplier (auto combat has its own) and the switches that
    /// change how a play is entered. Presentation reads this; none of it reaches the engine, so it never changes a result.
    /// </summary>
    public sealed class CombatPacing
    {
        public bool AutoCombat;
        public double Speed;
        public double AutoSpeed;
        public double StageGapMs;
        public double AnimationStepMs;
        public string AnimSpeed;
        public bool AskToUpcastAfterTarget;
        public bool OneTapPlay;
        public bool ScreenShake;
        public bool ReduceFlashes;

        /// <summary>The Rendering quality in force (performanceMode resolved for the device): lite leaves out the decorative glows.</summary>
        public bool Lite;

        /// <summary>The multiplier in force: auto combat runs at its own.</summary>
        public double EffectiveSpeed => AutoCombat ? AutoSpeed : Speed;

        /// <summary>One stage gap as waited, after the multiplier.</summary>
        public double StageGapScaledMs => StageGapMs / EffectiveSpeed;

        /// <summary>One animation step as played, after the multiplier.</summary>
        public double AnimationStepScaledMs => AnimationStepMs / EffectiveSpeed;

        public static CombatPacing Read(SettingsSession settings) => new CombatPacing
        {
            AutoCombat = settings.On(SI.AutoCombat),
            Speed = settings.Number(SI.CombatSpeed),
            AutoSpeed = settings.Number(SI.AutoCombatSpeed),
            StageGapMs = settings.Number(SI.CombatStepPause),
            AnimationStepMs = settings.Number(SI.AnimationStepDuration),
            AnimSpeed = RenderingQuality.Pacing(settings.Text(SI.AnimSpeed), RenderingQuality.IsLite(settings)),
            AskToUpcastAfterTarget = settings.On(SI.AskToUpcastAfterTarget),
            OneTapPlay = settings.On(SI.OneTapPlay),
            ScreenShake = settings.On(SI.ScreenShake),
            ReduceFlashes = settings.On(SI.ReduceFlashes),
            Lite = RenderingQuality.IsLite(settings),
        };
    }
}
