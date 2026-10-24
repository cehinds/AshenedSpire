using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using DK = Ashen.Generated.Core2DeckKeys;
using K = Ashen.Generated.CombatKeys;
using MI = Ashen.Generated.Core2ModuleIds;
using RM = Ashen.Generated.Core2RewardsMessages;
using RV = Ashen.Generated.Core2RewardsValues;
using RW = Ashen.Generated.Core2RewardsKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;
using WV = Ashen.Generated.RewardsValues;

namespace Ashen.Domain.Rewards
{
    /// <summary>A skill track of the Core 2 ledger (web model/skills.js skillTracks): its id, kind and the label a level notice shows.</summary>
    public sealed class Core2Track
    {
        public string Id;
        public string Kind;
        public string Label;
    }

    /// <summary>
    /// The skill ledger of a Core 2 run (web model/skills.js, model/classTree.js awardClassXp, engine/skillXp.js settleConfiguredSkillProgression; WP-15).
    /// Tracks are derived, never listed: the item-type nodes of the tag tree (the focus group its own kind, armour excluded), the armour weight classes,
    /// the combat-maneuver ability, dual-wield and one per class. A Core 2 level is informational: it queues no draft, upgrade, feat or attribute pick
    /// (<c>queueRewards</c> is off), so a climb only moves <c>xp</c> and <c>level</c>. Class mastery, expanded progression and the feat multipliers do not
    /// exist in a Core 2 run (it carries no mastery state, no rules version and no feats).
    /// </summary>
    public static class Core2Skills
    {
        private static JObject SkillBalance(Core2RewardsData d) => d.RunData.Balance.Obj(K.Skill) ?? new JObject();

        /// <summary>skillTracks(registries): weapon groups as the tree lists them, the focus group, armour classes, the ability, dual-wield, then the classes.</summary>
        public static List<Core2Track> Tracks(Core2RewardsData d)
        {
            var rewards = d.Rewards;
            var rules = rewards.Combat.Engine.Obj(K.SkillXp) ?? new JObject();
            var labels = d.Section(RW.TrackLabels);
            var armour = rules.Str(K.ArmorItemType);
            var focus = rules.Str(K.FocusItemType);
            var parent = rewards.RuleStr(WK.Tracks, WK.ItemTypeParent);
            var tracks = new List<Core2Track>();
            foreach (var node in Js.Items(rewards.Nodes).OfType<JObject>())
            {
                if (node.Str(WK.ParentId) != parent || node.Str(K.Id) == armour) continue;
                var isFocus = node.Str(K.Id) == focus;
                tracks.Add(new Core2Track { Id = node.Str(K.Id), Kind = isFocus ? WV.FocusKind : WV.WeaponKind, Label = isFocus ? labels.Str(RW.Focus) : node.Str(K.Label) });
            }
            foreach (var cls in Js.Items(rewards.Combat.Mechanics.Obj(K.Weight)?[K.Classes]).OfType<JObject>())
                tracks.Add(new Core2Track
                {
                    Id = rules.Str(K.ArmourSkillPrefix) + RunJs.Key(cls[K.Id]), Kind = V.ArmourKind,
                    Label = (Js.Truthy(cls[K.Label]) ? cls.Str(K.Label) : RunJs.Key(cls[K.Id])) + labels.Str(RW.ArmourSuffix),
                });
            tracks.Add(new Core2Track { Id = d.Rules.Str(RW.AbilitySkill), Kind = RV.AbilityKind, Label = labels.Str(RW.Ability) });
            tracks.Add(new Core2Track { Id = rules.Str(K.DualWieldSkill), Kind = WV.DualKind, Label = labels.Str(RW.Dual) });
            foreach (var cls in d.RunData.Classes.All)
                tracks.Add(new Core2Track { Id = Skills.ClassSkillId(RunJs.Key(cls[K.Id])), Kind = V.ClassKind, Label = Js.Truthy(cls[K.Name]) ? cls.Str(K.Name) : RunJs.Key(cls[K.Id]) });
            return tracks;
        }

        /// <summary>skillKindOf(registries, skillId): the kind a track id belongs to, or null.</summary>
        public static string KindOf(Core2RewardsData d, string skillId)
        {
            if (skillId == d.Rules.Str(RW.PerceptionSkill)) return RV.PerceptionKind;
            return Tracks(d).FirstOrDefault(t => t.Id == skillId)?.Kind;
        }

        private static JObject Curve(Core2RewardsData d, string kind)
        {
            if (kind == RV.PerceptionKind) return d.RunData.Balance.Obj(RW.EnemyKnowledge)?.Obj(RW.Perception);
            var skill = SkillBalance(d);
            var row = kind == V.ClassKind ? skill.Obj(V.ClassKind)?.Obj(K.Xp) : skill.Obj(K.Xp);
            return row ?? throw new InvalidOperationException(RunJs.Fmt(RM.CurveNotAuthored, kind == V.ClassKind ? RV.ClassCurvePath : RV.SkillCurvePath, kind));
        }

