using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using IK = Ashen.Generated.IntentKeys;
using K = Ashen.Generated.CombatKeys;

namespace Ashen.App.Combat
{
    /// <summary>What the player's card stage is told about a card: the action it plays, the rest it leaves the figure in and the stance that holds the figure while the card is selected or was the last one played.</summary>
    public sealed class CardStagePlan
    {
        /// <summary>cardActionFor: attack, smash, sweep, counter, defend, evade, spell, ranged or rangedMagic.</summary>
        public string Technique;

        /// <summary>The clip the stage plays for it: a Counter card prepares (counterPrepare), the rest are their own name (resolveCombatAnimation).</summary>
        public string Play;

        /// <summary>cardActionPlan: the rest a guarding action leaves the figure in (defend, counter, evade), else null.</summary>
        public string Rest;

        /// <summary>stanceForCard: the family of held frame (offensive, defensive, casting, counter, sweep, smash).</summary>
        public string Stance;

        /// <summary>selectedWeaponStanceFor with a card: the stance the Reaver's greatsword pilot holds (a guard is defend, a pure dodge is evade).</summary>
        public string PilotStance;

        /// <summary>selectedWeaponCounterFor: the pilot's return for a Counter card (counter-guard or counter-evade), or null.</summary>
        public string PilotCounter;

        /// <summary>The authored maneuver of the card's profile (kindForCard), which names the intent glyph over the figure; null for a card with none.</summary>
        public string Maneuver;
    }

    /// <summary>
    /// The card actions of the card stage (src/model/alternativeCardAnimation.js cardActionFor and cardActionPlan, model/alternativeStance.js stanceForCard, model/selectedWeaponStances.js
    /// selectedWeaponStanceFor and selectedWeaponCounterFor): read from the resolved card the way the web reads it, compared with the web for every profile card by the stage oracle (D-1490).
    /// The class's starter hands are what the web paints (DEFAULT_COMBAT_WEAPONS), so equipment never changes the choice.
    /// </summary>
    public static class CardStagePlans
    {
        private static readonly string[] KnownKinds =
        {
            StageTags.KindAttack, CV.KindSkill, CV.KindPower, StageTags.KindCurse, StageTags.KindStatus,
        };

        /// <summary>The plan of a resolved card definition (its tags the resolved ones), for a class the stage paints.</summary>
        public static CardStagePlan For(CombatState c, JObject card)
        {
            var profile = CoreStances.ProfileFor(c, card);
            var tags = CoreStances.TagIds(card) ?? new List<string>();
            var technique = Technique(c, card, profile, tags);
            var guard = technique == StageActions.Defend || technique == StageActions.Counter || technique == StageActions.Evade;
            var stance = Stance(c, card, profile, tags);
            return new CardStagePlan
            {
                Technique = technique,
                Play = technique == StageActions.Counter ? StageNames.CounterPrepare : technique,
                Rest = guard ? technique : null,
                Stance = stance,
                PilotStance = PilotStance(profile, card, stance),
                PilotCounter = PilotCounter(profile, tags),
                Maneuver = profile.Str(CK.Maneuver),
            };
        }

        /// <summary>
        /// playerActionIntent (ui/components/combatantOverhead.js): the glyph of the figure's intent for a maneuver; a ranged card shows the attack, and spells, untyped cards and the resting mark
        /// (no maneuver) show the prepare hourglass.
        /// </summary>
        public static string IntentGlyph(string maneuver)
        {
            if (maneuver == CV.StanceDefend) return IK.IconDefend;
            if (maneuver == CV.StanceCounter) return IK.IconCounter;
            if (maneuver == CV.StanceSmash) return IK.IconSmash;
            if (maneuver == CV.StanceSweep) return IK.IconSweep;
            if (maneuver == CV.StanceAttack || maneuver == CV.ManeuverRanged) return IK.IconAttack;
            return IK.IconPrepare;
        }

        private static bool PureDodge(JObject card)
        {
            if (card.Is(StageTags.WeightClassPriced) && card[StageTags.WeightClassPriced].Type == JTokenType.Boolean && card.Value<bool>(StageTags.WeightClassPriced)) return true;
            var effects = Js.Items(card[K.Effects]).ToList();
            return effects.Count > 0 && effects.All(e => Js.Str(Js.Get(e, K.Op)) == StageTags.DodgeRoll);
        }

        private static bool IsAttackKind(JObject card)
        {
            foreach (var id in Js.Items(card[StageTags.KindIds]).Select(Js.Str))
                if (KnownKinds.Contains(id)) return id == StageTags.KindAttack;
            return false;
        }

