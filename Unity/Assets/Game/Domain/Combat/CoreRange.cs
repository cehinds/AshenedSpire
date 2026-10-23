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
        public static JObject ActionPlan(CombatState c, string sourceKey, IReadOnlyList<JObject> contacts, string route)
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
                var actual = EngagementBand(c, sourceKey, targetKey);
                var distance = actual;
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

        /// <summary>
        /// A hook the range lineage has to follow (web engine/combatRangeLineage.js extendRangeEnvelope): an owned trigger whose effect strikes
        /// another creature. The lineage of a hit through such a trigger belongs to the range package (WP-12); a quote that meets one is refused.
        /// </summary>
        public static bool HasHostileHooks(CombatState c, JObject owner, IEnumerable<CombatAction> actions)
        {
            var ids = new List<string>();
            foreach (var p in (owner.Obj(K.Statuses) ?? new JObject()).Properties()) ids.Add(p.Name);
            foreach (var action in actions)
                if (action.Effect.Str(K.Op) == Ashen.Generated.CombatOps.ApplyStatus && (action.Effect.Str(K.Target) == V.TargetSelf || action.Effect.Str(K.Target) == V.RefOwner) && !ids.Contains(action.Effect.Str(K.Status))) ids.Add(action.Effect.Str(K.Status));
            var rows = new List<JObject>();
            var changed = true;
            while (changed)
            {
                changed = false;
                rows = HookRows(c, owner, ids);
                foreach (var trigger in rows)
                    foreach (var effect in Js.Items(trigger[K.Do]).OfType<JObject>())
                        if (effect.Str(K.Op) == Ashen.Generated.CombatOps.ApplyStatus && (effect.Str(K.Target) == V.TargetSelf || effect.Str(K.Target) == V.RefOwner) && !ids.Contains(effect.Str(K.Status))) { ids.Add(effect.Str(K.Status)); changed = true; }
            }
            return rows.Any(trigger => Js.Items(trigger[K.Do]).OfType<JObject>().Any(effect => IsHostileOp(effect.Str(K.Op)) && !Friendly.Contains(effect.Str(K.Target))));
        }

        private static List<JObject> HookRows(CombatState c, JObject owner, IEnumerable<string> statusIds)
        {
            var rows = new List<JObject>();
            if (owner.Str(K.Kind) == V.Player)
            {
                if (Js.Truthy(owner[K.StanceId])) foreach (var t in Js.Items(c.Data.Stances.Get(owner.Str(K.StanceId)).Arr(K.Hooks))) rows.Add((JObject)t);
                if (c.PropertyMounts != null && c.PropertyMounts.TryGetValue(OwnerKey(owner), out var mounts))
                    foreach (var key in mounts.SortedKeys())
                        foreach (var rule in mounts[key].Rules)
                            foreach (var t in Js.Items(rule[DK.Triggers])) rows.Add((JObject)t);
            }
            foreach (var id in statusIds.Distinct())
                if (c.Data.Statuses.Has(id)) foreach (var t in Js.Items(c.Data.Statuses.Get(id).Arr(K.Hooks))) rows.Add((JObject)t);
            return rows;
        }

        private static string OwnerKey(JObject owner) => owner.Str(K.Id);

        /// <summary>quoteRangeActions(ctx, source, actions, { route, preview }): the plan a committed action's hostile effects need. A preview changes nothing.</summary>
        public static JObject Quote(CombatState c, JObject source, List<CombatAction> actions, string route = null, bool preview = false)
        {
            if (State(c) == null) return null;
            route ??= DV.RouteAdvance;
            var contacts = new List<JObject>();
            for (var index = 0; index < actions.Count; index++)
            {
                var action = actions[index];
                var eff = action.Effect;
                if (!IsHostileOp(eff.Str(K.Op))) continue;
                if (eff.Str(K.Target) == V.TargetRandomEnemy) throw new NotSupportedException(DM.RandomTargetNotPorted);
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
                if (!preview)
                {
                    action.Meta = (action.Meta ?? ActionMeta.Empty()).Clone();
                    action.Meta.RangeTargetBatches = batches;
                }
            }
            if (HasHostileHooks(c, source, actions)) throw new NotSupportedException(DM.RangeLineageNotPorted);
            return ActionPlan(c, ActorKey(c, source), contacts, route);
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
        /// acceptRangeActions(ctx, source, actions, carrier, rootId): the plan a committed action carries. The Stamina to advance is paid here; a plan
        /// that needs one is the range package's (WP-12), so only an action in range is accepted.
        /// </summary>
        public static JObject Accept(CombatState c, JObject source, List<CombatAction> actions, JObject carrier, string rootId)
        {
            if (State(c) == null) return null;
            if (carrier.Is(DK.RangeReturnAuthorization)) throw new NotSupportedException(DM.CounterNotPorted);
            var plan = carrier.Obj(DK.RangePlan) ?? Quote(c, source, actions, carrier.Is(DK.RangeRoute) ? carrier.Str(DK.RangeRoute) : DV.RouteAdvance);
            if (!plan.Is(DK.Available)) throw new InvalidOperationException(DM.RangeRouteUnreachable);
            var extra = plan.Num(DK.AdvanceSp);
            if (!carrier.Is(DK.RangePlan) && extra > 0) throw new NotSupportedException(DM.RangeAdvanceNotPorted);
            var paid = carrier.Obj(DK.RangePlan)?.Obj(DK.Paid) ?? AddCosts(carrier.Obj(DK.RangeBaseCosts) ?? new JObject { [K.Energy] = 0, [K.Stamina] = 0, [K.Mana] = 0 }, plan);
            var accepted = Js.Spread(plan);
            accepted[DK.RootId] = rootId;
            accepted[DK.Paid] = paid;
            carrier[DK.RangePlan] = accepted;
            foreach (var key in new[] { DK.RangeBaseCosts, DK.RangeRoute, DK.RangeQuotedPlan, DK.RangeReturnAuthorization, DK.RangeCounterReservation }) carrier.Remove(key);
            foreach (var action in actions)
            {
                action.Card = Js.Spread(action.Card, new JObject { [DK.RangePlan] = accepted });
                action.Card.Remove(DK.RangeReturnAuthorization);
                action.Card.Remove(DK.RangeCounterReservation);
                action.Meta = (action.Meta ?? ActionMeta.Empty()).Clone();
                action.Meta.EnergySpent = (action.Meta.EnergySpent ?? 0) + extra;
                action.Meta.StaminaSpent = (action.Meta.StaminaSpent ?? 0) + extra;
            }
            return accepted;
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
            var first = ActionPlan(c, ActorKey(c, source), new List<JObject> { SourceContact(c, source, target, carrier, null) }, DV.RouteAdvance).Arr(DK.Targets)[0] as JObject;
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
