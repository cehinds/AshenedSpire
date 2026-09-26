using System;
using System.Collections.Generic;
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
    /// Character-creation reads (shipped model/startingKits.js, model/characterCreation.js hands and relic,
    /// model/relicModifiers.js, model/gracerefill.js flask charges, model/flaskgrowth.js, model/zones.js and
    /// model/seats.js).
    /// </summary>
    public static class Creation
    {
        // ------------------------------------------------------------------ starting kits

        private static IEnumerable<JObject> Kits(RunData d) => d.EquipmentRows(RK.StartingKits);

        private static bool IsTrue(JToken t) => t?.Type == JTokenType.Boolean && t.Value<bool>();

        private static List<string> KitPieceIds(JObject kit) =>
            new[] { RK.RightHand, RK.LeftHand }.Select(k => kit?[k]).Where(Js.Truthy).Select(Js.Str).ToList();

        /// <summary>kitIsDiscovered(kit, meta): the baseline always; others once every piece has been found.</summary>
        public static bool KitIsDiscovered(JObject kit, JObject meta)
        {
            if (IsTrue(kit[K.Baseline])) return true;
            var found = new HashSet<string>(RunJs.Strs(meta?[RK.DiscoveredArmaments]).Where(s => s != null), StringComparer.Ordinal);
            return KitPieceIds(kit).All(found.Contains);
        }

        /// <summary>resolveStartingKit(registries, classId, requestedId, meta).</summary>
        public static JObject ResolveStartingKit(RunData d, string classId, string requestedId, JObject meta)
        {
            var cls = d.Classes.Get(classId);
            var baseline = Kits(d).FirstOrDefault(row => row.Str(K.ClassId) == classId && IsTrue(row[K.Baseline]));
            var id = !string.IsNullOrEmpty(requestedId) ? requestedId : baseline?.Str(K.Id);
            if (string.IsNullOrEmpty(id)) throw new InvalidOperationException(RunJs.Fmt(RM.NoBaselineKit, classId));
            if (!RunJs.Strs(cls[RK.EligibleStartingKitIds]).Contains(id)) throw new InvalidOperationException(RunJs.Fmt(RM.KitUnavailable, id, classId));
            var row = Kits(d).FirstOrDefault(entry => entry.Str(K.Id) == id);
            if (row == null || row.Str(K.ClassId) != classId) throw new InvalidOperationException(RunJs.Fmt(RM.KitUnavailable, id, classId));
            if (!KitIsDiscovered(row, meta)) throw new InvalidOperationException(RunJs.Fmt(RM.KitNotDiscovered, id));
            return row;
        }

        /// <summary>startingKitSnapshot(kit).</summary>
        public static JObject StartingKitSnapshot(JObject kit)
        {
            var snapshot = Js.Obj(K.Id, kit[K.Id], K.ClassId, kit[K.ClassId],
                RK.RightHand, Js.Truthy(kit[RK.RightHand]) ? kit[RK.RightHand] : Js.Null(),
                RK.LeftHand, Js.Truthy(kit[RK.LeftHand]) ? kit[RK.LeftHand] : Js.Null());
            if (Js.Truthy(kit[RK.Customized])) snapshot[RK.Customized] = true;
            return snapshot;
        }

        // ------------------------------------------------------------------ character creation

        /// <summary>classCreationConfig(registries, classId).</summary>
        public static JObject ClassCreationConfig(RunData d, string classId)
        {
            var row = d.CharacterCreation?.Obj(K.Classes)?.Obj(classId);
            if (row == null) throw new InvalidOperationException(RunJs.Fmt(RM.NoCreationConfig, classId));
            return row;
        }

        private static List<JObject> ArmourRows(RunData d, string classId) => d.EquipmentRows(K.Armour).Where(o => o.Str(K.ClassId) == classId).ToList();

        /// <summary>armourIsStartingEligible(row, meta, registries, classId): free, listed by creation, or earned.</summary>
        public static bool ArmourIsStartingEligible(RunData d, JObject row, JObject meta, string classId)
        {
            if (row == null) return false;
            if (Js.Truthy(row[RK.SharedSet])) return false;
            if (row.Str(RK.Unlock) == string.Empty) return true;
            if (RunJs.Strs(ClassCreationConfig(d, classId)[RK.ArmourIds]).Contains(row.Str(K.Id))) return true;
            return RunJs.Strs(meta?[RK.Unlocked]).Contains(row.Str(RK.Unlock));
        }

        /// <summary>resolveStartingArmour(registries, classId, requestedId, meta).</summary>
        public static JObject ResolveStartingArmour(RunData d, string classId, string requestedId, JObject meta)
        {
            var rows = ArmourRows(d, classId);
            var free = rows.FirstOrDefault(row => row.Str(RK.Unlock) == string.Empty && !Js.Truthy(row[RK.SharedSet]));
            if (string.IsNullOrEmpty(requestedId))
            {
                if (free == null) throw new InvalidOperationException(RunJs.Fmt(RM.NoFreeArmour, classId));
                return free;
            }
            var chosen = rows.FirstOrDefault(entry => entry.Str(K.Id) == requestedId);
            if (chosen == null) throw new InvalidOperationException(RunJs.Fmt(RM.ArmourUnavailable, requestedId, classId));
            if (!ArmourIsStartingEligible(d, chosen, meta, classId)) throw new InvalidOperationException(RunJs.Fmt(RM.ArmourNotUnlocked, requestedId));
            return chosen;
        }

        private static bool FitsCreationHandSlot(RunData d, string slotId, JObject piece)
        {
            if (string.IsNullOrEmpty(slotId)) return true;
            var slot = d.EquipmentRows(K.Slots).FirstOrDefault(row => row.Str(K.Id) == slotId);
            if (slot == null || piece == null || !Js.Includes(slot[K.Kinds], piece.Str(K.Kind))) return false;
            return !Js.Truthy(piece[K.Hand]) || piece.Str(K.Hand) == RV.EitherHand || piece.Str(K.Hand) == slot.Str(K.Hand);
        }

        /// <summary>resolveCreationHands(registries, classId, requested, fallback): { leftHand, rightHand }.</summary>
        public static JObject ResolveCreationHands(RunData d, string classId, JObject requested, JObject fallback)
        {
            if (!Js.Truthy(requested))
                return Js.Obj(RK.LeftHand, Js.Truthy(fallback[RK.LeftHand]) ? fallback[RK.LeftHand] : Js.Null(),
                    RK.RightHand, Js.Truthy(fallback[RK.RightHand]) ? fallback[RK.RightHand] : Js.Null());
            var allowed = RunJs.Strs(ClassCreationConfig(d, classId)[RK.HandIds]);
            var result = Js.Obj(RK.LeftHand, Js.Truthy(requested[RK.LeftHand]) ? requested[RK.LeftHand] : Js.Null(),
                RK.RightHand, Js.Truthy(requested[RK.RightHand]) ? requested[RK.RightHand] : Js.Null());
            if (Js.Truthy(result[RK.LeftHand]) && result.Str(RK.LeftHand) == result.Str(RK.RightHand))
                throw new InvalidOperationException(RunJs.Fmt(RM.ArmamentBothHands, result.Str(RK.LeftHand)));
            foreach (var p in result.Properties())
            {
                if (!Js.Truthy(p.Value)) continue;
                var id = Js.Str(p.Value);
                if (!allowed.Contains(id)) throw new InvalidOperationException(RunJs.Fmt(RM.HandArmamentUnavailable, p.Name, id, classId));
                var piece = d.EquipmentRows(K.Armaments).FirstOrDefault(row => row.Str(K.Id) == id);
                if (!FitsCreationHandSlot(d, p.Name, piece)) throw new InvalidOperationException(RunJs.Fmt(RM.HandArmamentDoesNotFit, p.Name, id));
            }
            return result;
        }

        /// <summary>resolveCreationRelic(registries, classId, requestedId).</summary>
        public static JObject ResolveCreationRelic(RunData d, string classId, string requestedId)
        {
            var cls = d.Classes.Get(classId);
            var id = !string.IsNullOrEmpty(requestedId) ? requestedId : cls.Str(RK.StartingRelic);
            if (!RunJs.Strs(ClassCreationConfig(d, classId)[K.RelicIds]).Contains(id)) throw new InvalidOperationException(RunJs.Fmt(RM.RelicUnavailable, id, classId));
            return d.Relics.Get(id);
        }

        // ------------------------------------------------------------------ relic modifiers

        private static double TierSizeFor(RunData d, string resource, JObject row, JObject tierSizes)
        {
            if (Js.IsFinite(row[RK.PointsPerTier]) && row.Num(RK.PointsPerTier) > 0) return row.Num(RK.PointsPerTier);
            if (tierSizes != null && Js.IsFinite(tierSizes[resource]) && tierSizes.Num(resource) > 0) return tierSizes.Num(resource);
            var table = d.DerivedStatRules ?? new JObject();
            var authored = table.Obj(RK.Rules)?.Obj(resource) ?? new JObject();
            var per = Js.IsFinite(authored[RK.PointsPerTier]) ? authored.Num(RK.PointsPerTier) : Js.D(table.Obj(K.Defaults)?[RK.PointsPerTier]);
            if (double.IsNaN(per) || double.IsInfinity(per) || per <= 0) throw new InvalidOperationException(RunJs.Fmt(RM.RelicTierSizeUnknown, resource));
            return per;
        }

        /// <summary>resolveRelicModifiers(registries, relicIds, { attributes, tierSizes }): resources, school adds, sources.</summary>
        public static JObject ResolveRelicModifiers(RunData d, JArray relicIds, JObject attributes, JObject tierSizes)
        {
            var resources = new JObject();
            foreach (var id in d.RuleList(K.Relics, RK.ResourceIds)) resources[id] = Js.Obj(RK.Flat, 0, RK.AttributeTiers, new JArray(), K.Total, 0);
            var damage = new JObject();
            foreach (var school in RunJs.Strs(d.Combat.Engine[K.DamageSchools])) damage.Put(school, 0);
            var sources = new JArray();
            foreach (var relicId in Js.Items(relicIds).Select(Js.Str))
            {
                var relic = d.Relics.Get(relicId);
                var rows = Js.Items(relic.Obj(K.Passives)?[K.Modifiers]).ToList();
                for (var index = 0; index < rows.Count; index++)
                {
                    var row = rows[index] as JObject ?? new JObject();
                    var tag = row.Str(K.Tag);
                    var source = Js.Obj(K.RelicId, relicId, RK.Index, index, K.Tag, row[K.Tag]);
                    if (tag == RV.ResourceFlat)
                    {
                        var r = resources.Obj(row.Str(RK.Resource) ?? V.Undefined) ?? throw new InvalidOperationException(RunJs.Fmt(RM.RelicUnknownResource, relicId, index));
                        r.Put(RK.Flat, r.Num(RK.Flat) + row.Num(K.Amount));
                        r.Put(K.Total, r.Num(K.Total) + row.Num(K.Amount));
                        var s = Js.Spread(source);
                        s[RK.Resource] = row[RK.Resource]?.DeepClone();
                        s[K.Value] = row[K.Amount]?.DeepClone();
                        sources.Add(s);
                    }
                    else if (tag == RV.ResourceAttributeTier)
                    {
                        var points = attributes?[row.Str(RK.SourceStat) ?? V.Undefined];
                        if (!Js.IsFinite(points)) throw new InvalidOperationException(RunJs.Fmt(RM.RelicSourceStatNotFinite, relicId, index, row.Str(RK.SourceStat)));
                        var per = TierSizeFor(d, row.Str(RK.Resource), row, tierSizes);
                        var tier = Math.Floor(Js.D(points) / per);
                        var value = tier * row.Num(RK.AmountPerTier);
                        var term = Js.Obj(RK.SourceStat, row[RK.SourceStat], RK.PointsPerTier, per, RK.AmountPerTier, row[RK.AmountPerTier], RK.Tier, tier, K.Value, value);
                        var r = resources.Obj(row.Str(RK.Resource) ?? V.Undefined) ?? throw new InvalidOperationException(RunJs.Fmt(RM.RelicUnknownResource, relicId, index));
                        ((JArray)r[RK.AttributeTiers]).Add(term);
                        r.Put(K.Total, r.Num(K.Total) + value);
                        var s = Js.Spread(source);
                        s[RK.Resource] = row[RK.Resource]?.DeepClone();
                        foreach (var p in term.Properties()) s[p.Name] = p.Value.DeepClone();
                        sources.Add(s);
                    }
                    else if (tag == RV.DamageSchoolFlat)
                    {
                        var school = row.Str(K.School) ?? V.Undefined;
                        damage.Put(school, Js.D(damage[school]) + row.Num(K.Amount));
                        var s = Js.Spread(source);
                        s[K.School] = row[K.School]?.DeepClone();
                        s[K.Value] = row[K.Amount]?.DeepClone();
                        sources.Add(s);
                    }
                    else throw new InvalidOperationException(RunJs.Fmt(RM.RelicModifierTagUnknown, relicId, index, tag));
                }
            }
            return RunJs.Clone(Js.Obj(RK.Resources, resources, K.DamageBySchoolAdd, damage, RK.Sources, sources));
        }

        // ------------------------------------------------------------------ flasks

        /// <summary>flaskCapacity(balance): the fixed charge capacity.</summary>
        public static double FlaskCapacity(JObject balance)
        {
            var value = balance?[RK.FlaskCapacity];
            if (!Js.IsInt(value) || Js.D(value) <= 0) throw new InvalidOperationException(RM.FlaskCapacityInvalid);
            return Js.D(value);
        }

        /// <summary>createFlaskCharges(balance, allocation): the class's split of the capacity, full, with its ledger.</summary>
        public static JObject CreateFlaskCharges(JObject balance, JObject allocation)
        {
            var capacity = FlaskCapacity(balance);
            var hp = allocation?[K.Hp];
            var mana = allocation?[K.Mana];
            if (!Js.IsInt(hp) || Js.D(hp) < 0 || !Js.IsInt(mana) || Js.D(mana) < 0 || Js.D(hp) + Js.D(mana) != capacity)
                throw new InvalidOperationException(RunJs.Fmt(RM.FlaskAllocationInvalid, RunJs.NumStr(capacity)));
            return Js.Obj(K.Capacity, capacity, K.Base, capacity, K.Hp, hp, K.Mana, mana, K.Hp + V.CurrentSuffix, hp, K.Mana + V.CurrentSuffix, mana,
                RK.Grown, Js.Obj(K.Hp, 0, K.Mana, 0), RK.Granted, 0);
        }

        private static string WornTalismanId(JObject loadout)
        {
            var ids = loadout?.Obj(K.Sets)?[RK.Talisman] as JArray ?? new JArray();
            var index = (int)Js.Or0(loadout?.Obj(K.Active)?[RK.Talisman]);
            var id = index >= 0 && index < ids.Count ? ids[index] : null;
            return Js.Truthy(id) ? Js.Str(id) : null;
        }

        /// <summary>flaskGrowthPlan(registries, run).perKind: the held growth rows summed per charge kind.</summary>
        public static JObject FlaskGrowthPerKind(RunData d, JObject run)
        {
            var kinds = RunJs.Strs(d.Combat.Engine.Obj(K.Flasks)?[K.ChargeKinds]);
            var perKind = new JObject();
            foreach (var kind in kinds) perKind.Put(kind, 0);
            foreach (var raw in Js.Items(d.Balance[RK.FlaskGrowth]))
            {
                var row = raw as JObject;
                var source = row?.Str(RK.Source);
                var id = row?.Str(K.Id);
                var kind = row?.Str(K.Kind);
                var amount = row != null && Js.IsInt(row[K.Amount]) ? row.Num(K.Amount) : 0;
                var held = false;
                if (source == RV.RelicKindSource) held = run?[K.Relics] is JArray relics && Js.Includes(relics, id);
                else if (source == RV.TalismanSource) held = WornTalismanId(run?.Obj(K.Loadout)) == id;
                if (held && kind != null && kinds.Contains(kind)) perKind.Put(kind, perKind.Num(kind) + amount);
            }
            return perKind;
        }

        /// <summary>syncFlaskGrowth(registries, run): apply the growth chain's difference to the stored capacity.</summary>
        public static void SyncFlaskGrowth(RunData d, JObject run)
        {
            var f = run?.Obj(K.FlaskCharges);
            if (f == null) return;
            var perKind = FlaskGrowthPerKind(d, run);
            var kinds = RunJs.Strs(d.Combat.Engine.Obj(K.Flasks)?[K.ChargeKinds]);
            var grownIn = f.Obj(RK.Grown);
            var grown = grownIn != null && Js.IsInt(grownIn[K.Hp]) && Js.IsInt(grownIn[K.Mana]) ? grownIn : Js.Obj(K.Hp, 0, K.Mana, 0);
            foreach (var kind in kinds)
            {
                var delta = perKind.Num(kind) - grown.Num(kind);
                if (delta == 0) continue;
                if (delta > 0)
                {
                    f.Put(K.Capacity, f.Num(K.Capacity) + delta);
                    f.Put(kind, f.Num(kind) + delta);
                    f.Put(kind + V.CurrentSuffix, f.Num(kind + V.CurrentSuffix) + delta);
                }
                else
                {
                    var take = -delta;
                    var other = kind == K.Hp ? K.Mana : K.Hp;
                    f.Put(K.Capacity, f.Num(K.Capacity) - take);
                    var fromKind = Math.Min(take, f.Num(kind));
                    f.Put(kind, f.Num(kind) - fromKind);
                    f.Put(other, f.Num(other) - (take - fromKind));
                    f.Put(K.Hp + V.CurrentSuffix, Math.Min(f.Num(K.Hp + V.CurrentSuffix), f.Num(K.Hp)));
                    f.Put(K.Mana + V.CurrentSuffix, Math.Min(f.Num(K.Mana + V.CurrentSuffix), f.Num(K.Mana)));
                }
            }
            f[RK.Grown] = Js.Obj(K.Hp, perKind[K.Hp], K.Mana, perKind[K.Mana]);
        }

        // ------------------------------------------------------------------ zones

        private static JToken IdOrNull(JToken v) => Js.IsStr(v) && Js.Str(v).Length > 0 ? v.DeepClone() : Js.Null();

        private static JToken ActiveIn(JObject loadout, string slotId)
        {
            if (!(loadout?.Obj(K.Sets)?[slotId] is JArray sets)) return Js.Null();
            var activeIndex = loadout.Obj(K.Active)?[slotId];
            var index = Js.IsInt(activeIndex) ? (int)Js.D(activeIndex) : 0;
            return IdOrNull(index >= 0 && index < sets.Count ? sets[index] : null);
        }

        /// <summary>projectZones(run): the character's cards by zone, read off the fields that own them today.</summary>
        public static JObject ProjectZones(RunData d, JObject run, out JArray collection)
        {
            var loadout = run?.Obj(K.Loadout);
            var worn = new JObject();
            var wornSlots = d.RuleObj(RK.Zones, RK.WornSlotIds);
            foreach (var zone in d.RuleList(RK.Zones, RK.WornZoneSlots)) worn[zone] = ActiveIn(loadout, wornSlots.Str(zone));
            var hands = new JObject();
            foreach (var p in d.RuleObj(RK.Zones, RK.HandSlotIds).Properties()) hands[p.Name] = ActiveIn(loadout, Js.Str(p.Value));
            JArray Strings(JToken t) => new JArray(Js.Items(t).Where(x => Js.IsStr(x) && Js.Str(x).Length > 0).Select(x => x.DeepClone()));
            collection = run?[K.Deck] is JArray deck ? new JArray(deck.Where(Js.Truthy).Select(c => c.DeepClone())) : new JArray();
            return Js.Obj(RK.Core, IdOrNull(run?[RK.Class]), K.CoreTags, Strings(run?[K.CoreTags]), RK.Worn, worn, RK.Hands, hands, RK.Passive, Strings(run?[K.Relics]));
        }

        // ------------------------------------------------------------------ seats

        /// <summary>defaultSeatOrder(registries): seat ids by ascending base tier (then id).</summary>
        public static JArray DefaultSeatOrder(RunData d) =>
            new JArray(SeatOrder.DefaultOrder(d.Seats.All.Select(s => new SeatInfo(s.Str(K.Id), (int)s.Num(RK.BaseTier)))));

        /// <summary>tierOf(contentAct, cycle): 1..cycle, looping for Endless.</summary>
        public static double TierOf(double contentAct, double cycle)
        {
            var n = Math.Max(1, Math.Truncate(double.IsNaN(contentAct) || contentAct == 0 ? 1 : contentAct));
            var len = Math.Max(1, Math.Truncate(double.IsNaN(cycle) || cycle == 0 ? 1 : cycle));
            return ((n - 1) % len) + 1;
        }

        /// <summary>seatAtTier(seatOrder, tier).</summary>
        public static string SeatAtTier(JArray seatOrder, double tier)
        {
            if (seatOrder == null || seatOrder.Count == 0) throw new InvalidOperationException(RM.SeatAtTierNeedsOrder);
            return Js.Str(seatOrder[(int)TierOf(tier, seatOrder.Count) - 1]);
        }

        /// <summary>seatTierHpMult(registries, seatId, tier): seatTiers[tier] / seatTiers[baseTier], exactly 1 at the baseline.</summary>
        public static double SeatTierHpMult(RunData d, string seatId, double tier)
        {
            var seat = d.Seats.Get(seatId);
            var table = d.Balance.Obj(RK.SeatTiers) ?? new JObject();
            var finalTier = Js.D(d.Balance.Obj(RK.Endless)?[RK.ActsPerCycle]);
            var at = table[RunJs.NumStr(TierOf(tier, finalTier))];
            var baseValue = table[RunJs.Key(seat[RK.BaseTier])];
            if (!(Js.D(at) > 0) || !(Js.D(baseValue) > 0)) throw new InvalidOperationException(RunJs.Fmt(RM.SeatTierMissing, RunJs.NumStr(tier), RunJs.Key(seat[RK.BaseTier])));
            return Js.D(at) == Js.D(baseValue) ? 1 : Js.D(at) / Js.D(baseValue);
        }
    }
}
