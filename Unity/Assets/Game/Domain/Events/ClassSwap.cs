using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;
using EK = Ashen.Generated.EventKeys;
using EV = Ashen.Generated.EventValues;
using EM = Ashen.Generated.EventMessages;

namespace Ashen.Domain.Events
{
    /// <summary>
    /// The class swap (shipped model/classSwap.js swapRunClass; plan phase 5c) — the Turncoat's Mirror's op: the run's
    /// class (its core card) is replaced, the class tracks start over, tree picks the new class has no seat for are
    /// dropped, armour the new class cannot wear is set aside (its free set is worn), the deck is restamped, a
    /// classSwapped history row is written and the zones projection follows. Deck, relics, armaments, attributes and
    /// weapon skills stay the run's.
    /// </summary>
    public static class ClassSwap
    {
        private static double SkillLevel(JObject run, string skillId)
        {
            var row = run.Obj(K.Skills)?.Obj(skillId);
            return row != null && Js.IsInt(row[K.Level]) ? row.Num(K.Level) : 0;
        }

        /// <summary>
        /// swapRunClass(registries, run, classId) → { from, to, fromLevel, droppedTags, resetTracks, droppedArmour }.
        /// Throws by name on an unknown class; a swap to the run's own class changes nothing.
        /// </summary>
        public static JObject Swap(EventsData d, JObject run, string classId)
        {
            if (!d.Run.Classes.Has(classId)) throw new InvalidOperationException(RunJs.Fmt(EM.UnknownSwapClass, classId));
            var from = run.Str(RK.Class);
            var fromLevel = SkillLevel(run, Skills.ClassSkillId(from));
            if (from == classId)
                return Js.Obj(EK.From, from, EK.To, classId, EK.FromLevel, fromLevel, EK.DroppedTags, new JArray(), EK.ResetTracks, new JArray(), EK.DroppedArmour, new JArray());
            var permitted = new HashSet<string>(ClassTree.Rows(d.Rewards, classId).Select(r => r.NodeId), StringComparer.Ordinal);
            var before = run[K.CoreTags] as JArray ?? new JArray();
            var droppedTags = before.Where(t => !permitted.Contains(Js.Str(t) ?? V.Undefined)).Select(t => t.DeepClone()).ToList();
            run[K.CoreTags] = new JArray(before.Where(t => permitted.Contains(Js.Str(t) ?? V.Undefined)).Select(t => t.DeepClone()));
            var skills = run.Obj(K.Skills) ?? new JObject();
            var prefix = V.ClassSkillPrefix + V.KeySeparator;
            var resetTracks = skills.Properties().Select(p => p.Name).Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var id in resetTracks) run.Obj(K.Skills)?.Remove(id);
            var droppedArmour = new List<string>();
            var armour = d.Run.EquipmentRows(K.Armour).ToList();
            var sets = run.Obj(K.Loadout)?.Obj(K.Sets);
            if (sets != null && sets[V.Armor] is JArray worn)
            {
                bool Wears(JToken id) => Js.Truthy(id) && armour.Any(o => o.Str(K.ClassId) == classId && o.Str(K.Id) == Js.Str(id));
                var free = armour.FirstOrDefault(o => o.Str(K.ClassId) == classId && o.Str(RK.Unlock) == string.Empty);
                foreach (var id in worn)
                    if (Js.Truthy(id) && !Wears(id)) droppedArmour.Add(string.Join(V.ItemRefSeparator, V.ArmorRefPrefix, from ?? V.Undefined, RunJs.Key(id)));
                var next = new JArray(worn.Select(id => Wears(id) ? id.DeepClone() : Js.Null()));
                sets[V.Armor] = next;
                if (free != null && !Js.Includes(next, free.Str(K.Id))) next[0] = free[K.Id].DeepClone();
                var active = run.Obj(K.Loadout).Obj(K.Active);
                if (active != null) active.Put(V.Armor, Math.Max(0, next.ToList().FindIndex(id => Js.Truthy(id))));
            }
            run[RK.Class] = classId;
            if (Js.Truthy(run[K.Attributes]) && Js.Truthy(run[K.Loadout])) StartingDeck.StampDeck(d.Run, run);
            if (run[RK.History] is JArray history)
                history.Add(Js.Obj(K.Kind, EV.SwapKind, EK.From, from, EK.To, classId, EK.FromLevel, fromLevel, EK.DroppedTags, new JArray(droppedTags),
                    EK.DroppedArmour, new JArray(droppedArmour), RK.ActNumber, run[RK.ActNumber]?.DeepClone(), RK.Floor, run[RK.Floor]?.DeepClone(),
                    RK.MapNodeId, Js.Nullish(run[RK.MapNodeId]) ? Js.Null() : run[RK.MapNodeId].DeepClone()));
            RunState.SyncZones(d.Run, run);
            return Js.Obj(EK.From, from, EK.To, classId, EK.FromLevel, fromLevel, EK.DroppedTags, new JArray(droppedTags.Select(t => t.DeepClone())),
                EK.ResetTracks, new JArray(resetTracks), EK.DroppedArmour, new JArray(droppedArmour));
        }
    }
}
