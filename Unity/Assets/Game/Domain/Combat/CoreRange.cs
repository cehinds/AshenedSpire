using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DM = Ashen.Generated.Core2DamageMessages;
using DN = Ashen.Generated.Core2DamageNumbers;
using DV = Ashen.Generated.Core2DamageValues;
using MK = Ashen.Generated.Core2MatchupKeys;
using NK = Ashen.Generated.Core2RangeKeys;
using NM = Ashen.Generated.Core2RangeMessages;
using NV = Ashen.Generated.Core2RangeValues;
using RV = Ashen.Generated.Core2ReactionValues;
using CC = Ashen.Generated.Core2CostKeys;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// Where the fighters stand and how far apart (web model/formationOccupancy.js initializeFormationOccupancy, model/formationLayout.js
    /// perspective grid, engine/combatEngagement.js initializeCombatRange, model/combatEngagement.js createRangeState): one rank of the
    /// player's lanes against two ranks of enemies, and the engagement band of every player/enemy pair, set by the weapon each side
    /// starts with.
    /// </summary>
    public static class CoreRange
    {
        private static string Cell(JObject formation, int lane, double column) =>
            formation.Str(CK.Rows)[lane].ToString() + column.ToString(CultureInfo.InvariantCulture);

        /// <summary>initializeFormationOccupancy(combat): the profile's formation policy, then a cell for the player and every enemy.</summary>
        public static void InitializeFormation(CombatState c)
        {
            var policy = c.CoreData.Module(CK.FormationModule);
            if (policy == null) return;
            c.Fields[CK.FormationPolicy] = policy.DeepClone();
            var f = c.CoreData.Section(CK.Formation);
            var extra = policy.Obj(CK.ExtraRow);
            var enabled = extra != null && extra.Is(CK.Enabled) && Js.Includes(extra[CK.Sides], CV.SideEnemy);
            var lanes = f.Num(CK.Lanes);
            var rows = (int)Math.Min(f.Num(CK.MaxRows), Math.Max(lanes, Math.Max(Math.Ceiling(c.Enemies.Count / (enabled ? f.Num(CK.EnemiesPerLaneExtra) : f.Num(CK.EnemiesPerLane))), 1)));
            var order = new List<string>();
            var back = new List<int> { rows - 1, 0 };
            for (var lane = 1; lane < rows - 1; lane++) back.Add(lane);
            order.AddRange(back.Select(lane => Cell(f, lane, f.Num(CK.EnemyBackColumn))));
            for (var lane = rows - 1; lane >= 0; lane--) order.Add(Cell(f, lane, f.Num(CK.EnemyFrontColumn)));
            if (enabled) for (var lane = 0; lane < rows; lane++) order.Add(Cell(f, lane, f.Num(CK.ExtraColumn)));
            var taken = new HashSet<string>(StringComparer.Ordinal);
            c.Player[CK.FormationCell] = Cell(f, rows - 1, f.Num(CK.PlayerColumn));
            foreach (var enemy in c.Enemies)
            {
                var cell = order.FirstOrDefault(candidate => !taken.Contains(candidate)) ?? throw new InvalidOperationException(CM.FormationFull);
                enemy[CK.FormationCell] = cell;
                taken.Add(cell);
            }
        }

        internal static string PairKey(string a, string b)
        {
            var pair = new[] { a, b };
            Array.Sort(pair, StringComparer.Ordinal);
            return new JArray(pair).ToString(Formatting.None);
        }

        private static double Band(CombatState c, string range)
        {
            var ranges = Js.Items(c.CoreData.Section(CK.Vocabulary)[CK.Ranges]).Select(Js.Str).ToList();
            var index = ranges.IndexOf(range);
            return index;
        }

        private static string AttackSource(JObject piece) =>
            Js.Items(piece[K.Tags]).Select(Js.Str).FirstOrDefault(t => t != null && t.StartsWith(CV.TagSource, StringComparison.Ordinal))?.Substring(CV.TagSource.Length)
            ?? piece.Obj(CK.Attack)?.Str(CK.Source);

        /// <summary>initializeCombatRange(ctx, policy): every player/enemy pair starts at the wider of the two sides' starting bands.</summary>
        public static void InitializeRange(CombatState c, JObject policy)
        {
            if (!CoreFixCore.Enabled(c)) return;
            var start = new List<(string key, double band)>();
            var playerKey = CV.ActorPlayerKey;
            c.Player[CK.RangeActorKey] = playerKey;
            string primaryRange = null;
            var loadout = c.Loadout;
            foreach (var hand in Js.Items(c.CoreData.Section(CK.Range)[CK.Hands]).Select(Js.Str))
            {
                foreach (var slot in Js.Items(c.Data.Equipment[K.Slots]).OfType<JObject>().Where(s => s.Str(K.Hand) == hand))
                {
                    var piece = loadout != null ? Equipment.EquippedIn(c.Data, loadout, c.Player.Str(K.ClassId), slot.Str(K.Id)) : null;
                    if (piece != null && AttackSource(piece) == CV.SourceWeapon) { primaryRange = Js.Truthy(piece[CK.Range]) ? piece.Str(CK.Range) : CV.RangeMelee; break; }
                }
                if (primaryRange != null) break;
            }
            start.Add((playerKey, Band(c, primaryRange ?? CV.RangeMelee)));
            foreach (var enemy in c.Enemies)
            {
                var key = CV.ActorEnemyPrefix + enemy.Str(K.Id);
                enemy[CK.RangeActorKey] = key;
                var def = c.Data.Enemies.Get(enemy.Str(K.EnemyId));
                start.Add((key, Band(c, Js.Truthy(def[CK.StartingRange]) ? def.Str(CK.StartingRange) : CV.RangeMelee)));
            }
            var engagements = new JObject();
            foreach (var a in start)
                foreach (var b in start)
                    if (a.key.StartsWith(CV.ActorPlayerPrefix, StringComparison.Ordinal) && b.key.StartsWith(CV.ActorEnemyPrefix, StringComparison.Ordinal))
                        engagements.Put(PairKey(a.key, b.key), Math.Max(a.band, b.band));
            var rules = policy != null ? (JObject)policy.DeepClone() : Js.Spread(CoreFixCore.Raw(c).Obj(CK.Range));
            c.Fields[CK.RangeState] = new JObject { [CK.Version] = 1, [CK.Policy] = rules, [CK.Engagements] = engagements };
        }

        // ------------------------------------------------------------------ the action quote (web engine/combatEngagement.js, model/combatEngagement.js)

        private static JObject State(CombatState c) => c.Fields.Obj(CK.RangeState);

        /// <summary>rangeBand(range): a band number as is, a range name by its place in the vocabulary (-1 when it names none).</summary>
        public static double RangeBand(CombatState c, JToken range)
        {
            if (Js.IsNum(range) && Js.D(range) == Math.Floor(Js.D(range)) && Js.D(range) >= 0 && Js.D(range) <= DN.MaxBand) return Js.D(range);
            return Band(c, Js.Str(range));
        }

        /// <summary>actorRangeKey(ctx, entity): <c>enemy:&lt;id&gt;</c> for an enemy, the seat's key for the player.</summary>
        public static string ActorKey(CombatState c, JObject entity)
        {
            if (entity == null) return null;
            if (entity.Str(K.Kind) == V.Enemy || Js.Truthy(entity[K.EnemyId])) return CV.ActorEnemyPrefix + entity.Str(K.Id);
            return Js.Truthy(entity[CK.RangeActorKey]) ? entity.Str(CK.RangeActorKey) : entity == c.Player || entity.Str(K.Id) == DV.PlayerEntityId ? CV.ActorPlayerKey : null;
        }

        private static double EngagementBand(CombatState c, string a, string b)
        {
            var value = State(c)?.Obj(CK.Engagements)?[PairKey(a, b)];
            if (!Js.IsNum(value)) throw new InvalidOperationException(DM.EngagementMissing);
            return Js.D(value);
        }

        /// <summary>rangeActionPlan(state, sourceKey, contacts, { route }): the distance every contact must close, the Stamina to advance and the precision it costs.</summary>
        public static JObject ActionPlan(CombatState c, string sourceKey, IReadOnlyList<JObject> contacts, string route, JObject context = null)
        {
            var state = State(c);
            var policy = state.Obj(CK.Policy);
            var enabled = policy.Is(CK.Enabled);
            var available = route != DV.RouteThrow || enabled;
            double maxMissing = 0;
            var targets = new List<JObject>();
            for (var index = 0; index < contacts.Count; index++)
            {
                var contact = contacts[index];
                var targetKey = contact.Str(DK.TargetKey);
                var weapon = contact.Is(DK.Weapon);
                var throwCapable = contact.Is(DK.ThrowCapable);
                var bypass = contact.Is(DK.Bypass);
                var coverage = RangeBand(c, !Js.Nullish(contact[DK.Coverage]) ? contact[DK.Coverage] : Js.S(CV.RangeMelee));
                // An incoming root's context supplies the temporary distance to a reply nested in it (the reply of a Counter).
                var inherited = Js.Items(context?[DK.Targets]).OfType<JObject>().FirstOrDefault(row => PairKey(context.Str(DK.SourceKey), row.Str(DK.TargetKey)) == PairKey(sourceKey, targetKey));
                var actual = inherited != null && Js.IsNum(inherited[DK.ActualBand]) ? inherited.Num(DK.ActualBand) : EngagementBand(c, sourceKey, targetKey);
                var distance = inherited != null ? inherited.Num(DK.EffectiveBand) : actual;
                var missing = enabled && !bypass ? Math.Max(0, distance - coverage) : 0;
                var extended = route == DV.RouteThrow && missing > 0;
                if (extended && (!throwCapable || missing > 1)) available = false;
                maxMissing = Math.Max(maxMissing, missing);
                targets.Add(new JObject
                {
                    [DK.ContactId] = !Js.Nullish(contact[DK.ContactId]) ? contact[DK.ContactId].DeepClone() : Js.S(index.ToString(CultureInfo.InvariantCulture)),
                    [DK.TargetKey] = targetKey, [DK.ActualBand] = Js.N(actual), [DK.Band] = Js.N(distance), [DK.Coverage] = Js.N(coverage),
                    [DK.Weapon] = weapon, [DK.ThrowCapable] = throwCapable, [DK.Bypass] = bypass,
                    [DK.SourceWeaponId] = contact[DK.SourceWeaponId] != null ? contact[DK.SourceWeaponId].DeepClone() : Js.Null(),
                    [DK.MissingBands] = Js.N(missing),
                    [DK.EffectiveBand] = Js.N(!enabled || bypass ? Math.Min(distance, coverage) : route == DV.RouteAdvance ? Math.Min(distance, coverage) : distance),
                    [DK.PrecisionDelta] = Js.N(enabled ? (weapon && coverage == 1 && actual == 0 ? policy.Num(DK.ReachPrecisionBonus) : 0) - (extended ? policy.Num(DK.ThrowPrecisionPenalty) : 0) : 0),
                });
            }
            var approach = new Dictionary<string, double>(StringComparer.Ordinal);
            if (enabled && route == DV.RouteAdvance)
                foreach (var row in targets) approach[row.Str(DK.TargetKey)] = Math.Max(approach.TryGetValue(row.Str(DK.TargetKey), out var seen) ? seen : 0, row.Num(DK.MissingBands));
            foreach (var row in targets)
                row[DK.EffectiveBand] = Js.N(enabled ? Math.Max(0, row.Num(DK.Band) - (approach.TryGetValue(row.Str(DK.TargetKey), out var near) ? near : 0)) : row.Num(DK.Band));
            var advanceSp = enabled && route == DV.RouteAdvance ? maxMissing * policy.Num(DK.AdvanceSpPerBand) : 0;
            return new JObject
            {
                [CK.Version] = 1, [DK.SourceKey] = sourceKey, [DK.Route] = route, [DK.Available] = available, [DK.AdvanceSp] = Js.N(advanceSp), [DK.Targets] = new JArray(targets),
            };
        }

        private static JObject SourceProfile(CombatState c, JObject carrier, JObject effect)
        {
            var tagged = new JObject();
            if (effect.Is(K.Tags))
            {
                var probe = Js.Spread(carrier);
                probe[K.Tags] = effect[K.Tags].DeepClone();
                probe[K.Attack] = (effect.Is(K.Attack) ? effect[K.Attack] : carrier?[K.Attack])?.DeepClone();
                foreach (var p in CoreStances.ProfileFor(c, probe).Properties()) if (p.Value.Type != JTokenType.Null) tagged[p.Name] = p.Value.DeepClone();
            }
            return Js.Spread(CoreStances.ProfileFor(c, carrier), carrier?.Obj(CK.CombatProfile), tagged, effect.Obj(CK.CombatProfile));
        }

        private static JObject CommittedSource(CombatState c, JObject source, JObject carrier, JObject effect, JObject profile)
        {
            var resolved = carrier?.Obj(DK.ResolvedRangeSource) ?? carrier?.Obj(DK.ResolvedSource);
            if (resolved != null || source.Str(K.Kind) == V.Enemy) return resolved;
            var withAttack = Js.Spread(carrier);
            withAttack[K.Attack] = (effect.Is(K.Attack) ? effect[K.Attack] : carrier?[K.Attack])?.DeepClone();
            var attack = CoreAttack.Descriptor(withAttack);
            var kind = Js.Truthy(attack[CK.Source]) ? attack.Str(CK.Source) : profile.Str(CK.Camp) == CV.CampSpell ? DV.SourceSpell : CV.SourceWeapon;
            var explicitCarrier = Js.Spread(carrier);
            explicitCarrier[K.SourceHand] = (Js.Truthy(attack[K.Hand]) ? attack[K.Hand] : carrier?[K.SourceHand])?.DeepClone();
            if (attack.Is(K.Hand)) explicitCarrier.Remove(K.SourceArmamentId);
            JObject piece = kind == DV.SourceUnarmed ? null : CoreAttack.WieldedArmament(c, explicitCarrier, source, kind);
            if (piece != null && CoreAttack.Descriptor(piece).Str(CK.Source) != kind)
            {
                var free = Js.Spread(explicitCarrier);
                free.Remove(K.SourceArmamentId);
                piece = CoreAttack.WieldedArmament(c, free, source, kind);
            }
            if (piece != null && CoreAttack.Descriptor(piece).Str(CK.Source) != kind) piece = null;
            return piece != null
                ? new JObject { [DK.SourceType] = kind, [DK.ItemId] = piece[K.Id]?.DeepClone(), [CK.Range] = Js.Truthy(piece[CK.Range]) ? piece[CK.Range].DeepClone() : Js.S(CV.RangeMelee), [DK.PropertyTags] = new JArray(Js.Items(piece[DK.PropertyTags]).Select(t => t.DeepClone())) }
                : new JObject { [DK.SourceType] = kind == CV.SourceWeapon ? DV.SourceUnarmed : kind };
        }

        /// <summary>rangeSourceContact(ctx, source, target, carrier, effect, contactId): what one hit reaches with (its weapon, its coverage, whether it bypasses distance).</summary>
        public static JObject SourceContact(CombatState c, JObject source, JObject target, JObject carrier, JObject effect, string contactId = null)
        {
            effect ??= new JObject();
            var profile = SourceProfile(c, carrier, effect);
            var resolved = CommittedSource(c, source, carrier, effect, profile);
            var weapon = resolved?.Str(DK.SourceType) == CV.SourceWeapon;
            var piece = weapon ? Js.Items(c.Data.Equipment[K.Armaments]).OfType<JObject>().FirstOrDefault(row => row.Str(K.Id) == resolved.Str(DK.ItemId)) : null;
            JToken coverage = weapon ? (!Js.Nullish(resolved[CK.Range]) ? resolved[CK.Range] : !Js.Nullish(piece?[CK.Range]) ? piece[CK.Range] : Js.S(CV.RangeMelee))
                : resolved?.Str(DK.SourceType) == DV.SourceUnarmed ? Js.S(CV.RangeMelee)
                : Js.Truthy(profile[CK.Range]) ? profile[CK.Range] : Js.Truthy(resolved?[CK.Range]) ? resolved[CK.Range] : Js.S(CV.RangeMelee);
            var propertyTags = resolved != null && resolved[DK.PropertyTags] != null ? resolved[DK.PropertyTags] : piece?[DK.PropertyTags];
            return new JObject
            {
                [DK.ContactId] = contactId ?? DV.ContactDefault, [DK.TargetKey] = ActorKey(c, target), [DK.Coverage] = coverage.DeepClone(), [DK.Weapon] = weapon,
                [DK.SourceWeaponId] = weapon ? (Js.Truthy(resolved[DK.ItemId]) ? resolved[DK.ItemId].DeepClone() : resolved[K.Id]?.DeepClone() ?? Js.Null()) : Js.Null(),
                [DK.ThrowCapable] = weapon && Js.Includes(propertyTags ?? new JArray(), DV.TagThrow),
                [DK.Bypass] = profile[DK.RangeOverride] != null && profile[DK.RangeOverride].Type == JTokenType.Boolean && profile.Is(DK.RangeOverride) || profile.Is(DK.AdequateReach) || Js.Includes(profile[CK.Traits], DV.TraitAdequateReach),
            };
        }

        private static readonly HashSet<string> HostileOps = new HashSet<string>(StringComparer.Ordinal)
        {
            Ashen.Generated.CombatOps.Damage, Ashen.Generated.CombatOps.ApplyStatus, CV.OpBuildup, Ashen.Generated.CombatOps.PoiseDamage, DV.OpWardDamage, DV.OpLoseHp, DV.OpStagger, DV.OpArcaneBuildup,
        };

        private static readonly HashSet<string> Friendly = new HashSet<string>(StringComparer.Ordinal) { V.TargetSelf, V.RefOwner, V.RefPlayer, DV.TargetAlly, DV.TargetAllAllies };

        public static bool IsHostileOp(string op) => op != null && HostileOps.Contains(op);

        private static double Floor0(double value) => Math.Max(0, Math.Floor(value));

        public static bool IsFriendlyTarget(string target) => target != null && Friendly.Contains(target);

        /// <summary>The range name of a band (the vocabulary's order).</summary>
        public static JToken RangeName(CombatState c, double band)
        {
            var ranges = Js.Items(c.CoreData.Section(CK.Vocabulary)[CK.Ranges]).ToList();
            var index = (int)band;
            return index >= 0 && index < ranges.Count ? ranges[index].DeepClone() : Js.Null();
        }

        /// <summary>quoteRangeActions(ctx, source, actions, { route, preview, context }): the plan a committed action's hostile effects need. A preview changes nothing: it plans on a copy of the actions and of the RNG.</summary>
        public static JObject Quote(CombatState c, JObject source, List<CombatAction> actions, string route = null, bool preview = false, JObject context = null)
        {
            if (State(c) == null) return null;
            route ??= DV.RouteAdvance;
            context ??= null;
            var contacts = new List<JObject>();
            if (preview)
                actions = actions.Select(action => new CombatAction { Effect = action.Effect, Source = action.Source, Owner = action.Owner, Target = action.Target, Card = action.Card, Meta = (action.Meta ?? ActionMeta.Empty()).Clone() }).ToList();
            var saved = c.Rng;
            if (preview) c.Rng = saved.Clone();
            JObject lineage;
            try
            {
                for (var index = 0; index < actions.Count; index++)
                {
                    var action = actions[index];
                    var eff = action.Effect;
                    if (!IsHostileOp(eff.Str(K.Op))) continue;
                    var repeats = Floor0(Actions.EvalNum(c, action, eff[K.Repeat], 1, action.Target));
                    var hits = eff.Str(K.Op) == Ashen.Generated.CombatOps.Damage ? Floor0(Actions.EvalNum(c, action, eff[K.Hits], 1, action.Target)) : 1;
                    var batches = new List<List<string>>();
                    for (var r = 0; r < repeats; r++)
                        for (var h = 0; h < hits; h++)
                        {
                            var targets = Actions.ResolveTargets(c, action, eff.Str(K.Target));
                            batches.Add(targets.Select(t => t.Str(K.Id)).ToList());
                            foreach (var target in targets)
                                if (target.Is(K.Alive) && source.Str(K.Kind) != target.Str(K.Kind))
                                {
                                    var contact = SourceContact(c, source, target, action.Card, eff, string.Join(DV.ContactSeparator, index, r, h, ActorKey(c, target)));
                                    contacts.Add(contact);
                                    if (!preview && !(action.Card?.Is(DK.ResolvedSource) == true))
                                    {
                                        var coverage = contact[DK.Coverage];
                                        var resolved = new JObject
                                        {
                                            [DK.SourceType] = contact.Is(DK.Weapon) ? CV.SourceWeapon : Js.Str(coverage) == CV.RangeMelee ? DV.SourceUnarmed : DV.SourceSpell,
                                            [DK.ItemId] = contact[DK.SourceWeaponId].DeepClone(), [CK.Range] = coverage.DeepClone(),
                                            [DK.PropertyTags] = contact.Is(DK.ThrowCapable) ? new JArray(DV.TagThrow) : new JArray(),
                                        };
                                        action.Card = Js.Spread(action.Card, new JObject { [DK.ResolvedRangeSource] = resolved });
                                    }
                                }
                        }
                    action.Meta = (action.Meta ?? ActionMeta.Empty()).Clone();
                    action.Meta.RangeTargetBatches = batches;
                }
                lineage = CoreRangeLineage.Extend(c, source, actions, contacts, preview, actions.Count > 0 ? actions[0].Card : null);
            }
            finally { c.Rng = saved; }
            var plan = ActionPlan(c, ActorKey(c, source), contacts, route, context);
            if (lineage != null) plan[MK.Lineage] = lineage;
            return plan;
        }

        /// <summary>assertRangeCardEnabled(ctx, def): no hostile hook may fire while the card is prepared, and a card that only moves the engagement needs range mechanics on.</summary>
        public static void AssertCardEnabled(CombatState c, JObject def)
        {
            CoreRangeLineage.AssertPreparingSupported(c, c.Player, def);
            var effects = Js.Items(def[K.Effects]).OfType<JObject>().ToList();
            if (State(c) != null && State(c).Obj(CK.Policy)?.Is(CK.Enabled) != true && effects.Count > 0 && effects.All(effect => effect.Str(K.Op) == Ashen.Generated.Core2StatusValues.MoveEngagement))
                throw new PlayRefusalException(NM.RangeDisabled, NV.ReasonRangeDisabled);
        }

        /// <summary>
        /// executeEngagementMovement(ctx, action): the source moves one band toward (-1) or away from (1) its opponent. Without range mechanics it does
        /// nothing; against <c>pendingAttacker</c> it moves the creature whose hit the reaction answers.
        /// </summary>
        public static void MoveEngagement(CombatState c, CombatAction action)
        {
            var state = State(c);
            if (state?.Obj(CK.Policy)?.Is(CK.Enabled) != true) return;
            var target = action.Target;
            if (action.Effect.Str(K.Target) == NV.TargetPendingAttacker && (target == null || target.Str(K.Kind) == action.Source.Str(K.Kind)))
                target = c.Enemies.FirstOrDefault(row => row.Str(K.Id) == c.Fields.Str(Ashen.Generated.Core2StatusKeys.ReactionSourceId));
            if (target == null || target.Str(K.Kind) == action.Source.Str(K.Kind)) throw new InvalidOperationException(NM.MovementNeedsOpponent);
            var bands = action.Effect.Num(NK.Bands);
            if (bands != 1 && bands != -1) throw new InvalidOperationException(NM.MovementOneBand);
            var sourceKey = ActorKey(c, action.Source);
            var targetKey = ActorKey(c, target);
            var next = Math.Max(0, Math.Min(DN.MaxBand, EngagementBand(c, sourceKey, targetKey) + bands));
            var engagements = (JObject)state.Obj(CK.Engagements).DeepClone();
            engagements[PairKey(sourceKey, targetKey)] = Js.N(next);
            c.Fields[CK.RangeState] = Js.Spread(state, new JObject { [CK.Engagements] = engagements });
            c.Emit(NV.EventEngagementMoved, new JObject { [K.SourceId] = action.Source[K.Id].DeepClone(), [K.TargetId] = target[K.Id].DeepClone(), [NK.Bands] = Js.N(bands) });
        }

        /// <summary>An enemy's Stamina to spend (fixCoreStaminaRemaining ?? stamina ?? maxStamina ?? 0).</summary>
        internal static double StaminaOf(JObject enemy) =>
            !Js.Nullish(enemy[CK.FixCoreStaminaRemaining]) ? enemy.Num(CK.FixCoreStaminaRemaining) : !Js.Nullish(enemy[K.Stamina]) ? enemy.Num(K.Stamina) : !Js.Nullish(enemy[K.MaxStamina]) ? enemy.Num(K.MaxStamina) : 0;

        /// <summary>The Stamina the advance takes: an enemy's pool shrinks, a player's Actions are spent and told (energySpent, staminaSpent).</summary>
        private static void PayAdvance(CombatState c, JObject source, double available, double extra)
        {
            if (source.Str(K.Kind) == V.Enemy)
            {
                source.Put(CK.FixCoreStaminaRemaining, available - extra);
                source.Put(K.Stamina, available - extra);
                return;
            }
            source.Put(K.Stamina, available - extra);
            var counters = source.Obj(K.Counters);
            counters.Put(CC.StaminaSpentThisTurn, counters.Or0(CC.StaminaSpentThisTurn) + extra);
            c.Emit(Ashen.Generated.CombatEvents.EnergySpent, Js.Obj(K.Amount, extra));
            c.Emit(Ashen.Generated.CombatEvents.StaminaSpent, Js.Obj(K.Amount, extra));
        }

        /// <summary>addRangeCosts(costs, plan): the base costs plus the Stamina the advance takes.</summary>
        public static JObject AddCosts(JObject costs, JObject plan)
        {
            var extra = plan?.Num(DK.AdvanceSp) ?? 0;
            var next = Js.Spread(costs);
            next[K.Energy] = Js.N(costs.Or0(K.Energy) + extra);
            next[K.Stamina] = Js.N(costs.Or0(K.Stamina) + extra);
            return next;
        }

        /// <summary>
        /// acceptRangeActions(ctx, source, actions, carrier, rootId): the plan a committed action carries. The Stamina to advance is paid here (an enemy's
        /// pool shrinks, a player's Actions are spent), unless the plan was paid already: a card's plan is accepted once, and a Counter's reservation paid
        /// for its reply when the Counter was played. A plan that carries a lineage freezes it on the root.
        /// </summary>
        public static JObject Accept(CombatState c, JObject source, List<CombatAction> actions, JObject carrier, string rootId)
        {
            if (State(c) == null) return null;
            // A Counter's reply carries the quote that priced it (authorizeCounterRange); it is accepted, not requoted.
            var authorization = carrier.Obj(DK.RangeReturnAuthorization);
            var plan = authorization?.Obj(MK.RangeReturnPlan) ?? carrier.Obj(DK.RangePlan) ?? Quote(c, source, actions, carrier.Is(DK.RangeRoute) ? carrier.Str(DK.RangeRoute) : DV.RouteAdvance, false, c.RangeContext);
            var lineage = authorization?.Obj(MK.Lineage) ?? plan.Obj(MK.Lineage);
            if (authorization != null) CoreRangeLineage.BindReservedRootSites(c, actions, lineage);
            var prepaidReturn = authorization != null && Js.Truthy(authorization[DK.Paid]);
            if (!plan.Is(DK.Available)) throw new PlayRefusalException(DM.RangeRouteUnreachable, RV.ReasonRange);
            var extra = plan.Num(DK.AdvanceSp);
            if (!carrier.Is(DK.RangePlan) && !prepaidReturn)
            {
                var enemy = source.Str(K.Kind) == V.Enemy;
                var available = enemy ? StaminaOf(source) : source.Or0(K.Stamina);
                if (available < extra) throw new PlayRefusalException(NM.NotEnoughAdvance, RV.ReasonCost, CoreCosts.ShortOf(RV.ResourceStamina, extra, available));
                if (extra > 0) PayAdvance(c, source, available, extra);
            }
            var paid = carrier.Obj(DK.RangePlan)?.Obj(DK.Paid) ?? authorization?.Obj(DK.Paid) ?? AddCosts(carrier.Obj(DK.RangeBaseCosts) ?? new JObject { [K.Energy] = 0, [K.Stamina] = 0, [K.Mana] = 0 }, plan);
            var accepted = Js.Spread(plan);
            accepted.Remove(MK.Lineage);
            accepted[DK.RootId] = rootId;
            accepted[DK.Paid] = paid;
            carrier[DK.RangePlan] = accepted;
            if (lineage != null) carrier[MK.RangeLineagePlan] = Js.Spread(lineage, new JObject { [DK.RootId] = rootId });
            foreach (var key in new[] { DK.RangeBaseCosts, DK.RangeRoute, DK.RangeQuotedPlan, DK.RangeReturnAuthorization, DK.RangeCounterReservation }) carrier.Remove(key);
            foreach (var action in actions)
            {
                action.Card = Js.Spread(action.Card, new JObject { [DK.RangePlan] = accepted });
                if (carrier.Has(MK.RangeLineagePlan)) action.Card[MK.RangeLineagePlan] = carrier[MK.RangeLineagePlan].DeepClone();
                action.Card.Remove(DK.RangeReturnAuthorization);
                action.Card.Remove(DK.RangeCounterReservation);
                action.Meta = (action.Meta ?? ActionMeta.Empty()).Clone();
                action.Meta.EnergySpent = (action.Meta.EnergySpent ?? 0) + extra;
                action.Meta.StaminaSpent = (action.Meta.StaminaSpent ?? 0) + extra;
            }
            return accepted;
        }

        /// <summary>The hit preview a route row reads: (state, source, target, carrier) to the odds of that contact, or null.</summary>
        public delegate JObject HitPreview(CombatState c, JObject source, JObject target, JObject carrier);

        /// <summary>
        /// previewRangeRoutes(ctx, source, actions, baseCosts, { rangeRoute, hitPreview }): every route (advance, throw) quoted on a copy, with the price it
        /// adds, whether it reaches every target, and the odds of each contact before and after the approach. Null without a range state.
        /// </summary>
        public static JObject PreviewRoutes(CombatState c, JObject source, List<CombatAction> actions, JObject baseCosts, string rangeRoute, HitPreview hitPreview)
        {
            if (State(c) == null) return null;
            var plans = new[] { DV.RouteAdvance, DV.RouteThrow }.Select(route => Quote(c, source, actions, route, true, c.RangeContext)).ToList();
            var opponents = source.Str(K.Kind) != V.Enemy ? c.Enemies : new List<JObject> { c.Player };
            var rows = new JArray();
            foreach (var plan in plans)
            {
                var costs = AddCosts(baseCosts, plan);
                var targets = new List<JObject>();
                foreach (var row in plan.Arr(DK.Targets).OfType<JObject>())
                {
                    var head = row.Str(DK.ContactId).Split(DV.ContactSeparator[0])[0];
                    var index = int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
                    var projected = index >= 0 && index < actions.Count && actions[index].Target != null ? actions[index].Target : actions.FirstOrDefault(action => ActorKey(c, action.Target) == row.Str(DK.TargetKey))?.Target;
                    var target = projected != null && ActorKey(c, projected) == row.Str(DK.TargetKey) ? projected : opponents.FirstOrDefault(entity => ActorKey(c, entity) == row.Str(DK.TargetKey));
                    var carrier = index >= 0 && index < actions.Count ? actions[index].Card ?? new JObject() : new JObject();
                    var current = hitPreview?.Invoke(c, source, target, carrier);
                    var after = hitPreview?.Invoke(c, source, target, Js.Spread(carrier, new JObject { [DK.RangePlan] = plan }));
                    targets.Add(new JObject
                    {
                        [NK.Conditional] = row.Str(DK.ContactId).StartsWith(NV.RiderPrefix, StringComparison.Ordinal), [DK.ContactId] = row[DK.ContactId].DeepClone(), [DK.TargetKey] = row[DK.TargetKey].DeepClone(),
                        [K.TargetId] = target?[K.Id]?.DeepClone() ?? Js.Null(), [DK.Band] = row[DK.ActualBand].DeepClone(), [DK.Coverage] = row[DK.Coverage].DeepClone(), [DK.EffectiveBand] = row[DK.EffectiveBand].DeepClone(),
                        [DK.PrecisionDelta] = row[DK.PrecisionDelta].DeepClone(), [Ashen.Generated.Core2HitKeys.InRange] = after?.Is(Ashen.Generated.Core2HitKeys.InRange) == true,
                        [Ashen.Generated.Core2HitKeys.HitChance] = after?[Ashen.Generated.Core2HitKeys.HitChance]?.DeepClone() ?? Js.Null(), [NK.CurrentInRange] = current?.Is(Ashen.Generated.Core2HitKeys.InRange) == true,
                        [NK.CurrentHitChance] = current?[Ashen.Generated.Core2HitKeys.HitChance]?.DeepClone() ?? Js.Null(),
                    });
                }
                var grouped = new JArray();
                foreach (var key in targets.Select(row => row.Str(DK.TargetKey)).Distinct())
                {
                    var all = targets.Where(row => row.Str(DK.TargetKey) == key).ToList();
                    var conditional = all.Where(row => row.Is(NK.Conditional)).ToList();
                    var contacts = all.Where(row => !row.Is(NK.Conditional)).ToList();
                    var first = contacts.FirstOrDefault() ?? conditional.FirstOrDefault();
                    var hitChances = contacts.Select(row => row[Ashen.Generated.Core2HitKeys.HitChance]).ToList();
                    var currentChances = contacts.Select(row => row[NK.CurrentHitChance]).ToList();
                    JToken Uniform(List<JToken> chances) => chances.Count > 0 && chances.All(chance => JToken.DeepEquals(chance, chances[0])) ? chances[0].DeepClone() : Js.Null();
                    var group = (JObject)first.DeepClone();
                    group[NK.Contacts] = new JArray(contacts.Select(row => row.DeepClone()));
                    group[NK.ConditionalContacts] = new JArray(conditional.Select(row => row.DeepClone()));
                    group[NK.ConditionalOnly] = contacts.Count == 0;
                    group[NK.HitChances] = new JArray(hitChances.Select(chance => chance.DeepClone()));
                    group[NK.CurrentHitChances] = new JArray(currentChances.Select(chance => chance.DeepClone()));
                    group[Ashen.Generated.Core2HitKeys.InRange] = contacts.All(row => row.Is(Ashen.Generated.Core2HitKeys.InRange));
                    group[NK.CurrentInRange] = contacts.Any(row => row.Is(NK.CurrentInRange));
                    group[NK.AllCurrentInRange] = contacts.All(row => row.Is(NK.CurrentInRange));
                    group[Ashen.Generated.Core2HitKeys.HitChance] = Uniform(hitChances);
                    group[NK.CurrentHitChance] = Uniform(currentChances);
                    grouped.Add(group);
                }
                var affordable = source.Or0(K.Stamina) >= costs.Num(K.Energy) && source.Or0(K.Mana) >= costs.Num(K.Mana);
                var precision = Math.Min(0, plan.Arr(DK.Targets).OfType<JObject>().Select(row => row.Num(DK.PrecisionDelta)).DefaultIfEmpty(0).Min());
                rows.Add(new JObject
                {
                    [NK.Id] = plan[DK.Route].DeepClone(), [DK.Available] = plan[DK.Available].DeepClone(),
                    [K.Reason] = !plan.Is(DK.Available) ? (JToken)RV.ReasonRange : !affordable ? (JToken)NV.ReasonStamina : Js.Null(),
                    [DK.AdvanceSp] = plan[DK.AdvanceSp].DeepClone(), [DK.PrecisionDelta] = Js.N(precision), [Ashen.Generated.Core2ReactionKeys.Costs] = costs, [NK.Affordable] = affordable, [DK.Targets] = grouped,
                });
            }
            var selected = rows.OfType<JObject>().FirstOrDefault(row => row.Str(NK.Id) == rangeRoute) ?? throw new InvalidOperationException(NM.UnknownRoute);
            return new JObject
            {
                [CK.Version] = 1, [CK.Enabled] = State(c).Obj(CK.Policy)[CK.Enabled].DeepClone(), [NK.SelectedRoute] = rangeRoute, [NK.ChoiceRequired] = false,
                [DK.Targets] = selected[DK.Targets].DeepClone(), [NK.Routes] = rows,
            };
        }

        /// <summary>
        /// incomingRangeScope(ctx, group, defender): while a reaction is played against an incoming attack, the attack's quote for the defender is the
        /// context a Counter reply nested in the reaction prices its reach against. Returns the call that puts the previous scope back.
        /// </summary>
        public static Action IncomingScope(CombatState c, ExpansionGroup group, JObject defender)
        {
            var root = group?.Carrier?.Obj(DK.RangePlan);
            if (State(c) == null || root == null) return () => { };
            var priorContext = c.RangeContext;
            var priorIncoming = c.RangeIncoming;
            var key = ActorKey(c, defender);
            group.Targets.TryGetValue(defender.Str(K.Id), out var seat);
            var id = seat?.Contacts.FirstOrDefault()?.Str(DK.RangeContactId);
            var targets = Js.Items(root[DK.Targets]).OfType<JObject>().ToList();
            var contact = targets.FirstOrDefault(row => row.Str(DK.ContactId) == id) ?? targets.FirstOrDefault(row => row.Str(DK.TargetKey) == key);
            if (contact != null)
            {
                c.RangeContext = new JObject { [DK.SourceKey] = root[DK.SourceKey].DeepClone(), [DK.Targets] = new JArray(contact.DeepClone()) };
                c.RangeIncoming = new JObject { [DK.RootId] = root[DK.RootId].DeepClone(), [Ashen.Generated.Core2ReactionKeys.ContactId] = contact[DK.ContactId].DeepClone(), [DK.SourceKey] = root[DK.SourceKey].DeepClone(), [DK.TargetKey] = key };
            }
            return () => { c.RangeContext = priorContext; c.RangeIncoming = priorIncoming; };
        }

        /// <summary>rangeHitContact(ctx, source, target, carrier): the row of the committed plan this hit reads, else the live engagement; null without a range state.</summary>
        public static JObject HitContact(CombatState c, JObject source, JObject target, JObject carrier)
        {
            if (State(c) == null) return null;
            var targetKey = ActorKey(c, target);
            var plan = carrier?.Obj(DK.RangePlan);
            var enabled = State(c).Obj(CK.Policy).Is(CK.Enabled);
            if (plan != null)
            {
                JObject committed = Js.Truthy(carrier[DK.RangeContactId]) ? null : SourceContact(c, source, target, carrier, null);
                var rows = Js.Items(plan[DK.Targets]).OfType<JObject>();
                var row = Js.Truthy(carrier[DK.RangeContactId])
                    ? rows.FirstOrDefault(r => r.Str(DK.ContactId) == carrier.Str(DK.RangeContactId) && r.Str(DK.TargetKey) == targetKey)
                    : rows.FirstOrDefault(r => r.Str(DK.TargetKey) == targetKey && JToken.DeepEquals(r[DK.SourceWeaponId] ?? Js.Null(), committed[DK.SourceWeaponId] ?? Js.Null())
                        && r.Num(DK.Coverage) == RangeBand(c, committed[DK.Coverage]) && r.Is(DK.Bypass) == committed.Is(DK.Bypass));
                if (row == null) return new JObject { [DK.InRange] = false, [DK.PrecisionDelta] = 0, [K.Reason] = DV.ReasonUnplanned };
                var copy = Js.Spread(row);
                copy[DK.InRange] = !enabled || row.Is(DK.Bypass) || row.Num(DK.EffectiveBand) <= Math.Min(DN.MaxBand, row.Num(DK.Coverage) + (plan.Str(DK.Route) == DV.RouteThrow && row.Is(DK.ThrowCapable) ? 1 : 0));
                return copy;
            }
            var first = ActionPlan(c, ActorKey(c, source), new List<JObject> { SourceContact(c, source, target, carrier, null) }, DV.RouteAdvance, c.RangeContext).Arr(DK.Targets)[0] as JObject;
            var result = Js.Spread(first);
            result[DK.InRange] = !enabled || first.Is(DK.Bypass) || first.Num(DK.Band) <= first.Num(DK.Coverage);
            return result;
        }

        /// <summary>
        /// rangeCardSourceSnapshots(ctx, def, source, carrier): the piece each damage effect of a card is dealt with, frozen before payment. Returned in
        /// the order of the damage effects; null for a fight without a range state.
        /// </summary>
        public static List<JObject> CardSourceSnapshots(CombatState c, JObject def, JObject source, JObject carrier)
        {
            if (State(c) == null) return null;
            JObject Capture(JObject effect)
            {
                var row = SourceContact(c, source, null, carrier, effect ?? new JObject());
                var profile = SourceProfile(c, carrier, effect ?? new JObject());
                var resolved = CommittedSource(c, source, carrier, effect ?? new JObject(), profile);
                var unarmed = resolved?.Str(DK.SourceType) == DV.SourceUnarmed;
                return new JObject
                {
                    [DK.UsedArmamentId] = unarmed ? Js.Null() : Js.Truthy(resolved?[DK.ItemId]) ? resolved[DK.ItemId].DeepClone() : Js.Null(),
                    [DK.ResolvedRangeSource] = new JObject
                    {
                        [DK.SourceType] = row.Is(DK.Weapon) ? CV.SourceWeapon : unarmed ? DV.SourceUnarmed : DV.SourceSpell,
                        [DK.ItemId] = row[DK.SourceWeaponId].DeepClone(), [CK.Range] = row[DK.Coverage].DeepClone(),
                        [DK.PropertyTags] = row.Is(DK.ThrowCapable) ? new JArray(DV.TagThrow) : new JArray(),
                    },
                };
            }
            var snapshots = Js.Items(def[K.Effects]).OfType<JObject>().Where(effect => effect.Str(K.Op) == Ashen.Generated.CombatOps.Damage).Select(Capture).ToList();
            if (snapshots.Count == 0 && carrier.Obj(CK.CombatProfile)?.Str(CK.Maneuver) == CV.StanceCounter) snapshots.Add(Capture(null));
            return snapshots;
        }
    }
}
