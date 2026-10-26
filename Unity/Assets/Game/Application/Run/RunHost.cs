using System;
using System.Globalization;
using Ashen.App.Saves;
using Ashen.Generated;

namespace Ashen.App.Run
{
    /// <summary>A run a New Game or a Continue produced: the ruleset it plays under and its session.</summary>
    public sealed class HostedRun
    {
        public string Ruleset;
        public Core2RunSession Core2;

        /// <summary>Where the run stands ('map', 'combat', 'runEnd', ...).</summary>
        public string Location => Core2.Location;
    }

    /// <summary>What a Continue found in a slot.</summary>
    public sealed class HostedLoad
    {
        public HostedRun Run;

        /// <summary>The slot's status by name (<see cref="Core2RunLoadStatus"/>).</summary>
        public string Status;
        public bool Resumed => Run != null;
        public readonly System.Collections.Generic.List<string> Warnings = new System.Collections.Generic.List<string>();
    }

    /// <summary>
    /// The title-level door to a run (docs/design/11 WP-C1a, D-302): New Game starts the ruleset rules/runFlow.json <c>newRun.ruleset</c> names and Continue reads
    /// a slot. Core 2 is the only ruleset since WP-21 (D-1700): a slot of the 405 run (save format 2) reports <see cref="Core2RunLoadStatus.OtherRuleset"/> and is kept.
    /// </summary>
    public static class RunHost
    {
        /// <summary>The ruleset a New Game starts now (runFlow.json newRun.ruleset).</summary>
        public static string Ruleset(RunContent content) => content.Flow.NewRunRuleset;

        /// <summary>A new run in a slot under the data's ruleset (or the one named, for a test).</summary>
        public static HostedRun Start(RunContent content, SaveService saves, int slotIndex, uint seed, string classId, string name, Func<DateTime> clock = null, CreationChoices choices = null,
            string ruleset = null)
        {
            ruleset = ruleset ?? Ruleset(content);
            if (ruleset == RunFlowValues.RulesetCore2)
                return new HostedRun { Ruleset = ruleset, Core2 = Core2RunSession.New(content, saves, slotIndex, seed, classId, name, clock, choices) };
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.UnknownRuleset, ruleset), nameof(ruleset));
        }

        /// <summary>Resume a slot through <see cref="Core2RunSession.Load"/>.</summary>
        public static HostedLoad Load(RunContent content, SaveService saves, int slotIndex, Func<DateTime> clock = null)
        {
            var result = new HostedLoad();
            var loaded = Core2RunSession.Load(content, saves, slotIndex, clock);
            result.Status = loaded.Status.ToString();
            result.Warnings.AddRange(loaded.Warnings);
            if (loaded.Resumed) result.Run = new HostedRun { Ruleset = RunFlowValues.RulesetCore2, Core2 = loaded.Session };
            return result;
        }
    }
}
