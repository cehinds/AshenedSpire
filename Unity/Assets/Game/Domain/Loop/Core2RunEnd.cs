using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2RunKeys;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using RE = Ashen.Generated.Core2LoopKeys;
using RK = Ashen.Generated.RunKeys;

namespace Ashen.Domain.Loop
{
    /// <summary>What a Core 2 run's close-out did: the receipt (the run's record), and the classes the unlock ledger opened.</summary>
    public sealed class Core2RunEndReceipt
    {
        public RunEndReceipt Receipt;

        /// <summary>The class ids the run opened in the profile's ledger (the next of <c>classUnlocks.sequence</c>), empty when it opened none.</summary>
        public IReadOnlyList<string> ClassesUnlocked = Array.Empty<string>();

        /// <summary>The profile took the run (its finish id joined the ledger and its record joined the history).</summary>
        public bool Recorded;
    }

    /// <summary>
    /// The end of a configured Core 2 run (web main.js finishRun 2376-2420 over model/runCompletion.js completedRunMeta and model/gameProfileRun.js
    /// gameProfileCompletedRunMeta, WP-19). The run's record is <c>runResult(victory)</c> with the run's id and the pinned profile; the profile's meta takes it once: the run's
    /// finish id (its <c>runId</c>) joins <c>gameProfileUnlocks.&lt;profile&gt;.completedRunIds</c> when the outcome qualifies (<c>classUnlocks.qualifyingOutcomes</c>), which
    /// opens the next class of <c>classUnlocks.sequence</c>, and then the record is appended to <c>results</c> (the newest twenty, the profile's own ids beside it). A configured
    /// run writes no progress tally and evaluates no unlock of the 405 table. A finish id the ledger already holds changes nothing.
    /// </summary>
    public static class Core2RunEnd
    {
        /// <summary>finishRun(victory) on <paramref name="profile"/> (the stored profile JSON, rewritten in place).</summary>
        public static Core2RunEndReceipt Finish(Core2RewardsData d, Core2LoopData loop, JObject run, JObject profile, bool victory)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            var record = RunEnd.Result(run, victory, RunEnd.ClassName(d.Rewards, run));
            var finishId = run.Str(CK.RunId);
            var pin = CoreRunRules.PinOf(run);
            record[CK.RunId] = run[CK.RunId]?.DeepClone();
            record[RE.FinishId] = finishId;
            var ledger = profile.Obj(CK.GameProfileUnlocks)?.Obj(d.Run.ProfileId)?.Arr(CK.CompletedRunIds);
            var before = ledger?.Count ?? 0;
            var unlocked = CoreRunRules.CompletedRun(d.Run, profile, victory, finishId);
            var after = profile.Obj(CK.GameProfileUnlocks)?.Obj(d.Run.ProfileId)?.Arr(CK.CompletedRunIds)?.Count ?? 0;
            var recorded = after > before;
            if (recorded)
            {
                // The history row is the record without the profile pin, with the pin's id and hash beside it (completedRunMeta).
                var row = (JObject)record.DeepClone();
                row[RE.GameProfileId] = pin?[Ashen.Generated.Core2ProfileKeys.Id]?.DeepClone();
                row[RE.GameProfileHash] = pin?[Ashen.Generated.Core2ProfileKeys.Hash]?.DeepClone();
                var results = profile[LK.Results] as JArray ?? (JArray)(profile[LK.Results] = new JArray());
                results.Add(row);
                var limit = (int)loop.Engine.Obj(LK.Profile).Num(LK.HistoryLimit);
                while (results.Count > limit) results.RemoveAt(0);
                profile[RK.SchemaVersion] = loop.Engine.Obj(LK.Profile)[RK.SchemaVersion]?.DeepClone();
            }
            return new Core2RunEndReceipt { Receipt = new RunEndReceipt { Victory = victory, Result = record }, ClassesUnlocked = unlocked, Recorded = recorded };
        }
    }
}
