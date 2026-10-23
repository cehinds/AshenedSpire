using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DN = Ashen.Generated.Core2DamageNumbers;
using DV = Ashen.Generated.Core2DamageValues;
using K = Ashen.Generated.CombatKeys;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;
using SK = Ashen.Generated.Core2StatusKeys;
using SV = Ashen.Generated.Core2StatusValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The card traits of reaction rules version 2 (docs/combat-card-traits-contract.md; web content/traitProjection.js, engine/combatCardTraitMath.js,
    /// engine/combatCardTactics.js counterstrikeEffects, engine/combatExpansionActions.js applyTraitImpact): the projection that gives a card its
    /// Reaction, Counterstrike, Impact and Flux traits by its tier, the Counterstrike percentage and amount, and what Impact (Poise) and Flux (Ward)
    /// add to every target a completed card hit. The card sets, tuning and advantage table are data (<c>rules/core2Combat.json</c> traits and the
    /// fight's reaction rules): no card is named here.
    /// </summary>
    public static class CoreCardTraits
    {
        // ------------------------------------------------------------------ the arithmetic (combatCardTraitMath.js)

        private static JObject RulesOf(CombatState c) => c.Fields.Obj(CK.ReactionRules) ?? c.CoreData.Section(CK.ReactionRules);

        /// <summary>stanceManeuverOf(entity, resolvingManeuver): the maneuver an enemy or player stands in: the resolving move, its stance, the shown intent.</summary>
        public static string ManeuverOf(JObject entity, string resolving = null)
        {
            if (entity == null) return null;
            if (resolving != null) return resolving;
            return entity.Obj(CK.CombatStance)?.Str(CK.Maneuver) ?? entity.Obj(K.Intent)?.Obj(CK.CombatProfile)?.Str(CK.Maneuver) ?? entity.Obj(CK.CombatProfile)?.Str(CK.Maneuver);
        }

        /// <summary>hasStanceAdvantage(ctx, playerManeuver, targetManeuver): the player's stance beats the target's in the contract's table.</summary>
        public static bool HasAdvantage(CombatState c, string playerManeuver, string targetManeuver) =>
            playerManeuver != null && targetManeuver != null && Js.Includes(RulesOf(c).Obj(DK.Advantage)?[playerManeuver], targetManeuver);

        /// <summary>counterstrikePct(ctx, playerManeuver, targetManeuver): the advantaged percentage when the target is known and beaten, else the base.</summary>
        public static double CounterstrikePct(CombatState c, string playerManeuver, string targetManeuver)
        {
            var rules = RulesOf(c).Obj(DK.Counterstrike);
            return HasAdvantage(c, playerManeuver, targetManeuver) ? rules.Num(Ashen.Generated.Core2ReactionRuleKeys.AdvantagedPct) : rules.Num(K.Pct);
        }

        /// <summary>counterstrikeAmount(amount, pct): a positive amount grows by max(floor(amount x pct), 1); zero, negatives and non-numbers are unchanged.</summary>
        public static JToken CounterstrikeAmount(JToken amount, double pct)
        {
            if (!Js.IsFinite(amount) || Js.D(amount) <= 0) return amount;
            var a = Js.D(amount);
            return Js.N(a + Math.Max(Math.Floor(a * pct), 1));
        }

        /// <summary>traitHitAmount(ownAmount, n): the card's own amount again (doubling it) when it has one, else N.</summary>
        public static double HitAmount(double ownAmount, double n) => ownAmount > 0 ? ownAmount : n > 0 ? n : 0;

        // ------------------------------------------------------------------ the projection (traitProjection.js)

        /// <summary>cardTier(def): the authored ability rank for graded cards, otherwise min(5, floor(rank / 2)).</summary>
        public static double Tier(JObject def)
        {
            if (def[Ashen.Generated.Core2CostKeys.GradeProfiles] is JArray) return Js.IsInt(def[CK.AbilityRank]) ? def.Num(CK.AbilityRank) : 0;
            var rank = Js.IsInt(def[DK.Rank]) ? def.Num(DK.Rank) : 0;
            return Math.Min(5, Math.Floor(rank / 2));
        }

        private static bool Listed(JObject sets, string key, string id) => Js.Includes(sets[key], id);

        private static bool HasEffect(JObject def, IEnumerable<string> ops, IEnumerable<string> targets = null) =>
            Js.Items(def[K.Effects]).OfType<JObject>().Any(e => ops.Contains(e.Str(K.Op)) && (targets == null || targets.Contains(e.Str(K.Target))));

        /// <summary>actualCardRank(def): a graded card's ability rank, else its rank (1 when unranked).</summary>
        public static double ActualRank(JObject def) => def[Ashen.Generated.Core2CostKeys.GradeProfiles] is JArray ? Js.Or0(def[CK.AbilityRank]) : Js.Nullish(def[DK.Rank]) ? 1 : def.Num(DK.Rank);

        /// <summary>picks(def, profile, tier, rules): { reaction, counterstrike, impact, flux } for a card; impact and flux are the N values.</summary>
        public static JObject Picks(CombatState c, JObject def, JObject profile, double tier, JObject rules)
        {
            var sets = c.CoreData.Section(CK.Traits);
            var id = def.Str(K.Id);
            var maneuver = profile.Str(CK.Maneuver);
            var camp = profile.Str(CK.Camp);
            var type = def.Str(K.Type);
            var rank = ActualRank(def);
            var found = new JObject();
            if (maneuver == CV.StanceCounter || def[CK.Evade] != null) found[CK.Reaction] = true;
            if (maneuver == CV.StanceSweep && rank >= 2) found[CK.Reaction] = true;
            if (maneuver == CV.StanceDefend && rank >= 1) found[CK.Reaction] = true;
            if (maneuver == CV.StanceCounter && !Listed(sets, SK.CounterNoStrike, id)) found[DK.Counterstrike] = true;
            if (Listed(sets, SK.SweepStrikeTier4, id) && tier >= 4) found[DK.Counterstrike] = true;
            var impactBase = false;
            if (maneuver == CV.StanceSmash) impactBase = true;
            else if (maneuver == CV.StanceCounter && camp == CV.CampPhysical && !Listed(sets, SK.CounterImpactExcluded, id)) impactBase = true;
            else if (Listed(sets, SK.CounterImpactSpell, id)) impactBase = true;
            else if (maneuver == CV.StanceAttack && camp == CV.CampPhysical && tier >= 3) impactBase = true;
            else if (maneuver == CV.StanceSweep && tier >= 2) impactBase = true;
            else if (maneuver == CV.StanceDefend && camp == CV.CampSpell && HasEffect(def, Js.Items(sets[SK.DefendBarrierEffects]).Select(Js.Str))) impactBase = true;
            else if (maneuver == CV.StanceDefend && Listed(sets, SK.DefendImpactTier4, id) && tier >= 4) impactBase = true;
            if (impactBase)
            {
                var impact = rules.Obj(K.Impact);
                found[K.Impact] = Js.N(impact.Obj(K.Base).Num(maneuver == CV.StanceSmash ? CV.StanceSmash : CK.Default) + Math.Floor(tier / 2) * impact.Num(Ashen.Generated.Core2ReactionRuleKeys.PerTwoTiers));
            }
            var flux = false;
            if (camp == CV.CampSpell && HasEffect(def, new[] { Op.Damage }, Js.Items(sets[SK.EnemyDamageTargets]).Select(Js.Str))) flux = true;
            if (Listed(sets, SK.CounterFluxWard, id)) flux = true;
            if (Listed(sets, SK.FluxTier2, id) && tier >= 2) flux = true;
            if (maneuver == CV.StanceAttack && camp == CV.CampPhysical && tier >= 3 && Js.Includes(sets[SK.FluxDamageTypes], profile.Str(CK.DamageType))) flux = true;
            if (camp == CV.CampSpell && type == CV.TypePower && tier >= 4) flux = true;
            if (flux)
            {
                var fluxRules = rules.Obj(DK.Flux);
                found[DK.Flux] = Js.N(fluxRules.Num(K.Base) + Math.Floor(tier / 2) * fluxRules.Num(Ashen.Generated.Core2ReactionRuleKeys.PerTwoTiers));
            }
            return found;
        }

        /// <summary>
        /// applyCardTraits(def, rules): the card with its trait tags and values, or the card itself when no trait applies. The input is never changed.
        /// <paramref name="baseTags"/> are the tag ids the card carries by its row when it has no <c>tags</c> of its own.
        /// </summary>
        public static JObject Apply(CombatState c, JObject def, JObject rules, IReadOnlyList<string> baseTags = null)
        {
            var profile = CoreStances.ProfileFor(c, def);
            var found = Picks(c, def, profile, Tier(def), rules);
            var keys = new[] { CK.Reaction, DK.Counterstrike, K.Impact, DK.Flux }.Where(key => found[key] != null && !(found[key].Type == JTokenType.Boolean && !found[key].Value<bool>())).ToList();
            if (keys.Count == 0) return def;
            var ids = keys.Select(key => CV.TagTrait + key).ToList();
            var tags = (Js.Nullish(def[K.Tags]) ? (baseTags ?? new List<string>()) : Js.Items(def[K.Tags]).Select(Js.Str)).ToList();
            var next = Js.Spread(def);
            next[K.Tags] = new JArray(tags.Concat(ids.Where(tag => !tags.Contains(tag))));
            if (def[CK.CardTags] is JArray cardTags) next[CK.CardTags] = new JArray(cardTags.Select(Js.Str).Concat(ids.Where(tag => !Js.Includes(cardTags, tag))));
            var values = new JObject();
            foreach (var key in keys) values[key] = found[key].DeepClone();
            next[DK.TraitValues] = values;
            return next;
        }

        // ------------------------------------------------------------------ Counterstrike on a played card

        /// <summary>The creature a Counterstrike card answers: the chosen enemy, else the attacker of the reaction in progress, else none.</summary>
        private static JObject CounterstrikeTarget(CombatState c, JObject target)
        {
            if (target?.Str(K.Kind) == V.Enemy) return target;
            var id = c.Fields[SK.ReactionSourceId];
            return Js.Nullish(id) ? null : c.Enemies.FirstOrDefault(e => e.Str(K.Id) == Js.Str(id));
        }

        /// <summary>
        /// counterstrikeEffects(ctx, source, target, carrier, effects, arm): a played card with the Counterstrike trait scales the amounts of its effects
        /// on a per-play copy (a formula grows through <c>traitBonus</c>). Counter cards are left to their return payload. Returns the effects to play.
        /// </summary>
        public static List<JObject> CounterstrikeEffects(CombatState c, JObject source, JObject target, JObject carrier, List<JObject> effects, bool arm)
        {
            if (c.CoreData.ReactionVersion != DN.ReactionStackVersion || carrier.Obj(DK.TraitValues)?[DK.Counterstrike]?.Type != JTokenType.Boolean || !carrier.Obj(DK.TraitValues).Is(DK.Counterstrike)) return effects;
            var profile = carrier.Obj(CK.CombatProfile);
            var cardManeuver = profile?.Str(CK.Maneuver);
            var setStance = arm && cardManeuver != null && !(profile[CK.StanceTrigger] != null && profile[CK.StanceTrigger].Type == JTokenType.Boolean && !profile.Is(CK.StanceTrigger));
            var playerManeuver = setStance ? cardManeuver : ManeuverOf(source);
            var enemy = CounterstrikeTarget(c, target);
            var targetManeuver = enemy != null ? ManeuverOf(enemy) : null;
            var pct = CounterstrikePct(c, playerManeuver, targetManeuver);
            var advantaged = HasAdvantage(c, playerManeuver, targetManeuver);
            carrier[SK.CounterstrikePct] = Js.N(pct);
            c.Emit(SV.CombatCounterstrikeApplied, Js.Obj(K.CardId, carrier[K.CardId], K.Pct, pct, SK.Advantaged, advantaged));
            var amountOps = Js.Items(c.CoreData.Section(CK.Traits)[SK.CounterstrikeAmountOps]).Select(Js.Str).ToList();
            return effects.Select(effect =>
            {
                if (!amountOps.Contains(effect.Str(K.Op)) || effect[K.Amount] == null) return effect;
                var scaled = Js.Spread(effect);
                scaled[K.Amount] = Formulas.IsFormula(effect[K.Amount]) ? Js.Obj(K.F, SV.TraitBonus, K.Base, effect[K.Amount], K.Pct, pct) : CounterstrikeAmount(effect[K.Amount], pct);
                return scaled;
            }).ToList();
        }

        // ------------------------------------------------------------------ Impact and Flux on a completed card

        /// <summary>trackOwnPressure(ctx, action, target, meter, amount): the Poise or Ward a card's own effect dealt to a target, kept on its group for Impact and Flux.</summary>
        public static void TrackOwnPressure(CombatState c, CombatAction action, JObject target, string meter, double amount)
        {
            var group = action.Meta?.Group;
            if (c.CoreData.ReactionVersion != DN.ReactionStackVersion || group == null || target == null || !(amount > 0)) return;
            var pressure = group.OwnPressure ??= new JObject();
            var key = target.Str(K.Id);
            var row = pressure.Obj(key);
            if (row == null) pressure[key] = row = Js.Obj(K.Id, target[K.Id], SK.PoiseDealt, 0, SK.WardDealt, 0);
            row.Put(meter, row.Num(meter) + amount);
        }

        /// <summary>traitLanded(row, own): a target counts as hit when a damage contact connected, or a status landed, or the card's own Poise or Ward reached it.</summary>
        private static bool Landed(TargetRow row, JObject own)
        {
            var receipt = row?.Receipt;
            if (receipt != null && row.Contacts.Any(contact => !contact.Is(DK.Effect))) return receipt.Is(DK.Connected);
            if (receipt != null && receipt.Num(DK.StatusAccepted) > 0) return true;
            return own.Num(SK.PoiseDealt) > 0 || own.Num(SK.WardDealt) > 0;
        }

        private static void DealFlux(CombatState c, CombatAction action, JObject carrier, JObject target, double amount, bool doubled)
        {
            if (!(amount > 0)) return;
            CoreStatusControl.SpendWard(c, target, amount, Js.Obj(K.SourceId, action.Source?[K.Id]));
            c.Emit(SV.CombatFluxApplied, Js.Obj(K.CardId, carrier[K.CardId], K.SourceId, action.Source?[K.Id], K.TargetId, target[K.Id], K.Amount, amount, SK.Doubled, doubled));
        }

        /// <summary>
        /// applyTraitImpact(ctx, action, group): a completed card's Impact adds Poise and its Flux adds Ward, once per target that was hit (a card
        /// with its own Poise or Ward doubles it; otherwise N). A Power with Flux deals its Ward to every living enemy when played.
        /// </summary>
        public static void ApplyImpact(CombatState c, CombatAction action, ExpansionGroup group)
        {
            var carrier = group.Carrier ?? new JObject();
            if (c.CoreData.ReactionVersion != DN.ReactionStackVersion || carrier.Is(DK.CombatReaction)) return;
            var values = carrier.Obj(DK.TraitValues);
            var impact = values?.Num(K.Impact) > 0 ? values.Num(K.Impact) : 0;
            var flux = values?.Num(DK.Flux) > 0 ? values.Num(DK.Flux) : 0;
            if (impact == 0 && flux == 0) return;
            var power = flux > 0 && carrier.Str(K.Type) == CV.TypePower;
            if (power)
                foreach (var enemy in c.Enemies.Where(e => e.Is(K.Alive))) DealFlux(c, action, carrier, enemy, flux, false);
            var keys = group.Targets.Keys.Concat((group.OwnPressure ?? new JObject()).Properties().Select(p => p.Name)).Distinct().ToList();
            foreach (var key in keys)
            {
                group.Targets.TryGetValue(key, out var row);
                var own = group.OwnPressure?.Obj(key) ?? Js.Obj(SK.PoiseDealt, 0, SK.WardDealt, 0);
                var target = Triggers.FindEntity(c, key);
                if (target == null || !target.Is(K.Alive) || !Landed(row, own)) continue;
                if (impact > 0)
                {
                    var amount = HitAmount(own.Num(SK.PoiseDealt), impact);
                    CoreImpact.DealPoiseDamage(c, target, amount, action.Source);
                    c.Emit(SV.CombatImpactApplied, Js.Obj(K.CardId, carrier[K.CardId], K.SourceId, action.Source?[K.Id], K.TargetId, target[K.Id], K.Amount, amount, SK.Doubled, own.Num(SK.PoiseDealt) > 0));
                }
                if (flux > 0 && !power) DealFlux(c, action, carrier, target, HitAmount(own.Num(SK.WardDealt), flux), own.Num(SK.WardDealt) > 0);
            }
        }
    }
}
