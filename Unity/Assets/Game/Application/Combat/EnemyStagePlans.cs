using System;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;

namespace Ashen.App.Combat
{
    /// <summary>What an enemy's figure does for a beat: the pose its stage plays, the family and motion of its action and the actor's tempo and reach (ui/screens/combat.js animateActor).</summary>
    public sealed class EnemyAction
    {
        public string EnemyId;
        public string MoveId;
        public string Kind;
        public EnemyMoveAction Move;

        /// <summary>enemyActionPose: attack, magicAttack, projectile, sweep, guard or buff.</summary>
        public string Pose;

        /// <summary>The motion of the action's family (sweep, lunge, impact, release, cast, brace, sidestep).</summary>
        public string Motion;

        public double Tempo;
        public double Reach;
    }

    /// <summary>
    /// How an enemy's figure plays a beat (src/ui/screens/combat.js animateActor and playFamilyAnimation, model/enemyActionPose.js): the move a beat starts or a counter returns, the pose the stage plays for it,
    /// how long the action takes (the pace's lunge scaled by the actor's tempo) and when its blow lands (the windup share of it).
    /// </summary>
    public static class EnemyStagePlans
    {
        /// <summary>The action of an enemy's beat, or null when the beat starts no move (the default swing or guard plays then).</summary>
        public static EnemyAction ActionOf(EnemyStageData data, FxBeat beat, string defId)
        {
            if (data == null || beat == null || defId == null) return null;
            var returned = beat.Events.FirstOrDefault(l => l.Type == FxEvents.CombatCounterTriggered && l.Event?.Str(CK.SourceKind) == CombatValues.Enemy);
            var started = beat.Events.FirstOrDefault(l => l.Type == FxEvents.EnemyMoveStarted || l.Type == FxEvents.CombatHitStarted || l.Type == FxEvents.CombatContactPrepared);
            var moved = returned ?? started;
            if (moved?.Event == null) return null;
            var kind = returned != null ? EnemyKinds.Attack : moved.Event.Str(FxKeys.Kind);
            var moveId = moved.Event.Str(FxKeys.MoveId);
            var move = data.Move(defId, moveId);
            var actor = data.Actor(defId);
            return new EnemyAction
            {
                EnemyId = defId,
                MoveId = moveId,
                Kind = kind,
                Move = move,
                Pose = data.ActionPose(kind, move),
                Motion = move.Family != null && data.Motion.TryGetValue(move.Family, out var motion) ? motion : null,
                Tempo = actor.Tempo,
                Reach = actor.Reach,
            };
        }

        /// <summary>playFamilyAnimation's timing: the pace's lunge scaled by the actor's tempo and where the blow lands (the windup share of it).</summary>
        public static StageTiming Timing(EnemyAction action, double lungeMs, double windupFraction)
        {
            var total = Math.Floor(lungeMs * (action?.Tempo ?? 1) + P1Math.Half);
            return new StageTiming { TotalMs = total, ImpactMs = Math.Floor(total * windupFraction + P1Math.Half) };
        }
    }
}
