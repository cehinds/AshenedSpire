using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using CCK = Ashen.Generated.Core2CombatKeys;
using CK = Ashen.Generated.Core2RunKeys;
using HK = Ashen.Generated.Core2HostKeys;
using CM = Ashen.Generated.Core2RunMessages;
using CV = Ashen.Generated.Core2RunValues;
using K = Ashen.Generated.CombatKeys;
using MI = Ashen.Generated.Core2ModuleIds;
using PC = Ashen.Generated.Core2ProfileContentKeys;
using PK = Ashen.Generated.Core2ProfileKeys;
using RK = Ashen.Generated.RunKeys;
using RM = Ashen.Generated.RunMessages;
using RV = Ashen.Generated.RunValues;
using SK = Ashen.Generated.Core2RunSnapshotKeys;
using V = Ashen.Generated.CombatValues;
using VK = Ashen.Generated.Core2VersionKeys;

namespace Ashen.Domain.Run
{
    /// <summary>The creation choices a configured Core 2 run accepts (all optional; absent is the class default).</summary>
    public sealed class CoreRunOptions
    {
        public string AttributeMode;
        public JObject Attributes;
        public JObject DerivedStatOptions;
        public string StartingKitId;
        public string StartingArmourId;
        public string StartingRelicId;
        public JObject ProfileMeta;
        public string RunId;

        /// <summary>The oracle's options object ({ attributeMode, ... }).</summary>
        public static CoreRunOptions FromJson(JObject o, JObject profileMeta)
        {
            if (o == null) return new CoreRunOptions { ProfileMeta = profileMeta };
            return new CoreRunOptions
            {
                AttributeMode = o.Str(RK.AttributeMode),
                Attributes = o.Obj(K.Attributes),
                DerivedStatOptions = o.Obj(RK.DerivedStatOptions),
                StartingKitId = o.Str(RK.StartingKitId),
                StartingArmourId = o.Str(RK.StartingArmourId),
                StartingRelicId = o.Str(RK.StartingRelicId),
                ProfileMeta = o.Obj(RK.ProfileMeta) ?? profileMeta,
                RunId = o.Str(CK.RunId),
            };
        }
    }

    /// <summary>
    /// Core 2 run creation (shipped model/state.js createRunState for a configured profile, model/gameProfileRun.js and the
    /// run reads in model/loadout.js, model/ashenBlight.js and model/startingStatConfig.js): a new run at floor 0, act 1,
    /// as the JSON document the web saves. It stands beside <see cref="RunState"/> (the 405 path, which stays until the
    /// owner rules on D-164). No RNG is consumed: the run's stream counters are empty after creation.
    /// </summary>
    public static class CoreRunRules
    {
        // ------------------------------------------------------------------ the class unlock gate and the starter deck

        /// <summary>
        /// gameProfileClassAvailable(subject, meta, classId): the profile's initial classes plus one of its sequence per run
        /// the profile's ledger (<c>meta.gameProfileUnlocks[profileId].completedRunIds</c>) records.
        /// </summary>
        public static bool ClassAvailable(CoreRunData d, JObject profileMeta, string classId)
        {
            var subject = d.Profile;
            var rules = CoreRunData.Content(subject, PC.ClassUnlocks) as JObject;
            if (!CoreRunData.ModuleConfigured(subject, MI.ClassSelection) || rules == null) return true;
            var ledger = profileMeta?.Obj(CK.GameProfileUnlocks)?.Obj(d.ProfileId);
            var completed = ledger?[CK.CompletedRunIds] is JArray ids ? ids.Count : 0;
            var open = Js.Items(rules[CK.Initial]).Select(Js.Str).Concat(Js.Items(rules[CK.Sequence]).Take(completed).Select(Js.Str));
            return open.Contains(classId);
        }

        /// <summary>How many more finished runs open a class (0 when it is open or the profile has no gate): its place in the unlock sequence less the runs the ledger holds (the web's "Play 1 game to unlock.").</summary>
        public static int GamesToUnlock(CoreRunData d, JObject profileMeta, string classId)
        {
            if (ClassAvailable(d, profileMeta, classId)) return 0;
            var rules = CoreRunData.Content(d.Profile, PC.ClassUnlocks) as JObject;
            var at = Js.Items(rules?[CK.Sequence]).Select(Js.Str).ToList().IndexOf(classId);
            if (at < 0) return 0;
            var ledger = profileMeta?.Obj(CK.GameProfileUnlocks)?.Obj(d.ProfileId);
            var completed = ledger?[CK.CompletedRunIds] is JArray ids ? ids.Count : 0;
            return Math.Max(0, at + 1 - completed);
        }

        /// <summary>
        /// gameProfileCompletedRunMeta(subject, meta, { finishId, victory }): a finished run joins the profile's ledger once (by its finish id) when its outcome qualifies
        /// (<c>classUnlocks.qualifyingOutcomes</c>), which opens the next class of the sequence. Returns the class ids it unlocked; <paramref name="profileMeta"/> is written in place.
        /// </summary>
        public static List<string> CompletedRun(CoreRunData d, JObject profileMeta, bool victory, string finishId)
        {
            var none = new List<string>();
            var rules = CoreRunData.Content(d.Profile, PC.ClassUnlocks) as JObject;
            if (!CoreRunData.IsConfigured(d.Profile) || rules == null) return none;
            if (string.IsNullOrEmpty(finishId)) throw new InvalidOperationException(CM.CompletionNeedsFinishId);
            var unlocks = profileMeta.Obj(CK.GameProfileUnlocks) ?? (JObject)(profileMeta[CK.GameProfileUnlocks] = new JObject());
            var ledger = unlocks.Obj(d.ProfileId) ?? (JObject)(unlocks[d.ProfileId] = new JObject { [CK.CompletedRunIds] = new JArray() });
            var done = ledger.Arr(CK.CompletedRunIds) ?? (JArray)(ledger[CK.CompletedRunIds] = new JArray());
            if (Js.Includes(done, finishId)) return none;
            if (!Js.Includes(rules[HK.QualifyingOutcomes], victory ? V.Victory : V.Defeat)) return none;
            var count = done.Count;
            done.Add(finishId);
            var sequence = Js.Items(rules[CK.Sequence]).ToList();
            return count < sequence.Count && Js.Truthy(sequence[count]) ? new List<string> { Js.Str(sequence[count]) } : none;
        }

