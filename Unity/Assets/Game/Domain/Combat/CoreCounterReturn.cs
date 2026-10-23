using System;
using Ashen.Generated;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DM = Ashen.Generated.Core2DamageMessages;
using DN = Ashen.Generated.Core2DamageNumbers;
using DV = Ashen.Generated.Core2DamageValues;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.Core2MatchupKeys;
using MM = Ashen.Generated.Core2MatchupMessages;
using MN = Ashen.Generated.Core2MatchupNumbers;
using MV = Ashen.Generated.Core2MatchupValues;
using Op = Ashen.Generated.CombatOps;
using RK = Ashen.Generated.Core2ReactionKeys;
using RM = Ashen.Generated.Core2ReactionMessages;
using RV = Ashen.Generated.Core2ReactionValues;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The Core 2 Counter arithmetic (web engine/counterRule.js, engine/counterReturn.js; owner ruling 2026-10-19, SPEC 17). One home for the live
    /// return, the Block it adds and the target preview: a Counter returns half of the damage the hit it covered would have done, Poise worth a
    /// fifth of that return, and, when its stance beats the incoming maneuver, a quarter of the incoming damage more, Poise worth half the
    /// return and a quarter of the incoming damage as Block before the hit is absorbed. The fractions are the FixCore rules' <c>counter</c> block
    /// over the defaults of <c>rules/core2Combat.json</c>. "Incoming" is the attack's damage after its whole chain, before Block, Barrier or Poise
    /// take anything: the <c>incoming</c> figure of the damage receipt.
    /// </summary>
    public static class CoreCounterReturn
    {
        /// <summary>What a Counter returns against one hit: Health damage, Poise damage and, when it has the advantage, the Block it adds.</summary>
        public sealed class Return
        {
            public double Hp;
            public double Poise;
            public double Block;

            public JObject ToJson() => new JObject { [K.Hp] = Js.N(Hp), [CK.Poise] = Js.N(Poise), [K.Block] = Js.N(Block) };
        }

        /// <summary>counterRuleActive(ctx): the Core 2 Counter rule governs a FixCore fight.</summary>
        public static bool RuleActive(CombatState c) => CoreFixCore.Enabled(c);

        /// <summary>fixCoreCounterRules(rules): the snapshot's <c>counter</c> block over the shipped defaults.</summary>
        public static JObject Rules(CombatState c) => Js.Spread(c.CoreData.Section(MK.SectionMatchups).Obj(MK.CounterDefaults), CoreFixCore.Rules(c).Obj(CK.Counter));

        /// <summary>counterBeats(ctx, incomingManeuver): a Counter stance beats the incoming maneuver in the stances module's matchup table.</summary>
        public static bool Beats(CombatState c, string incomingManeuver) =>
            !string.IsNullOrEmpty(incomingManeuver) && Js.Includes(c.CoreData.Configured(CK.StancesModule)?.Obj(CK.Matchups)?[CK.Counter], incomingManeuver);

        /// <summary>counterReturn(ctx, incoming, advantaged): { hp, poise, block } a Counter returns against one hit of <paramref name="incoming"/> damage.</summary>
        public static Return Of(CombatState c, double incoming, bool advantaged)
        {
            var cfg = Rules(c);
            var whole = Math.Max(0, Math.Floor(double.IsNaN(incoming) ? 0 : incoming));
            var hp = Math.Floor(cfg.Num(MK.ReturnFraction) * whole) + (advantaged ? Math.Floor(cfg.Num(MK.AdvantageIncomingFraction) * whole) : 0);
            return new Return
            {
                Hp = hp,
                Poise = Math.Floor((advantaged ? cfg.Num(MK.AdvantagePoiseFraction) : cfg.Num(MK.PoiseFraction)) * hp),
                Block = advantaged ? Math.Floor(cfg.Num(MK.AdvantageBlockFraction) * whole) : 0,
            };
        }

        /// <summary>Number(token) || 0.</summary>
        private static double Num0(JToken token) => Js.IsNum(token) && !double.IsNaN(Js.D(token)) ? Js.D(token) : 0;

        /// <summary>counterReturnForReceipt(ctx, receipt): the return of an executed action, summed over the contacts the Counter covered.</summary>
        public static Return ForReceipt(CombatState c, JObject receipt)
        {
            var total = new Return();
            var covered = receipt.Arr(DK.Covered) ?? new JArray();
            for (var index = 0; index < covered.Count; index++)
            {
                if (!Js.Truthy(covered[index])) continue;
                var incomings = receipt.Arr(DK.Incomings);
                var amounts = receipt.Arr(DK.Amounts);
                var seen = incomings != null && index < incomings.Count && !Js.Nullish(incomings[index]) ? incomings[index]
                    : amounts != null && index < amounts.Count && !Js.Nullish(amounts[index]) ? amounts[index] : null;
                var maneuver = receipt.Arr(DK.Profiles) is JArray profiles && index < profiles.Count ? (profiles[index] as JObject)?.Str(CK.Maneuver) : null;
                var row = Of(c, Num0(seen), Beats(c, maneuver));
                total.Hp += row.Hp;
                total.Poise += row.Poise;
                total.Block += row.Block;
            }
            return total;
        }

        /// <summary>counterReturnSpent(entity): an automatic return shares the owner-cycle budget.</summary>
        public static bool Spent(JObject entity)
        {
            var spent = entity?[MK.CombatCounterReturnedCycle];
            return Js.IsNum(spent) && Js.D(spent) == entity.Or0(CK.CombatOwnerCycle);
        }

        /// <summary>paidCounterContact(ctx, carrier, counter): a paid per-contact reply carries the serial of the preparation it answers.</summary>
        public static bool PaidContact(CombatState c, JObject carrier, JObject counter)
        {
            var manual = carrier?[MK.ManualCounterSerial];
            return c.Fields.Obj(CK.ReactionRules)?.Str(DK.ContactPolicy) == DV.PolicyPerHit && Js.IsInt(manual) && Js.IsNum(counter?[CK.Serial]) && Js.D(manual) == Js.D(counter[CK.Serial]);
        }

        /// <summary>counterBlockGrant(ctx, entity, amount): the part of <paramref name="amount"/> Block the entity can take under its blockCap (Unbreakable).</summary>
        public static double BlockGrant(CombatState c, JObject entity, double amount)
        {
            var cap = CoreActions.StatusCap(c, entity, K.BlockCap);
            return cap == null ? amount : Math.Max(0, Math.Min(amount, cap.Value - entity.Or0(K.Block)));
        }

        /// <summary>counterBlockBonus(ctx, receipt, index, incoming): the Block a covered, advantaged hit adds to the Counter's owner before it is absorbed.</summary>
        public static double BlockBonus(CombatState c, JObject receipt, int index, JToken incoming)
        {
            var covered = receipt?.Arr(DK.Covered);
            if (!RuleActive(c) || !receipt.Is(DK.CounterEligible) || receipt.Is(DK.Avoided) || covered == null || index >= covered.Count || !Js.Truthy(covered[index])) return 0;
            var profiles = receipt.Arr(DK.Profiles);
            var maneuver = profiles != null && index < profiles.Count ? (profiles[index] as JObject)?.Str(CK.Maneuver) : null;
            if (!Beats(c, maneuver)) return 0;
            return Of(c, Num0(incoming), true).Block;
        }

        /// <summary>counterAttackPreview(ctx, source, target, carrier): the Accuracy check a Counter reply would make (null for anything else).</summary>
        public static JObject AttackPreview(CombatState c, JObject source, JObject target, JObject carrier)
        {
            if (Js.D(c.Fields[CK.CombatExpansionVersion]) != c.CoreData.ExpandedVersion || !carrier.Is(DK.CounterReturnAttack)) return null;
            return CoreHitRoll.Preview(c, source, target, carrier, HitOptionsFor(c, source, target, carrier));
        }

        /// <summary>resolveCounterAttack(ctx, source, target, carrier): the one Accuracy decision that gates the whole reply (null for anything else).</summary>
        public static JObject ResolveAttack(CombatState c, JObject source, JObject target, JObject carrier, Func<int, int, int> draw = null)
        {
            if (Js.D(c.Fields[CK.CombatExpansionVersion]) != c.CoreData.ExpandedVersion || !carrier.Is(DK.CounterReturnAttack)) return null;
            return CoreHitRoll.Resolve(c, source, target, carrier, HitOptionsFor(c, source, target, carrier), draw);
        }

        private static HitOptions HitOptionsFor(CombatState c, JObject source, JObject target, JObject carrier) =>
            new HitOptions { RangeContact = CoreRange.HitContact(c, source, target, carrier), DamageType = CoreDamage.ProposalDamageType(c, carrier) };

        // ------------------------------------------------------------------ the reply

        private static bool SameNumber(JToken a, JToken b) => Js.IsNum(a) && Js.IsNum(b) && Js.D(a) == Js.D(b);

        private static List<JObject> Profiles(JObject receipt) => (receipt.Arr(DK.Profiles) ?? new JArray()).OfType<JObject>().ToList();

        /// <summary>schoolReturnMultiplier(ctx, counter, incoming): a school's edge or weakness against what it meets (legacy Counters; a Core 2 return reads only its Ward edge).</summary>
        private static double SchoolReturnMultiplier(CombatState c, JObject counter, JObject incoming)
        {
            var school = counter.Obj(CK.Carrier)?.Obj(CK.CombatProfile)?.Str(CK.School);
            var cfg = c.Fields.Obj(CK.CombatExpansionRules)?.Obj(CK.Matchups)?.Obj(CK.Counter);
            var match = school != null ? cfg?.Obj(MK.SchoolMatchups)?.Obj(school) : null;
            if (match == null || !counter.Is(MK.SchoolEffect)) return 1;
            bool Check(string prefix) =>
                Js.Includes(match[prefix + MK.SchoolsSuffix], incoming.Str(CK.School)) || Js.Includes(match[prefix + MK.DamageTypesSuffix], incoming.Str(CK.DamageType))
                || Js.Includes(match[prefix + MK.RangesSuffix], incoming.Str(CK.Range)) || Js.Includes(match[prefix + MK.TargetingSuffix], incoming.Str(CK.Targeting))
                || (match[prefix + MK.TraitsSuffix] is JArray traits && traits.Any(trait => CoreTactical.HasTrait(incoming, Js.Str(trait))));
            if (Check(MK.WeakPrefix)) return cfg.Num(MK.SchoolWeakMultiplier);
            return Check(MK.StrongPrefix) ? cfg.Num(MK.SchoolStrongMultiplier) : 1;
        }

        /// <summary>
        /// completeTacticalAction (the Counter's half): once the action's contacts have resolved, a Counter the action met answers it. The return is the
        /// SPEC 17 fraction of what each covered contact would have done, one return per owner cycle, aimed at the attacker through the range quote of the
        /// Counter's own reach. A Counter spent by a Poise break on the covered hit still answers that hit.
        /// </summary>
        public static void Complete(CombatState c, JObject source, JObject target, JObject carrier, JObject receipt)
        {
            var counter = receipt.Obj(CK.Counter);
            var stillPrepared = counter != null && (SameNumber(target?.Obj(CK.CombatCounter)?[CK.Serial], counter[CK.Serial])
                || (RuleActive(c) && SameNumber(target?[DK.CombatCounterStaggered], counter[CK.Serial])));
            target?.Remove(DK.CombatCounterStaggered);
            var profiles = Profiles(receipt);
            if (!stillPrepared || CoreControl.Locked(c, target) || receipt.Is(DK.CounterDisabledAtStart) || receipt.Is(DK.Avoided) || receipt.Is(MK.CounterBroken)
                || profiles.Any(p => CoreTactical.HasTrait(p, MV.TraitUnreflectable)) || !target.Is(K.Alive) || !source.Is(K.Alive)) return;
            if (!RuleActive(c)) throw new NotSupportedException(MM.LegacyCounterNotPorted);
            if (Spent(target) && !PaidContact(c, carrier, counter)) return;
            var held = counter.Obj(CK.Carrier);
            var deferred = c.Fields.Obj(CK.RangeState)?.Obj(CK.Policy)?[CK.Enabled] is JToken enabled && enabled.Type == JTokenType.Boolean && enabled.Value<bool>();
            JObject returnCarrier = held;
            if (!deferred)
            {
                returnCarrier = AuthorizeRange(c, target, source, held, carrier);
                if (returnCarrier == null) return;
                target.Put(MK.CombatCounterReturnedCycle, target.Or0(CK.CombatOwnerCycle));
            }
            // Reaction rules version 2: Counterstrike, Impact and Flux are card traits (WP-9).
            var trait = c.CoreData.ReactionVersion == DN.ReactionStackVersion ? held?.Obj(DK.TraitValues) ?? new JObject() : new JObject();
            double? strike = trait.Is(DK.Counterstrike) ? CoreCardTraits.CounterstrikePct(c, CV.StanceCounter, CoreCardTraits.ManeuverOf(source, receipt.Obj(DK.Profile)?.Str(CK.Maneuver))) : (double?)null;
            var covered = receipt.Arr(DK.Covered) ?? new JArray();
            bool Covered(int index) => index < covered.Count && Js.Truthy(covered[index]);
            // Core 2 (SPEC 17): the return is a fraction of the damage that hit, not the card's printed numbers; a spell Counter returns Health and Poise only.
            var core = ForReceipt(c, receipt);
            var hp = core.Hp;
            var poise = core.Poise;
            var ward = CoreMatchups.IsSpellCounter(held) ? 0 : Js.IsFinite(counter.Obj(CK.Payload)?[CK.Ward]) ? Math.Max(0, Js.D(counter.Obj(CK.Payload)[CK.Ward])) : 0;
            // Counterstrike scales the authored Ward first (the Core rule fixes Health and Poise); Flux doubles the final Ward, or gives N when it has none.
            if (strike != null) ward = Js.D(CoreCardTraits.CounterstrikeAmount(Js.N(ward), strike.Value));
            var heldProfile = held?.Obj(CK.CombatProfile);
            var magical = heldProfile != null && CoreSets.SpellCamps.Contains(heldProfile.Str(CK.Camp));
            var edges = profiles.Where((p, index) => Covered(index)).Select(profile => SchoolReturnMultiplier(c, counter, profile)).ToList();
            var schoolEdge = edges.Any(edge => edge < 1) ? edges.Min() : Math.Max(1, edges.Count > 0 ? edges.Max() : 1);
            var pierceWard = magical && heldProfile.Str(CK.DamageType) == MV.DamagePiercing;
            var wardEdge = counter.Str(MK.SchoolEffect) == CK.Ward && schoolEdge != 1 ? schoolEdge : pierceWard ? MN.PiercingWardEdge : 1;
            ward *= wardEdge;
            if (!CoreMatchups.IsSpellCounter(held) && trait.Num(DK.Flux) > 0) ward = ward > 0 ? CombatMath.Two * ward : trait.Num(DK.Flux);
            var body = CoreMatchups.EventCarrier(target, held);
            body[K.SourceId] = target[K.Id].DeepClone();
            body[K.TargetId] = source[K.Id].DeepClone();
            body[K.Amount] = Js.N(hp);
            body[DK.PoiseDamage] = Js.N(poise);
            body[CK.WardDamage] = Js.N(ward);
            body[MK.WardOnly] = magical && hp == 0;
            if (deferred)
            {
                // No offensive action exists after rounding: nothing is reserved and the owner's cycle is not spent.
                if (!(hp > 0 || poise > 0 || ward > 0)) { c.Emit(MV.EventCombatCounterTriggered, body); return; }
                returnCarrier = AuthorizeRange(c, target, source, held, carrier);
                if (returnCarrier == null) return;
                target.Put(MK.CombatCounterReturnedCycle, target.Or0(CK.CombatOwnerCycle));
            }
            var ranges = Js.Items(c.CoreData.Section(CK.Vocabulary)[CK.Ranges]).Select(Js.Str).ToList();
            var reach = profiles.Where((p, index) => Covered(index)).Aggregate(Js.Truthy(receipt.Obj(DK.Profile)?[CK.Range]) ? receipt.Obj(DK.Profile).Str(CK.Range) : CV.RangeMelee,
                (range, profile) => ranges.IndexOf(profile.Str(CK.Range)) > ranges.IndexOf(range) ? profile.Str(CK.Range) : range);
            var depth = Math.Max(held.Or0(DK.ReactionDepth), carrier?.Or0(DK.ReactionDepth) ?? 0);
            var queue = new List<CombatAction>();
            void Enqueue(JObject effect)
            {
                var card = Js.Spread(returnCarrier);
                card[DK.CombatReaction] = true;
                card[DK.SkipRatingBonus] = true;
                card[DK.CounterReturnAttack] = true;
                card[Ashen.Generated.Core2HitKeys.CounterRequiredRange] = reach;
                card[DK.ReactionDepth] = Js.N(depth);
                if (effect.Str(K.Op) == DV.OpWardDamage) card[MK.CombatWardEdgeApplied] = true;
                queue.Add(new CombatAction { Effect = effect, Source = target, Owner = target, Target = source, Card = card, Meta = new ActionMeta { CombatCounterReaction = true, EnemyKnowledgeActionSerial = source.Obj(Ashen.Generated.Core2CombatKeys.KnowledgeAction)?[Ashen.Generated.Core2CombatKeys.Serial]?.DeepClone() } });
            }
            if (hp > 0) Enqueue(new JObject { [K.Op] = Op.Damage, [CK.Amount] = Js.N(hp) });
            if (poise > 0) Enqueue(new JObject { [K.Op] = Op.PoiseDamage, [CK.Amount] = Js.N(poise) });
            if (ward > 0) Enqueue(new JObject { [K.Op] = DV.OpWardDamage, [CK.Amount] = Js.N(ward) });
            if (queue.Count > 0)
            {
                var group = CoreExpansion.Enqueue(c, queue, target, source, queue[0].Card);
                if (group != null) group.CounterReturn = body;
            }
            else c.Emit(MV.EventCombatCounterTriggered, body);
        }

        // ------------------------------------------------------------------ the reach of the reply

        /// <summary>quoteCounterReturn(ctx, source, target, carrier, options): the range quote of a reply, priced as the Counter's own damage, Poise and (for a physical Counter that carries Ward) Ward.</summary>
        public static JObject QuoteReturn(CombatState c, JObject source, JObject target, JObject carrier, JObject context, bool preview, string route = null)
        {
            var card = Js.Spread(carrier);
            card[DK.CounterReturnAttack] = true;
            foreach (var key in new[] { DK.RangePlan, MK.RangeLineagePlan, DK.RangeCounterReservation, DK.RangeReturnAuthorization }) card.Remove(key);
            var profile = carrier.Obj(CK.CombatProfile);
            var ward = profile?.Str(CK.Camp) != CV.CampSpell && (carrier.Obj(DK.TraitValues)?.Num(DK.Flux) > 0 || source.Obj(CK.CombatCounter)?.Obj(CK.Payload)?.Num(CK.Ward) > 0 || Js.Truthy(profile?.Obj(CK.CounterPayload)?[CK.Ward]));
            var ops = new List<string> { Op.Damage, Op.PoiseDamage };
            if (ward) ops.Add(DV.OpWardDamage);
            var actions = ops.Select(op => new CombatAction
            {
                Source = source, Owner = source, Target = target, Card = card, Effect = new JObject { [K.Op] = op, [CK.Amount] = 1 }, Meta = ActionMeta.Empty(),
            }).ToList();
            return CoreRange.Quote(c, source, actions, route ?? DV.RouteAdvance, preview, context);
        }

        /// <summary>authorizedReturnCarrier(carrier, authorization): the Counter's carrier with the quote that prices its reply and the source it is dealt with, frozen.</summary>
        private static JObject AuthorizedCarrier(CombatState c, JObject carrier, JObject authorization)
        {
            var plan = authorization.Obj(MK.RangeReturnPlan);
            var next = Js.Spread(carrier);
            next[DK.RangeReturnAuthorization] = authorization;
            foreach (var key in new[] { DK.RangePlan, MK.RangeLineagePlan, MK.RangeLineage, DK.RangeCounterReservation }) next.Remove(key);
            var frozen = plan.Arr(DK.Targets)?.FirstOrDefault() as JObject;
            if (frozen != null)
            {
                var ranges = Js.Items(c.CoreData.Section(CK.Vocabulary)[CK.Ranges]).ToList();
                var index = (int)frozen.Num(DK.Coverage);
                next[DK.ResolvedRangeSource] = new JObject
                {
                    [DK.SourceType] = frozen.Is(DK.Weapon) ? CV.SourceWeapon : carrier.Obj(CK.CombatProfile)?.Str(CK.Camp) == CV.CampSpell ? DV.SourceSpell : DV.SourceUnarmed,
                    [DK.ItemId] = frozen[DK.SourceWeaponId]?.DeepClone() ?? Js.Null(),
                    [CK.Range] = index >= 0 && index < ranges.Count ? ranges[index].DeepClone() : Js.Null(),
                    [DK.PropertyTags] = frozen.Is(DK.ThrowCapable) ? new JArray(DV.TagThrow) : new JArray(),
                };
            }
            return next;
        }

        /// <summary>
        /// authorizeCounterRange(ctx, defender, attacker, counterCarrier, incomingCarrier): price an automatic reply before any offensive work. Its
        /// defensive interception has already happened and is never restored: a Counter that cannot pay to reach its attacker keeps its defence and
        /// replies with nothing (and says so). A reply that needs the Counter's owner to advance is the range package's (WP-12).
        /// </summary>
        public static JObject AuthorizeRange(CombatState c, JObject defender, JObject attacker, JObject counterCarrier, JObject incomingCarrier)
        {
            if (c.Fields.Obj(CK.RangeState) == null) return counterCarrier;
            var incomingPlan = incomingCarrier?.Obj(DK.RangePlan);
            var rows = (incomingPlan?.Arr(DK.Targets) ?? new JArray()).OfType<JObject>().ToList();
            var contact = rows.FirstOrDefault(row => Js.Truthy(incomingCarrier[DK.RangeContactId]) && row.Str(DK.ContactId) == incomingCarrier.Str(DK.RangeContactId))
                ?? rows.FirstOrDefault(row => row.Str(DK.TargetKey) == CoreRange.ActorKey(c, defender));
            var context = contact != null ? new JObject { [DK.SourceKey] = incomingPlan[DK.SourceKey].DeepClone(), [DK.Targets] = new JArray(contact.DeepClone()) } : null;
            // A reply the player paid for when it played the Counter as a reaction was priced then: it is accepted as quoted, once, for the hit it answered.
            var reservation = counterCarrier.Obj(DK.RangeCounterReservation);
            if (reservation != null && !reservation.Is(RK.Consumed) && reservation.Str(RK.IncomingRootId) == incomingPlan?.Str(DK.RootId) && reservation.Str(RK.ContactId) == contact?.Str(DK.ContactId)
                && reservation.Str(DK.SourceKey) == CoreRange.ActorKey(c, defender) && reservation.Str(DK.TargetKey) == CoreRange.ActorKey(c, attacker))
            {
                var accepted = (JObject)reservation.Obj(RK.RangePlan).DeepClone();
                counterCarrier.Remove(DK.RangeCounterReservation);
                var authorization = new JObject { [MK.RangeReturnPlan] = accepted, [DK.Paid] = accepted[DK.Paid]?.DeepClone() };
                if (reservation.Has(MK.RangeLineagePlan)) authorization[MK.Lineage] = reservation[MK.RangeLineagePlan].DeepClone();
                return AuthorizedCarrier(c, counterCarrier, authorization);
            }
            var plan = QuoteReturn(c, defender, attacker, counterCarrier, context, true);
            var available = defender.Str(K.Kind) == V.Enemy
                ? (!Js.Nullish(defender[CK.FixCoreStaminaRemaining]) ? defender.Num(CK.FixCoreStaminaRemaining) : !Js.Nullish(defender[K.Stamina]) ? defender.Num(K.Stamina) : !Js.Nullish(defender[K.MaxStamina]) ? defender.Num(K.MaxStamina) : 0)
                : defender.Or0(K.Stamina);
            if (available < plan.Num(DK.AdvanceSp))
            {
                c.Emit(MV.EventCombatRangeReturnUnavailable, new JObject
                {
                    [K.SourceId] = defender[K.Id].DeepClone(), [K.TargetId] = attacker[K.Id].DeepClone(), [DK.OwnerId] = V.Player, [DK.Required] = plan[DK.AdvanceSp].DeepClone(),
                    [DK.Available] = Js.N(available), [DK.AdvanceSp] = plan[DK.AdvanceSp].DeepClone(), [K.Reason] = MV.ReasonStamina, [MK.DefenseKept] = true,
                });
                return null;
            }
            plan = QuoteReturn(c, defender, attacker, counterCarrier, context, false);
            return AuthorizedCarrier(c, counterCarrier, new JObject { [MK.RangeReturnPlan] = plan, [MK.Lineage] = plan[MK.Lineage]?.DeepClone() ?? Js.Null() });
        }

        /// <summary>
        /// paidCounterRange(ctx, source, carrier, baseCosts, route): a Counter played as a reaction pays for its reply's reach when it is played. The
        /// reply is quoted against the incoming attacker (from the incoming hit's own distance), the Counter's price grows by the Stamina to advance and
        /// the quote is reserved on the Counter for that one hit. Null when the card is not a Counter played against an incoming attack.
        /// </summary>
        public static JObject PaidRange(CombatState c, JObject source, JObject carrier, JObject baseCosts, string route)
        {
            if (c.Fields.Obj(CK.RangeState) == null || c.RangeIncoming == null || !c.ReactionPlaying || carrier.Obj(CK.CombatProfile)?.Str(CK.Maneuver) != CV.StanceCounter) return null;
            var incoming = c.RangeIncoming;
            var target = c.Enemies.Concat(new[] { c.Player }).FirstOrDefault(entity => CoreRange.ActorKey(c, entity) == incoming.Str(DK.SourceKey));
            if (target == null) throw new PlayRefusalException(RM.IncomingAttackerUnavailable, RV.ReasonRange);
            var plan = QuoteReturn(c, source, target, carrier, c.RangeContext, false, route);
            if (!plan.Is(DK.Available)) throw new PlayRefusalException(RM.CounterReturnUnreachable, RV.ReasonRange);
            var costs = CoreRange.AddCosts(baseCosts, plan);
            var rootId = string.Join(DV.KeySeparator, incoming.Str(DK.RootId), RV.SegmentReturn, incoming.Str(RK.ContactId), Js.Truthy(carrier[K.InstanceId]) ? carrier.Str(K.InstanceId) : carrier.Str(K.CardId));
            var reserved = Js.Spread(plan);
            reserved.Remove(MK.Lineage);
            reserved[DK.RootId] = rootId;
            reserved[DK.Paid] = costs;
            var reservation = new JObject
            {
                [RK.Version] = 1, [DK.SourceKey] = plan[DK.SourceKey].DeepClone(), [DK.TargetKey] = incoming[DK.SourceKey].DeepClone(), [RK.IncomingRootId] = incoming[DK.RootId].DeepClone(),
                [RK.ContactId] = incoming[RK.ContactId].DeepClone(), [RK.RangePlan] = reserved,
            };
            if (plan.Obj(MK.Lineage) != null) reservation[MK.RangeLineagePlan] = Js.Spread(plan.Obj(MK.Lineage), new JObject { [DK.RootId] = rootId });
            reservation[RK.Consumed] = false;
            return new JObject { [RK.Costs] = costs, [RK.Reservation] = reservation };
        }

        /// <summary>discardCounterRangeReservation(group, target, row): the hit a reserved reply was priced for has settled; what was not used is not kept.</summary>
        public static void DiscardReservation(ExpansionGroup group, JObject target, TargetRow row)
        {
            var rootId = group.Carrier?.Obj(DK.RangePlan)?.Str(DK.RootId);
            var contactIds = new HashSet<string>(row.Contacts.Select(contact => contact.Str(DK.RangeContactId)), StringComparer.Ordinal);
            foreach (var carrier in new[] { target?.Obj(CK.CombatCounter)?.Obj(CK.Carrier), row.Receipt?.Obj(CK.Counter)?.Obj(CK.Carrier) })
            {
                var reservation = carrier?.Obj(DK.RangeCounterReservation);
                if (reservation != null && reservation.Str(RK.IncomingRootId) == rootId && contactIds.Contains(reservation.Str(RK.ContactId))) carrier.Remove(DK.RangeCounterReservation);
            }
        }

        // ------------------------------------------------------------------ arming from a card

        private static double Whole0(CombatState c, CombatAction probe, JToken value, double fallback) =>
            Math.Max(0, Math.Floor(Actions.EvalNum(c, probe, value, fallback, probe.Target)));

        /// <summary>counterEffectPreview(ctx, source, target, carrier, effect, meta): one payload effect's value and hit count, or null for an effect that is not part of the reply.</summary>
        private static (double value, double hits, double total)? EffectPreview(CombatState c, JObject source, JObject target, JObject carrier, JObject effect, ActionMeta meta)
        {
            var op = effect.Str(K.Op);
            if (op != Op.Damage && op != Op.PoiseDamage && op != DV.OpWardDamage) return null;
            if (effect.Is(K.If)) throw new NotSupportedException(string.Format(System.Globalization.CultureInfo.InvariantCulture, Ashen.Generated.Core2CombatMessages.OpcodeNotPorted, op));
            var probe = new CombatAction { Source = source, Owner = source, Target = target, Card = carrier, Meta = meta, Effect = effect };
            var baseAmount = Whole0(c, probe, effect[CK.Amount], 0);
            var hits = Whole0(c, probe, effect[K.Hits], 1) * Whole0(c, probe, effect[CK.Repeat], 1);
            var value = op == Op.Damage ? baseAmount + Ratings.CardBonus(c, source, carrier, Op.Damage, baseAmount) : baseAmount;
            return (value, hits, value * hits);
        }

        /// <summary>
        /// prepareTacticalCard for a Counter (the arming branch): the reply's payload is the card's damage, Poise and Ward effects (or its authored
        /// payload), a Counter with no Poise of its own gets the stance rules' fallback Poise, the Counter is armed on its owner, and only the card's
        /// support effects (Block, Barrier) go on to be played now. Returns those effects.
        /// </summary>
        public static List<JObject> PrepareCounter(CombatState c, JObject source, JObject target, JObject carrier, IReadOnlyList<JObject> effects, ActionMeta meta)
        {
            double damage = 0, poiseDamage = 0, wardDamage = 0;
            var listedPoise = false;
            var listedWard = false;
            foreach (var effect in effects)
            {
                var preview = EffectPreview(c, source, target, carrier, effect, meta);
                if (preview == null) continue;
                if (effect.Str(K.Op) == Op.Damage) damage += preview.Value.total;
                else if (effect.Str(K.Op) == DV.OpWardDamage) { listedWard = true; wardDamage += preview.Value.total; }
                else { listedPoise = true; poiseDamage += preview.Value.total; }
            }
            // Projected cards print impact in their rating badge instead of an opcode; an explicit opcode wins.
            var profile = carrier.Obj(CK.CombatProfile);
            var impactKey = profile?.Str(CK.Camp) == CV.CampSpell ? CK.Ward : CK.Poise;
            var listedImpact = carrier.Obj(DK.CardRatingValues)?[impactKey];
            if (listedImpact != null)
            {
                var probe = new CombatAction { Source = source, Owner = source, Target = target, Card = carrier, Meta = meta, Effect = new JObject() };
                var impact = Whole0(c, probe, listedImpact, 0);
                if (impactKey == CK.Ward) { if (!listedWard) wardDamage = impact; }
                else if (!listedPoise) poiseDamage = impact;
            }
            var payload = new JObject { [K.Hp] = Js.N(damage), [CK.Poise] = Js.N(poiseDamage), [CK.Ward] = Js.N(wardDamage) };
            var strike = c.CoreData.ReactionVersion == DN.ReactionStackVersion && Js.Truthy(carrier.Obj(DK.TraitValues)?[DK.Counterstrike]);
            var authored = profile?.Obj(CK.CounterPayload);
            if (authored != null)
            {
                var probe = new CombatAction { Source = source, Owner = source, Target = target, Card = carrier, Meta = meta, Effect = new JObject() };
                foreach (var key in new[] { K.Hp, CK.Poise, CK.Ward, CK.SmashPoiseBonus })
                {
                    if (authored[key] == null) continue;
                    var baseAmount = Math.Max(0, Actions.EvalRaw(c, probe, authored[key], 0, target));
                    var noRating = Js.Spread(carrier);
                    noRating.Remove(CK.RatingId);
                    var rating = key == K.Hp && baseAmount > 0 && !(authored[MK.HpRating] != null && authored[MK.HpRating].Type == JTokenType.Boolean && !authored.Is(MK.HpRating))
                        ? Ratings.CardBonus(c, source, noRating, Op.Damage, baseAmount) : 0;
                    payload[key] = Js.N(baseAmount + rating);
                    // Counterstrike scales the authored base and re-derives the rating from it; only the authored Poise is scaled.
                    if (strike && key == K.Hp) { payload[CK.HpBase] = Js.N(baseAmount); payload[CK.HpRating] = Js.N(rating); }
                    if (strike && key == CK.Poise) payload[CK.PoiseBase] = Js.N(baseAmount);
                }
                if (authored[CK.HpAgainstNonCaster] != null) payload[CK.HpAgainstNonCaster] = authored[CK.HpAgainstNonCaster].DeepClone();
            }
            else if (strike)
            {
                // A reply derived from the card's own damage effect: its base and rating are recorded so Counterstrike scales the base (one single-hit damage effect).
                var damaging = effects.Select(effect => (effect, preview: effect.Str(K.Op) == Op.Damage ? EffectPreview(c, source, target, carrier, effect, meta) : null)).Where(row => row.preview != null).ToList();
                if (damaging.Count == 1 && damaging[0].preview.Value.hits == 1)
                {
                    var probe = new CombatAction { Source = source, Owner = source, Target = target, Card = carrier, Meta = meta, Effect = new JObject() };
                    var baseAmount = Whole0(c, probe, damaging[0].effect[CK.Amount] ?? Js.N(0), 0);
                    payload[CK.HpBase] = Js.N(baseAmount);
                    payload[CK.HpRating] = Js.N(damaging[0].preview.Value.value - baseAmount);
                }
                payload[CK.PoiseBase] = Js.N(Math.Max(0, poiseDamage));
            }
            // A Counter with no authored Poise impact receives the shared fallback once.
            if (!(payload.Num(CK.Poise) > 0) && !effects.Any(effect => effect.Str(K.Op) == Op.PoiseDamage))
                payload[CK.Poise] = CoreStances.Rules(c)[CK.FallbackPoise]?.DeepClone();
            var options = new JObject { [K.Damage] = Js.N(damage), [CK.PoiseDamage] = Js.N(poiseDamage), [CK.WardDamage] = Js.N(wardDamage), [CK.Payload] = payload };
            CoreCounters.Arm(c, source, carrier, options);
            return effects.Where(effect => { var op = effect.Str(K.Op); return op != Op.Damage && op != Op.PoiseDamage && op != DV.OpWardDamage; }).ToList();
        }
    }
}
