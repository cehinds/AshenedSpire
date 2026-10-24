using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CC = Ashen.Generated.Core2CostKeys;
using CCM = Ashen.Generated.Core2CostMessages;
using CCV = Ashen.Generated.Core2CostValues;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;
using RK = Ashen.Generated.Core2ReactionKeys;
using RV = Ashen.Generated.Core2ReactionValues;
using SK = Ashen.Generated.Core2StatusKeys;
using SM = Ashen.Generated.Core2StatusMessages;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// What a player card costs in a Core 2 fight and what stops it being played (web engine/combat.js cardPlayCosts, resolvedCardPlayCosts,
    /// preparedPlayCosts, the cost part of previewCard and the cost gates of doPlayCard; engine/gameProfileCombat.js profileCardCosts;
    /// engine/combatExpansion.js resolveCombatCard and engine/cardBehavior.js projectCardBehavior for a configured fight; model/proposalStatusRules.js
    /// proposalCardRules and engine/concurrentStances.js stanceCapacity). The price is the profile card's own: the authored Stamina and Mana, the
    /// Mana discount of a matching stance (<see cref="CoreStances.ManaCost"/>), the enemy-phase discount of a reaction card in its stance
    /// (<see cref="CoreStances.StaminaCost"/>), and the flat tax of a status such as Restrained. Combat Rounds queues (not part of a Core 2 run, WP-21) and the cardPreparing
    /// preview of a fight that has a listener are refused by name.
    /// </summary>
    public static class CoreCosts
    {
        // ------------------------------------------------------------------ the card as the fight reads it

        /// <summary>resolveCombatCard(ctx, instance) for a configured fight: the profile's card with its behaviour projected (a play limit, a cost growth).</summary>
        public static JObject Resolve(CombatState c, JObject instance)
        {
            var projected = CoreCards.Project(c, instance);
            if (projected == null) throw new NotSupportedException(string.Format(CultureInfo.InvariantCulture, CCM.CardOutsideProfile, instance.Str(K.CardId)));
            return Behavior(c, instance, projected);
        }

        /// <summary>projectCardBehavior(ctx, instance, def): a card at its play limit is unplayable; a growing cost grows with the uses of the card.</summary>
        public static JObject Behavior(CombatState c, JObject instance, JObject def)
        {
            var result = def;
            var riders = c.Player.Obj(CK.AbilityRiders);
            if (Js.Truthy(def[CC.PlayLimit]))
            {
                var uses = Js.Or0(riders?.Obj(CC.InstanceUsesCombat)?[instance.Str(K.InstanceId) ?? string.Empty]);
                if (uses >= def.Num(CC.PlayLimit))
                {
                    result = Js.Spread(result);
                    var keywords = Js.Items(result[K.Keywords]).Select(Js.Str).ToList();
                    if (!keywords.Contains(CCV.KeywordUnplayable)) keywords.Add(CCV.KeywordUnplayable);
                    result[K.Keywords] = new JArray(keywords);
                }
            }
            if (instance.Obj(CC.ProposalChanges) is JObject changes)
            {
                // Changes a card effect wrote on the instance: a new range or maneuver on the face, and a retain that lasts until a turn.
                var original = result;
                result = Js.Spread(original);
                var range = changes[CK.Range];
                var maneuver = changes[CK.Maneuver];
                if (Js.Truthy(range))
                {
                    result[CK.Range] = range.DeepClone();
                    var profile = Js.Spread(original.Obj(CK.CombatProfile));
                    profile[CK.Range] = range.DeepClone();
                    result[CK.CombatProfile] = profile;
                }
                if (Js.Truthy(maneuver))
                {
                    var profile = Js.Spread(original.Obj(CK.CombatProfile));
                    profile[CK.Maneuver] = maneuver.DeepClone();
                    if (Js.Truthy(range)) profile[CK.Range] = range.DeepClone();
                    result[CK.CombatProfile] = profile;
                }
                if (Js.Truthy(changes[K.Retain]) && Js.Coalesce(changes[SK.RetainUntilTurn], c.Turn) >= c.Turn)
                {
                    var keywords = Js.Items(result[K.Keywords]).Select(Js.Str).ToList();
                    if (!keywords.Contains(K.Retain)) keywords.Add(K.Retain);
                    result[K.Keywords] = new JArray(keywords);
                }
            }
            if (Js.Truthy(def[CC.CostGrowth]))
            {
                var growth = def.Obj(CC.CostGrowth);
                var plays = Js.Or0(riders?.Obj(CK.CardUsesCombat)?[instance.Str(K.CardId) ?? string.Empty]);
                var perUse = Js.Truthy(growth[CC.PerUse]) ? growth.Num(CC.PerUse) : 1;
                var steps = Math.Min(Js.Nullish(growth[CC.Max]) ? c.CoreData.Section(CC.Costs).Num(CC.GrowthMax) : growth.Num(CC.Max), Math.Floor(plays / perUse));
                var baseStamina = result.Obj(CK.ResourceCosts)?[CV.PoolSpShort] ?? result[CK.StaminaCost] ?? result[K.Cost];
                var stamina = Js.IsNum(baseStamina) ? Math.Max(0, Js.D(baseStamina) + steps * Js.Or0(growth[K.Stamina])) : double.NaN;
                var mana = Math.Max(0, Js.Or0(result[K.ManaCost]) + steps * Js.Or0(growth[K.Mana]));
                var grown = Js.Spread(result);
                var staminaToken = double.IsNaN(stamina) ? baseStamina.DeepClone() : Js.N(stamina);
                grown[K.Cost] = staminaToken;
                grown[CK.StaminaCost] = staminaToken.DeepClone();
                grown[K.ManaCost] = Js.N(mana);
                if (Js.Truthy(result[CK.ResourceCosts]))
                {
                    var costs = Js.Spread(result.Obj(CK.ResourceCosts));
                    costs[CV.PoolSpShort] = staminaToken.DeepClone();
                    costs[CV.PoolMpShort] = Js.N(mana);
                    grown[CK.ResourceCosts] = costs;
                }
                result = grown;
            }
            return result;
        }

        // ------------------------------------------------------------------ status rules that touch a card's price and the stances held

        /// <summary>The capabilities the proposal rules of a statuses are gated by (web engine/proposalStatusRuntime.js proposalContext).</summary>
        private static JObject Capabilities(CombatState c)
        {
            var profile = c.CoreData.Profile;
            var modules = profile.Obj(CC.Modules);
            var result = new JObject
            {
                [CC.Modules] = new JArray((modules?.Properties() ?? Enumerable.Empty<JProperty>())
                    .Where(p => !(p.Value.Type == JTokenType.Boolean && !p.Value.Value<bool>()) && !Js.Nullish(p.Value)).Select(p => (JToken)p.Name)),
            };
            if (profile.Obj(CC.Foundation)?.Str(K.Mode) != CV.ModeLegacy)
            {
                result[CC.Elements] = new JArray();
                var include = modules?.Obj(CK.DefensesModule)?[CK.Include];
                result[CC.Protections] = Js.Truthy(include) ? include.DeepClone() : new JArray();
            }
            return result;
        }

        private static bool RuleEnabled(JObject rule, JObject capabilities)
        {
            if (rule == null) return false;
            foreach (var requirement in (rule.Obj(CC.Requires) ?? new JObject()).Properties())
            {
                var have = capabilities[requirement.Name];
                if (Js.Nullish(have)) continue;
                if (!Js.Items(requirement.Value).All(value => Js.Includes(have, Js.Str(value)))) return false;
            }
            return true;
        }

        private static bool Matches(JToken list, string value) => !(list is JArray array && array.Count > 0) || Js.Includes(list, value);

        private static JToken TagsOf(JObject card) => !Js.Nullish(card?[CK.CardTags]) ? card[CK.CardTags] : card?[K.Tags];

        private static string RangeOf(JObject card)
        {
            if (!Js.Nullish(card?[CK.Range])) return Js.Str(card[CK.Range]);
            foreach (var tag in Js.Items(TagsOf(card)))
            {
                var text = Js.Str(tag);
                if (text != null && text.StartsWith(CV.TagRange, StringComparison.Ordinal)) return text.Substring(CV.TagRange.Length);
            }
            return null;
        }

        /// <summary>proposalCardRules({ actor, card }): the refusal, the flat Stamina and Mana tax and the extra stance capacity the actor's statuses give a card.</summary>
        public static JObject ProposalCardPlan(CombatState c, JObject actor, JObject card)
        {
            var result = new JObject { [CC.Allowed] = true, [CC.Refusal] = Js.Null(), [CC.StaminaTax] = 0, [CC.ManaTax] = 0, [CC.AdditionalStanceCapacity] = 0, [CK.Effects] = new JArray(), [Ashen.Generated.Core2HitKeys.StatusChanges] = new JArray() };
            var rules = c.CoreData.ProposalStatusRules;
            var capabilities = Capabilities(c);
            double staminaTax = 0, manaTax = 0, capacity = 0;
            foreach (var status in (actor?.Obj(K.Statuses) ?? new JObject()).Properties())
            {
                var stacks = Math.Max(0, Js.Or0((status.Value as JObject)?[K.Stacks]));
                var rule = rules.Obj(status.Name);
                if (stacks == 0 || !RuleEnabled(rule, capabilities)) continue;
                foreach (var behavior in Js.Items(rule[CC.Behaviors]).OfType<JObject>())
                {
                    var kind = behavior.Str(K.Kind);
                    if (kind == CCV.KindRestriction && Matches(behavior[CC.CardTypes], card?.Str(K.Type)))
                    {
                        result[CC.Allowed] = false;
                        result[CC.Refusal] = string.Format(CultureInfo.InvariantCulture, CCM.RestrictedBy, status.Name);
                    }
                    if (kind == CCV.KindCardTax && Matches(behavior[CK.Ranges], RangeOf(card)))
                    {
                        staminaTax += Js.Or0(behavior[K.Stamina]);
                        manaTax += Js.Or0(behavior[K.Mana]);
                    }
                    if (kind == CCV.KindStanceCapacity) capacity += stacks * behavior.Num(CC.AdditionalPerStack);
                    var required = behavior[CC.RequiredTags];
                    var tags = Js.Items(TagsOf(card)).Select(Js.Str).ToList();
                    var tagged = !(required is JArray list && list.Count > 0) || Js.Items(required).Any(tag => tags.Contains(Js.Str(tag)));
                    if (!tagged) continue;
                    if (kind == CCV.KindExtraWeapon && card?.Str(K.Type) == CCV.TypeAttack)
                    {
                        ((JArray)result[CK.Effects]).Add(CoreProposal.ExtraWeapon(actor, card, behavior));
                        ((JArray)result[Ashen.Generated.Core2HitKeys.StatusChanges]).Add(CoreProposal.StatusChange(actor, status.Name, -Math.Min(stacks, behavior.Num(Ashen.Generated.Core2DamageKeys.ConsumeStacks))));
                    }
                    if (kind == CCV.KindExtraShield)
                    {
                        ((JArray)result[CK.Effects]).Add(CoreProposal.ExtraShield(actor, card, behavior));
                        ((JArray)result[Ashen.Generated.Core2HitKeys.StatusChanges]).Add(CoreProposal.StatusChange(actor, status.Name, -Math.Min(stacks, behavior.Num(Ashen.Generated.Core2DamageKeys.ConsumeStacks))));
                    }
                }
            }
            result[CC.StaminaTax] = Js.N(staminaTax);
            result[CC.ManaTax] = Js.N(manaTax);
            result[CC.AdditionalStanceCapacity] = Js.N(capacity);
            return result;
        }

        /// <summary>stanceCapacity(ctx, entity): the stances an entity holds at once: one, or what a Many Forms status allows (at most its own limit).</summary>
        public static double StanceCapacity(CombatState c, JObject entity)
        {
            JObject policy = null;
            foreach (var rule in c.CoreData.ProposalStatusRules.Properties())
            {
                if (Js.Or0(entity?.Obj(K.Statuses)?.Obj(rule.Name)?[K.Stacks]) <= 0) continue;
                policy = Js.Items((rule.Value as JObject)?[CC.Behaviors]).OfType<JObject>().FirstOrDefault(b => b.Str(K.Kind) == CCV.KindStanceCapacity);
                if (policy != null) break;
            }
            var limit = Js.Nullish(policy?[CC.MaxStances]) ? c.CoreData.Section(CC.Costs).Num(CC.DefaultMaxStances) : policy.Num(CC.MaxStances);
            return Math.Min(limit, Math.Max(1, 1 + ProposalCardPlan(c, entity, new JObject()).Num(CC.AdditionalStanceCapacity)));
        }

        /// <summary>rememberCombatStance(ctx, entity, previous): at capacity 1 nothing is remembered; above it the stances held, the staggered one never.</summary>
        public static void RememberStance(CombatState c, JObject entity, JToken previous)
        {
            var capacity = StanceCapacity(c, entity);
            if (capacity <= 1)
            {
                entity.Remove(CK.ConcurrentStances);
                return;
            }
            var rows = new List<JToken>();
            rows.AddRange(Js.Items(entity[CK.ConcurrentStances]));
            rows.Add(previous);
            rows.Add(entity[CK.CombatStance]);
            var unique = new List<KeyValuePair<string, JToken>>();
            foreach (var row in rows)
            {
                if (!(row is JObject stance) || stance.Str(CK.Maneuver) == CCV.StanceStaggered) continue;
                var key = stance.Str(CK.Camp) + CCV.StanceKeySeparator + stance.Str(CK.Maneuver);
                var at = unique.FindIndex(pair => pair.Key == key);
                if (at >= 0) unique[at] = new KeyValuePair<string, JToken>(key, stance); else unique.Add(new KeyValuePair<string, JToken>(key, stance));
            }
            var take = (int)capacity;
            entity[CK.ConcurrentStances] = new JArray(unique.Skip(Math.Max(0, unique.Count - take)).Select(pair => pair.Value.DeepClone()));
        }

        // ------------------------------------------------------------------ the price

        private static double FirstNumber(double fallback, params JToken[] tokens)
        {
            foreach (var token in tokens) if (!Js.Nullish(token)) return Js.D(token);
            return fallback;
        }

        /// <summary>profileCardCosts(ctx, def): the Stamina and Mana a configured fight asks for a card, the pools the profile includes only.</summary>
        public static JObject ProfileCardCosts(CombatState c, JObject def)
        {
            var include = Js.Items(c.CoreData.Configured(CK.ResourcesModule)?[CK.Include]).Select(Js.Str).ToList();
            var authored = def.Obj(CK.ResourceCosts) ?? def.Obj(CC.Costs) ?? new JObject();
            var isX = def.Str(K.Cost) == CCV.CostX;
            var player = c.Player;
            double baseSp = isX && include.Contains(CV.PoolSpShort)
                ? Math.Max(0, FirstNumber(0, player[K.Stamina], player[K.Energy]))
                : FirstNumber(Js.IsNum(def[K.Cost]) ? def.Num(K.Cost) : 0, authored[CV.PoolSpShort], def[CK.StaminaCost]);
            var baseMp = FirstNumber(0, authored[CV.PoolMpShort], def[K.ManaCost]);
            var sp = CoreFixCore.Enabled(c) ? CoreStances.StaminaCost(c, baseSp, player, def, baseMp) : baseSp;
            var mp = CoreStances.ManaCost(c, baseMp, player, def);
            if ((!include.Contains(CV.PoolSpShort) && sp > 0) || (!include.Contains(CV.PoolMpShort) && mp > 0))
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, CCM.DisabledResource, Js.Truthy(def[K.Id]) ? def.Str(K.Id) : CCV.Unknown));
            var spent = include.Contains(CV.PoolSpShort) ? Math.Max(0, sp) : 0;
            return new JObject { [K.Energy] = Js.N(spent), [K.Stamina] = Js.N(spent), [K.Mana] = Js.N(include.Contains(CV.PoolMpShort) ? Math.Max(0, mp) : 0) };
        }

        /// <summary>resolvedCardPriceReceipt(combat, def): what the card pays, and what its face shows, a status tax included.</summary>
        public static JObject PriceReceipt(CombatState c, JObject def)
        {
            var paid = ProfileCardCosts(c, def);
            var display = new JObject { [CC.Action] = paid[K.Energy].DeepClone(), [K.Stamina] = paid[K.Stamina].DeepClone(), [K.Mana] = paid[K.Mana].DeepClone() };
            var plan = ProposalCardPlan(c, c.Player, def);
            var staminaTax = plan.Num(CC.StaminaTax);
            var manaTax = plan.Num(CC.ManaTax);
            if (staminaTax != 0 || manaTax != 0)
            {
                paid.Put(K.Energy, paid.Num(K.Energy) + staminaTax);
                paid.Put(K.Stamina, paid.Num(K.Stamina) + staminaTax);
                paid.Put(K.Mana, paid.Num(K.Mana) + manaTax);
                display.Put(CC.Action, display.Num(CC.Action) + staminaTax);
                display.Put(K.Stamina, display.Num(K.Stamina) + staminaTax);
                display.Put(K.Mana, display.Num(K.Mana) + manaTax);
            }
            return new JObject { [CC.Paid] = paid, [CC.Display] = display, [CC.SelfFunded] = false };
        }

        /// <summary>resolvedCardPlayCosts(combat, def): { energy, stamina, mana } the card pays.</summary>
        public static JObject PlayCosts(CombatState c, JObject def) => PriceReceipt(c, def).Obj(CC.Paid);

        private static JObject Instance(CombatState c, string instanceId, bool anyPile)
        {
            var found = c.Piles.Hand.FirstOrDefault(card => card.Str(K.InstanceId) == instanceId);
            if (found == null && anyPile)
                found = c.Piles.Draw.Concat(c.Piles.Discard).Concat(c.Piles.Exhaust).FirstOrDefault(card => card.Str(K.InstanceId) == instanceId);
            if (found == null)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, anyPile ? CCM.UnknownInstance : CCM.NotInHand, instanceId));
            return found;
        }

        /// <summary>hasEventTriggers(ctx, 'cardPreparing', { cardTags }): whether anything on the fight listens for a card being prepared (stance hooks, mounted rules, status hooks, enemy phases).</summary>
        public static bool ListensForPreparing(CombatState c, List<string> cardTags)
        {
            bool Required(JToken pred, List<string> into)
            {
                if (!(pred is JObject p)) return true;
                if (p.Str(CC.Predicate) == CCV.PredicateCardTagIs && Js.IsStr(p[CC.Tag])) into.Add(p.Str(CC.Tag));
                else if (p.Str(CC.Predicate) == CCV.PredicateAll && p[CC.Preds] is JArray preds) foreach (var inner in preds) Required(inner, into);
                return true;
            }
            bool Listens(JToken rows)
            {
                if (Js.Nullish(rows)) return false;
                if (!(rows is JArray array)) return true;
                foreach (var row in array)
                {
                    if (!(row is JObject r)) return true;
                    if (r.Str(K.On) != CCV.EventCardPreparing) continue;
                    var required = new List<string>();
                    Required(r[K.If], required);
                    if (!required.Any(tag => !cardTags.Contains(tag))) return true;
                }
                return false;
            }
            var player = c.Player;
            if (Js.Truthy(player[K.StanceId]) && Listens(c.Data.Stances.Get(player.Str(K.StanceId)).Arr(K.Hooks))) return true;
            if (c.PropertyMounts != null)
                foreach (var owner in c.PropertyMounts.Entries())
                    foreach (var source in owner.Value.Entries())
                        foreach (var rule in source.Value.Rules)
                            if (rule == null || Listens(rule[K.Triggers])) return true;
            foreach (var entity in new[] { player }.Concat(c.Enemies))
                foreach (var id in (entity.Obj(K.Statuses) ?? new JObject()).Properties().Select(p => p.Name))
                    if (Listens(c.Data.Statuses.Get(id).Arr(K.Hooks))) return true;
            foreach (var enemy in c.Enemies)
                if (Listens(c.Data.Enemies.Get(enemy.Str(K.EnemyId)).Arr(K.Phases))) return true;
            return false;
        }

        /// <summary>
        /// preparedPlayCosts(combat, inst, def): the price as the play pays it, after the card's cardPreparing triggers. With nothing listening the price is the direct one; with a listener
        /// (a discount or a surcharge) a detached copy of the fight prepares the card and the price is read from it (<see cref="CorePreparing"/>). Combat Rounds is refused by name.
        /// </summary>
        private static JObject PreparedPlayCosts(CombatState c, JObject instance, JObject def, string targetId)
        {
            if (Js.Truthy(c.Fields[CC.CombatRounds])) throw new NotSupportedException(CCM.CombatRoundsNotPorted);
            var tags = TagsOf(def) != null ? Js.Items(TagsOf(def)).Select(t => Js.IsStr(t) ? Js.Str(t) : Js.Str(Js.Get(t, K.Id))).ToList() : new List<string>();
            if (!ListensForPreparing(c, tags)) return PlayCosts(c, def);
            return PlayCosts(CorePreparing.Preview(c, instance, def, targetId), def);
        }

        /// <summary>cardPlayCosts(combat, cardInstanceId): { energy, mana, stamina } for a card in hand.</summary>
        public static JObject CardPlayCosts(CombatState c, string instanceId)
        {
            var instance = Instance(c, instanceId, false);
            return PreparedPlayCosts(c, instance, Resolve(c, instance), null);
        }

        /// <summary>The cost part of previewCard(combat, id): the Stamina and Mana as the face shows them, and the Precision a stance entry would grant (the range quote adds its Advance price on top, <see cref="CoreRangePreview"/>).</summary>
        public static JObject PreviewCosts(CombatState c, string instanceId)
        {
            var instance = Instance(c, instanceId, true);
            var def = Resolve(c, instance);
            var paid = PreparedPlayCosts(c, instance, def, null);
            var isX = def.Str(K.Cost) == CCV.CostX;
            var preview = new JObject
            {
                [K.Cost] = paid[K.Energy].DeepClone(), [CC.CostIsX] = isX, [K.ManaCost] = paid[K.Mana].DeepClone(), [CK.StaminaCost] = paid[K.Stamina].DeepClone(),
            };
            var resources = def.Obj(CK.ResourceCosts) ?? def.Obj(CC.Costs) ?? new JObject();
            var authoredSp = FirstNumber(double.NaN, resources[CV.PoolSpShort], def[CK.StaminaCost], Js.IsNum(def[K.Cost]) ? def[K.Cost] : null);
            if (!isX && !double.IsNaN(authoredSp) && paid.Num(K.Stamina) != authoredSp) preview[CC.CostShift] = Js.N(Math.Sign(paid.Num(K.Stamina) - authoredSp));
            if (CoreFixCore.Enabled(c))
            {
                var entry = CoreStances.EntryBonuses(c, def).Num(CK.Precision);
                if (entry > 0) preview[CC.EntryPrecision] = Js.N(CoreStances.PrecisionEntryGrant(c, c.Player, entry, def));
            }
            return preview;
        }

        // ------------------------------------------------------------------ the gates

        /// <summary>The first reason the cost gates of doPlayCard refuse a card: the status that restricts it, an unplayable card, too little Stamina or Mana. Null when none does.</summary>
        public static string PlayRefusal(CombatState c, string instanceId, string targetId = null) => PlayRefusalFor(c, instanceId, targetId)?.Message;

        /// <summary>
        /// The refusal of the cost gates as the web raises it (F.playRefusal): its message, the reason a reaction offer reports (controlled, unplayable, cost)
        /// and, for a price, the resource and what it needed and had. Null when the card can be played.
        /// </summary>
        public static PlayRefusalException PlayRefusalFor(CombatState c, string instanceId, string targetId = null, bool direct = false)
        {
            if (c.Phase != V.PhasePlayer && !c.ReactionPlaying) return new PlayRefusalException(CCM.PlayerTurnOnly, null);
            var instance = Instance(c, instanceId, false);
            var def = Resolve(c, instance);
            try { CoreRange.AssertCardEnabled(c, def); }
            catch (PlayRefusalException refused) { return refused; }
            var plan = ProposalCardPlan(c, c.Player, def);
            if (!plan.Is(CC.Allowed)) return new PlayRefusalException(plan.Str(CC.Refusal), RV.ReasonControlled);
            if (!CoreStatusControl.Gate(c, c.Player, def).Is(Ashen.Generated.Core2DamageKeys.Allowed)) return new PlayRefusalException(SM.RecoverFirst, RV.ReasonControlled);
            if (Js.Includes(def[K.Keywords], CCV.KeywordUnplayable)) return new PlayRefusalException(string.Format(CultureInfo.InvariantCulture, CCM.Unplayable, def.Str(K.Name)), RV.ReasonUnplayable);
            var paid = direct ? PlayCosts(c, def) : PreparedPlayCosts(c, instance, def, targetId);
            if (c.Player.Num(K.Stamina) < paid.Num(K.Energy)) return new PlayRefusalException(CCM.NotEnough, RV.ReasonCost, ShortOf(RV.ResourceStamina, paid.Num(K.Energy), c.Player.Num(K.Stamina)));
            if (c.Player.Num(K.Mana) < paid.Num(K.Mana)) return new PlayRefusalException(CCM.NotEnough, RV.ReasonCost, ShortOf(RV.ResourceMana, paid.Num(K.Mana), c.Player.Num(K.Mana)));
            return null;
        }

        /// <summary>What a price refusal reports: the resource, what the card needed and what the player had.</summary>
        public static JObject ShortOf(string resource, double need, double have) =>
            new JObject { [RK.Resource] = resource, [RK.Need] = Js.N(need), [RK.Have] = Js.N(have) };
    }

    /// <summary>web F.playRefusal: a play the rules refuse, with the reason a reaction offer names it by (controlled, unplayable, cost, range) and what it adds (the resource and the amounts).</summary>
    public sealed class PlayRefusalException : InvalidOperationException
    {
        public PlayRefusalException(string message, string reason, JObject extra = null) : base(message)
        {
            Reason = reason;
            Extra = extra;
        }

        public string Reason { get; }

        public JObject Extra { get; }
    }
}