        /// <summary>The ids of the classes available at a number of completed runs, in profile order.</summary>
        public static List<string> AvailableClasses(CoreRunData d, JObject profileMeta) =>
            d.Run.Classes.Ids.Where(id => ClassAvailable(d, profileMeta, id)).ToList();

        /// <summary>gameProfileStarterDeck(subject, classId): the card ids of the opening deck, or null when the profile has none.</summary>
        public static List<string> StarterDeck(CoreRunData d, JObject subject, string classId)
        {
            var deck = CoreRunData.Content(subject, PC.StarterDeck) as JObject;
            if (!CoreRunData.ModuleConfigured(subject, MI.Cards) || deck == null) return null;
            var ids = new List<string>();
            void Repeat(JToken row) { for (var i = 0; i < Js.D(Js.Get(row, CK.Copies)); i++) ids.Add(Js.Str(Js.Get(row, CK.Card))); }
            var own = deck.Obj(CK.ClassDecks)?[classId];
            if (own is JArray rows)
            {
                foreach (var row in rows) Repeat(row);
                return ids;
            }
            foreach (var row in Js.Items(deck[CK.Shared])) Repeat(row);
            var special = deck.Obj(CK.ClassSpecials)?[classId];
            if (!Js.Truthy(special)) throw new InvalidOperationException(RunJs.Fmt(CM.NoClassSpecial, classId));
            for (var i = 0; i < Js.D(deck[CK.ClassSpecificCopies]); i++) ids.Add(Js.Str(special));
            return ids;
        }

        // ------------------------------------------------------------------ stat options

        /// <summary>
        /// gameProfileStatOptions(subject, options, classId): the profile's <c>stats</c> content as an explicit override layer, the
        /// primary weights replaced (retaining the secondary terms and authored bounds), the class's pool bases and the hand rows.
        /// </summary>
        public static JObject StatOptions(CoreRunData d, JObject options, string classId)
        {
            options = options ?? new JObject();
            var subject = d.Profile;
            if (!CoreRunData.ModuleConfigured(subject, MI.Stats)) return options;
            var stats = CoreRunData.Content(subject, PC.Stats) as JObject;
            var settings = d.RuleSection(CK.StatOptions);
            var aliases = settings.Obj(CK.Aliases) ?? new JObject();
            var rows = new JObject();
            foreach (var attribute in (stats.Obj(CK.LevelGains) ?? new JObject()).Properties())
                foreach (var benefit in ((JObject)attribute.Value).Properties())
                {
                    var id = aliases[benefit.Name] != null ? aliases.Str(benefit.Name) : benefit.Name;
                    rows[id] = Js.Spread(rows.Obj(id), Js.Obj(attribute.Name, benefit.Value.DeepClone()));
                }
            foreach (var pool in (stats.Obj(CK.PoolBasesByClass)?.Obj(classId) ?? new JObject()).Properties())
                rows[pool.Name] = Js.Spread(rows.Obj(pool.Name), Js.Obj(K.Base, pool.Value.DeepClone()));
            foreach (var id in RunJs.Strs(settings[CK.HandRows]))
                rows[id] = Js.Spread(rows.Obj(id), Js.Obj(K.Base, stats[CK.HandBase]?.DeepClone(), stats.Str(CK.HandAttribute), 1,
                    CK.AttributeBaseline, settings[CK.HandBaseline]?.DeepClone(), CK.ByClass, new JObject()));
            var explicitOverride = options.Obj(RK.ExplicitOverride);
            var merged = Js.Spread(options);
            merged[RK.ExplicitOverride] = Js.Spread(explicitOverride, Js.Obj(K.Rules, Js.Spread(explicitOverride?.Obj(K.Rules), rows)));
            return merged;
        }

        private static DerivedOptions DerivedOptionsFor(CoreRunData d, JObject options)
        {
            JToken StatLayer(JToken layer)
            {
                if (!(layer is JObject o)) return layer;
                var stats = new JObject();
                foreach (var p in o.Properties()) if (p.Name != RK.EquipmentProfiles) stats[p.Name] = p.Value.DeepClone();
                return stats;
            }
            var run = options?[RK.RunModifiers];
            return new DerivedOptions
            {
                ModeModifiers = StatLayer(options?[RK.ModeModifiers]) as JObject,
                RunModifiers = run is JArray a ? new JArray(a.Select(StatLayer)) : StatLayer(run),
                ExplicitOverride = StatLayer(options?[RK.ExplicitOverride]) as JObject,
                Authority = RV.Host,
                AttributeIds = d.Run.Attributes.Ids.ToList(),
                ClassFields = d.Run.RuleList(RK.Derived, RK.ClassFields),
                DamageSchools = RunJs.Strs(d.Run.Combat.Engine[K.DamageSchools]),
            };
        }

        // ------------------------------------------------------------------ rule snapshots

        private static JObject RangePolicy(CoreRunData d, JObject profileMeta)
        {
            var policy = RunJs.Clone(d.RangePolicy);
            var settings = profileMeta?.Obj(CK.Settings) ?? new JObject();
            foreach (var key in d.RuleSection(CK.RangeSettingKeys).Properties())
                if (settings[Js.Str(key.Value)] != null) policy[key.Name] = settings[Js.Str(key.Value)].DeepClone();
            if (!RangePolicyValid(d, policy)) throw new InvalidOperationException(CM.InvalidRangePolicy);
            return policy;
        }

        private static bool RangePolicyValid(CoreRunData d, JObject policy)
        {
            var allowed = d.RangePolicy.Properties().Select(p => p.Name).ToList();
            var counts = new[] { Ashen.Generated.Core2RangePolicyKeys.AdvanceSpPerBand, Ashen.Generated.Core2RangePolicyKeys.ReachPrecisionBonus, Ashen.Generated.Core2RangePolicyKeys.ThrowPrecisionPenalty };
            return policy.Properties().All(p => allowed.Contains(p.Name))
                && Js.D(policy[Ashen.Generated.Core2RangePolicyKeys.Version]) == Js.D(d.RangePolicy[Ashen.Generated.Core2RangePolicyKeys.Version])
                && policy[Ashen.Generated.Core2RangePolicyKeys.Enabled]?.Type == JTokenType.Boolean
                && counts.All(k => Js.IsInt(policy[k]) && policy.Num(k) >= 0);
        }

