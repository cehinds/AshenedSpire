using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RV = Ashen.Generated.RunValues;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;
using WM = Ashen.Generated.RewardsMessages;
using WV = Ashen.Generated.RewardsValues;

namespace Ashen.Domain.Rewards
{
    /// <summary>A skill track (shipped skillTracks row, without its display label).</summary>
    public sealed class SkillTrack
    {
        public string Id;
        public string Kind;
    }

    /// <summary>
    /// The skill ledger and its curve (shipped model/skills.js, plan phases 4a/4b) and the run-side half of
    /// engine/skillXp.js (<c>applySkillXp</c>). Tracks are derived, never listed: the item-type nodes of the tag tree
    /// (the focus type its own kind, armour excluded), the framework's weight classes, dual-wield, one per class.
    /// Track vocabulary shared with the combat hooks (focus/armour item types, the armour prefix, the dual-wield id)
    /// is read from rules/combatEngine.json skillXp; the tree parents and kind lists from rules/rewardsEngine.json.
    /// </summary>
    public static class Skills
    {
        private static JObject TrackRules(RewardsData d) => d.Combat.Engine.Obj(K.SkillXp) ?? new JObject();

        private static JObject SkillBalance(RewardsData d) => d.Balance.Obj(K.Skill) ?? new JObject();

        /// <summary>classSkillId(classId): the class track a class id names.</summary>
        public static string ClassSkillId(string classId) => V.ClassSkillPrefix + V.KeySeparator + (classId ?? V.Undefined);

        /// <summary>skillTracks(registries): weapon groups as the tree lists them, the focus group, armour classes, dual-wield, classes.</summary>
        public static List<SkillTrack> Tracks(RewardsData d)
        {
            var rules = TrackRules(d);
            var armour = rules.Str(K.ArmorItemType);
            var focus = rules.Str(K.FocusItemType);
            var parent = d.RuleStr(WK.Tracks, WK.ItemTypeParent);
            var tracks = new List<SkillTrack>();
            foreach (var node in Js.Items(d.Nodes).OfType<JObject>())
            {
                if (node.Str(WK.ParentId) != parent || node.Str(K.Id) == armour) continue;
                tracks.Add(new SkillTrack { Id = node.Str(K.Id), Kind = node.Str(K.Id) == focus ? WV.FocusKind : WV.WeaponKind });
            }
            foreach (var cls in Js.Items(d.Combat.Mechanics.Obj(K.Weight)?[K.Classes]).OfType<JObject>())
                tracks.Add(new SkillTrack { Id = rules.Str(K.ArmourSkillPrefix) + RunJs.Key(cls[K.Id]), Kind = V.ArmourKind });
            tracks.Add(new SkillTrack { Id = rules.Str(K.DualWieldSkill), Kind = WV.DualKind });
            foreach (var cls in d.Run.Classes.All) tracks.Add(new SkillTrack { Id = ClassSkillId(RunJs.Key(cls[K.Id])), Kind = V.ClassKind });
            return tracks;
        }

        /// <summary>skillKindOf(registries, skillId): the kind a track id belongs to, or null.</summary>
        public static string KindOf(RewardsData d, string skillId) => Tracks(d).FirstOrDefault(t => t.Id == skillId)?.Kind;

        /// <summary>xpToNext(registries, kind, level): round(base × growth^level, roundTo), at least one unit.</summary>
        public static double XpToNext(RewardsData d, string kind, JToken level)
        {
            var kinds = d.RuleList(WK.Tracks, K.Kinds);
            if (!kinds.Contains(kind)) throw new InvalidOperationException(RunJs.Fmt(WM.NotASkillKind, kind, string.Join(RV.ListJoiner, kinds)));
            var skill = SkillBalance(d);
            var row = kind == V.ClassKind ? skill.Obj(V.ClassKind)?.Obj(K.Xp) : skill.Obj(K.Xp);
            if (row == null) throw new InvalidOperationException(kind == V.ClassKind ? WM.ClassCurveNotAuthored : RunJs.Fmt(WM.SkillCurveNotAuthored, kind));
            var step = Js.IsInt(level) && Js.D(level) > 0 ? Js.D(level) : 0;
            var raw = row.Num(K.Base) * Math.Pow(row.Num(K.Growth), step);
            var unit = Js.IsInt(row[WK.RoundTo]) && row.Num(WK.RoundTo) > 0 ? row.Num(WK.RoundTo) : 1;
            return Math.Max(unit, RunJs.Round(raw / unit) * unit);
        }