        /// <summary>xpToNext(registries, kind, level): the XP the step from <paramref name="level"/> to the next costs (the exponential or linear curve, rounded to a unit, at least one unit).</summary>
        public static double XpToNext(Core2RewardsData d, string kind, JToken level)
        {
            var kinds = Js.Items(d.Rules[RW.SkillKinds]).Select(Js.Str).ToList();
            if (!kinds.Contains(kind)) throw new InvalidOperationException(RunJs.Fmt(RM.NotASkillKind, kind, string.Join(RV.ListJoiner, kinds)));
            var curve = Curve(d, kind);
            var step = Js.IsInt(level) && Js.D(level) > 0 ? Js.D(level) : 0;
            var linear = curve[RW.Linear] != null && curve[RW.Linear].Type == JTokenType.Boolean && curve[RW.Linear].Value<bool>();
            var raw = linear ? curve.Num(K.Base) + step * curve.Num(K.Base) * curve.Num(RW.MultScaler) : curve.Num(K.Base) * Math.Pow(curve.Num(K.Growth), step);
            var unit = Js.IsInt(curve[WK.RoundTo]) && curve.Num(WK.RoundTo) > 0 ? curve.Num(WK.RoundTo) : 1;
            var epsilon = linear ? d.Rules.Num(RW.LinearEpsilon) : 0;
            return Math.Max(unit, RunJs.Round(raw / unit + epsilon) * unit);
        }

        /// <summary>skillMaxLevel(registries, kind): the level a track stops at (<c>balance.skill.xp.maxLevel</c>, the class track's own), or null for no ceiling.</summary>
        public static double? MaxLevel(Core2RewardsData d, string kind)
        {
            var cap = Curve(d, kind == RV.AbilityKind ? WV.WeaponKind : kind)[RW.MaxLevel];
            return Js.IsInt(cap) && Js.D(cap) > 0 ? (double?)Js.D(cap) : null;
        }

        private static bool BelowCap(Core2RewardsData d, string kind, double level)
        {
            var cap = MaxLevel(d, kind);
            return cap == null || level < cap;
        }

        /// <summary>skillLevel(run, skillId): the level a run holds in a track; 0 for a track never touched.</summary>
        public static double Level(JObject run, string skillId) => Skills.LevelOf(run, skillId);

        /// <summary>expandedProgression(run): the run carries the expanded progression rules (a Core 2 run is born with them).</summary>
        public static bool ExpandedProgression(Core2RewardsData d, JObject run) =>
            Js.IsNum(run?[RW.ProgressionRulesVersion]) && Js.D(run[RW.ProgressionRulesVersion]) == d.Rules.Num(RW.ExpandedProgressionVersion);

        /// <summary>isAbilitySkill(id): the focus group (Spellcraft) and the combat maneuvers.</summary>
        private static bool IsAbilitySkill(Core2RewardsData d, string skillId) =>
            skillId == d.Rewards.Combat.Engine.Obj(K.SkillXp).Str(K.FocusItemType) || skillId == d.Rules.Str(RW.AbilitySkill);

        /// <summary>activateAbilitySkill(run, skillId): the first XP of an ability opens its track at level 1 with a draft owed.</summary>
        private static void ActivateAbilitySkill(JObject run, string skillId)
        {
            var skills = run.Obj(K.Skills);
            if (!(skills[skillId] is JObject row)) skills[skillId] = row = new JObject { [K.Xp] = 0, [K.Level] = 0, [WK.PendingDrafts] = 0 };
            if (row.Num(K.Level) > 0) return;
            row.Put(K.Level, 1);
            row.Put(WK.PendingDrafts, row.Num(WK.PendingDrafts) + 1);
        }

        /// <summary>
        /// awardSkillXp(registries, run, skillId, amount, { queueRewards: false }) → { skillId, before, after, levelUps, gained, skillAwards }: writes the ledger
        /// and climbs as many steps as the XP buys (a level at the cap leaves its XP on the ledger). A non-positive or non-finite amount writes nothing.
        /// </summary>
        public static JObject Award(Core2RewardsData d, JObject run, string skillId, double amount)
        {
            if (skillId == d.Rules.Str(RW.PerceptionSkill)) throw new InvalidOperationException(RM.PerceptionNeedsKnowledge);
            var kind = KindOf(d, skillId) ?? throw new InvalidOperationException(RunJs.Fmt(RM.NotASkillTrack, skillId));
            if (!(run[K.Skills] is JObject skills)) run[K.Skills] = skills = new JObject();
            if (ExpandedProgression(d, run) && IsAbilitySkill(d, skillId) && amount > 0) ActivateAbilitySkill(run, skillId);
            if (!Js.Truthy(skills[skillId])) skills[skillId] = new JObject { [K.Xp] = 0, [K.Level] = 0, [WK.PendingDrafts] = 0 };
            var row = skills.Obj(skillId);
            var before = row[K.Level]?.DeepClone();
            var gain = double.IsNaN(amount) || double.IsInfinity(amount) ? 0 : Math.Floor(amount);
            if (gain <= 0)
                return new JObject { [WK.SkillId] = skillId, [WK.Before] = before, [WK.After] = before?.DeepClone(), [Ashen.Generated.RunKeys.LevelUps] = 0, [WK.Gained] = 0 };
            row.Put(K.Xp, row.Num(K.Xp) + gain);
            var cost = XpToNext(d, kind, row[K.Level]);
            while (BelowCap(d, kind, row.Num(K.Level)) && row.Num(K.Xp) >= cost)
            {
                row.Put(K.Xp, row.Num(K.Xp) - cost);
                row.Put(K.Level, row.Num(K.Level) + 1);
                cost = XpToNext(d, kind, row[K.Level]);
            }
            return new JObject
            {
                [WK.SkillId] = skillId, [WK.Before] = before, [WK.After] = row[K.Level]?.DeepClone(), [Ashen.Generated.RunKeys.LevelUps] = Js.N(row.Num(K.Level) - Js.D(before)),
                [WK.Gained] = Js.N(gain), [RW.SkillAwards] = new JArray(),
            };
        }