        // ------------------------------------------------------------------ gear, pools and ashen blight

        /// <summary>ashenBlightBonuses(owner).attributes and .maxStaminaPenalty: what the run's chosen blight tiers add. Zero for a new run.</summary>
        private static JObject BlightAttributes(CoreRunData d, JObject owner, out double staminaPenalty)
        {
            var rules = d.RuleSection(CK.AshenBlight);
            var paths = rules.Obj(CK.PathAttributes) ?? new JObject();
            var attributes = new JObject();
            foreach (var p in paths.Properties()) attributes.Put(Js.Str(p.Value), 0);
            double penalty = 0;
            foreach (var row in Js.Items(owner?.Obj(SK.AshenBlight)?[CK.Milestones]).OfType<JObject>())
            {
                var path = row.Str(CK.Path);
                if (path == null || paths[path] == null) continue;
                attributes.Put(paths.Str(path), attributes.Num(paths.Str(path)) + row.Num(CK.Tier));
                if (path == rules.Str(CK.StaminaPenaltyPath)) penalty = Math.Max(penalty, row.Num(CK.Tier));
            }
            staminaPenalty = penalty;
            return attributes;
        }

        /// <summary>effectiveAshenBlightAttributes(owner): the allocation plus the blight tiers' attribute points (an expanded run only).</summary>
        public static JObject EffectiveBlightAttributes(CoreRunData d, JObject owner)
        {
            var allocated = owner?.Obj(K.Attributes) ?? new JObject();
            var result = RunJs.Clone(allocated);
            if (owner?.Num(SK.CombatExpansionVersion) != d.Versions.Num(VK.CombatExpansionVersion)) return result;
            foreach (var p in BlightAttributes(d, owner, out _).Properties()) result.Put(p.Name, Js.Or0(result[p.Name]) + Js.D(p.Value));
            return result;
        }

        private static double BlightStaminaPenalty(CoreRunData d, JObject run)
        {
            BlightAttributes(d, run, out var penalty);
            return run.Num(SK.CombatExpansionVersion) == d.Versions.Num(VK.CombatExpansionVersion) ? penalty : 0;
        }

        /// <summary>
        /// runMods(registries, loadout, classId, run).pools / .startStatuses: the worn pieces' run-scoped mods, the pool bonuses of
        /// resolved gear replacing the pieces' own, plus the gear receipt's HP, Mana and Stamina.
        /// </summary>
        public static RunModsResult RunMods(CoreRunData d, JObject loadout, string classId, JObject run)
        {
            var subject = run ?? new JObject { [SK.GameProfile] = d.Profile, [K.Loadout] = loadout, [RK.Class] = classId };
            var gear = CoreGear.EquipmentReceipt(d, subject);
            var resolvedRefs = new HashSet<string>(Js.Items(gear[CK.Sources]).Select(s => Js.Str(Js.Get(s, CK.SourceId))), StringComparer.Ordinal);
            var data = d.Run;
            var fields = data.Equipment.Obj(K.ModFields) ?? new JObject();
            var poolFields = Loadout.PoolFields(data);
            var pools = poolFields.ToDictionary(f => f, f => 0d);
            var stacks = new OrderedMap<double>();
            double swapCostDelta = 0;
            foreach (var piece in Combat.Equipment.EquippedPieces(data.Combat, loadout, classId, new JObject()))
            {
                var pieceRef = piece.Str(K.Kind) == V.Armor
                    ? string.Join(V.ItemRefSeparator, V.ArmorRefPrefix, classId, piece.Str(K.Id))
                    : string.Join(V.ItemRefSeparator, V.ArmamentRefPrefix, piece.Str(K.Id));
                var resolvedPiece = resolvedRefs.Contains(pieceRef);
                foreach (var raw in Js.Items(piece[K.Mods]))
                {
                    var mod = Loadout.ParseMod(raw);
                    var spec = mod != null ? fields.Obj(mod.Field) : null;
                    if (spec == null || spec.Str(RK.Scope) != RV.RunScope) continue;
                    var apply = spec.Str(K.Apply);
                    var isPool = apply != null && poolFields.Contains(apply);
                    if (resolvedPiece && isPool) continue;
                    if (isPool) pools[apply] = mod.Add ? pools[apply] + mod.Value : mod.Value;
                    else if (apply == RV.SwapCostApply) swapCostDelta = mod.Add ? swapCostDelta + mod.Value : mod.Value;
                    else if (apply == RV.StartStatusApply)
                    {
                        var status = spec.Str(K.Status) ?? V.Undefined;
                        var prev = stacks.TryGetValue(status, out var s) ? s : 0;
                        stacks[status] = mod.Add ? prev + mod.Value : mod.Value;
                    }
                }
            }
            var totals = gear.Obj(CK.Totals);
            foreach (var pair in d.RuleSection(CK.Gear).Obj(CK.PoolStats).Properties())
                if (pools.ContainsKey(pair.Name)) pools[pair.Name] += totals.Num(Js.Str(pair.Value));
            var result = new RunModsResult { SwapCostDelta = swapCostDelta };
            foreach (var f in poolFields) result.Pools.Add(new KeyValuePair<string, double>(f, pools[f]));
            foreach (var e in stacks.Entries())
                if (e.Value > 0) result.StartStatuses.Add(Js.Obj(K.Status, e.Key, K.Stacks, e.Value));
            return result;
        }

