using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DM = Ashen.Generated.Core2DamageMessages;
using DV = Ashen.Generated.Core2DamageValues;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.Core2MatchupKeys;
using MM = Ashen.Generated.Core2MatchupMessages;
using MN = Ashen.Generated.Core2MatchupNumbers;
using MV = Ashen.Generated.Core2MatchupValues;
using V = Ashen.Generated.CombatValues;
using SK = Ashen.Generated.Core2StatusKeys;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// What a stance does to the stance across from it (web engine/combatMatchups.js, engine/combatCardTactics.js, the Smash and Poise lanes of
    /// engine/actions.js), for the configured Core 2 profile: whether an armed Counter answers an incoming contact (preparedCounter,
    /// counterCoverageMatches, counterCoversAttack), the receipt the committed action keeps of it (the contacts it covers, the Counter being
    /// spent the moment an attack that is not avoided meets it), the Guard break of a Smash, a Smash against a spell stance (its Poise and Ward
    /// pooled and split by the stance rules' Ward share), Ward damage and the round-start decay of Evade. The damage chain (Sweep and Smash
    /// advantage, the defensive stance) is <see cref="CoreDamage"/>; the Counter's reply is <see cref="CoreCounterReturn"/>.
    /// </summary>
    public static class CoreMatchups
    {
        private static double Number0(JToken t) => Js.IsFinite(t) ? Math.Max(0, Js.D(t)) : 0;

        // ------------------------------------------------------------------ profiles and traits

        /// <summary>combatProfileOf(carrier): the carrier's profile when its camp is one a combat profile may carry.</summary>
        public static JObject CombatProfileOf(JObject carrier)
        {
            var profile = carrier?.Obj(CK.CombatProfile);
            return profile != null && CoreSets.CampsWithProfile.Contains(profile.Str(CK.Camp)) ? profile : null;
        }

        /// <summary>The names of a carrier's tags, ids first (a tag is its id or a row with one).</summary>
        private static List<string> TagIds(JObject card) => CoreStances.TagIds(card) ?? new List<string>();

        /// <summary>isSpellCounter(card): a Spell that carries Counter coverage is a Counter in Core; it keeps the prepared stance.</summary>
        public static bool IsSpellCounter(JObject card)
        {
            var profile = card?.Obj(CK.CombatProfile) ?? new JObject();
            var tags = TagIds(card);
            var stanceTrigger = card?[MK.StanceTrigger] as JObject;
            string maneuver = Js.Truthy(stanceTrigger?[CK.Stance]) ? stanceTrigger.Str(CK.Stance)
                : Js.Truthy(profile[CK.Maneuver]) ? profile.Str(CK.Maneuver)
                : Js.Truthy(card?[CK.Maneuver]) ? card.Str(CK.Maneuver)
                : tags.FirstOrDefault(tag => tag != null && tag.StartsWith(CV.TagManeuver, StringComparison.Ordinal))?.Substring(CV.TagManeuver.Length);
            var camp = !Js.Nullish(profile[CK.Camp]) ? profile.Str(CK.Camp) : tags.Contains(CV.TagCamp + CV.CampSpell) ? CV.CampSpell : null;
            return maneuver == CV.StanceCounter && camp == CV.CampSpell && (Js.Truthy(card?[CK.CounterCoverage]) || Js.Truthy(profile[CK.CounterCoverage]));
        }

        /// <summary>armsAsCoreCounter(subject, card): the stances module includes Counter and the card is a Counter by stance, or a spell Counter with coverage.</summary>
        public static bool ArmsAsCoreCounter(CombatState c, JObject card)
        {
            var rule = c.CoreData.Configured(CK.StancesModule);
            if (!Js.Includes(rule?[CK.Include], CV.StanceCounter)) return false;
            var stance = CoreStances.ConfiguredStance(c, card);
            if (stance == CV.StanceCounter) return true;
            return IsSpellCounter(card) && Js.Includes(rule[CK.Include], stance);
        }

        // ------------------------------------------------------------------ the event carrier

        /// <summary>eventCarrier(source, carrier): the fields of a Counter event that name who and what (undefined fields are left out, as JSON does).</summary>
        public static JObject EventCarrier(JObject source, JObject carrier)
        {
            var body = new JObject();
            void Put(string key, JToken value) { if (value != null && value.Type != JTokenType.Null && value.Type != JTokenType.Undefined) body[key] = value.DeepClone(); }
            // JSON drops a field the web leaves undefined; here the field is present and null, so it still overrides what the executing action's card
            // would decorate the event with (the web's { ...decoration, ...payload } does the same with undefined).
            void PutOrNull(string key, JToken value) => body[key] = value != null && value.Type != JTokenType.Undefined ? value.DeepClone() : Js.Null();
            Put(MK.SourceKind, source?[K.Kind]);
            PutOrNull(K.CardId, carrier?[K.CardId]);
            Put(K.EnemyId, source?[K.EnemyId]);
            Put(K.MoveId, carrier?[K.MoveId]);
            if (Js.Truthy(carrier?[CK.CombatProfile])) Put(CK.CombatProfile, carrier[CK.CombatProfile]);
            if (Js.Truthy(carrier?[DK.CommittedInstance])) Put(DK.CardInstance, carrier[DK.CommittedInstance]);
            if (Js.IsInt(carrier?[MK.UpcastTier])) Put(MK.UpcastTier, carrier[MK.UpcastTier]);
            PutOrNull(DK.CardInstanceId, carrier?[K.InstanceId]);
            PutOrNull(DK.CardType, carrier?[K.Type]);
            body[DK.CardTags] = (Js.Truthy(carrier?[DK.AuthoredTags]) ? carrier[DK.AuthoredTags] : Js.Truthy(carrier?[K.Tags]) ? carrier[K.Tags] : new JArray()).DeepClone();
            PutOrNull(DK.AbilityKind, carrier?[DK.AbilityKind]);
            return body;
        }

        // ------------------------------------------------------------------ coverage

        private static bool Lists(JToken values, JToken value) => values is JArray array && !Js.Nullish(value) && array.Any(item => JToken.DeepEquals(item, value));

        /// <summary>counterCoverageMatches(coverage, profile, effect): every filter the coverage declares must match; Area is never inferred from the target count.</summary>
        public static bool CounterCoverageMatches(JObject coverage, JObject profile, string effect = null)
        {
            if (coverage == null || profile == null) return false;
            effect ??= MV.EffectDamage;
            foreach (var pair in new[] { (CK.Camps, CK.Camp), (CK.Schools, CK.School), (CK.Ranges, CK.Range), (CK.Targeting, CK.Targeting), (CK.Maneuvers, CK.Maneuver) })
            {
                // A Counter reply keeps its stance for matchup math; its delivery is an Attack for authored incoming-maneuver coverage.
                var value = pair.Item2 == CK.Maneuver && profile.Is(DK.CounterReturnAttack) && profile.Str(CK.Maneuver) == CV.StanceCounter ? Js.S(CV.StanceAttack) : profile[pair.Item2];
                var values = coverage[pair.Item1] as JArray;
                if (values != null && values.Count > 0 && !Lists(values, value)) return false;
            }
            var effects = coverage[K.Effects] as JArray;
            if (effects != null && effects.Count > 0 && !Js.Includes(effects, effect)) return false;
            if (profile.Str(CK.Targeting) == CV.TargetingArea && !Js.Includes(coverage[CK.Targeting], CV.TargetingArea)) return false;
            if (coverage[MK.RequiredTraits] is JArray required && required.Any(trait => !CoreTactical.HasTrait(profile, Js.Str(trait)))) return false;
            if (coverage[MK.ExcludedTraits] is JArray excluded && excluded.Any(trait => CoreTactical.HasTrait(profile, Js.Str(trait)))) return false;
            return true;
        }

        /// <summary>counterCoversAttack(ctx, def, profile, effect): a card whose maneuver is Counter answers an attack its coverage reaches (SPEC 4.10).</summary>
        public static bool CounterCoversAttack(CombatState c, JObject def, JObject profile, string effect = null)
        {
            var tags = TagIds(def);
            if (!tags.Contains(CV.TagManeuver + CV.StanceCounter)) return false;
            var mode = tags.Contains(CV.TagCounter + CV.CampSpell) ? CV.CampSpell : tags.Contains(CV.TagCounter + CV.StanceRanged) ? CV.StanceRanged : CV.RangeMelee;
            var coverage = def[CK.CounterCoverage] as JObject ?? c.Fields.Obj(CK.CombatExpansionRules)?.Obj(CK.Matchups)?.Obj(CK.Counter)?.Obj(CK.DefaultCoverage)?.Obj(mode);
            return CounterCoverageMatches(coverage, profile, effect);
        }

        // ------------------------------------------------------------------ the prepared Counter

        /// <summary>
        /// preparedCounter(ctx, target, carrier, options): the Counter the target holds ready for this carrier, if it answers: one with a charge left,
        /// not held down by a control status, standing in the Counter stance (or, for a spell Counter, the stance it entered, no card played since),
        /// and not past the owner cycle it was armed for.
        /// </summary>
        public static JObject PreparedCounter(CombatState c, JObject target, JObject carrier)
        {
            var counter = target?.Obj(CK.CombatCounter);
            if (counter == null || !Js.Truthy(counter[CK.Charges])) return null;
            var manual = carrier?[MK.ManualCounterSerial];
            if (carrier.Is(DK.CombatReaction) && !(Js.IsNum(manual) && Js.IsNum(counter[CK.Serial]) && Js.D(manual) == Js.D(counter[CK.Serial]))) return null;
            if (CoreControl.Locked(c, target)) return null;
            var stance = CoreDamage.ActiveCombatStances(c, target).Any(row => row.Str(CK.Maneuver) == CV.StanceCounter) ? CV.StanceCounter : target.Obj(CK.CombatStance)?.Str(CK.Maneuver);
            var held = counter.Obj(CK.Carrier);
            var authoredCounter = held?.Obj(CK.CombatProfile)?.Str(CK.Maneuver) == CV.StanceCounter;
            // Core: a spell Counter arms while its owner still stands in the stance the card entered (prepared).
            var coreSpell = CoreFixCore.Enabled(c) && IsSpellCounter(held) && ArmsAsCoreCounter(c, held);
            // Any card accepted after a spell Counter was armed (Spell, Skill, Strike, Evade) disarms it, whatever stance that card leaves.
            if (coreSpell && Js.Or0(counter[CK.ArmedAtCardCount]) != target.Obj(K.Counters).Or0(K.CardsPlayedThisCombat)) return null;
            var spellCounter = coreSpell && stance == CoreStances.ConfiguredStance(c, held);
            var stancesOff = c.CoreData.Configured(CK.StancesModule) == null;
            if (!stancesOff && stance != CV.StanceCounter && !(stance == CV.StanceEvade && authoredCounter) && !spellCounter) return null;
            if (counter[CK.ExpiresOnOwnerCycle] != null && target.Or0(CK.CombatOwnerCycle) >= counter.Num(CK.ExpiresOnOwnerCycle)) return null;
            return counter;
        }

        /// <summary>What a committed action's receipt keeps of the target's Counter: the Counter itself, which contacts it covers, and whether it was held down at the start.</summary>
        public sealed class Cover
        {
            public JObject Counter;
            public List<bool> Covered = new List<bool>();
            public bool Eligible;
            public bool DisabledAtStart;
        }

        /// <summary>The coverage half of previewTacticalAction: a contact is covered when the Counter's coverage reaches its profile and it carries damage (or, for a status contact, pressure).</summary>
        public static Cover CoverContacts(CombatState c, JObject target, JObject carrier, IReadOnlyList<JObject> profiles, IReadOnlyList<JObject> contacts)
        {
            var counter = PreparedCounter(c, target, carrier);
            var cover = new Cover { DisabledAtStart = CoreControl.Locked(c, target) };
            for (var index = 0; index < profiles.Count; index++)
            {
                var profile = profiles[index];
                var effect = Js.Truthy(profile[DK.Effect]) ? profile.Str(DK.Effect) : MV.EffectDamage;
                var carries = profile.Str(DK.Effect) == MV.EffectStatus
                    ? Number0(!Js.Nullish(contacts[index][DK.Pressure]) ? contacts[index][DK.Pressure] : contacts[index][CK.Amount]) > 0
                    : Number0(contacts[index][CK.Amount]) > 0;
                cover.Covered.Add(CounterCoverageMatches(counter?.Obj(CK.Coverage), profile, effect) && carries);
            }
            cover.Eligible = counter != null && cover.Covered.Any(b => b);
            cover.Counter = cover.Eligible ? (JObject)counter.DeepClone() : null;
            return cover;
        }

        /// <summary>beginTacticalAction, after the action's one d20: an attack that is not avoided spends the Counter it met and says so.</summary>
        public static void ConsumeCounter(CombatState c, JObject source, JObject target, JObject receipt)
        {
            if (!receipt.Is(DK.CounterEligible) || receipt.Is(DK.Avoided)) return;
            var counter = receipt.Obj(CK.Counter);
            target.Obj(CK.CombatCounter).Put(CK.Charges, 0);
            var body = EventCarrier(target, counter.Obj(CK.Carrier));
            body[K.SourceId] = target[K.Id]?.DeepClone();
            if (source?[K.Id] != null) body[K.TargetId] = source[K.Id].DeepClone();
            body[CK.Serial] = counter[CK.Serial]?.DeepClone();
            c.Emit(MV.EventCombatCounterConsumed, body);
        }

        // ------------------------------------------------------------------ Smash: the Guard break and the spell stance

        /// <summary>isSmashGuardBreak(receipt, target): the one Smash Guard-break test, a Smash that connects and leaves the target with no Block.</summary>
        public static bool IsSmashGuardBreak(JObject receipt, JObject target)
        {
            if (receipt == null) return false;
            var smash = receipt[MK.Smash];
            var connected = Js.Nullish(receipt[DK.Connected]) || Js.Truthy(receipt[DK.Connected]);
            return smash != null && smash.Type == JTokenType.Boolean && smash.Value<bool>() && connected && Number0(receipt[MK.GuardBefore]) > 0 && Number0(target?[K.Block]) <= 0;
        }

        /// <summary>completeGuardBreak(ctx, source, target, carrier, receipt): a Smash that breaks the target's last Guard, reported even when the hit killed the target (a dead target takes no bonus Poise).</summary>
        public static void CompleteGuardBreak(CombatState c, JObject source, JObject target, JObject carrier, JObject receipt)
        {
            if (!IsSmashGuardBreak(receipt, target)) return;
            var amount = !Js.Nullish(carrier?.Obj(CK.CombatProfile)?[MK.BreakPoiseBonus]) ? carrier.Obj(CK.CombatProfile)[MK.BreakPoiseBonus]
                : c.Fields.Obj(CK.CombatExpansionRules)?.Obj(CK.Matchups)?.Obj(MK.Smash)?[MK.GuardBreakPoiseBonus];
            var poise = Number0(amount);
            c.Emit(MV.EventCombatGuardBroken, new JObject { [K.TargetId] = target[K.Id].DeepClone(), [K.SourceId] = source?[K.Id]?.DeepClone(), [CK.Poise] = Js.N(poise) });
            if (Js.IsNum(amount) && Js.D(amount) > 0 && target.Is(K.Alive))
            {
                var card = Js.Spread(carrier);
                card[DK.CombatReaction] = true;
                c.Enqueue(new CombatAction { Effect = new JObject { [K.Op] = Ashen.Generated.CombatOps.PoiseDamage, [CK.Amount] = amount.DeepClone() }, Source = source, Owner = source, Target = target, Card = card });
            }
        }

        /// <summary>allocateInteger(total, weights): the largest-remainder split of a whole budget, stable by index.</summary>
        public static double[] AllocateInteger(double total, IReadOnlyList<double> weights)
        {
            if (weights.Count == 0) return new double[0];
            var sum = weights.Sum();
            if (sum <= 0) throw new InvalidOperationException(MM.AllocationNeedsWeight);
            var whole = Math.Floor(total);
            var raw = weights.Select(w => whole * w / sum).ToArray();
            var result = raw.Select(Math.Floor).ToArray();
            var order = raw.Select((v, i) => (i, fraction: v - result[i])).OrderByDescending(x => x.fraction).ThenBy(x => x.i).ToList();
            var remainder = whole - result.Sum();
            foreach (var item in order) if (remainder-- > 0) result[item.i]++;
            return result;
        }

        /// <summary>spendPersistentWard on a Core 2 body: Ward is off, so the pool holds nothing to spend. A body with Ward in it is not a Core 2 body.</summary>
        public static double SpendWard(CombatState c, JObject entity, double amount)
        {
            var ward = entity?.Obj(CK.PersistentWard);
            if (ward == null || !(amount > 0) || !(ward.Num(K.Value) > 0)) return 0;
            throw new NotSupportedException(MM.WardNotPorted);
        }

        /// <summary>
        /// splitSmashPressure(ctx, action, target, row): a Smash against a caster's stance pools every Poise and Ward contact of the card once and
        /// splits it by the stance rules' Ward share (the rest of its Poise and Ward effects then do nothing). True once the pressure is pooled.
        /// </summary>
        public static bool SplitSmashPressure(CombatState c, CombatAction action, JObject target, TargetRow row)
        {
            if (action.Card?.Obj(CK.CombatProfile)?.Str(CK.Maneuver) != CV.StanceSmash || !CoreSets.CastingManeuvers.Contains(target.Obj(CK.CombatStance)?.Str(CK.Maneuver)) || row == null) return false;
            var group = action.Meta.Group;
            var key = target.Str(K.Id);
            group.SmashPressureApplied ??= new List<string>();
            if (group.SmashPressureApplied.Contains(key)) return true;
            group.SmashPressureApplied.Add(key);
            var total = row.Contacts.Where(contact => contact.Str(DK.Effect) == DV.EffectImpact).Sum(contact => Math.Max(0, contact.Or0(DK.Pressure)));
            var amount = Math.Ceiling(total * CoreTactical.StancePressureMultiplier(c, action.Source, target, action.Card, row.Receipt?.Obj(DK.CounterAttack)));
            var wardShare = CoreStances.Rules(c).Num(MK.SpellSmashWardShare);
            var split = AllocateInteger(amount, new[] { 1 - wardShare, wardShare });
            if (split[0] > 0) CoreImpact.DealPoiseDamage(c, target, split[0]);
            if (split[1] > 0) SpendWard(c, target, split[1]);
            return true;
        }

        /// <summary>
        /// The wardDamage opcode: Ward is off in a Core 2 run, so it drains Poise instead, with the multipliers the Ward lane carries (the stance pressure,
        /// Piercing's edge against Ward on a spell).
        /// </summary>
        public static void RunWardDamage(CombatState c, CombatAction action, JObject eff)
        {
            foreach (var t in Actions.ResolveTargets(c, action, eff.Str(K.Target)))
            {
                TargetRow row = null;
                action.Meta?.Group?.Targets.TryGetValue(t.Str(K.Id), out row);
                if (row?.Receipt?.Is(DK.Avoided) == true) continue;
                if (SplitSmashPressure(c, action, t, row)) continue;
                var profile = action.Card?.Obj(CK.CombatProfile);
                var edge = !action.Card.Is(MK.CombatWardEdgeApplied) && profile?.Str(CK.Camp) == CV.CampSpell && profile.Str(CK.DamageType) == MV.DamagePiercing ? MN.PiercingWardEdge : 1;
                var amount = Actions.EvalNum(c, action, eff[CK.Amount], 0, t) * CoreTactical.StancePressureMultiplier(c, action.Source, t, action.Card, row?.Receipt?.Obj(DK.CounterAttack)) * edge;
                if (CoreFixCore.WardDisabled(c) && t.Obj(CK.PersistentPoise) != null)
                {
                    var drained = CoreImpact.DrainPoise(c, action.Source, t, amount);
                    CoreCardTraits.TrackOwnPressure(c, action, t, SK.PoiseDealt, drained);
                    // creditKnowledgeImpact: a reply the player's Counter makes that drains an enemy's Poise pays the Perception of the action it answered (WP-11).
                    if (drained > 0 && action.Meta?.CombatCounterReaction == true && action.Source?.Str(K.Kind) == V.Player && t.Str(K.Kind) == V.Enemy && c.Fields[CK.EnemyKnowledge] != null)
                        throw new NotSupportedException(DM.KnowledgeCreditNotPorted);
                    continue;
                }
                var spent = CoreStatusControl.SpendWard(c, t, amount, Js.Obj(K.SourceId, action.Source?[K.Id]));
                CoreCardTraits.TrackOwnPressure(c, action, t, SK.WardDealt, spent.Num(DK.Spent));
            }
        }

        // ------------------------------------------------------------------ the round boundary

        /// <summary>
        /// decayStanceRoundStart(ctx): one shared round boundary. Every living creature loses the stance rules' round-start decay of the statuses it
        /// holds (Evade by one). The web runs it for the fights that do not use FixCore; a Core 2 run never calls it.
        /// </summary>
        public static void DecayStanceRoundStart(CombatState c)
        {
            if (Js.D(c.Fields[CK.CombatExpansionVersion]) != c.CoreData.ExpandedVersion || c.Turn <= 1) return;
            var rows = new List<JObject> { c.Player };
            rows.AddRange(c.Enemies);
            var decay = CoreStances.Rules(c).Obj(MK.RoundStartDecay);
            foreach (var entity in rows)
            {
                if (!entity.Is(K.Alive) || decay == null) continue;
                foreach (var pair in decay.Properties().ToList())
                    if (entity.Obj(K.Statuses)?.Obj(pair.Name)?.Num(K.Stacks) > 0) CoreStatuses.Remove(c, entity, pair.Name, MV.ReasonRoundStart, Js.D(pair.Value));
            }
        }
    }
}
