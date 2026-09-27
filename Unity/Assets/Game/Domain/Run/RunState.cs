using System;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RM = Ashen.Generated.RunMessages;
using RV = Ashen.Generated.RunValues;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Run
{
    /// <summary>
    /// The creation choices createRunState accepts (all optional; absent is the class default): the creation mode, a
    /// custom attribute allocation (normalizeRunAttributes), derived-stat override layers (modeModifiers, runModifiers,
    /// explicitOverride — the Settings tier dial is an explicitOverride defaults layer), a saved derived-stat rule
    /// snapshot to be born under, the starting kit, hands, armour and relic, and the profile meta that discovers pieces.
    /// </summary>
    public sealed class RunOptions
    {
        public string AttributeMode;
        public JObject Attributes;
        public JObject DerivedStatOptions;
        public JObject DerivedStatRuleSnapshot;
        public string StartingKitId;
        public JObject StartingHands;
        public string StartingArmourId;
        public string StartingRelicId;
        public JObject ProfileMeta;

        /// <summary>The oracle's options object ({ attributeMode, startingKitId, … }).</summary>
        public static RunOptions FromJson(JObject o)
        {
            if (o == null) return new RunOptions();
            return new RunOptions
            {
                AttributeMode = o.Str(RK.AttributeMode),
                Attributes = o.Obj(K.Attributes),
                DerivedStatOptions = o.Obj(RK.DerivedStatOptions),
                DerivedStatRuleSnapshot = o.Obj(K.DerivedStatRuleSnapshot),
                StartingKitId = o.Str(RK.StartingKitId),
                StartingHands = o.Obj(RK.StartingHands),
                StartingArmourId = o.Str(RK.StartingArmourId),
                StartingRelicId = o.Str(RK.StartingRelicId),
                ProfileMeta = o.Obj(RK.ProfileMeta),
            };
        }
    }

    /// <summary>
    /// Run creation (shipped model/state.js createRunState, initializeRunDerivedStats, createIdGen,
    /// createCardInstance, characterLevelOf, derivedOptions and syncZones): a new run at floor 0, act 1, as the JSON
    /// document the shipped save holds (D-037). No RNG is consumed.
    /// </summary>
    public static class RunState
    {
        /// <summary>createIdGen(prefix): 'rc1', 'rc2', … deterministic instance ids.</summary>
        private sealed class IdGen
        {
            private readonly string _prefix;
            private long _n;

            public IdGen(string prefix) => _prefix = prefix;

            public string Next() => _prefix + (++_n).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>characterLevelOf(run): the level the pools derive at; 1 when the ledger is absent.</summary>
        public static double CharacterLevelOf(JObject run)
        {
            var row = run?.Obj(K.Level);
            return row != null && Js.IsInt(row[K.Level]) && row.Num(K.Level) >= 1 ? row.Num(K.Level) : 1;
        }

        /// <summary>createRunState({ seed, classId, registries, ...options }).</summary>
        public static JObject Create(RunData d, uint seed, string classId, RunOptions options = null)
        {
            options ??= new RunOptions();
            var profileMeta = options.ProfileMeta ?? new JObject();

            var classDef = d.Classes.Get(classId);
            var mode = options.AttributeMode ?? CreationStats.DefaultCreationModeId(d);
            var attributes = options.Attributes == null
                ? CreationStats.ClassAttributePreset(d, classId, mode)
                : CreationStats.NormalizeRunAttributes(d, new JObject { [RK.Class] = classId, [RK.AttributeMode] = mode, [K.Attributes] = options.Attributes.DeepClone() }).Obj(K.Attributes);
            var modeSnapshot = CreationStats.CreationModeSnapshot(d, mode);
            var ids = new IdGen(d.RuleStr(RK.Run, RK.InstanceIdPrefix));
            var baseKit = Creation.ResolveStartingKit(d, classId, options.StartingKitId, profileMeta);
            var hands = Creation.ResolveCreationHands(d, classId, options.StartingHands, baseKit);
            var startingKit = Js.Spread(baseKit, hands, options.StartingHands != null ? Js.Obj(RK.Customized, true) : null);
            var startingRelic = Creation.ResolveCreationRelic(d, classId, options.StartingRelicId);
            var startingArmour = Creation.ResolveStartingArmour(d, classId, options.StartingArmourId, profileMeta);
            var loadout = Loadout.CreateLoadout(d, classId, startingKit, startingArmour);
            if (!Js.Truthy(startingKit[K.Baseline]))
                foreach (var slotId in new[] { RK.RightHand, RK.LeftHand })
                {
                    var itemId = startingKit[slotId];
                    if (!Js.Truthy(itemId)) continue;
                    var piece = d.EquipmentRows(K.Armaments).FirstOrDefault(row => JToken.DeepEquals(row[K.Id], itemId));
                    var failures = Loadout.RequirementFailures(d, piece, attributes);
                    if (failures.Count > 0)
                    {
                        var failed = failures[0];
                        throw new InvalidOperationException(RunJs.Fmt(RM.KitRequirementUnmet, startingKit.Str(K.Id), slotId, Js.Str(itemId),
                            failed.Str(RK.AttributeId), RunJs.Show(failed[RK.Required]), RunJs.Show(failed[RK.Actual])));
                    }
                }

            var startingRunMods = Loadout.RunMods(d, loadout, classId);
            var poolBonuses = new JObject();
            foreach (var field in Loadout.PoolFields(d)) poolBonuses.Put(field, startingRunMods.Pool(field));
            var oldMaxHp = classDef.Num(K.MaxHp) + poolBonuses.Num(Loadout.PoolFields(d)[0]);
            var damage = new JObject();
            foreach (var school in RunJs.Strs(d.Combat.Engine[K.DamageSchools])) damage.Put(school, 0);
            var relics = new JArray(startingRelic[K.Id].DeepClone());
            var kitRelic = classDef[RK.KitRelic];
            if (Js.Truthy(kitRelic) && !JToken.DeepEquals(kitRelic, startingRelic[K.Id])) relics.Add(kitRelic.DeepClone());
            var deck = new JArray();
            foreach (var r in StartingDeck.Refs(d, loadout, classId))
            {
                var inst = Js.Obj(K.InstanceId, ids.Next(), K.CardId, r[K.CardId], K.Upgraded, false);
                foreach (var p in r.Properties()) inst[p.Name] = p.Value.DeepClone();
                deck.Add(inst);
            }

            var run = new JObject
            {
                [RK.SchemaVersion] = Js.N(d.RuleNum(RK.Run, RK.SchemaVersion)),
                [RK.ContentVersion] = Js.S(d.ContentVersion),
                [RK.Seed] = Js.N(seed),
                [RK.StreamCounters] = new JObject(),
                [RK.Class] = classId,
                [RK.StartingKitId] = startingKit[K.Id]?.DeepClone(),
                [RK.StartingKitSnapshot] = Creation.StartingKitSnapshot(startingKit),
                [RK.AttributeMode] = mode,
                [RK.AttributeModeSnapshot] = modeSnapshot,
                [K.Attributes] = attributes,
                [RK.LevelUps] = 0,
                [K.Level] = Js.Obj(K.Xp, 0, K.Level, 1, RK.UnspentPoints, 0),
                [K.Skills] = new JObject(),
                [K.CoreTags] = new JArray(),
                [RK.LevelPoints] = 0,
                [RK.Floor] = 0,
                [RK.ActNumber] = 1,
                [RK.SeatOrder] = Creation.DefaultSeatOrder(d),
                [RK.MapNodeId] = Js.Null(),
                [K.Hp] = Js.N(oldMaxHp),
                [K.MaxHp] = Js.N(oldMaxHp),
                [RK.MaxHpAdjustment] = 0,
                [RK.EquipmentPoolBonuses] = poolBonuses,
                [K.EquipmentPoolDeficits] = Js.Obj(K.Hp, 0, K.Mana, 0, K.Stamina, 0),
                [RK.Cinders] = Js.N(Js.Or0(d.Balance[RK.StartingCinders])),
                [RK.SmithingStones] = 0,
                [K.ItemUpgradeLevels] = new JObject(),
                [RK.SmithingRewardClaims] = new JArray(),
                [K.Deck] = deck,
                [K.Loadout] = loadout,
                [K.EquipmentAttackSlotCount] = Js.Null(),
                [K.Relics] = relics,
                [K.DamageBySchoolAdd] = damage,
                [K.Flasks] = new JArray(),
                [K.FlaskCharges] = Creation.CreateFlaskCharges(d.Balance, classDef.Obj(RK.StartingFlaskAllocation)),
                [RK.SeedString] = Js.Null(),
                [RK.MapGraph] = Js.Null(),
                [RK.CombatEntered] = Js.Null(),
                [RK.History] = new JArray(),
                [K.Modifiers] = new JArray(),
            };
            run.Put(K.EquipmentAttackSlotCount, deck.OfType<JObject>().Count(c => c.Str(K.EquipmentRole) == RV.RoleAttack));
            InitializeDerivedStats(d, run, options.DerivedStatRuleSnapshot, options.DerivedStatOptions, false);
            StartingDeck.StampDeck(d, run);
            StartingDeck.OrderStartingDeck(d, run);
            Creation.SyncFlaskGrowth(d, run);
            SyncZones(d, run);
            return run;
        }

        /// <summary>
        /// derivedOptions(registries, extra): the stat layers (equipmentProfiles stripped) plus the host authority and
        /// the attribute ids, class fields and damage schools the derived-stat door validates against.
        /// </summary>
        private static DerivedOptions DerivedOptionsFor(RunData d, DerivedOptions extra)
        {
            JToken StatLayer(JToken layer)
            {
                if (!(layer is JObject o)) return layer;
                var stats = new JObject();
                foreach (var p in o.Properties()) if (p.Name != RK.EquipmentProfiles) stats[p.Name] = p.Value.DeepClone();
                return stats;
            }
            var run = extra?.RunModifiers;
            return new DerivedOptions
            {
                ModeModifiers = StatLayer(extra?.ModeModifiers) as JObject,
                RunModifiers = run is JArray a ? new JArray(a.Select(StatLayer)) : StatLayer(run),
                ExplicitOverride = StatLayer(extra?.ExplicitOverride) as JObject,
                Authority = RV.Host,
                AttributeIds = d.Attributes.Ids.ToList(),
                ClassFields = d.RuleList(RK.Derived, RK.ClassFields),
                DamageSchools = RunJs.Strs(d.Combat.Engine[K.DamageSchools]),
            };
        }

        /// <summary>
        /// initializeRunDerivedStats(run, registries, { snapshot, derivedStatOptions, preserveDeficits }): the one door for
        /// HP, Mana, Stamina, Energy and Draw. A run born here resolves the host rule snapshot (the creation mode's
        /// equipment profiles plus any override layers) and fills every pool. A run that already owns a current
        /// snapshot (ruleset 3 and later) is restored: validated against it and trusted. An older save is migrated once:
        /// missing equipment pool bonuses are inferred from its persisted maxima and its own snapshot, missing pool
        /// deficits are captured from its current/max pools, a missing max-HP adjustment is inferred from the old rule,
        /// and a pre-current snapshot is re-derived under the current rules with its deficits carried.
        /// </summary>
        public static void InitializeDerivedStats(RunData d, JObject run, JObject snapshot, JObject derivedStatOptions, bool preserveDeficits)
        {
            var opts = derivedStatOptions ?? new JObject();
            var modeProfiles = run.Obj(RK.AttributeModeSnapshot)?[RK.EquipmentProfiles];
            JObject modeModifiers;
            if (Js.Truthy(modeProfiles))
            {
                modeModifiers = Js.Spread(opts.Obj(RK.ModeModifiers));
                modeModifiers[RK.EquipmentProfiles] = modeProfiles.DeepClone();
            }
            else modeModifiers = opts.Obj(RK.ModeModifiers);
            var effective = new DerivedOptions
            {
                ModeModifiers = modeModifiers,
                RunModifiers = opts[RK.RunModifiers],
                ExplicitOverride = opts.Obj(RK.ExplicitOverride),
            };
            var existing = snapshot ?? (Js.Truthy(run[K.DerivedStatRuleSnapshot]) ? run[K.DerivedStatRuleSnapshot] : null);
            var classDef = d.Classes.Get(run.Str(RK.Class));
            var poolFields = Loadout.PoolFields(d);
            var current = d.RuleObj(K.Loadout, RK.PoolCurrent);
            var liveEquipmentMods = run.Obj(K.Loadout) != null ? Loadout.RunMods(d, run.Obj(K.Loadout), run.Str(RK.Class)) : null;
            run[K.EquipmentProfileRuleSnapshot] = Js.Truthy(run[K.EquipmentProfileRuleSnapshot])
                ? Loadout.RestoreProfileSnapshot(d, run[K.EquipmentProfileRuleSnapshot])
                : Loadout.CreateProfileSnapshot(d, effective);
            var currentRuleset = d.DerivedStatRules[RK.RulesetVersion];
            var existingRuleset = Js.Get(existing, RK.RulesetVersion);
            var existingIsCurrent = existing != null && Js.IsNum(existingRuleset) && Js.IsNum(currentRuleset)
                && Js.D(existingRuleset) >= d.RuleNum(RK.Derived, RK.CurrentRulesetSince) && Js.D(existingRuleset) <= Js.D(currentRuleset);
            JObject restoredExisting = null;
            if (existing != null) restoredExisting = DerivedStats.RestoreSnapshot(d, existing, DerivedOptionsFor(d, effective));
            var level = CharacterLevelOf(run);
            var attributes = run.Obj(K.Attributes);
            double DeriveOld(string statId) => DerivedStats.Derive(restoredExisting.Obj(K.Rules), statId, attributes, classDef, level).Value;

            // Schema v4 and older: infer the equipment contribution once from the run's own snapshot.
            if (!Js.Truthy(run[RK.EquipmentPoolBonuses]))
            {
                var inferred = new JObject();
                foreach (var field in poolFields) inferred.Put(field, liveEquipmentMods != null ? liveEquipmentMods.Pool(field) : 0);
                if (restoredExisting != null)
                    foreach (var maxField in poolFields)
                    {
                        var persistedMax = run[maxField];
                        var adjustment = maxField == poolFields[0] ? run[RK.MaxHpAdjustment] : Js.N(0);
                        if (!Js.IsFinite(persistedMax) || !Js.IsInt(adjustment)) continue;
                        inferred.Put(maxField, Js.D(persistedMax) - DeriveOld(current.Str(maxField)) - Js.D(adjustment));
                    }
                run[RK.EquipmentPoolBonuses] = inferred;
            }
            foreach (var field in poolFields)
                if (!Js.IsInt(run.Obj(RK.EquipmentPoolBonuses)[field])) throw new InvalidOperationException(RunJs.Fmt(RM.PoolBonusNotInteger, field));
            var hpEquipmentBonus = run.Obj(RK.EquipmentPoolBonuses).Num(poolFields[0]);
            var deficitsAbsent = !Js.Truthy(run[K.EquipmentPoolDeficits]);
            if (deficitsAbsent)
            {
                double Deficit(string maxField, string currentField) =>
                    Js.IsFinite(run[maxField]) && Js.IsFinite(run[currentField]) ? Math.Max(0, run.Num(maxField) - run.Num(currentField)) : 0;
                var deficits = new JObject();
                foreach (var field in poolFields) deficits.Put(current.Str(field), Deficit(field, current.Str(field)));
                run[K.EquipmentPoolDeficits] = deficits;
            }
            foreach (var field in poolFields.Select(f => current.Str(f)))
            {
                var v = run.Obj(K.EquipmentPoolDeficits)?[field];
                if (!Js.IsInt(v) || Js.D(v) < 0) throw new InvalidOperationException(RunJs.Fmt(RM.PoolDeficitInvalid, field));
            }

            // Schema v3 and older: infer the permanent max-HP residual once from the old rule plus current equipment.
            if (run[RK.MaxHpAdjustment] == null)
            {
                if (restoredExisting != null && Js.IsFinite(run[K.MaxHp])) run.Put(RK.MaxHpAdjustment, run.Num(K.MaxHp) - (DeriveOld(RV.StatHp) + hpEquipmentBonus));
                else run.Put(RK.MaxHpAdjustment, 0);
            }
            if (!Js.IsInt(run[RK.MaxHpAdjustment])) throw new InvalidOperationException(RunJs.Fmt(RM.MaxHpAdjustmentNotInteger, RunJs.Show(run[RK.MaxHpAdjustment])));

            var schools = RunJs.Strs(d.Combat.Engine[K.DamageSchools]);
            JObject Zeros()
            {
                var zeros = new JObject();
                foreach (var school in schools) zeros.Put(school, 0);
                return zeros;
            }
            if (existingIsCurrent && Js.Truthy(run[K.DerivedStatRuleSnapshot]))
            {
                var bonuses = run.Obj(RK.EquipmentPoolBonuses);
                foreach (var pair in d.RuleObj(RK.Derived, RK.PersistedStats).Properties())
                {
                    var key = pair.Name;
                    var value = run[key];
                    if (!Js.IsInt(value) || Js.D(value) < 0) throw new InvalidOperationException(RunJs.Fmt(RM.PersistedStatInvalid, key));
                    var equipmentBonus = poolFields.Contains(key) ? bonuses.Num(key) : 0;
                    var expected = Math.Max(0, DeriveOld(Js.Str(pair.Value)) + equipmentBonus);
                    if (Js.D(value) != expected) throw new InvalidOperationException(RunJs.Fmt(RM.PersistedStatContradicts, key, RunJs.NumStr(Js.D(value)), RunJs.NumStr(expected)));
                }
                var expectedMaxHp = Math.Max(1, DeriveOld(RV.StatHp) + hpEquipmentBonus + run.Num(RK.MaxHpAdjustment));
                if (!Js.IsNum(run[K.MaxHp]) || run.Num(K.MaxHp) != expectedMaxHp)
                    throw new InvalidOperationException(RunJs.Fmt(RM.PersistedMaxHpContradicts, RunJs.Key(run[K.MaxHp]), RunJs.NumStr(expectedMaxHp)));
                var stampedDamage = restoredExisting.Obj(RK.RelicModifiers)?[K.DamageBySchoolAdd] is JObject stampedObject ? stampedObject : Zeros();
                if (run[K.DamageBySchoolAdd] == null) run[K.DamageBySchoolAdd] = stampedDamage.DeepClone();
                foreach (var school in schools)
                    if (!Equipment.StrictEquals(Js.Get(run[K.DamageBySchoolAdd], school), Js.N(Js.Or0(stampedDamage[school]))))
                        throw new InvalidOperationException(RunJs.Fmt(RM.PersistedDamageContradicts, school));
            }
            if (existingIsCurrent && Js.Truthy(run[K.DerivedStatRuleSnapshot])
                && new[] { K.MaxHp, K.Hp, K.MaxMana, K.Mana, K.MaxStamina, K.Stamina, K.EnergyMax, K.DrawPerTurn }.All(key => run[key] != null))
                return;

            var host = DerivedOptionsFor(d, effective);
            var hostRules = DerivedStats.ResolveRules(d, d.DerivedStatRules, host);
            var tierSizes = new JObject();
            foreach (var p in hostRules.Obj(K.Rules).Properties()) tierSizes[p.Name] = ((JObject)p.Value)[K.PointsPerTier]?.DeepClone();
            var relicReceipt = Creation.ResolveRelicModifiers(d, run[K.Relics] as JArray, attributes, tierSizes);
            var receipt = existingIsCurrent ? restoredExisting : DerivedStats.CreateSnapshot(d, d.DerivedStatRules, DerivedOptionsFor(d, effective).With(classDef, relicReceipt));
            var rules = receipt.Obj(K.Rules);
            var hp = DerivedStats.Derive(rules, RV.StatHp, attributes, classDef, level);
            var mana = DerivedStats.Derive(rules, RV.StatMana, attributes, classDef, level);
            var stamina = DerivedStats.Derive(rules, RV.StatStamina, attributes, classDef, level);
            var energy = DerivedStats.Derive(rules, RV.StatEnergy, attributes, classDef, level);
            var draw = DerivedStats.Derive(rules, RV.StatDraw, attributes, classDef, level);

            var oldHpMax = run[K.MaxHp];
            var oldHp = run[K.Hp];
            var oldManaMax = run[K.MaxMana];
            var oldMana = run[K.Mana];
            run[K.DerivedStatRuleSnapshot] = RunJs.Clone(receipt);
            var poolBonuses = run.Obj(RK.EquipmentPoolBonuses);
            run.Put(K.MaxHp, Math.Max(1, hp.Value + hpEquipmentBonus + run.Num(RK.MaxHpAdjustment)));
            run.Put(K.MaxMana, Math.Max(0, mana.Value + poolBonuses.Num(K.MaxMana)));
            run.Put(K.MaxStamina, Math.Max(0, stamina.Value + poolBonuses.Num(K.MaxStamina)));
            run.Put(K.EnergyMax, energy.Value);
            run.Put(K.DrawPerTurn, draw.Value);
            var stamped = receipt.Obj(RK.RelicModifiers)?[K.DamageBySchoolAdd];
            run[K.DamageBySchoolAdd] = Js.Truthy(stamped) ? stamped.DeepClone() : Zeros();
            if (preserveDeficits && Js.IsFinite(oldHpMax) && Js.IsFinite(oldHp))
                run.Put(K.Hp, Math.Max(0, run.Num(K.MaxHp) - Math.Max(0, Js.D(oldHpMax) - Js.D(oldHp))));
            else run.Put(K.Hp, run.Num(K.MaxHp));
            if (preserveDeficits && Js.IsFinite(oldManaMax) && Js.D(oldManaMax) > 0 && Js.IsFinite(oldMana))
            {
                var ratio = Math.Max(0, Math.Min(1, Js.D(oldMana) / Js.D(oldManaMax)));
                run.Put(K.Mana, Math.Max(0, Math.Min(run.Num(K.MaxMana), RunJs.Round(ratio * run.Num(K.MaxMana)))));
            }
            else run.Put(K.Mana, run.Num(K.MaxMana));
            run.Put(K.Stamina, run.Num(K.MaxStamina));
            if (deficitsAbsent)
            {
                var deficits = new JObject();
                foreach (var field in poolFields) deficits.Put(current.Str(field), Math.Max(0, run.Num(field) - run.Num(current.Str(field))));
                run[K.EquipmentPoolDeficits] = deficits;
            }
        }

        /// <summary>
        /// The load door's call (engine/save.js loadRun): restore or migrate a saved run's derived stats, carrying its
        /// deficits (initializeRunDerivedStats(run, registries, { preserveDeficits: true })).
        /// </summary>
        public static void RestoreDerivedStats(RunData d, JObject run) => InitializeDerivedStats(d, run, null, null, true);

        /// <summary>syncZones(run): write the zones/collection projection when it changed. Returns whether it did.</summary>
        public static bool SyncZones(RunData d, JObject run)
        {
            var zones = Creation.ProjectZones(d, run, out var collection);
            var current = new JObject();
            if (run[RK.Zones] != null) current[RK.Z] = run[RK.Zones].DeepClone();
            if (run[RK.Collection] != null) current[RK.C] = run[RK.Collection].DeepClone();
            var next = Js.Obj(RK.Z, zones, RK.C, collection);
            if (RunJs.Json(current) == RunJs.Json(next)) return false;
            run[RK.Zones] = zones.DeepClone();
            run[RK.Collection] = collection.DeepClone();
            return true;
        }
    }
}