        /// <summary>moveEquipmentPool(holder, maxField, nextMax, carriedDeficit): move a vessel, carrying its absolute deficit (and Stamina's surplus).</summary>
        private static double MovePool(CoreRunData d, JObject holder, string maxField, double nextMax, JToken carriedDeficit)
        {
            var currentField = d.Run.RuleObj(K.Loadout, RK.PoolCurrent).Str(maxField);
            if (!Js.IsFinite(holder[maxField]) || !Js.IsFinite(holder[currentField]) || !Js.IsFinite(Js.N(nextMax)))
                throw new InvalidOperationException(RunJs.Fmt(CM.MovePoolNeedsFinite, maxField, currentField));
            var oldMax = holder.Num(maxField);
            var surplus = currentField == RV.StatStamina ? Math.Max(0, holder.Num(currentField) - oldMax) : 0;
            var observed = Math.Max(0, oldMax - holder.Num(currentField));
            var prior = Js.IsInt(carriedDeficit) && Js.D(carriedDeficit) >= 0 ? Js.D(carriedDeficit) : observed;
            var represented = Math.Min(prior, oldMax);
            var deficit = Math.Max(0, prior + observed - represented);
            holder.Put(maxField, nextMax);
            holder.Put(currentField, Math.Max(0, nextMax - deficit) + surplus);
            return deficit;
        }

        /// <summary>reconcileRunLoadoutHp(registries, run, { adoptEquipmentBonuses }): every pool re-derived from the snapshot, carrying each deficit.</summary>
        public static bool ReconcilePools(CoreRunData d, JObject run, bool adoptEquipmentBonuses)
        {
            var data = d.Run;
            var poolFields = Loadout.PoolFields(data);
            var current = data.RuleObj(K.Loadout, RK.PoolCurrent);
            if (run?.Obj(K.DerivedStatRuleSnapshot)?.Obj(K.Rules) == null
                || !poolFields.All(f => Js.IsFinite(run[f]) && Js.IsFinite(run[current.Str(f)]))) return false;
            if (!Js.IsInt(run[RK.MaxHpAdjustment])) throw new InvalidOperationException(RM.ReconcileNeedsAdjustment);
            var classDef = data.Classes.Get(run.Str(RK.Class));
            var live = RunMods(d, run.Obj(K.Loadout), run.Str(RK.Class), run);
            var liveBonuses = new JObject();
            foreach (var f in poolFields) liveBonuses.Put(f, live.Pool(f));
            var bonuses = adoptEquipmentBonuses || Js.Truthy(CoreRunData.Module(d.SubjectOf(run), MI.Gear)?[CK.ArmorBudget]) ? liveBonuses : run.Obj(RK.EquipmentPoolBonuses) ?? liveBonuses;
            var level = RunState.CharacterLevelOf(run);
            var expanded = run.Num(SK.CombatExpansionVersion) == d.Versions.Num(VK.CombatExpansionVersion);
            var attributes = CoreGear.EffectiveAttributes(d, run, EffectiveBlightAttributes(d, run));
            var staminaPenalty = expanded ? BlightStaminaPenalty(d, run) : 0;
            var rules = run.Obj(K.DerivedStatRuleSnapshot).Obj(K.Rules);
            var deficits = new Dictionary<string, double>();
            var hpField = poolFields[0];
            var hp = CoreDerivedStats.Derive(d, rules, RV.StatHp, attributes, classDef, level);
            deficits[hpField] = MovePool(d, run, hpField, Math.Max(1, hp.Value + bonuses.Num(hpField) + run.Num(RK.MaxHpAdjustment)), run.Obj(K.EquipmentPoolDeficits)?[current.Str(hpField)]);
            foreach (var f in poolFields.Skip(1))
            {
                var statId = current.Str(f);
                var derived = CoreDerivedStats.Derive(d, rules, statId, attributes, classDef, level);
                var penalty = statId == RV.StatStamina ? staminaPenalty : 0;
                deficits[f] = MovePool(d, run, f, Math.Max(0, derived.Value + bonuses.Num(f) - penalty), run.Obj(K.EquipmentPoolDeficits)?[statId]);
            }
            run[RK.EquipmentPoolBonuses] = Js.Spread(bonuses);
            if (expanded)
            {
                run.Put(K.EnergyMax, run.Num(K.MaxStamina));
                var basePools = new JObject();
                foreach (var f in poolFields)
                {
                    var derived = CoreDerivedStats.Derive(d, rules, current.Str(f), run.Obj(K.Attributes), classDef, level).Value;
                    var adjustment = f == hpField ? run.Num(RK.MaxHpAdjustment) : 0;
                    basePools.Put(f, Math.Max(f == hpField ? 1 : 0, derived + bonuses.Num(f) + adjustment));
                }
                run[CK.AshenBlightBasePools] = basePools;
            }
            var ordered = new JObject();
            foreach (var f in poolFields) ordered.Put(current.Str(f), deficits[f]);
            run[K.EquipmentPoolDeficits] = ordered;
            return true;
        }