        private static string Technique(CombatState c, JObject card, JObject profile, List<string> tags)
        {
            var maneuver = profile.Str(CK.Maneuver);
            var camp = profile.Str(CK.Camp);
            var spell = camp == CV.CampSpell || tags.Contains(StageTags.SourceSpell);
            if (CoreStances.PreparationCategory(c, card) != null) return StageActions.Spell;
            if (maneuver == CV.ManeuverRanged) return spell ? StageActions.RangedMagic : StageActions.Ranged;
            if (camp == CV.CampPhysical && PureDodge(card)) return StageActions.Evade;
            if (maneuver == CV.StanceCounter || maneuver == CV.StanceDefend) return maneuver;
            if (spell) return StageActions.Spell;
            if (maneuver == CV.StanceAttack || maneuver == CV.StanceSmash || maneuver == CV.StanceSweep) return maneuver;
            if (tags.Contains(StageTags.Ranged) || tags.Contains(StageTags.Bow)) return StageActions.Ranged;
            if (tags.Contains(StageTags.Guard) || tags.Contains(StageTags.Block)) return StageActions.Defend;
            return IsAttackKind(card) ? StageActions.Attack : StageActions.Spell;
        }

        private static string Stance(CombatState c, JObject card, JObject profile, List<string> tags)
        {
            var maneuver = profile.Str(CK.Maneuver);
            if (CoreStances.PreparationCategory(c, card) != null) return StageActions.Casting;
            if (maneuver == CV.StanceCounter) return StageActions.Counter;
            if (maneuver == CV.StanceDefend || maneuver == StageActions.Defending) return StageActions.Defensive;
            if (profile.Str(CK.Camp) == CV.CampSpell) return StageActions.Casting;
            if (maneuver == CV.StanceSweep) return StageActions.Sweep;
            if (maneuver == CV.StanceSmash) return StageActions.Smash;
            if (maneuver == CV.StanceAttack || maneuver == CV.StanceSmash || maneuver == CV.StanceSweep || maneuver == CV.ManeuverRanged
                || maneuver == StageActions.Attacking || maneuver == StageActions.Smashing || maneuver == StageActions.Sweeping) return StageActions.Offensive;
            if (tags.Contains(StageTags.SourceSpell) || card.Str(StageTags.AbilityKind) == StageTags.TypeSpell || card.Str(K.Type) == StageTags.TypeSpell) return StageActions.Casting;
            if (tags.Contains(StageTags.Guard) || tags.Contains(StageTags.Block)) return StageActions.Defensive;
            if (card.Str(K.Type) == StageTags.TypeAttack) return StageActions.Offensive;
            return StageActions.Casting;
        }

        /// <summary>preparesEvade: a physical pure dodge, or a defend that rolls a dodge or carries an Evade.</summary>
        private static bool PreparesEvade(JObject profile, JObject card)
        {
            if (profile.Str(CK.Camp) != CV.CampPhysical) return false;
            if (PureDodge(card)) return true;
            return profile.Str(CK.Maneuver) == CV.StanceDefend
                && (Js.Items(card[K.Effects]).Any(e => Js.Str(Js.Get(e, K.Op)) == StageTags.DodgeRoll) || Js.Truthy(profile[StageTags.Evade]));
        }

        private static string PilotStance(JObject profile, JObject card, string stance)
        {
            var held = stance == StageActions.Defensive || stance == StageActions.Defend || stance == StageActions.Immune ? StageActions.Defend : stance;
            return PreparesEvade(profile, card) ? StageActions.Evade : held;
        }

        private static string PilotCounter(JObject profile, List<string> tags)
        {
            if (profile.Str(CK.Camp) != CV.CampPhysical) return null;
            if (tags.Contains(StageTags.Guard) || tags.Contains(StageTags.ClassificationGuard)) return StageActions.CounterGuard;
            if (tags.Contains(StageTags.UtilityEvasion)) return StageActions.CounterEvade;
            return null;
        }

        /// <summary>
        /// The stance the pilot holds when no card is selected and none was played: Evade ready (an Evade on the entity, or a prepared dodge that has not lapsed) holds the evade stance, else
        /// the stance the figure is in (selectedWeaponStanceFor without a card; combatTacticsDisplay.js preparedEvadeCharges). A fallen figure keeps its stance.
        /// </summary>
        public static string PilotEntityStance(JObject entity, string stance)
        {
            if (entity == null || (entity[StageTags.EntityAlive] != null && !entity.Is(StageTags.EntityAlive))) return stance;
            var held = stance == StageActions.Defensive || stance == StageActions.Defend || stance == StageActions.Immune ? StageActions.Defend : stance;
            return EvadeReady(entity) ? StageActions.Evade : held;
        }

        private static bool EvadeReady(JObject entity) =>
            Statuses.Stacks(entity, CV.StatusEvade) > 0 || PreparedEvadeCharges(entity) > 0;

        private static double PreparedEvadeCharges(JObject entity)
        {
            var prepared = entity.Obj(StageTags.CombatEvade);
            if (prepared == null || !(prepared.Num(StageTags.Charges) > 0)) return 0;
            var gate = prepared.Str(StageTags.WhileStatus);
            if (gate != null)
            {
                var status = entity.Obj(StageTags.Statuses)?.Obj(gate);
                if (status == null || !(status.Num(StageTags.Stacks) > 0) || status.Num(StageTags.Duration) == 0 && status[StageTags.Duration] != null) return 0;
            }
            var expires = prepared[StageTags.ExpiresOnOwnerCycle];
            if (!Js.Nullish(expires) && entity.Or0(StageTags.CombatOwnerCycle) >= Js.D(expires)) return 0;
            return System.Math.Floor(prepared.Num(StageTags.Charges));
        }
    }
}
