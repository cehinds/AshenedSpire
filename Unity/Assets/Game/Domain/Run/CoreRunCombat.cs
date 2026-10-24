using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Newtonsoft.Json.Linq;
using CC = Ashen.Generated.Core2CombatKeys;
using CK = Ashen.Generated.Core2RunKeys;
using CV = Ashen.Generated.Core2CombatValues;
using EK = Ashen.Generated.Core2EnemyKeys;
using EV = Ashen.Generated.Core2EnemyValues;
using HK = Ashen.Generated.Core2HostKeys;
using K = Ashen.Generated.CombatKeys;
using MI = Ashen.Generated.Core2ModuleIds;
using RK = Ashen.Generated.RunKeys;
using SK = Ashen.Generated.Core2RunSnapshotKeys;

namespace Ashen.Domain.Run
{
    /// <summary>
    /// The one door a Core 2 run's fight is built through (web engine/runCombat.js createRunCombat for a configured profile,
    /// docs/design/11 WP-4): the arguments main.js enters a fight with (<see cref="CoreRunRules.CombatArgs"/>), the rating receipt of the
    /// worn gear, the effective attributes, the hand rules this run was born with, and then <see cref="CoreCombatStart.Create"/>.
    /// </summary>
    public static class CoreRunCombat
    {
        private static double Positive(JToken value) => Js.Truthy(value) ? Js.D(value) : 0;

        /// <summary>gameProfileRatingReceipt(registries, run): the five ratings from the attributes and the worn gear, with their sources.</summary>
        public static JObject RatingReceipt(CoreCombatData cd, JObject run)
        {
            var d = cd.Run;
            var ids = Js.Items(cd.Rules[CC.RatingIds]).Select(Js.Str).ToList();
            var subject = d.SubjectOf(run);
            var totals = new JObject();
            foreach (var id in ids) totals.Put(id, 0);
            var gear = CoreGear.EquipmentReceipt(d, run);
            var attributes = CoreGear.EffectiveAttributes(d, run, run.Obj(K.Attributes));
            var stats = CoreRunData.Module(subject, MI.Stats)?.Str(CK.Mode) == Ashen.Generated.Core2RunValues.Configured;
            if (stats)
            {
                var table = run.Obj(K.DerivedStatRuleSnapshot)?.Obj(K.Rules);
                var classDef = d.Run.Classes.Get(run.Str(RK.Class));
                var level = Js.Truthy(run.Obj(K.Level)?[K.Level]) ? run.Obj(K.Level).Num(K.Level) : 1;
                foreach (var id in ids) totals.Put(id, CoreDerivedStats.Derive(d, table, id, attributes, classDef, level).Value);
            }
            var sources = new JArray { Js.Spread(new JObject { [K.Name] = CV.SourceAttributes, [K.Kind] = CV.KindAttribute }, totals) };
            foreach (var source in Js.Items(gear[CK.Sources]).OfType<JObject>())
            {
                var rated = Js.Spread(source);
                rated.Put(CC.RatingPr, 0);
                foreach (var id in ids)
                {
                    rated.Put(id, Positive(source[id]));
                    totals.Put(id, totals.Num(id) + rated.Num(id));
                }
                sources.Add(rated);
            }
            var policy = CoreRunData.Module(subject, MI.Equipment)?.Obj(CC.UpgradeBonuses);
            var loadout = run.Obj(K.Loadout);
            if (stats && policy != null && loadout != null)
            {
                double weaponTier = 0, armorTier = 0;
                var gearModule = CoreRunData.Module(subject, MI.Gear);
                var budgeted = Js.Truthy(gearModule?[CC.ArmorBudget]);
                double Tier(string reference)
                {
                    var value = run.Obj(K.ItemUpgradeLevels)?[reference];
                    return Js.IsInt(value) && Js.D(value) > 0 ? Js.D(value) : 0;
                }
                foreach (var slot in (loadout.Obj(K.Sets) ?? new JObject()).Properties())
                {
                    var sets = slot.Value as JArray;
                    var index = Js.Truthy(loadout.Obj(K.Active)?[slot.Name]) ? (int)loadout.Obj(K.Active).Num(slot.Name) : 0;
                    var id = sets != null && index >= 0 && index < sets.Count ? Js.Str(sets[index]) : null;
                    if (!Js.Truthy(id)) continue;
                    if (slot.Name == RK.Armor)
                    {
                        armorTier = Math.Max(armorTier, Tier(string.Join(Ashen.Generated.CombatValues.ItemRefSeparator, Ashen.Generated.CombatValues.ArmorRefPrefix, run.Str(RK.Class), id)));
                        continue;
                    }
                    var piece = Js.Items(d.Run.Equipment[K.Armaments]).OfType<JObject>().FirstOrDefault(row => row.Str(K.Id) == id);
                    if (piece == null) continue;
                    var reference = string.Join(Ashen.Generated.CombatValues.ItemRefSeparator, Ashen.Generated.CombatValues.ArmamentRefPrefix, id);
                    if (piece.Str(K.Kind) == d.RuleSection(CK.Gear).Str(CK.ShieldKind)) { armorTier = Math.Max(armorTier, Tier(reference)); continue; }
                    var itemTier = Tier(reference);
                    weaponTier = Math.Max(weaponTier, itemTier);
                    if (budgeted && itemTier > 0)
                    {
                        var ar = itemTier * policy.Num(CC.WeaponArPerTier);
                        totals.Put(CC.RatingAr, totals.Num(CC.RatingAr) + ar);
                        sources.Add(new JObject
                        {
                            [K.Name] = Js.Truthy(piece[K.Name]) ? piece[K.Name].DeepClone() : Js.S(id), [K.Kind] = Ashen.Generated.Core2RunValues.EquipmentKind,
                            [CK.SourceId] = reference, [CK.Slot] = slot.Name, [CK.ItemId] = id,
                            [CC.RatingAr] = ar, [CC.RatingDr] = 0, [CC.RatingPr] = 0, [CC.RatingWard] = 0, [CC.RatingPoise] = 0, [CC.WeaponTier] = itemTier,
                        });
                    }
                }
                var arBonus = budgeted ? 0 : weaponTier * policy.Num(CC.WeaponArPerTier);
                var drBonus = budgeted ? 0 : armorTier * policy.Num(CC.ArmorDrPerTier);
                totals.Put(CC.RatingAr, totals.Num(CC.RatingAr) + arBonus);
                totals.Put(CC.RatingDr, totals.Num(CC.RatingDr) + drBonus);
                if (arBonus != 0 || drBonus != 0)
                    sources.Add(new JObject
                    {
                        [K.Name] = CV.SourceUpgradeTiers, [K.Kind] = Ashen.Generated.Core2RunValues.EquipmentKind,
                        [CK.SourceId] = CV.SourceUpgradeTiersId,
                        [CC.RatingAr] = arBonus, [CC.RatingDr] = drBonus, [CC.RatingPr] = 0, [CC.RatingWard] = 0, [CC.RatingPoise] = 0,
                        [CC.WeaponTier] = weaponTier, [CC.ArmorTier] = armorTier,
                    });
            }
            return new JObject
            {
                [CK.Totals] = totals, [CK.Sources] = sources,
                [CC.BaseEvasionBonus] = gear.Obj(CK.Totals)[CC.Evasion]?.DeepClone() ?? Js.N(0),
                [CC.BasePrecisionBonus] = gear.Obj(CK.Totals)[CC.Precision]?.DeepClone() ?? Js.N(0),
            };
        }

