using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Domain.Random;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The combat snapshot service (shipped engine/combatSnapshot.js): a save is a committed combat state, not a
    /// replay instruction. Registries, RNG, queue and buffers stay outside the data; property mounts and the
    /// player's rating meters are re-derived on restore exactly as the shipped engine re-derives them.
    /// </summary>
    public static class CombatSnapshot
    {
        private static readonly Regex LegacyRelicGate = new Regex(Ashen.Generated.CombatPatterns.LegacyRelicGate, RegexOptions.CultureInvariant);

        private static List<JObject> Pile(JObject piles, string name) => Js.Items(piles?[name]).OfType<JObject>().ToList();

        /// <summary>restoreCombatSnapshot: rebuild a live combat from a snapshot without replaying combat start.</summary>
        public static CombatState Restore(CombatData data, Rng rng, JObject snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var saved = (JObject)snapshot.DeepClone();
            var c = new CombatState
            {
                Data = data,
                Rng = rng,
                RatingsRules = Js.Truthy(saved[K.RatingsRules]) ? saved.Obj(K.RatingsRules) : null,
                HandRules = Js.Truthy(saved[K.HandRules]) ? saved.Obj(K.HandRules) : null,
                PendingDiscardDraw = Js.Truthy(saved[K.HandRules]) ? saved.Or0(K.PendingDiscardDraw) : 0,
                EquipmentProfileRuleSnapshot = saved.Obj(K.EquipmentProfileRuleSnapshot),
                RemovedAttackSlotIds = saved[K.RemovedAttackSlotIds] ?? new JArray(),
                EquipmentAttackSlotCount = Js.IsFinite(saved[K.EquipmentAttackSlotCount]) ? saved[K.EquipmentAttackSlotCount] : null,
                ItemUpgradeLevels = saved.Obj(K.ItemUpgradeLevels) ?? new JObject(),
                ItemMounts = saved[K.ItemMounts],
                EquipmentPoolDeficits = saved.Obj(K.EquipmentPoolDeficits),
                EquipmentChanged = saved.Is(K.EquipmentChanged),
                Turn = saved.Num(K.Turn),
                Phase = saved.Str(K.Phase),
                Result = saved.Str(K.Result),
                HandMax = saved.Num(K.HandMax),
                DrawPerTurn = saved[K.DrawPerTurn],
                Player = saved.Obj(K.Player),
                Enemies = Js.Items(saved[K.Enemies]).OfType<JObject>().ToList(),
                Loadout = saved.Obj(K.Loadout),
                Attributes = saved.Obj(K.Attributes),
                DerivedStatRuleSnapshot = Js.Truthy(saved[K.DerivedStatRuleSnapshot]) ? saved[K.DerivedStatRuleSnapshot] : null,
                SwapCostRule = saved[K.SwapCostRule],
                SwapsLeft = saved.Or0(K.SwapsLeft),
                EventLog = Js.Items(saved[K.EventLog]).OfType<JObject>().ToList(),
                IdCounter = saved.Or0(K.IdCounter),
                EmitDepth = saved.Or0(K.EmitDepth),
                Skills = saved.Obj(K.Skills) ?? new JObject(),
                SkillXp = saved.Obj(K.SkillXp) ?? new JObject(),
                CoreTags = saved[K.CoreTags] as JArray ?? new JArray(),
            };
            var piles = saved.Obj(K.Piles);
            c.Piles.Draw = Pile(piles, K.Draw);
            c.Piles.Hand = Pile(piles, K.Hand);
            c.Piles.Discard = Pile(piles, K.Discard);
            c.Piles.Exhaust = Pile(piles, K.Exhaust);
            // Detach the documents the combat now owns, so moving them between piles never clones them.
            foreach (var doc in c.Enemies.Concat(c.Piles.Draw).Concat(c.Piles.Hand).Concat(c.Piles.Discard).Concat(c.Piles.Exhaust).Concat(c.EventLog).ToList()) doc.Remove();
            foreach (var entry in Js.Items(saved[K.TriggerState]).OfType<JArray>())
            {
                var key = Js.Str(entry[0]);
                var m = key == null ? null : LegacyRelicGate.Match(key);
                if (m != null && m.Success) key = string.Join(V.KeySeparator, V.PropertyGate, m.Groups[V.GroupOwner].Value, V.RelicKind, m.Groups[V.GroupRelic].Value, m.Groups[V.GroupIndex].Value);
                var st = entry.Count > 1 ? entry[1] as JObject : null;
                c.TriggerState[key] = new TriggerGate { Fires = Js.Or0(st?[K.Fires]), Turn = Js.Coalesce(st?[K.Turn], -1), TurnFires = Js.Or0(st?[K.TurnFires]) };
            }

            Properties.SyncLoadout(c);
            Properties.SyncRelics(c);
            Properties.SyncClass(c);
            if (c.RatingsRules != null) Ratings.Refresh(c);
            else if (c.Player != null && c.Loadout != null)
                CombatStart.StampPoiseMax(data, c.Player, Equipment.PoiseThreshold(data, c.Loadout, c.Player[K.RelicIds] ?? new JArray(), c.Player.Str(K.ClassId),
                    c.ItemUpgradeLevels, c.Attributes, c.DerivedStatRuleSnapshot));
            return c;
        }

        /// <summary>serializeCombatSnapshot: the JSON-safe state of one fully committed combat turn.</summary>
        public static JObject Serialize(CombatState c)
        {
            if (c.Buffer != null || c.Queue.Count > 0) throw new InvalidOperationException(M.StillResolving);
            var s = new JObject { [K.Version] = c.Data.Engine[K.SnapshotVersion]?.DeepClone() };
            if (c.RatingsRules != null) s[K.RatingsRules] = c.RatingsRules.DeepClone();
            if (c.HandRules != null)
            {
                s[K.HandRules] = c.HandRules.DeepClone();
                s.Put(K.PendingDiscardDraw, c.PendingDiscardDraw);
            }
            void Put(string key, JToken value)
            {
                if (value != null) s[key] = value.DeepClone();
            }
            Put(K.EquipmentProfileRuleSnapshot, c.EquipmentProfileRuleSnapshot);
            Put(K.EquipmentAttackSlotCount, c.EquipmentAttackSlotCount);
            Put(K.RemovedAttackSlotIds, c.RemovedAttackSlotIds);
            Put(K.ItemUpgradeLevels, c.ItemUpgradeLevels);
            Put(K.ItemMounts, c.ItemMounts);
            Put(K.EquipmentPoolDeficits, c.EquipmentPoolDeficits);
            s.Put(K.EquipmentChanged, c.EquipmentChanged);
            s.Put(K.Turn, c.Turn);
            s[K.Phase] = Js.S(c.Phase);
            s[K.Result] = Js.S(c.Result);
            s.Put(K.HandMax, c.HandMax);
            Put(K.DrawPerTurn, c.DrawPerTurn);
            Put(K.Player, c.Player);
            s[K.Enemies] = new JArray(c.Enemies.Select(e => e.DeepClone()));
            s[K.Loadout] = c.Loadout?.DeepClone() ?? Js.Null();
            s[K.Attributes] = c.Attributes?.DeepClone() ?? Js.Null();
            s[K.DerivedStatRuleSnapshot] = c.DerivedStatRuleSnapshot?.DeepClone() ?? Js.Null();
            Put(K.SwapCostRule, c.SwapCostRule);
            s.Put(K.SwapsLeft, c.SwapsLeft);
            s[K.Piles] = new JObject
            {
                [K.Draw] = new JArray(c.Piles.Draw.Select(x => x.DeepClone())),
                [K.Hand] = new JArray(c.Piles.Hand.Select(x => x.DeepClone())),
                [K.Discard] = new JArray(c.Piles.Discard.Select(x => x.DeepClone())),
                [K.Exhaust] = new JArray(c.Piles.Exhaust.Select(x => x.DeepClone())),
            };
            s[K.EventLog] = new JArray(c.EventLog.Select(x => x.DeepClone()));
            s[K.TriggerState] = new JArray(c.TriggerState.Entries().Select(kv =>
                new JArray(kv.Key, Js.Obj(K.Fires, kv.Value.Fires, K.Turn, kv.Value.Turn, K.TurnFires, kv.Value.TurnFires))));
            s.Put(K.IdCounter, c.IdCounter);
            s.Put(K.EmitDepth, c.EmitDepth);
            s[K.Skills] = c.Skills.DeepClone();
            s[K.SkillXp] = c.SkillXp.DeepClone();
            s[K.CoreTags] = c.CoreTags.DeepClone();
            return s;
        }
    }
}
