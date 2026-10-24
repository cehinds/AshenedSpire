using System;
using System.Globalization;
using Ashen.App.Saves;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Run
{
    /// <summary>A run a New Game or a Continue produced: the ruleset it plays under and its session (exactly one is set).</summary>
    public sealed class HostedRun
    {
        public string Ruleset;
        public RunSession Legacy;
        public Core2RunSession Core2;

        /// <summary>Where the run stands (the same words for both rulesets: 'map', 'combat', 'runEnd', ...).</summary>
        public string Location => Core2 != null ? Core2.Location : Legacy.Location;

        public bool IsCore2 => Core2 != null;
    }

    /// <summary>What a Continue found in a slot.</summary>
    public sealed class HostedLoad
    {
        public HostedRun Run;

        /// <summary>The slot's status as the ruleset that read it reports it (RunLoadStatus or Core2RunLoadStatus, by name).</summary>
        public string Status;
        public bool Resumed => Run != null;
        public readonly System.Collections.Generic.List<string> Warnings = new System.Collections.Generic.List<string>();
    }

    /// <summary>
    /// The title-level door to a run (docs/design/11 WP-C1a, D-302): New Game starts the ruleset rules/runFlow.json <c>newRun.ruleset</c> names — the 405 <see cref="RunSession"/> or the
    /// Core 2 <see cref="Core2RunSession"/> — and Continue reads a slot by the save format it holds (2 = a 405 run, 3 = a Core 2 run), so one title screen serves both. The
    /// ruleset is data: 'core2' since WP-C1b (D-313); the Core 2 reward, rest, shop and event nodes (WP-15 to WP-19) answer 'not yet available' meanwhile.
    /// </summary>
    public static class RunHost
    {
        /// <summary>The ruleset a New Game starts now (runFlow.json newRun.ruleset).</summary>
        public static string Ruleset(RunContent content) => content.Flow.NewRunRuleset;

        /// <summary>A new run in a slot under the data's ruleset (or the one named, for a test or a debug start).</summary>
        public static HostedRun Start(RunContent content, SaveService saves, int slotIndex, uint seed, string classId, string name, Func<DateTime> clock = null, CreationChoices choices = null,
            JObject custom = null, string ruleset = null)
        {
            ruleset = ruleset ?? Ruleset(content);
            if (ruleset == RunFlowValues.RulesetCore2)
                return new HostedRun { Ruleset = ruleset, Core2 = Core2RunSession.New(content, saves, slotIndex, seed, classId, name, clock, choices) };
            if (ruleset == RunFlowValues.RulesetLegacy)
                return new HostedRun { Ruleset = ruleset, Legacy = RunSession.New(content, saves, slotIndex, seed, classId, name, clock, custom, choices) };
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.UnknownRuleset, ruleset), nameof(ruleset));
        }

        /// <summary>Resume a slot: a Core 2 save (format 3) with <see cref="Core2RunSession"/>, anything else with <see cref="RunSession"/>.</summary>
        public static HostedLoad Load(RunContent content, SaveService saves, int slotIndex, Func<DateTime> clock = null)
        {
            var result = new HostedLoad();
            if (Core2RunSession.FormatOf(saves, slotIndex) == (int)Core2SnapshotNumbers.SaveFormat)
            {
                var loaded = Core2RunSession.Load(content, saves, slotIndex, clock);
                result.Status = loaded.Status.ToString();
                result.Warnings.AddRange(loaded.Warnings);
                if (loaded.Resumed) result.Run = new HostedRun { Ruleset = RunFlowValues.RulesetCore2, Core2 = loaded.Session };
                return result;
            }
            var legacy = RunSession.Load(content, saves, slotIndex, clock);
            result.Status = legacy.Status.ToString();
            result.Warnings.AddRange(legacy.Warnings);
            if (legacy.Resumed) result.Run = new HostedRun { Ruleset = RunFlowValues.RulesetLegacy, Legacy = legacy.Session };
            return result;
        }
    }
}