        /// <summary>runHandRules(registries, run, settings): the behaviour options at their defaults and the run's own three count rows.</summary>
        public static JObject HandRules(CoreCombatData d, JObject run)
        {
            var rules = (JObject)d.HandDefaults.DeepClone();
            var table = run.Obj(K.DerivedStatRuleSnapshot)?.Obj(K.Rules);
            var rows = new JObject();
            foreach (var id in Js.Items(d.Section(CC.Hand)[CC.RowIds]).Select(Js.Str))
                rows[id] = CoreDerivedStats.ResolvedRow(d.Run, table, id, run.Str(RK.Class));
            rules[CC.Rows] = rows;
            return rules;
        }

        /// <summary>
        /// runCombatEnd(run, combat): the pools a fight leaves behind are written back to the run, so the next fight opens where this one ended — the proposal
        /// cinders (once), what the player learned of the enemies, the equipment the fight left (a Core deck keeps its granted cards), the Ashen Blight pools,
        /// the flasks that stay drunk and their charges, Hit Points, Mana and Stamina with their maxima, the equipment pool deficits, the consumables the fight
        /// left and every companion one fight closer to leaving.
        /// </summary>
        public static void End(CoreCombatData d, JObject run, CombatState c)
        {
            var player = c.Player;
            var rewards = c.Fields.Obj(Ashen.Generated.Core2StatusKeys.ProposalRewards);
            if (rewards != null && !rewards.Is(Ashen.Generated.Core2SnapshotKeys.Settled))
            {
                run[RK.Cinders] = Js.N(run.Num(RK.Cinders) + Math.Max(0, rewards.Num(RK.Cinders)));
                rewards[Ashen.Generated.Core2SnapshotKeys.Settled] = true;
            }
            CoreKnowledge.Reconcile(run, c);
            var subject = d.Run.SubjectOf(run);
            if (CoreRunData.IsConfigured(subject) && CoreRunData.ModuleConfigured(subject, MI.Cards))
            {
                run[K.Loadout] = c.Loadout.DeepClone();
                run[K.DrawPerTurn] = (player[K.DrawPerTurn] ?? c.DrawPerTurn)?.DeepClone();
                run[K.EnergyMax] = player[K.MaxStamina]?.DeepClone();
                CoreRunRules.ReconcileGearGrants(d.Run, run);
            }
            if (Js.Truthy(c.Fields[CC.CombatExpansionVersion]) && Js.D(c.Fields[CC.CombatExpansionVersion]) == d.ExpandedVersion)
            {
                run[SK.AshenBlight] = player[SK.AshenBlight]?.DeepClone();
                run[CK.AshenBlightBasePools] = player[CC.BaseResourceMaxima]?.DeepClone();
            }
            run[K.Flasks] = player[K.Flasks]?.DeepClone();
            if (player[K.FlaskCharges] is JObject charges) run[K.FlaskCharges] = charges.DeepClone();
            run[K.Hp] = player[K.Hp]?.DeepClone();
            run[K.Mana] = player[K.Mana]?.DeepClone();
            run[K.Stamina] = Js.N(Math.Min(player.Num(K.Stamina), player.Num(K.MaxStamina)));
            foreach (var key in new[] { K.MaxHp, K.MaxMana, K.MaxStamina }) run[key] = player[key]?.DeepClone();
            run[K.EquipmentPoolDeficits] = c.EquipmentPoolDeficits.DeepClone();
            if (c.Fields[CC.Consumables] is JObject consumables)
            {
                var kept = new JObject();
                foreach (var held in consumables.Properties()) if (Js.IsInt(held.Value) && Js.D(held.Value) >= 1) kept[held.Name] = held.Value.DeepClone();
                run[CC.Consumables] = kept;
            }
            if (run[CK.Companions] is JArray companions)
            {
                var staying = new JArray();
                foreach (var row in companions.OfType<JObject>())
                {
                    var next = (JObject)row.DeepClone();
                    next[HK.CombatsLeft] = Js.N(row.Num(HK.CombatsLeft) - 1);
                    if (next.Num(HK.CombatsLeft) >= 1) staying.Add(next);
                }
                run[CK.Companions] = staying;
            }
        }