        /// <summary>
        /// initializeRunDerivedStats(run, registries, { derivedStatOptions, preserveDeficits: false }) for a run being born: the
        /// equipment profile snapshot, the host rule snapshot (relic modifiers folded in) and every pool filled to its maximum.
        /// </summary>
        private static void InitializeDerivedStats(CoreRunData d, JObject run, JObject derivedStatOptions)
        {
            var data = d.Run;
            var modeProfiles = run.Obj(RK.AttributeModeSnapshot)?[RK.EquipmentProfiles];
            var options = Js.Spread(derivedStatOptions);
            if (Js.Truthy(modeProfiles)) options[RK.ModeModifiers] = Js.Spread(derivedStatOptions?.Obj(RK.ModeModifiers), Js.Obj(RK.EquipmentProfiles, modeProfiles.DeepClone()));
            var effective = DerivedOptionsFor(d, options);
            var classDef = data.Classes.Get(run.Str(RK.Class));
            var attributes = CoreGear.EffectiveAttributes(d, run, EffectiveBlightAttributes(d, run));
            var blightPenalty = run.Num(SK.CombatExpansionVersion) == d.Versions.Num(VK.CombatExpansionVersion) ? BlightStaminaPenalty(d, run) : 0;
            run[K.EquipmentProfileRuleSnapshot] = EquipmentProfileSnapshot(d, effective);
            foreach (var field in Loadout.PoolFields(data))
                if (!Js.IsInt(run.Obj(RK.EquipmentPoolBonuses)[field])) throw new InvalidOperationException(RunJs.Fmt(RM.PoolBonusNotInteger, field));
            if (!Js.IsInt(run[RK.MaxHpAdjustment])) throw new InvalidOperationException(RunJs.Fmt(RM.MaxHpAdjustmentNotInteger, RunJs.Show(run[RK.MaxHpAdjustment])));
            var source = data.DerivedStatRules;
            var hostRules = CoreDerivedStats.ResolveRules(d, source, effective);
            var tierSizes = new JObject();
            foreach (var p in hostRules.Obj(K.Rules).Properties())
            {
                var size = CoreDerivedStats.RuleTierSize(d, (JObject)p.Value);
                tierSizes[p.Name] = size.HasValue ? Js.N(size.Value) : Js.Null();
            }
            var relicReceipt = Creation.ResolveRelicModifiers(data, run[K.Relics] as JArray, attributes, tierSizes);
            var receipt = CoreDerivedStats.CreateSnapshot(d, source, effective.With(classDef, relicReceipt));
            var rules = receipt.Obj(K.Rules);
            var level = RunState.CharacterLevelOf(run);
            double Stat(string id) => CoreDerivedStats.Derive(d, rules, id, attributes, classDef, level).Value;
            var bonuses = run.Obj(RK.EquipmentPoolBonuses);
            run[K.DerivedStatRuleSnapshot] = RunJs.Clone(receipt);
            run.Put(K.MaxHp, Math.Max(1, Stat(RV.StatHp) + bonuses.Num(K.MaxHp) + run.Num(RK.MaxHpAdjustment)));
            run.Put(K.MaxMana, Math.Max(0, Stat(RV.StatMana) + bonuses.Num(K.MaxMana)));
            run.Put(K.MaxStamina, Math.Max(0, Stat(RV.StatStamina) + bonuses.Num(K.MaxStamina) - blightPenalty));
            run.Put(K.EnergyMax, run.Num(SK.CombatExpansionVersion) == d.Versions.Num(VK.CombatExpansionVersion) ? run.Num(K.MaxStamina) : Stat(RV.StatEnergy));
            run.Put(K.DrawPerTurn, Stat(RV.StatDraw));
            var stamped = receipt.Obj(RK.RelicModifiers)?[K.DamageBySchoolAdd];
            run[K.DamageBySchoolAdd] = Js.Truthy(stamped) ? stamped.DeepClone() : ZeroSchools(data);
            run.Put(K.Hp, run.Num(K.MaxHp));
            run.Put(K.Mana, run.Num(K.MaxMana));
            run.Put(K.Stamina, run.Num(K.MaxStamina));
        }

        private static JObject ZeroSchools(RunData data)
        {
            var zeros = new JObject();
            foreach (var school in RunJs.Strs(data.Combat.Engine[K.DamageSchools])) zeros.Put(school, 0);
            return zeros;
        }

        /// <summary>createEquipmentProfileRuleSnapshot(registries, options): the host-owned equipment scaling rows, resolved once per run.</summary>
        private static JObject EquipmentProfileSnapshot(CoreRunData d, DerivedOptions options) => Loadout.CreateProfileSnapshot(d.Run, options);

        // ------------------------------------------------------------------ gear-granted cards

        /// <summary>
        /// configuredGearGrantInstances(registries, run): the item-owned cards the worn Core gear lends (its <c>grantedCards</c>),
        /// with the ids and shape of a weapon package grant.
        /// </summary>
        public static List<JObject> GearGrantInstances(CoreRunData d, JObject run)
        {
            var subject = d.SubjectOf(run);
            if (!CoreRunData.IsConfigured(subject) || !CoreRunData.ModuleConfigured(subject, MI.Cards) || !(CoreRunData.Content(subject, PC.Gear) is JArray catalog) || run.Obj(K.Loadout) == null)
                return new List<JObject>();
            var gear = d.RuleSection(CK.Gear);
            var rows = catalog.OfType<JObject>().Where(row => row[CK.GrantedCards] is JArray cards && cards.Count > 0).ToList();
            var copies = new Dictionary<string, double>(StringComparer.Ordinal);
            var output = new List<JObject>();
            var classId = run.Str(RK.Class);
            foreach (var piece in Combat.Equipment.EquippedPieces(d.Run.Combat, run.Obj(K.Loadout), classId, new JObject()))
            {
                var armor = piece.Str(K.Kind) == gear.Str(CK.ArmorKind);
                var row = rows.FirstOrDefault(candidate =>
                    (Js.Truthy(candidate.Obj(CK.Source)?[K.Id]) ? candidate.Obj(CK.Source).Str(K.Id) : candidate.Str(K.Id)) == piece.Str(K.Id)
                    && (candidate.Str(K.Kind) == gear.Str(CK.ArmorKind)) == armor
                    && (!Js.Truthy(candidate[CK.Classes]) || Js.Includes(candidate[CK.Classes], classId)));
                if (row == null) continue;
                var owner = armor ? Combat.Equipment.PieceItemRef(piece) : piece.Str(K.Id);
                var source = StartingDeck.GrantSourceFor(StartingDeck.Settings(d.Run), armor ? RK.Armor : RK.Weapon);
                foreach (var grant in Js.Items(row[CK.GrantedCards]).OfType<JObject>())
                {
                    var key = string.Join(V.KeySeparator, owner, grant.Str(CK.Card));
                    copies.TryGetValue(key, out var start);
                    copies[key] = start + grant.Num(CK.Copies);
                    for (var i = start; i < start + grant.Num(CK.Copies); i++)
                        output.Add(Js.Obj(K.InstanceId, string.Join(V.KeySeparator, RV.RoleGranted, owner, grant.Str(CK.Card), RunJs.NumStr(i)),
                            K.CardId, grant.Str(CK.Card), K.Upgraded, false, K.EquipmentRole, RV.RoleGranted, K.GrantedBy, owner, K.GrantSource, source));
                }
            }
            return output;
        }