        /// <summary>awardClassXp(registries, run, { victory, pool, multiplier, queueRewards: false }) → the award, or null: a lost fight pays nothing; a boss pays <c>bossKill</c> on top of <c>perWin</c>.</summary>
        public static JObject ClassAward(Core2RewardsData d, JObject run, bool victory, string pool, double multiplier)
        {
            if (!victory || run == null || !run.Is(Ashen.Generated.RunKeys.Class) || run.Is(RW.ClassUnequipped)) return null;
            var xp = SkillBalance(d).Obj(V.ClassKind)?.Obj(K.Xp) ?? new JObject();
            var amount = Math.Floor((xp.Or0(WK.PerWin) + (pool == WV.Boss ? xp.Or0(WK.BossKill) : 0)) * multiplier);
            if (!(amount > 0)) return null;
            return Award(d, run, Skills.ClassSkillId(RunJs.Key(run[Ashen.Generated.RunKeys.Class])), amount);
        }

        /// <summary>configuredSkillXpMultiplier(subject, kind): the progression module's rate for weapon or class XP, 1 when it authors none, clamped to the authored range.</summary>
        public static double XpMultiplier(Core2RewardsData d, JObject run, bool classKind)
        {
            var value = d.Module(run, MI.Progression)?[classKind ? RW.ClassXpMultiplier : RW.WeaponXpMultiplier];
            return Js.IsFinite(value) ? Math.Max(0, Math.Min(d.Rules.Num(RW.XpMultiplierMax), Js.D(value))) : 1;
        }

        /// <summary>configuredSkillProgression(run): a configured foundation whose progression module is configured.</summary>
        public static bool Configured(Core2RewardsData d, JObject run) =>
            CoreRunData.IsConfigured(d.Run.SubjectOf(run)) && CoreRunData.ModuleConfigured(d.Run.SubjectOf(run), MI.Progression);

        /// <summary>
        /// settleConfiguredSkillProgression: one terminal receipt, paid through the existing curves without a draft queue — each track of the receipt scaled by the
        /// weapon rate and floored, then the class track for a won fight. Returns the track awards in receipt order and the class award (or null).
        /// </summary>
        public static JArray SettleTracks(Core2RewardsData d, JObject run, JObject receipt)
        {
            var multiplier = XpMultiplier(d, run, false);
            var awards = new JArray();
            foreach (var p in receipt.Properties().ToList()) awards.Add(Award(d, run, p.Name, Math.Floor(Js.D(p.Value) * multiplier)));
            return awards;
        }

        /// <summary>skillLevelNotices(registries, awards, { usedArmaments }): only the actual level crossings, never a re-derived modal on reload.</summary>
        public static JArray LevelNotices(Core2RewardsData d, IEnumerable<JObject> awards, JArray usedArmaments)
        {
            var tracks = Tracks(d);
            var notices = new JArray();
            foreach (var award in awards.Where(a => a != null && Js.D(a[Ashen.Generated.RunKeys.LevelUps]) > 0))
            {
                var skillId = award.Str(WK.SkillId);
                var weapons = Js.Items(usedArmaments).Select(Js.Str)
                    .Select(id => d.RunData.EquipmentRows(DK.Armaments).FirstOrDefault(piece => piece.Str(DK.Id) == id))
                    .Where(piece => piece != null && Js.Includes(piece[RW.ItemTypeTags], skillId)).Select(piece => piece.Str(K.Name)).Distinct().ToList();
                var notice = new JObject
                {
                    [WK.SkillId] = skillId,
                    [K.Label] = tracks.FirstOrDefault(t => t.Id == skillId)?.Label is string label && label.Length > 0 ? label : skillId,
                    [WK.Before] = award[WK.Before]?.DeepClone(), [WK.After] = award[WK.After]?.DeepClone(),
                };
                if (weapons.Count > 0) notice[RW.Weapons] = new JArray(weapons);
                notices.Add(notice);
            }
            return notices;
        }
    }
}