        /// <summary>createRunCombat({registries, rng, run, enemyIds, encounter, settings}) for a configured run.</summary>
        public static CombatState Create(CoreCombatData d, Rng rng, JObject run, string encounterId, JObject settings = null, IReadOnlyList<string> enemyIds = null)
        {
            var args = CoreRunRules.CombatArgs(d.Run, run, encounterId, settings, enemyIds);
            var player = args.Obj(K.Player);
            var receipt = RatingReceipt(d, run);
            player[CC.Ratings] = receipt[CK.Totals].DeepClone();
            player[CC.RatingSources] = receipt[CK.Sources].DeepClone();
            player[K.Attributes] = CoreGear.EffectiveAttributes(d.Run, run, run.Obj(K.Attributes));
            player[CC.BaseEvasionBonus] = receipt[CC.BaseEvasionBonus].DeepClone();
            player[CC.BasePrecisionBonus] = receipt[CC.BasePrecisionBonus].DeepClone();
            args[K.HandRules] = HandRules(d, run);
            args[CC.ReactionRules] = run[SK.ReactionRules]?.DeepClone();
            args[CC.CombatExpansionRules] = run[SK.CombatExpansionRules]?.DeepClone();
            // A run that carries enemy knowledge (a ruleset that opens it, never a configured run's) accepts the fight as an encounter of its ledger.
            JObject knowledgeRun = null;
            if (run.Obj(EK.EnemyKnowledgeState) != null)
            {
                knowledgeRun = Js.Spread(run);
                var profile = Ashen.Domain.Combat.CoreKnowledgeLedger.Empty();
                var enemies = Js.Items(args[K.EnemyIds]).Select(Js.Str).ToList();
                var encounter = Ashen.Domain.Combat.CoreKnowledgeLedger.OpenEncounter(knowledgeRun, Js.Truthy(run[RK.MapNodeId]) ? Js.Str(run[RK.MapNodeId]) : EV.KnowledgeNodeDefault,
                    Js.Truthy(args[CK.EncounterId]) ? Js.Str(args[CK.EncounterId]) : string.Join(Ashen.Generated.CombatValues.ListSeparator, enemies), enemies, profile);
                var state = knowledgeRun.Obj(EK.EnemyKnowledgeState);
                args[CC.Knowledge] = new JObject
                {
                    [CC.Rules] = run[EK.EnemyKnowledgeRules].DeepClone(), [CC.Encounter] = encounter.DeepClone(), [CC.Bankable] = state[EK.Bankable].DeepClone(),
                    [EK.Profiles] = new JObject { [Ashen.Generated.CombatValues.Player] = Ashen.Domain.Combat.CoreKnowledgeLedger.Merge(state.Obj(EK.Pending), profile) },
                };
            }
            var statuses = new JArray(Js.Items(args[K.PlayerStatuses]).Select(s => s.DeepClone()));
            foreach (var s in Js.Items(args[CC.StartStatuses])) statuses.Add(s.DeepClone());
            args[K.PlayerStatuses] = statuses;
            var combat = CoreCombatStart.Create(d, rng, args);
            if (knowledgeRun != null) run[EK.EnemyKnowledgeState] = knowledgeRun[EK.EnemyKnowledgeState].DeepClone();
            return combat;
        }
    }
}