        /// <summary>reconcileGrantedCards(registries, run) for a Core deck: the authored list plus what the worn gear lends, in place.</summary>
        public static void ReconcileGearGrants(CoreRunData d, JObject run)
        {
            if (!(run[K.Deck] is JArray deck)) run[K.Deck] = deck = new JArray();
            var desired = GearGrantInstances(d, run);
            var wanted = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var w in desired) wanted[w.Str(K.InstanceId) ?? V.Undefined] = w;
            var present = new HashSet<string>(StringComparer.Ordinal);
            if (run[CK.Sideboard] is JArray sideboard)
            {
                var keep = new JArray();
                foreach (var card in sideboard)
                {
                    var inst = card as JObject;
                    var id = inst?.Str(K.InstanceId) ?? V.Undefined;
                    if (!StartingDeck.IsItemOwned(d.Run, inst)) { keep.Add(card); continue; }
                    if (!wanted.ContainsKey(id) || present.Contains(id)) continue;
                    present.Add(id);
                    keep.Add(card);
                }
                run[CK.Sideboard] = keep;
            }
            var kept = new List<JObject>();
            foreach (var inst in deck.OfType<JObject>().ToList())
            {
                if (!StartingDeck.IsItemOwned(d.Run, inst)) { kept.Add(inst); continue; }
                var id = inst.Str(K.InstanceId) ?? V.Undefined;
                if (!wanted.TryGetValue(id, out var want) || present.Contains(id)) continue;
                present.Add(id);
                kept.Add(StartingDeck.AdoptWanted(inst, want));
            }
            deck.RemoveAll();
            foreach (var inst in kept) deck.Add(inst);
            foreach (var w in desired) if (!present.Contains(w.Str(K.InstanceId) ?? V.Undefined)) deck.Add(w);
        }

        // ------------------------------------------------------------------ creation

        /// <summary>createRunState({ seed, classId, registries, profileMeta, ... }) for the configured Core 2 profile.</summary>
        public static JObject Create(CoreRunData d, uint seed, string classId, CoreRunOptions options = null)
        {
            options = options ?? new CoreRunOptions();
            var profile = d.Profile;
            var data = d.Run;
            var profileMeta = options.ProfileMeta ?? new JObject();
            if (!CoreRunData.IsConfigured(profile)) throw new InvalidOperationException(CM.NotConfigured);
            var expanded = d.Versions.Num(VK.CombatExpansionVersion);
            var reactionVersion = d.Versions.Num(VK.ReactionRulesVersion);
            if (!ClassAvailable(d, profileMeta, classId)) throw new InvalidOperationException(RunJs.Fmt(CM.ClassLocked, classId));
            var selection = CoreRunData.ModuleConfigured(profile, MI.ClassSelection);
            var fixedAttributes = selection ? (CoreRunData.Content(profile, PC.ClassAttributes) as JObject)?.Obj(classId) : null;
            if (selection && fixedAttributes == null) throw new InvalidOperationException(RunJs.Fmt(CM.NoAttributes, classId));
            if (fixedAttributes != null && options.Attributes != null && fixedAttributes.Properties().Any(p => !Equipment.StrictEquals(options.Attributes[p.Name], p.Value)))
                throw new InvalidOperationException(CM.FixedAttributes);
            var statOptions = StatOptions(d, options.DerivedStatOptions, classId);
            var classDef = data.Classes.Get(classId);
            var mode = options.AttributeMode ?? CreationStats.DefaultCreationModeId(data);
            var attributes = fixedAttributes != null ? RunJs.Clone(fixedAttributes)
                : options.Attributes == null ? CreationStats.ClassAttributePreset(data, classId, mode)
                : CreationStats.NormalizeRunAttributes(data, new JObject { [RK.Class] = classId, [RK.AttributeMode] = mode, [K.Attributes] = options.Attributes.DeepClone() }).Obj(K.Attributes);
            var modeSnapshot = CreationStats.CreationModeSnapshot(data, mode);
            if (fixedAttributes != null)
            {
                var fixedMode = d.RuleSection(CK.FixedMode);
                var values = fixedAttributes.Properties().Select(p => Js.D(p.Value)).ToList();
                modeSnapshot.Put(K.Baseline, fixedMode.Num(K.Baseline));
                modeSnapshot.Put(RK.BonusPool, values.Sum() - values.Count * fixedMode.Num(K.Baseline));
                modeSnapshot.Put(K.Minimum, fixedMode.Num(K.Minimum));
                modeSnapshot.Put(K.Maximum, values.Max());
            }
            var baseKit = Creation.ResolveStartingKit(data, classId, options.StartingKitId, profileMeta);
            var hands = Creation.ResolveCreationHands(data, classId, null, baseKit);
            var fixedHands = (CoreRunData.Content(profile, PC.Classes) as JArray)?.OfType<JObject>().FirstOrDefault(row => row.Str(K.Id) == classId)?.Obj(CK.Loadout);
            var startingKit = Js.Spread(baseKit, hands, fixedHands);
            var startingRelic = Creation.ResolveCreationRelic(data, classId, options.StartingRelicId);
            var startingArmour = Creation.ResolveStartingArmour(data, classId, options.StartingArmourId, profileMeta);
            var loadout = Loadout.CreateLoadout(data, classId, startingKit, startingArmour);
            if (!Js.Truthy(startingKit[K.Baseline]))
                foreach (var slotId in new[] { RK.RightHand, RK.LeftHand })
                {
                    var itemId = startingKit[slotId];
                    if (!Js.Truthy(itemId)) continue;
                    var piece = data.EquipmentRows(K.Armaments).FirstOrDefault(row => JToken.DeepEquals(row[K.Id], itemId));
                    var failures = Loadout.RequirementFailures(data, piece, attributes);
                    if (failures.Count > 0)
                    {
                        var failed = failures[0];
                        throw new InvalidOperationException(RunJs.Fmt(RM.KitRequirementUnmet, startingKit.Str(K.Id), slotId, Js.Str(itemId),
                            failed.Str(RK.AttributeId), RunJs.Show(failed[RK.Required]), RunJs.Show(failed[RK.Actual])));
                    }
                }
            var startingMods = RunMods(d, loadout, classId, null);
            var poolBonuses = new JObject();
            foreach (var field in Loadout.PoolFields(data)) poolBonuses.Put(field, startingMods.Pool(field));
            var oldMaxHp = classDef.Num(K.MaxHp) + poolBonuses.Num(Loadout.PoolFields(data)[0]);

            var run = RunJs.Clone(d.RuleSection(CK.Template));
            run[SK.GameProfile] = profile.DeepClone();
            run[CK.RunId] = Js.S(options.RunId ?? Guid.NewGuid().ToString());
            run.Put(SK.CombatExpansionVersion, expanded);
            if (Js.D(CoreRunData.Module(profile, MI.Effects)?[CK.FixCoreVersion]) == 1) run[SK.RangePolicy] = RangePolicy(d, profileMeta);
            run.Put(SK.ReactionRulesVersion, reactionVersion);
            run[SK.ReactionRules] = RunJs.Clone(d.ReactionRules);
            run[SK.CombatExpansionRules] = RunJs.Clone(d.CombatExpansionRules);
            var advanced = RunJs.Clone(d.RuleSection(CK.AdvancedConfig));
            advanced.Put(VK.BreakMeterVersion, d.Versions.Num(VK.BreakMeterVersion));
            run[CK.AdvancedConfigSnapshot] = advanced;
            run.Put(RK.SchemaVersion, d.Versions.Num(VK.RunSchemaVersion));
            run[RK.ContentVersion] = Js.S(data.ContentVersion);
            run.Put(RK.Seed, seed);
            run[RK.Class] = Js.S(classId);
            run[RK.StartingKitId] = startingKit[K.Id]?.DeepClone();
            run[RK.StartingKitSnapshot] = Creation.StartingKitSnapshot(startingKit);
            run[RK.AttributeMode] = Js.S(mode);
            run[RK.AttributeModeSnapshot] = modeSnapshot;
            run[K.Attributes] = attributes;
            if (data.Balance[CK.Progression] != null)
            {
                run[CK.ProgressionRuleSnapshot] = data.Balance[CK.Progression].DeepClone();
            }
            else run.Remove(CK.ProgressionRulesVersion);
            run[RK.SeatOrder] = Creation.DefaultSeatOrder(data);
            run.Put(K.Hp, oldMaxHp);
            run.Put(K.MaxHp, oldMaxHp);
            run[RK.EquipmentPoolBonuses] = poolBonuses;
            run[K.EquipmentPoolDeficits] = Js.Obj(K.Hp, 0, K.Mana, 0, K.Stamina, 0);
            run.Put(RK.Cinders, Js.Or0(data.Balance[RK.StartingCinders]));
            run[K.Deck] = new JArray();
            run[K.Loadout] = loadout;
            run[K.EquipmentAttackSlotCount] = Js.Null();
            var relics = new JArray(startingRelic[K.Id].DeepClone());
            var kitRelic = classDef[RK.KitRelic];
            if (Js.Truthy(kitRelic) && !JToken.DeepEquals(kitRelic, startingRelic[K.Id])) relics.Add(kitRelic.DeepClone());
            run[K.Relics] = relics;
            run[K.DamageBySchoolAdd] = ZeroSchools(data);
            run[K.FlaskCharges] = Creation.CreateFlaskCharges(data.Balance, classDef.Obj(RK.StartingFlaskAllocation));
            run.Put(K.EquipmentAttackSlotCount, 0);

            InitializeDerivedStats(d, run, statOptions);
            // stampDeck: the pool reconcile (adopting the worn gear's bonuses and writing the blight-free base maxima); a Core deck
            // is its authored list, so there is nothing to stamp.
            ReconcilePools(d, run, true);
            var starter = StarterDeck(d, d.SubjectOf(run), classId);
            if (starter == null) throw new InvalidOperationException(CM.NoStarterDeck);
            var deck = new JArray();
            for (var i = 0; i < starter.Count; i++)
            {
                if (!data.Cards.Has(starter[i])) throw new InvalidOperationException(RunJs.Fmt(CM.UnknownStarterCard, starter[i]));
                deck.Add(Js.Obj(K.InstanceId, string.Concat(CV.StarterIdPrefix, (i + 1).ToString(CultureInfo.InvariantCulture)), K.CardId, starter[i], K.Upgraded, false));
            }
            run[K.Deck] = deck;
            run[CK.Sideboard] = new JArray();
            run.Put(K.EquipmentAttackSlotCount, 0);
            run[CK.Feats] = new JArray();
            run[K.Skills] = new JObject();
            run[K.CoreTags] = new JArray();
            ReconcileGearGrants(d, run);
            Creation.SyncFlaskGrowth(data, run);
            RunState.SyncZones(data, run);
            return run;
        }