        /// <summary>skillLevel(run, skillId): the level a run holds in a track; 0 for a track never touched.</summary>
        public static double LevelOf(JObject run, string skillId)
        {
            var row = run?.Obj(K.Skills)?.Obj(skillId);
            return row != null && Js.IsInt(row[K.Level]) ? row.Num(K.Level) : 0;
        }

        /// <summary>
        /// awardSkillXp(registries, run, skillId, amount) → { skillId, before, after, levelUps, upgraded, gained }: writes
        /// the ledger, climbs as many steps as the XP buys (each queues one draft) and applies the standing auto-upgrade
        /// rule. A non-positive or non-finite amount writes nothing.
        /// </summary>
        public static JObject Award(RewardsData d, JObject run, string skillId, double amount)
        {
            var kind = KindOf(d, skillId) ?? throw new InvalidOperationException(RunJs.Fmt(WM.NotASkillTrack, skillId));
            if (!(run[K.Skills] is JContainer)) run[K.Skills] = new JObject();
            var skills = run.Obj(K.Skills);
            if (!Js.Truthy(skills[skillId])) skills[skillId] = Js.Obj(K.Xp, 0.0, K.Level, 0.0, WK.PendingDrafts, 0.0);
            var row = skills.Obj(skillId);
            var before = row[K.Level]?.DeepClone();
            var gain = double.IsNaN(amount) || double.IsInfinity(amount) ? 0 : Math.Floor(amount);
            if (gain <= 0)
                return Js.Obj(WK.SkillId, skillId, WK.Before, before, WK.After, before?.DeepClone(), RK.LevelUps, 0.0, K.Upgraded, new JArray(), WK.Gained, 0.0);
            row.Put(K.Xp, row.Num(K.Xp) + gain);
            var cost = XpToNext(d, kind, row[K.Level]);
            while (row.Num(K.Xp) >= cost)
            {
                row.Put(K.Xp, row.Num(K.Xp) - cost);
                row.Put(K.Level, row.Num(K.Level) + 1);
                row.Put(WK.PendingDrafts, row.Num(WK.PendingDrafts) + 1);
                cost = XpToNext(d, kind, row[K.Level]);
            }
            var upgraded = UpgradesCards(d, row.Num(K.Level)) ? ApplyUpgrades(d, run, skillId) : new JArray();
            return Js.Obj(WK.SkillId, skillId, WK.Before, before, WK.After, row[K.Level]?.DeepClone(), RK.LevelUps, row.Num(K.Level) - Js.D(before),
                K.Upgraded, upgraded, WK.Gained, gain);
        }

        /// <summary>applySkillXp(registries, run, receipt) → the awards, one per receipt row, in receipt order.</summary>
        public static JArray ApplyXp(RewardsData d, JObject run, JObject receipt)
        {
            var awards = new JArray();
            foreach (var p in (receipt ?? new JObject()).Properties().ToList()) awards.Add(Award(d, run, p.Name, Js.D(p.Value)));
            return awards;
        }

        /// <summary>activeIn(loadout, slotId): the item a loadout slot has active, or null.</summary>
        public static string ActiveIn(JObject loadout, string slotId)
        {
            if (!(loadout?.Obj(K.Sets)?[slotId] is JArray sets)) return null;
            var activeIndex = loadout.Obj(K.Active)?[slotId];
            var index = Js.IsInt(activeIndex) ? Js.D(activeIndex) : 0;
            var id = index >= 0 && index < sets.Count ? Js.Str(sets[(int)index]) : null;
            return string.IsNullOrEmpty(id) ? null : id;
        }

