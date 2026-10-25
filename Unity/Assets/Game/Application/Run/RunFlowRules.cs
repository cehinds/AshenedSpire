using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Run
{
    /// <summary>A profile setting the loop reads: its key in profile.settings and the default when neither the profile nor the preset sets it.</summary>
    public sealed class FlowSetting
    {
        public string Key;
        public bool Default;

        internal static FlowSetting From(JToken json) => new FlowSetting
        {
            Key = (string)json?[RunFlowKeys.Key],
            Default = json?[RunFlowKeys.Def]?.Type == JTokenType.Boolean && (bool)json[RunFlowKeys.Def],
        };
    }

    /// <summary>
    /// rules/runFlow.json: the default character, the review fight rule, the autosave policy (D-071), what the caller hands
    /// the run loop's newRun, the settings the loop reads, and the location → screen routes (the climb's screen seam).
    /// </summary>
    public sealed class RunFlowRules
    {
        public int SaveFormat;
        public string DefaultClass;
        public string DefaultTint;
        public string PortraitTemplate;
        public string Journey;
        public string NameKey;
        public string FirstFightSeat;
        public string FirstFightPool;
        public int FirstFightOrdinal;
        public string AfterCommand;
        public IReadOnlyCollection<string> CheckpointOn = Array.Empty<string>();

        /// <summary>newRun.customization: the parts of the character's customization that are data (the glyph).</summary>
        public JObject Customization = new JObject();

        /// <summary>newRun.advancedConfigSnapshot: advancedConfigSnapshot(settings) for empty advanced settings.</summary>
        public JObject AdvancedConfigSnapshot = new JObject();

        /// <summary>newRun.prologue: shouldPlayPrologue (false while the prologue screen is planned).</summary>
        public bool Prologue;

        /// <summary>newRun.opening: a New Game plays the opening (W-05) before the climb's first step. It is the screen's, not the run's: the run itself stays headless (bots, tests); the 405 run's own prologue row is <see cref="Prologue"/>.</summary>
        public bool Opening;

        /// <summary>newRun.ruleset: the ruleset a New Game starts; 'core2' (<see cref="Core2RunSession"/>) is the only one since WP-21; see <see cref="RunHost"/>.</summary>
        public string NewRunRuleset;

        public FlowSetting MultiUse = new FlowSetting();
        public FlowSetting ShopSell = new FlowSetting();
        public FlowSetting HoldConfirm = new FlowSetting();

        /// <summary>core2Screens: Core2RunSession.Location → the screen that shows it.</summary>
        public IReadOnlyDictionary<string, string> Core2Screens = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The screen a Core 2 run's location routes to, or null.</summary>
        public string Core2ScreenFor(string location) => location != null && Core2Screens.TryGetValue(location, out var id) ? id : null;

        public static RunFlowRules From(JObject json)
        {
            var creation = (JObject)json[RunFlowKeys.Creation];
            var fight = (JObject)json[RunFlowKeys.FirstFight];
            var autosave = (JObject)json[RunFlowKeys.Autosave];
            var newRun = json[RunFlowKeys.NewRun] as JObject ?? new JObject();
            var settings = json[RunFlowKeys.Settings] as JObject ?? new JObject();
            var core2Screens = json[RunFlowKeys.Core2Screens] as JObject ?? new JObject();
            return new RunFlowRules
            {
                SaveFormat = (int)json[RunFlowKeys.SaveFormat],
                DefaultClass = (string)creation[RunFlowKeys.DefaultClass],
                DefaultTint = (string)creation[RunFlowKeys.DefaultTint],
                PortraitTemplate = (string)creation[RunFlowKeys.Portrait],
                Journey = (string)creation[RunFlowKeys.Journey],
                NameKey = (string)creation[RunFlowKeys.NameKey],
                FirstFightSeat = (string)fight[RunFlowKeys.Seat],
                FirstFightPool = (string)fight[RunFlowKeys.Pool],
                FirstFightOrdinal = (int)fight[RunFlowKeys.Ordinal],
                AfterCommand = (string)autosave[RunFlowKeys.AfterCommand],
                CheckpointOn = new HashSet<string>((autosave[RunFlowKeys.CheckpointOn] as JArray ?? new JArray()).Select(t => (string)t), StringComparer.Ordinal),
                Customization = newRun[RunFlowKeys.Customization] as JObject ?? new JObject(),
                AdvancedConfigSnapshot = newRun[RunFlowKeys.AdvancedConfigSnapshot] as JObject ?? new JObject(),
                Prologue = newRun[RunFlowKeys.Prologue]?.Type == JTokenType.Boolean && (bool)newRun[RunFlowKeys.Prologue],
                Opening = newRun[RunFlowKeys.Opening]?.Type == JTokenType.Boolean && (bool)newRun[RunFlowKeys.Opening],
                NewRunRuleset = (string)newRun[RunFlowKeys.Ruleset] ?? RunFlowValues.RulesetLegacy,
                MultiUse = FlowSetting.From(settings[RunFlowKeys.MultiUse]),
                ShopSell = FlowSetting.From(settings[RunFlowKeys.ShopSell]),
                HoldConfirm = FlowSetting.From(settings[RunFlowKeys.HoldConfirm]),
                Core2Screens = core2Screens.Properties().ToDictionary(p => p.Name, p => (string)p.Value, StringComparer.Ordinal),
            };
        }
    }
}