        // ------------------------------------------------------------------ the fight a run enters

        /// <summary>
        /// runCombatPlayer(run): the run fields a fight consumes, by name. A fight opens with the Stamina the combat-start rule gives
        /// (full: the maximum, settling the deficit an equipment swap carried); Mana carries as it is.
        /// </summary>
        public static JObject CombatPlayer(CoreRunData d, JObject run)
        {
            var refill = Js.Truthy(d.RuleSection(CK.Combat)[CK.StaminaRefillsToFull]);
            var player = new JObject();
            void Put(string key, JToken value)
            {
                if (value != null) player[key] = value.DeepClone();
            }
            Put(K.ClassId, run[RK.Class]);
            if (Js.Truthy(run[CK.ClassUnequipped])) player[CK.ClassUnequipped] = true;
            Put(K.Attributes, run[K.Attributes]);
            Put(CK.AllocatedAttributes, run[K.Attributes]);
            Put(SK.AshenBlight, run[SK.AshenBlight]);
            Put(CK.BaseResourceMaxima, run[CK.AshenBlightBasePools]);
            player[K.Level] = Js.N(RunState.CharacterLevelOf(run));
            Put(RK.AttributeMode, run[RK.AttributeMode]);
            Put(K.DerivedStatRuleSnapshot, run[K.DerivedStatRuleSnapshot]);
            Put(K.Skills, run[K.Skills]);
            player[CK.SkillFeats] = run[CK.SkillFeats] is JArray feats ? feats.DeepClone() : new JArray();
            player[CK.CritRules] = new JArray();
            Put(K.CoreTags, run[K.CoreTags]);
            foreach (var key in new[] { K.MaxHp, K.Hp, K.MaxMana, K.Mana, K.MaxStamina }) Put(key, run[key]);
            player[K.Stamina] = refill ? run[K.MaxStamina]?.DeepClone() : run[K.Stamina]?.DeepClone();
            foreach (var key in new[] { K.EnergyMax, K.DrawPerTurn, K.DamageBySchoolAdd, K.EquipmentProfileRuleSnapshot, K.EquipmentAttackSlotCount, K.RemovedAttackSlotIds }) Put(key, run[key]);
            player[CK.SideboardedEquipmentCardIds] = new JArray(Js.Items(run[CK.Sideboard]).Select(c => Js.Get(c, K.InstanceId)?.DeepClone()));
            var deficits = run.Obj(K.EquipmentPoolDeficits);
            if (deficits != null && refill) { deficits = RunJs.Clone(deficits); deficits.Put(RV.StatStamina, 0); }
            Put(K.EquipmentPoolDeficits, deficits ?? run[K.EquipmentPoolDeficits]);
            foreach (var key in new[] { K.ItemUpgradeLevels, K.ItemMounts, RK.ArmamentLevels, K.Deck }) Put(key, run[key]);
            Put(K.RelicIds, run[K.Relics]);
            Put(K.Flasks, run[K.Flasks]);
            Put(K.FlaskCharges, run[K.FlaskCharges]);
            Put(K.Loadout, run[K.Loadout]);
            player[CK.Consumables] = run[CK.Consumables] is JObject consumables ? consumables.DeepClone() : new JObject();
            player[CK.CompanionIds] = new JArray(Js.Items(run[CK.Companions]).Select(c => Js.Get(c, K.Id)?.DeepClone()));
            player[CK.SigilSlots] = run[CK.SigilSlots] is JObject slots ? slots.DeepClone() : new JObject();
            player[CK.AttunedSigils] = run[CK.AttunedSigils] is JArray attuned ? attuned.DeepClone() : new JArray();
            return player;
        }

