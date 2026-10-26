using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CCM = Ashen.Generated.Core2CostMessages;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.Core2RemainingKeys;
using RM = Ashen.Generated.Core2RemainingMessages;
using RV = Ashen.Generated.Core2RemainingValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The intents a default Core 2 fight can send that no Core 2 content can satisfy, refused in the web's own words (WP-C0, D-290): the Combat Rounds queue
    /// (a setting no Core 2 run turns on) and an Ashen Blight feat choice (no Core 2 card pays Ashen Blight, so no milestone is ever pending). A fight that
    /// holds either (a forged snapshot) is refused by name.
    /// </summary>
    public static class CoreRefusals
    {
        /// <summary>unqueueRoundCard(combat, ...): the queue exists only while Combat Rounds is on.</summary>
        public static void UnqueueCard(CombatState c)
        {
            if (Js.Truthy(c.Fields[Ashen.Generated.Core2CostKeys.CombatRounds])) throw new NotSupportedException(CCM.CombatRoundsNotPorted);
            throw new InvalidOperationException(RM.NoRoundsPreview);
        }

        /// <summary>chooseAshenBlightFeat(ctx, owner, { threshold, path }): the checks in the web's order; a pending milestone is not ported.</summary>
        public static void ChooseBlightFeat(CombatState c, JObject intent)
        {
            if (Js.D(c.Fields[CK.CombatExpansionVersion]) != c.CoreData.ExpandedVersion) throw new InvalidOperationException(RM.BlightNeedsExpansion);
            var state = c.Player.Obj(CK.AshenBlight) ?? new JObject();
            var rules = c.Fields.Obj(CK.CombatExpansionRules)?.Obj(CK.AshenBlight);
            var paths = c.CoreData.Run.RuleSection(CK.AshenBlight).Obj(RK.PathAttributes) ?? new JObject();
            var threshold = intent[RK.Threshold];
            var path = intent.Str(RK.Path);
            var known = Js.Items(rules?[RK.Milestones]).Any(m => Js.IsNum(threshold) && Js.D(m) == Js.D(threshold)) && path != null && paths.Property(path) != null;
            if (!known) throw new InvalidOperationException(RM.BlightUnknownFeat);
            var entries = Js.Items(state[RK.Entries]).OfType<JObject>();
            if (state.Str(RK.ThresholdOutcome) == RV.OutcomeLost || entries.Any(row => row.Str(RK.Outcome) == RV.OutcomeLost)) throw new InvalidOperationException(RM.BlightLossNoChoice);
            var milestones = Js.Items(state[RK.Milestones]).OfType<JObject>().ToList();
            var chosen = milestones.FirstOrDefault(row => Js.D(row[RK.Threshold]) == Js.D(threshold));
            if (chosen == null || !Js.Nullish(chosen[RK.Path])) throw new InvalidOperationException(RM.BlightNotPending);
            if (milestones.Any(row => Js.D(row[RK.Threshold]) < Js.D(threshold) && Js.Nullish(row[RK.Path]))) throw new InvalidOperationException(RM.BlightEarlierFirst);
            throw new NotSupportedException(RM.BlightNotPorted);
        }
    }
}