        /// <summary>
        /// skillSchools(registries, loadout, skillId): the card schools a weapon, focus or dual track drafts from —
        /// the card-domain tags the HELD pieces of that item type carry (both hands for dual-wield).
        /// </summary>
        public static List<string> Schools(RewardsData d, JObject loadout, string skillId)
        {
            var kind = KindOf(d, skillId);
            if (!d.RuleList(WK.Tracks, WK.DraftingKinds).Contains(kind)) return new List<string>();
            var cardParent = d.RuleStr(WK.Tracks, WK.CardParent);
            var schools = new HashSet<string>(Js.Items(d.Nodes).OfType<JObject>().Where(n => n.Str(WK.ParentId) == cardParent).Select(n => n.Str(K.Id)).Where(id => id != null), StringComparer.Ordinal);
            var armaments = d.Run.EquipmentRows(K.Armaments).ToList();
            var held = d.Run.RuleObj(RK.Zones, RK.HandSlotIds).Properties()
                .Select(p => ActiveIn(loadout, Js.Str(p.Value)))
                .Select(id => id != null ? armaments.FirstOrDefault(a => a.Str(K.Id) == id) : null)
                .Where(piece => piece != null).ToList();
            var pieces = kind == WV.DualKind ? held : held.Where(piece => Js.Includes(piece[K.ItemTypeTags], skillId)).ToList();
            var out_ = new List<string>();
            foreach (var piece in pieces)
                foreach (var tag in Js.Items(piece[K.Tags]).Select(Js.Str))
                    if (tag != null && schools.Contains(tag) && !out_.Contains(tag)) out_.Add(tag);
            return out_;
        }

        /// <summary>rarityUnlockedAt(registries, level): every balance.skill.rarityUnlock row the level has reached.</summary>
        public static List<string> RarityUnlockedAt(RewardsData d, double level)
        {
            var unlock = SkillBalance(d).Obj(WK.RarityUnlock) ?? new JObject();
            return unlock.Properties().Where(p => Js.IsInt(p.Value) && level >= Js.D(p.Value)).Select(p => p.Name).ToList();
        }

        /// <summary>skillUpgradesCards(registries, level): whether a track at the level has reached the auto-upgrade threshold.</summary>
        public static bool UpgradesCards(RewardsData d, double level)
        {
            var at = SkillBalance(d)[WK.UpgradeAt];
            return Js.IsInt(at) && Js.D(at) > 0 && level >= Js.D(at);
        }

        /// <summary>
        /// applySkillUpgrades(registries, run, skillId) → the instance ids upgraded: every ordinary deck card carrying one
        /// of the track's schools (not equipment-bound, not item-owned, not already upgraded) gains <c>upgraded: true</c>.
        /// </summary>
        public static JArray ApplyUpgrades(RewardsData d, JObject run, string skillId)
        {
            var schools = new HashSet<string>(Schools(d, run.Obj(K.Loadout), skillId), StringComparer.Ordinal);
            var out_ = new JArray();
            if (schools.Count == 0 || !(run[K.Deck] is JArray deck)) return out_;
            var itemOwned = d.Run.RuleList(K.Loadout, RK.ItemOwnedRoles);
            foreach (var token in deck)
            {
                if (!(token is JObject inst) || inst.Is(K.Upgraded) || inst.Is(K.SourceArmamentId) || itemOwned.Contains(inst.Str(K.EquipmentRole))) continue;
                var cardId = inst.Str(K.CardId);
                var def = d.Run.Cards.Has(cardId) ? d.Run.Cards.Get(cardId) : null;
                if (def == null || !Js.Items(def[K.Tags]).Any(t => Js.IsStr(t) && schools.Contains(Js.Str(t)))) continue;
                inst[K.Upgraded] = true;
                out_.Add(inst[K.InstanceId]?.DeepClone() ?? Js.Null());
            }
            return out_;
        }
    }
}