        /// <summary>
        /// The arguments main.js enterCombat hands createRunCombat to enter <paramref name="encounterId"/> from a fresh run (the call's
        /// arguments only, JSON-plain): the encounter, the seat-tier HP ratio, the run's own start statuses, the swap price rule, the
        /// combat key, the Core version stamps and the run fields the fight consumes. Journeys, legacy dungeons and
        /// Custom Climb are not part of a Core 2 run and are not read; a boss is scaled by the tier it is met at.
        /// </summary>
        public static JObject CombatArgs(CoreRunData d, JObject run, string encounterId, JObject settings = null, IReadOnlyList<string> enemyIds = null)
        {
            settings = settings ?? new JObject();
            var data = d.Run;
            var encounter = encounterId == null ? null : data.Encounters.Get(encounterId);
            double hpMult = 1, damageMult = 1;
            if (encounter != null && run[RK.SeatOrder] is JArray seatOrder)
            {
                var act = run.Num(RK.ActNumber);
                // A BOSS is scaled by the tier it is met at: its seat's tier ratio on HP and move damage times that tier's boss row (main.js combatMods).
                if (Creation.BossTierScale(data, encounter, act, out var bossHp, out var bossDamage))
                {
                    hpMult *= bossHp;
                    damageMult = bossDamage;
                }
                else hpMult *= Creation.SeatTierHpMult(data, Creation.SeatAtTier(seatOrder, act), act);
            }
            var enemyList = enemyIds != null ? new JArray(enemyIds) : encounter[K.Enemies];
            var mapNode = Js.Truthy(run[RK.MapNodeId]) ? Js.Str(run[RK.MapNodeId]) : string.Join(V.ListSeparator, Js.Items(enemyList).Select(Js.Str));
            var args = new JObject
            {
                [CK.EncounterId] = encounter?[K.Id]?.DeepClone(),
                [K.EnemyIds] = enemyList?.DeepClone(),
                [CK.Pool] = encounter?[CK.Pool]?.DeepClone(),
                [CK.Settings] = settings.DeepClone(),
                [K.HpMult] = Js.N(hpMult),
                [CK.EnemyDamageMult] = Js.N(damageMult),
                [K.EnemyStatuses] = new JArray(),
                [K.PlayerStatuses] = new JArray(),
                [CK.StartStatuses] = RunMods(d, run.Obj(K.Loadout), run.Str(RK.Class), run).StartStatuses.DeepClone(),
                [K.SwapCostRule] = RunCombat.ResolveSwapCostRule(data, settings),
                [CK.CombatKey] = Js.S(string.Join(V.ItemRefSeparator, RunJs.Key(run[RK.Seed]), RunJs.Key(run[RK.ActNumber]), RunJs.Key(run[RK.Floor]), mapNode)),
                [SK.CombatExpansionVersion] = run[SK.CombatExpansionVersion]?.DeepClone(),
                [SK.ReactionRulesVersion] = run[SK.ReactionRulesVersion]?.DeepClone(),
                [CK.BreakMeterVersion] = run.Obj(CK.AdvancedConfigSnapshot)?[CK.BreakMeterVersion]?.DeepClone(),
                [SK.RangePolicy] = run[SK.RangePolicy]?.DeepClone() ?? Js.Null(),
                [SK.GameProfile] = PinOf(run),
                [K.Player] = CombatPlayer(d, run),
                // Play in deck order (SPEC 14.1): read here, once, like the other rules.
                [CCK.OrderedDraw] = DeckRules.PlayInDeckOrder(d, settings),
            };
            return args;
        }

        // ------------------------------------------------------------------ pinning

        /// <summary>
        /// Whether a saved run may load under <paramref name="profileHash"/> (D-170, the web's save.js loadRun): a run that pins a profile
        /// loads only under the profile with the same hash. A mismatch is recoverable: the slot is kept, never archived or migrated.
        /// A run with no pin (saved before profiles) loads under any.
        /// </summary>
        public static bool MatchesProfile(JObject run, string profileHash)
        {
            var pinned = run?.Obj(SK.GameProfile);
            return pinned == null || pinned.Str(PK.Hash) == profileHash;
        }

        /// <summary>The pinned profile's id and hash, as a run record states them (D-167); null for a run with no pin.</summary>
        public static JObject PinOf(JObject run)
        {
            var pinned = run?.Obj(SK.GameProfile);
            return pinned == null ? null : Js.Obj(PK.Id, pinned[PK.Id]?.DeepClone(), PK.Hash, pinned[PK.Hash]?.DeepClone());
        }
    }
}
