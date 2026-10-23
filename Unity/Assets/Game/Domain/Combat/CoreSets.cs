using System;
using System.Collections.Generic;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using DV = Ashen.Generated.Core2DamageValues;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;
using SV = Ashen.Generated.Core2StatusValues;

namespace Ashen.Domain.Combat
{
    /// <summary>The closed sets the committed-action machinery tests membership of, built once from the generated ids.</summary>
    public static class CoreSets
    {
        private static HashSet<string> Of(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

        /// <summary>The camp names that read as a spell.</summary>
        public static readonly HashSet<string> SpellCamps = Of(CV.CampSpell, DV.CampSpells, DV.CampMagic, DV.CampMagical);

        /// <summary>The camp names a combat profile may carry.</summary>
        public static readonly HashSet<string> CampsWithProfile = Of(CV.CampPhysical, CV.CampSpell, DV.CampSpells, DV.CampMagic, DV.CampMagical);

        /// <summary>The damage types a sweep or a holy blow reveals a decoy with.</summary>
        public static readonly HashSet<string> RevealTypes = Of(DV.DamageSacred, DV.DamageHoly);

        /// <summary>The target selectors that name a creature to plan a contact with.</summary>
        public static readonly HashSet<string> TargetedSpecs = Of(DV.TargetEnemy, DV.TargetAllEnemies, DV.TargetOtherEnemies, V.TargetRandomEnemy, V.RefPlayer);

        /// <summary>The ops that always plan a contact.</summary>
        public static readonly HashSet<string> TargetedOps = Of(Op.RemoveStatus, DV.OpLoseHp, DV.OpStagger);

        /// <summary>The discard reasons that count toward the ability riders.</summary>
        public static readonly HashSet<string> DiscardReasons = Of(DV.ReasonEffect, DV.ReasonChosen, DV.ReasonRandom);

        /// <summary>The ops a played card may not carry yet: the move of the engagement (WP-12) and the arcane buildup.</summary>
        public static readonly HashSet<string> DeferredOps = Of(SV.MoveEngagement, Op.ArcaneBuildup);

        /// <summary>The ops a played card may carry (damage, Block and Barrier, and the Poise and Ward a Smash or a Counter deals).</summary>
        public static readonly HashSet<string> CardOps = Of(Op.Damage, Op.Block, CV.OpGainBarrier, Op.PoiseDamage, DV.OpWardDamage);

        /// <summary>The target selectors that name a hostile creature.</summary>
        public static readonly HashSet<string> HostileSpecs = Of(DV.TargetEnemy, DV.TargetAllEnemies, V.TargetRandomEnemy, DV.TargetOtherEnemies);

        /// <summary>The maneuvers a caster stands in.</summary>
        public static readonly HashSet<string> CastingManeuvers = Of(DV.ManeuverCasting, DV.ManeuverSpell);

        /// <summary>The ops that act on their owner when written without a target.</summary>
        public static readonly HashSet<string> OwnerOps = Of(DV.OpDraw, DV.OpDiscard, DV.OpExhaust, DV.OpAddCard, DV.OpGainEnergy, DV.OpRetain, DV.OpGrantCardCharge, DV.OpShuffleDiscardIntoDraw, DV.OpEnterStance, DV.OpGrantRollMode);

        /// <summary>The ops that plan a contact whatever their target.</summary>
        public static readonly HashSet<string> PlannedOps = Of(Op.Damage, CV.OpBuildup, Op.ApplyStatus, Op.PoiseDamage, DV.OpWardDamage);
    }
}
