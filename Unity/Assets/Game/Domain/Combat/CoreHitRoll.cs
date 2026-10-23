using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using FK = Ashen.Generated.Core2FixCoreKeys;
using HK = Ashen.Generated.Core2HitKeys;
using HM = Ashen.Generated.Core2HitMessages;
using HV = Ashen.Generated.Core2HitValues;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>What a hit takes that a later work package owns, and how it is announced (see <see cref="CoreHitRoll"/>).</summary>
    public sealed class HitOptions
    {
        /// <summary>The target's Evade does not count: no stacks, no prepared charge, no advantage from it.</summary>
        public bool EvadeDisabled;

        /// <summary>effectiveEvasion's <c>baseEntity</c>: whose Dexterity and flat Evasion bonus the target's Evasion reads (the target itself when null).</summary>
        public JObject BaseEntity;

        /// <summary>
        /// engagement.rangeHitContact(ctx, source, target, carrier): the range row of this contact (<c>inRange</c>, <c>precisionDelta</c>).
        /// The engagement planner is WP-12; a fight that carries a range state must be handed the contact, a fight without one is not.
        /// </summary>
        public JObject RangeContact;

        /// <summary>proposalDamageType(carrier): the damage type the proposal rules match (derived with the damage receipt, WP-7).</summary>
        public string DamageType;

        /// <summary>Read the proposal capabilities of a legacy foundation (every element allowed): what the web's context does for a legacy profile.</summary>
        public bool OpenProposalCapabilities;

        /// <summary>options.suppressOutcomeEvent: resolve without announcing attackHit or attackMissed.</summary>
        public bool SuppressOutcomeEvent;
    }

    /// <summary>
    /// The Core 2 d20 hit (web engine/stanceHit.js attackHitPreview, resolveAttackHit, previewCardAttackHit, effectiveEvasion, evadeStackGain,
    /// precisionStatMix, precisionEntryGrant; engine/counterReturn.js attributesOf and levelOf): one seeded Accuracy decision gates a whole
    /// committed hostile action. Accuracy is Precision (the FixCore base, the attacker's stat mix times its Precision stacks plus one, a flat
    /// bonus, the maneuver's, the proposal rules' and the range contact's) and Evasion is the base, the target's Dexterity and its Evade
    /// stacks (each worth the base armour class plus Dexterity); the die needs <c>Evasion - Accuracy</c>. A natural 1 always misses and a
    /// natural 20 always hits (never against the strict enumeration: the preview and the roll agree). Advantage and disadvantage roll two
    /// dice. Everything here is a pure function of the fight's state except <see cref="Resolve"/>, which draws from the
    /// <c>combatAvoidance</c> stream, spends the Evade it contested and announces the outcome.
    /// <para>Only the FixCore path exists in a Core 2 run; the web's non-FixCore Accuracy (the Evasion table, Sweep's flat bonus) is refused
    /// by name. The fight's range contact and the damage type of the proposal rules come from work packages not yet ported and are
    /// handed in through <see cref="HitOptions"/>.</para>
    /// </summary>
    public static class CoreHitRoll
    {
        // ------------------------------------------------------------------ small readers

        /// <summary><c>Number(x) || 0</c>.</summary>
        private static double Num0(JToken value) => Js.IsNum(value) && !double.IsNaN(Js.D(value)) ? Js.D(value) : 0;

        private static JObject Rule(CombatState c) => c.CoreData.Section(HK.SectionHit);

        /// <summary>stacks(entity, id): the whole, non-negative stack count of a status (0 when it is absent).</summary>
        public static double Stacks(JObject entity, string id) => Math.Max(0, Num0(entity?.Obj(K.Statuses)?.Obj(id)?[K.Stacks]));

        private static bool Flag(JObject entity, string key) => entity != null && Js.Truthy(entity[key]);

        private static JObject Accuracy(CombatState c, JObject entity) => CoreFixCore.Family(c, CK.Accuracy, entity);

        private static void RequireFixCore(CombatState c)
        {
            if (!CoreFixCore.Enabled(c)) throw new NotSupportedException(HM.LegacyHitNotPorted);
        }

        private static bool Expanded(CombatState c) => Js.D(c.Fields[CK.CombatExpansionVersion]) == c.CoreData.ExpandedVersion;

        /// <summary>attributesOf(ctx, entity): the entity's own attributes, the fight's for the player, else none.</summary>
        public static JObject AttributesOf(CombatState c, JObject entity) =>
            entity?.Obj(K.Attributes) ?? (entity != null && entity.Str(K.Kind) == V.Player ? c.Attributes : null) ?? new JObject();

        /// <summary>levelOf(ctx, entity): the level object's or number, else the fight's character level for the player, else 0.</summary>
        public static double LevelOf(CombatState c, JObject entity)
        {
            if (entity == null) return 0;
            var own = entity[K.Level] is JObject level ? level[K.Level] : null;
            JToken value = !Js.Nullish(own) ? own : entity[K.Level];
            if (Js.Nullish(value) && entity.Str(K.Kind) == V.Player) value = c.Fields[CK.CharacterLevel];
            return Js.IsFinite(value) ? Js.D(value) : 0;
        }

        /// <summary>An enemy whose definition supplies no level profile is synthesized at level 1 for scaling elsewhere; the Precision mix counts it as level 0.</summary>
        private static double MixLevel(CombatState c, JObject entity)
        {
            var enemy = entity.Str(K.Kind) == V.Enemy;
            var id = entity.Str(K.EnemyId);
            var definition = enemy && id != null && c.Data.Enemies.Has(id) ? c.Data.Enemies.Get(id) : null;
            return enemy && !Js.Truthy(definition?[CK.LevelProfile]) ? 0 : LevelOf(c, entity);
        }

        // ------------------------------------------------------------------ precision and evade stacks

        /// <summary>precisionStatMix(ctx, entity, carrier): floor(0.5 DEX + 0.1 STR + 0.2 (WIS + INT) + level), the weights from <c>accuracy.precisionWeights</c>.</summary>
        public static double PrecisionStatMix(CombatState c, JObject entity, JObject carrier = null)
        {
            var weights = Accuracy(c, entity).Obj(CK.PrecisionWeights);
            var attributes = AttributesOf(c, entity);
            double Attr(string id) => Num0(attributes[id]);
            return Math.Floor(weights.Num(CK.WeightDex) * Attr(CK.AttrDexterity) + weights.Num(CK.WeightStr) * Attr(CK.AttrStrength)
                + weights.Num(CK.WeightWis) * Attr(CK.AttrWisdom) + weights.Num(CK.WeightInt) * Attr(CK.AttrIntelligence) + weights.Num(CK.WeightLevel) * MixLevel(c, entity));
        }

        /// <summary>precisionEntryGrant(ctx, entity, base, carrier): the Precision a stance entry grants under FixCore, the base grant plus the stat mix.</summary>
        public static double PrecisionEntryGrant(CombatState c, JObject entity, double baseGrant, JObject carrier = null) =>
            baseGrant > 0 ? baseGrant + PrecisionStatMix(c, entity, carrier) : 0;

        /// <summary>evadeStackGain(ctx, entity, carrier): the stacks one Evade play adds, <c>evadeStackBase + evadeStackDex x DEX</c> (0 without an entity).</summary>
        public static double EvadeStackGain(CombatState c, JObject entity, JObject carrier = null)
        {
            if (!CoreFixCore.Enabled(c) || entity == null) return 0;
            var cfg = Accuracy(c, entity);
            return Math.Floor(cfg.Num(FK.EvadeStackBase) + cfg.Num(FK.EvadeStackDex) * Num0(AttributesOf(c, entity)[CK.AttrDexterity]));
        }

        // ------------------------------------------------------------------ evade eligibility and the hit mode

        /// <summary>eligiblePreparedEvade(entity): the prepared Evade while it has charges, has not expired and its status is held; else null.</summary>
        public static JObject EligiblePreparedEvade(JObject entity)
        {
            var prepared = entity?.Obj(CK.CombatEvade);
            if (prepared == null || !(Js.D(prepared[CK.Charges]) > 0)) return null;
            var live = Js.Nullish(prepared[CK.ExpiresOnOwnerCycle]) || entity.Or0(CK.CombatOwnerCycle) < Js.D(prepared[CK.ExpiresOnOwnerCycle]);
            var held = !Js.Truthy(prepared[CK.WhileStatus]) || Stacks(entity, prepared.Str(CK.WhileStatus)) > 0;
            return live && held ? prepared : null;
        }

        /// <summary>eligibleEvadeMode(entity): the entity's pending next-Evade mode while it has Evade to apply it to.</summary>
        public static JObject EligibleEvadeMode(JObject entity)
        {
            var mode = entity?.Obj(HK.NextEvadeMode);
            var eligible = Stacks(entity, CV.StatusEvade) > 0 || EligiblePreparedEvade(entity) != null;
            return eligible && mode != null && (!Js.Truthy(mode[CK.WhileStatus]) || Stacks(entity, mode.Str(CK.WhileStatus)) > 0) ? mode : null;
        }

        /// <summary>hitMode(source, target, options): normal, advantage or disadvantage; the two sources cancel.</summary>
        public static string HitMode(JObject source, JObject target, bool evadeDisabled)
        {
            var pending = EligibleEvadeMode(target);
            var prepared = EligiblePreparedEvade(target);
            var advantage = Flag(source, HK.AccuracyAdvantage) || (!evadeDisabled && (Flag(target, HK.EvadeDisadvantage) || Flag(prepared, HK.Disadvantage) || Flag(pending, HK.Disadvantage)));
            var disadvantage = Flag(source, HK.AccuracyDisadvantage) || (!evadeDisabled && (Flag(target, HK.EvadeAdvantage) || Flag(prepared, HK.Advantage) || Flag(pending, HK.Advantage)));
            return advantage == disadvantage ? HV.ModeNormal : advantage ? HV.ModeAdvantage : HV.ModeDisadvantage;
        }

        /// <summary>diceHit(dice, need, mode, strictNatural): a natural die connects (any die unless strict); the kept die hits on 20, or when it is not 1 and reaches the need.</summary>
        public static bool DiceHit(CombatState c, IReadOnlyList<int> dice, double need, string mode, bool strictNatural)
        {
            var rule = Rule(c);
            var top = (int)rule.Num(HK.Die);
            if (!strictNatural && dice.Contains(top)) return true;
            var die = mode == HV.ModeDisadvantage ? dice.Min() : dice.Max();
            return die == top || (die != (int)rule.Num(HK.NaturalMiss) && die >= need);
        }

        // ------------------------------------------------------------------ evasion

        /// <summary>effectiveEvasion(ctx, entity, options): <c>evasionBase + DEX + flat bonus + stacks x (baseAC + DEX) + the prepared Evade's bonus</c>, all from the base entity's Dexterity.</summary>
        public static double EffectiveEvasion(CombatState c, JObject entity, bool evadeDisabled = false, JObject baseEntity = null)
        {
            RequireFixCore(c);
            baseEntity = baseEntity ?? entity;
            var prepared = !evadeDisabled ? EligiblePreparedEvade(entity) : null;
            var cfg = Accuracy(c, entity);
            var dex = Num0(AttributesOf(c, baseEntity)[CK.AttrDexterity]);
            var perStack = cfg.Num(FK.BaseAC) + dex;
            return cfg.Num(FK.EvasionBase) + dex + Num0(baseEntity[CK.BaseEvasionBonus])
                + (evadeDisabled ? 0 : perStack * Stacks(entity, CV.StatusEvade) + (prepared != null ? Num0(prepared[CK.Bonus]) : 0));
        }

        // ------------------------------------------------------------------ the preview

        /// <summary>
        /// attackHitPreview(ctx, source, target, carrier, options): the odds of a contact before anything is drawn, or null when the contact
        /// is not an opposed hostile one (a different combat expansion, no entity, the same side). Pure: the 20 (or 400) outcomes of the
        /// fixed defense are enumerated, not rolled.
        /// </summary>
        public static JObject Preview(CombatState c, JObject source, JObject target, JObject carrier, HitOptions options = null)
        {
            options = options ?? new HitOptions();
            if (!Expanded(c) || source == null || target == null || ReferenceEquals(source, target) || source.Str(K.Kind) == target.Str(K.Kind)) return null;
            RequireFixCore(c);
            carrier = carrier ?? new JObject();
            var profile = carrier.Obj(CK.CombatProfile) ?? new JObject();
            var tags = Js.Truthy(carrier[K.AuthoredTags]) ? carrier[K.AuthoredTags] : Js.Truthy(carrier[K.Tags]) ? carrier[K.Tags] : new JArray();
            var spell = Js.Includes(c.CoreData.Section(CK.Vocabulary)[CK.SpellCamps], profile.Str(CK.Camp));
            var proposal = CoreProposal.TargetPlan(c, source, target, carrier, options.DamageType, options.OpenProposalCapabilities);
            var bypass = proposal.Is(HK.Unavoidable) || (spell && Js.Includes(tags, HV.TagTarget));
            var range = Js.Truthy(profile[CK.Range]) ? profile.Str(CK.Range) : CV.RangeMelee;
            // A spell stance at Reach is delivery (how the caster casts), not a held distance, so melee still connects; a physical Reach
            // stance and any Far stance still hold distance.
            var stance = target.Obj(CK.CombatStance);
            var delivery = stance?.Str(CK.Camp) == CV.CampSpell && stance.Str(CK.Range) == CV.RangeReach;
            var requiredRange = Js.Truthy(carrier[HK.CounterRequiredRange]) ? carrier.Str(HK.CounterRequiredRange)
                : delivery ? CV.RangeMelee : Js.Truthy(stance?[CK.Range]) ? stance.Str(CK.Range) : CV.RangeMelee;
            var traits = profile[CK.Traits];
            var rangeOverride = (profile[HK.RangeOverride] != null && profile[HK.RangeOverride].Type == JTokenType.Boolean && profile[HK.RangeOverride].Value<bool>())
                || (profile[HK.AdequateReach] != null && profile[HK.AdequateReach].Type == JTokenType.Boolean && profile[HK.AdequateReach].Value<bool>())
                || Js.Includes(traits, HV.TraitAdequateReach);
            // Target bypasses Evasion only; its spell still needs sufficient reach.
            JObject contact = null;
            if (c.Fields[CK.RangeState] != null)
                contact = options.RangeContact ?? throw new NotSupportedException(HM.RangeContactNotPorted);
            var ranges = Js.Items(c.CoreData.Section(CK.Vocabulary)[CK.Ranges]).Select(Js.Str).ToList();
            var inRange = contact != null ? contact.Is(HK.InRange)
                : rangeOverride || (ranges.Contains(range) && ranges.Contains(requiredRange) && ranges.IndexOf(range) >= ranges.IndexOf(requiredRange));
            var mirrorProbe = new JObject { [CK.CombatProfile] = profile, [CK.Mirrors] = carrier[CK.Mirrors]?.DeepClone() };
            var maneuver = CoreStances.MirroredManeuver(c, mirrorProbe) ?? profile.Str(CK.Maneuver);
            var precision = Stacks(source, CV.StatusPrecision);
            var cfg = Accuracy(c, source);
            var maneuverBonus = maneuver != null ? Num0(cfg.Obj(FK.ManeuverPrecision)?[maneuver]) : 0;
            var accuracy = Math.Floor(cfg.Num(FK.PrecisionBase) + PrecisionStatMix(c, source, carrier) * (precision + 1) + Num0(source[CK.BasePrecisionBonus]) + maneuverBonus
                + proposal.Num(HK.AccuracyDelta) / Rule(c).Num(HK.ProposalAccuracyDivisor) + (contact != null ? Num0(contact[HK.PrecisionDelta]) : 0));
            var evadeStacks = options.EvadeDisabled ? 0 : Stacks(target, CV.StatusEvade);
            var evasion = Math.Floor(EffectiveEvasion(c, target, options.EvadeDisabled, options.BaseEntity));
            var need = evasion - accuracy;
            var evadeAttempted = inRange && !bypass && !options.EvadeDisabled && (evadeStacks > 0 || EligiblePreparedEvade(target) != null);
            var mode = HitMode(source, target, options.EvadeDisabled);
            var die = (int)Rule(c).Num(HK.Die);
            double hits = 0;
            double outcomes = mode == HV.ModeNormal ? die : die * die;
            if (inRange)
                for (var first = 1; first <= die; first++)
                {
                    if (mode == HV.ModeNormal) { if (bypass || DiceHit(c, new[] { first }, need, mode, true)) hits++; }
                    else for (var second = 1; second <= die; second++) if (bypass || DiceHit(c, new[] { first, second }, need, mode, true)) hits++;
                }
            // A shown percentage is whole, rounded down; advantage and disadvantage give quarters.
            var hitChance = Math.Floor(Rule(c).Num(HK.Percent) * hits / outcomes);
            var preview = new JObject
            {
                [CK.Range] = range, [HK.RequiredRange] = requiredRange, [HK.InRange] = inRange, [CK.Accuracy] = Js.N(accuracy), [CK.Evasion] = Js.N(evasion), [HK.Need] = Js.N(need),
                [HK.Bypass] = bypass, [HK.Opposed] = false, [HK.FixedDefense] = true, [CK.Mode] = mode, [HK.Advantage] = mode == HV.ModeAdvantage, [HK.Disadvantage] = mode == HV.ModeDisadvantage,
                [HK.EvadeStacks] = Js.N(evadeStacks), [HK.EvadeActive] = evadeAttempted, [HK.EvadeAttempted] = evadeAttempted, [HK.HitChance] = Js.N(hitChance), [HK.Chance] = Js.N(hitChance),
            };
            if (!inRange) preview[HK.MissReason] = HV.MissRange;
            return preview;
        }

        // ------------------------------------------------------------------ the roll

        /// <summary>The die a hit rolls: <c>combatAvoidance</c> on the fight's seeded streams.</summary>
        public static Func<int, int, int> StreamDie(CombatState c) => (low, high) => c.Rng.Int(Core2RngStream.CombatAvoidance, low, high);

        /// <summary>
        /// resolveAttackHit(ctx, source, target, carrier, options): the preview, then the dice (one, or two with advantage or disadvantage; none
        /// when the contact is out of range or cannot be avoided), the outcome, the Evade it spent and the attackHit or attackMissed event.
        /// A miss in range against Evade stacks consumes one stack; the prepared charge and the next-Evade mode are spent whenever dice were rolled
        /// against an attempted Evade. <paramref name="draw"/> is the die (low, high); the default draws on the <c>combatAvoidance</c> stream.
        /// </summary>
        public static JObject Resolve(CombatState c, JObject source, JObject target, JObject carrier, HitOptions options = null, Func<int, int, int> draw = null)
        {
            options = options ?? new HitOptions();
            var preview = Preview(c, source, target, carrier, options);
            if (preview == null) return null;
            draw = draw ?? StreamDie(c);
            var die = (int)Rule(c).Num(HK.Die);
            var inRange = preview.Is(HK.InRange);
            var bypass = preview.Is(HK.Bypass);
            var mode = preview.Str(CK.Mode);
            var dice = new List<int>();
            if (inRange && !bypass)
            {
                dice.Add(draw(1, die));
                if (mode != HV.ModeNormal) dice.Add(draw(1, die));
            }
            var attackDie = dice.Count > 0 ? (mode == HV.ModeDisadvantage ? dice.Min() : dice.Max()) : (int?)null;
            var hit = inRange && (bypass || DiceHit(c, dice, preview.Num(HK.Need), mode, preview.Is(HK.FixedDefense)));
            if (dice.Count > 0 && preview.Is(HK.EvadeAttempted))
            {
                if (EligibleEvadeMode(target) != null) target.Remove(HK.NextEvadeMode);
                // Prepared charges from older saves and dodge rolls use the same Accuracy decision.
                var prepared = EligiblePreparedEvade(target);
                if (prepared != null) prepared.Put(CK.Charges, prepared.Num(CK.Charges) - 1);
            }
            var result = Js.Spread(preview);
            result[HK.D20] = dice.Count > 0 ? Js.N(dice[0]) : Js.Null();
            result[HK.Dice] = new JArray(dice.Select(d => Js.N(d)));
            result[HK.Hit] = hit;
            if (preview.Is(HK.FixedDefense) && dice.Count > 0)
            {
                result[HK.AttackTotal] = Js.N(attackDie.Value + preview.Num(CK.Accuracy));
                result[HK.EvasionTotal] = preview[CK.Evasion].DeepClone();
            }
            if (Js.Truthy(source[CK.KnowledgeAction]) && source.Obj(CK.KnowledgeAction)?[CK.Serial] != null) result[HK.EnemyActionSerial] = source.Obj(CK.KnowledgeAction)[CK.Serial].DeepClone();
            if (!hit) result[HK.MissReason] = inRange ? HV.MissAccuracy : HV.MissRange;
            if (!hit && inRange && !bypass && preview.Num(HK.EvadeStacks) > 0) CoreStatuses.Remove(c, target, CV.StatusEvade, CV.ReasonConsumed, 1);
            var label = hit ? preview.Is(HK.EvadeActive) && !bypass ? HV.LabelHitEvadeFailed : HV.LabelHit : HV.LabelMissed;
            if (!options.SuppressOutcomeEvent)
            {
                var payload = new JObject();
                if (!Js.Nullish(source[K.Id])) payload[K.SourceId] = source[K.Id].DeepClone();
                if (!Js.Nullish(target[K.Id])) payload[K.TargetId] = target[K.Id].DeepClone();
                if (carrier?[K.CardId] != null) payload[K.CardId] = carrier[K.CardId].DeepClone();
                if (carrier?[K.MoveId] != null) payload[K.MoveId] = carrier[K.MoveId].DeepClone();
                foreach (var p in result.Properties()) payload[p.Name] = p.Value.DeepClone();
                payload[HK.Label] = label;
                c.Emit(hit ? HV.EventAttackHit : HV.EventAttackMissed, payload);
            }
            return result;
        }

        // ------------------------------------------------------------------ the selected card

        /// <summary>
        /// previewCardAttackHit(ctx, source, target, carrier, options): a selected card's Accuracy after the stance entry it would make
        /// (the entry's Precision grant, stat mix included), without changing the live state.
        /// </summary>
        public static JObject PreviewCard(CombatState c, JObject source, JObject target, JObject carrier, HitOptions options = null)
        {
            if (!Expanded(c) || source == null || Js.Truthy(carrier?[HK.CounterReturnAttack])) return Preview(c, source, target, carrier, options);
            var profile = Js.Spread(carrier?.Obj(CK.CombatProfile));
            var evade = profile.Obj(CK.Evade);
            if (Js.Truthy(evade?[CK.WhileStatus])) profile[HK.EvadeEligible] = Stacks(source, evade.Str(CK.WhileStatus)) > 0;
            var selected = Js.Spread(carrier);
            selected[CK.CombatProfile] = profile;
            var next = CoreStances.StanceOf(c, selected, source);
            if (next == null) return Preview(c, source, target, carrier, options);
            var grants = !CoreStances.SameStance(c, source, selected) ? CoreStances.EntryBonuses(c, selected) : new JObject { [CK.Precision] = 0 };
            if (CoreFixCore.Enabled(c)) grants[CK.Precision] = Js.N(PrecisionEntryGrant(c, source, grants.Num(CK.Precision), selected));
            var projected = (JObject)source.DeepClone();
            projected[CK.CombatStance] = next;
            if (grants.Num(CK.Precision) > 0)
            {
                var statuses = projected.Obj(K.Statuses);
                if (statuses == null) projected[K.Statuses] = statuses = new JObject();
                var precision = Js.Spread(source.Obj(K.Statuses)?.Obj(CV.StatusPrecision));
                precision.Put(K.Stacks, Stacks(source, CV.StatusPrecision) + grants.Num(CK.Precision));
                statuses[CV.StatusPrecision] = precision;
            }
            return Preview(c, projected, target, selected, options);
        }
    }
}
