using System;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using E = Ashen.Generated.FxEvents;
using F = Ashen.Generated.FxKeys;
using V = Ashen.Generated.FxValues;

namespace Ashen.App.Combat
{
    /// <summary>
    /// The body reactions of the Reaver's greatsword pilot to an enemy blow (src/model/selectedWeaponStances.js selectedWeaponReactionFor): an evade, a guard break, an invulnerable defence and a
    /// blow that Block took whole each have a painted reaction of their own, played in the reaction stage of the beat beside the hurt reaction.
    /// </summary>
    public static class CardStageReactions
    {
        /// <summary>The reaction the player's figure plays for one receipt of a blow it was the target of (evade, guard-break, defend, fully-defended), or null.</summary>
        public static string KindFor(Core2LogLine line, string playerId)
        {
            var e = line?.Event;
            if (e == null || e.Str(StageEvents.TargetId) != playerId) return null;
            if (line.Type == E.CombatGuardBroken) return StageReactions.GuardBreak;
            if (line.Type == E.AttackMissed && e.Str(F.MissReason) == V.Accuracy && (e.Is(F.EvadeActive) || (Js.IsNum(e[StageEvents.EvadeStacks]) && e.Num(StageEvents.EvadeStacks) > 0)))
                return StageReactions.Evade;
            var avoided = line.Type == E.CombatAvoidanceResolved && e.Is(F.Avoided);
            if (line.Type == E.AttackEvaded || (avoided && e.Obj(F.Evade)?.Is(F.Success) == true)) return StageReactions.Evade;
            if (avoided && e[F.Avoided].Type == JTokenType.Boolean && e.Str(StageEvents.Prevention) == StageEvents.Invulnerability) return StageReactions.Defend;
            if (line.Type == E.DamageDealt && e[StageEvents.IsAttack]?.Type == JTokenType.Boolean && e.Is(StageEvents.IsAttack)
                && !string.IsNullOrEmpty(e.Str(StageEvents.SourceId)) && e.Str(StageEvents.SourceId) != e.Str(StageEvents.TargetId))
            {
                var warded = Js.IsNum(e[StageEvents.WardResisted]) ? e.Num(StageEvents.WardResisted) : 0;
                var resisted = e[StageEvents.Resisted] == null ? warded : Js.IsNum(e[StageEvents.Resisted]) ? e.Num(StageEvents.Resisted) : double.NaN;
                if (Js.IsNum(e[StageEvents.Amount]) && Js.IsNum(e[F.Blocked]) && Finite(e.Num(StageEvents.Amount)) && Finite(e.Num(F.Blocked)) && Finite(resisted)
                    && e.Num(StageEvents.Amount) >= 0 && e.Num(F.Blocked) >= 0 && resisted >= 0
                    && e.Num(StageEvents.Amount) == e.Num(F.Blocked) && e.Num(StageEvents.Amount) + resisted > 0) return StageReactions.FullyDefended;
            }
            return null;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>
        /// The reactions a group of receipts plays (combat.js reactionForReceipt): each receipt its own, but not a guard break that comes after the blow that took the last of the player's Health (that
        /// beat is the fall). <paramref name="lines"/> are the receipts of the whole step, in order; <paramref name="playerDied"/> says the step ended the player.
        /// </summary>
        public static bool Defeated(string kind, Core2LogLine line, System.Collections.Generic.IReadOnlyList<Core2LogLine> lines, string playerId, bool playerDied)
        {
            if (kind != StageReactions.GuardBreak || !playerDied || lines == null) return false;
            var lethal = -1;
            for (var i = 0; i < lines.Count; i++) if (lines[i].Type == E.HpLost && lines[i].Event?.Str(StageEvents.TargetId) == playerId) lethal = i;
            var at = -1;
            for (var i = 0; i < lines.Count; i++) if (ReferenceEquals(lines[i], line)) at = i;
            return lethal >= 0 && at > lethal;
        }
    }
}
