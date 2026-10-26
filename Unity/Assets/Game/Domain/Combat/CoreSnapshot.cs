using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Random;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using DK = Ashen.Generated.Core2DamageKeys;
using DV = Ashen.Generated.Core2DamageValues;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using NK = Ashen.Generated.Core2SnapshotKeys;
using NM = Ashen.Generated.Core2SnapshotMessages;
using RK = Ashen.Generated.Core2ReactionKeys;
using SK = Ashen.Generated.Core2StatusKeys;

namespace Ashen.Domain.Combat
{
    /// <summary>A snapshot the restore door refuses: the reasons are field-addressed (combatSnapshotProblems), and a profile mismatch is its own recoverable case (D-171).</summary>
    public sealed class SnapshotRefusedException : InvalidOperationException
    {
        public SnapshotRefusedException(IReadOnlyList<string> problems, bool profileMismatch = false)
            : base(string.Join(NM.ProblemSeparator, problems)) { Problems = problems; ProfileMismatch = profileMismatch; }

        public IReadOnlyList<string> Problems { get; }

        /// <summary>The fight was saved under another profile than the one this build loaded: recoverable, the slot is kept as it is.</summary>
        public bool ProfileMismatch { get; }
    }

    /// <summary>
    /// The snapshot of a Core 2 fight (web engine/combatSnapshot.js serializeCombatSnapshot and restoreCombatSnapshot for a configured profile, docs/design/11
    /// WP-13): a save is a committed combat state, not a replay instruction, in the web's own shape. The fight between two dispatches is data (the
    /// fighters, the piles, the Core fields); a fight paused on a reaction offer or a card choice also holds queued work, the stack and the plays that wait,
    /// which are written as the web writes them (<see cref="CoreSnapshotActions"/>). The pinned profile body is not written: the snapshot carries its pin
    /// <c>{ id, hash }</c> and the restore door refuses another profile (D-171, D-280). The previews the UI reads (an offer's intent preview, an option's range
    /// quote) are not carried.
    /// </summary>
    public static class CoreSnapshot
    {
        private static JToken Clone(JToken token) => token?.DeepClone();

        // ------------------------------------------------------------------ serialize

        /// <summary>serializeCombatSnapshot(combat): the JSON-safe state of one fully committed combat turn.</summary>
        public static JObject Serialize(CombatState c)
        {
            if (c == null || !c.IsCore) throw new ArgumentException(NM.NotCore);
            var pending = Js.Truthy(c.Fields[DK.PendingReaction]);
            var choice = CoreCardZones.HasChoice(c);
            if (c.Buffer != null || (c.Queue.Count > 0 && !choice && !pending)) throw new InvalidOperationException(M.StillResolving);
            var f = c.Fields;
            var s = new JObject { [K.Version] = c.Data.Engine[K.SnapshotVersion]?.DeepClone() };
            void Field(string key) { if (f[key] != null && f[key].Type != JTokenType.Null) s[key] = f[key].DeepClone(); }
            void Truthy(string key) { if (Js.Truthy(f[key])) s[key] = f[key].DeepClone(); }
            Field(CK.FixCoreRules);
            Field(CK.RangeState);
            Truthy(CK.FormationPolicy);
            s[NK.GameProfile] = new JObject { [K.Id] = c.CoreData.Run.ProfileId, [NK.Hash] = c.CoreData.Run.ProfileHash };
            s.Put(NK.GameProfileCommands, Js.Or0(f[CK.GameProfileCommands]));
            s[DK.GameProfileInterrupts] = Clone(f[DK.GameProfileInterrupts]) ?? new JObject();
            if (Js.D(f[CK.CombatExpansionVersion]) == c.CoreData.ExpandedVersion)
            {
                s[CK.CombatExpansionVersion] = Clone(f[CK.CombatExpansionVersion]);
                s[CK.CombatExpansionRules] = Clone(f[CK.CombatExpansionRules]);
                s[CK.CombatKey] = Clone(f[CK.CombatKey]);
                s.Put(DK.ExpansionActionSerial, Js.Or0(f[DK.ExpansionActionSerial]));
                s.Put(DK.PendingExpansionActions, Js.Or0(f[DK.PendingExpansionActions]));
                s[CK.AllocatedAttributes] = Clone(f[CK.AllocatedAttributes]);
            }
            Truthy(CK.CombatMatchupRules);
            Truthy(CK.CombatIntentRules);
            Truthy(CK.EnemyKnowledge);
            // Beyond the web's shape (D-281): the follow-up trace the intent row reads while an enemy turn is paused (the web does not save it, so its reload loses the row), and whether
            // the fight has begun to pay skill XP (the web listens from the start; the port from the first player intent).
            Truthy(CK.FollowTrace);
            if (c.RecordSkillXp) s[NK.RecordSkillXp] = true;
            if (f[CK.ReactionRulesVersion] != null)
            {
                s[CK.ReactionRulesVersion] = Clone(f[CK.ReactionRulesVersion]);
                Field(NK.FoundationNextSerial);
                Truthy(DK.ReactionCursor);
                Truthy(CK.ReactionHandCleanup);
                Truthy(DK.PendingReaction);
                if (f[CK.ReactionRules] != null) s[CK.ReactionRules] = Clone(f[CK.ReactionRules]);
                if (Js.Truthy(f[DK.ReactionStack])) s[DK.ReactionStack] = StackToJson(c, f.Obj(DK.ReactionStack));
                Truthy(CK.EnemyReactionRolls);
                if (Js.Truthy(f[DK.ReactionRootPlay])) s[DK.ReactionRootPlay] = PlayToJson(f.Obj(DK.ReactionRootPlay), c.StashedPlayInstance, c.StashedPlayRef, c.StashedPlayBefore);
                if (Js.Truthy(f[DK.ReactionResume]))
                {
                    var resume = Js.Spread(f.Obj(DK.ReactionResume));
                    resume[RK.Queue] = new JArray((c.ReactionResumeQueue ?? new List<CombatAction>()).Select(a => (JToken)CoreSnapshotActions.ActionToJson(a)));
                    s[DK.ReactionResume] = resume;
                }
            }
            if (f[CK.BreakMeterVersion] != null) s[CK.BreakMeterVersion] = Clone(f[CK.BreakMeterVersion]);
            if (c.HandRules != null)
            {
                s[K.HandRules] = c.HandRules.DeepClone();
                s.Put(K.PendingDiscardDraw, c.PendingDiscardDraw);
            }
            Truthy(CK.OrderedDraw);
            void Put(string key, JToken value) { if (value != null) s[key] = value.DeepClone(); }
            Put(K.EquipmentProfileRuleSnapshot, c.EquipmentProfileRuleSnapshot);
            Put(K.EquipmentAttackSlotCount, c.EquipmentAttackSlotCount);
            Put(K.RemovedAttackSlotIds, c.RemovedAttackSlotIds);
            if (f[CK.SideboardedEquipmentCardIds] is JArray sideboarded && sideboarded.Count > 0) s[CK.SideboardedEquipmentCardIds] = sideboarded.DeepClone();
            Put(K.ItemUpgradeLevels, c.ItemUpgradeLevels);
            Put(K.ItemMounts, c.ItemMounts);
            Put(K.EquipmentPoolDeficits, c.EquipmentPoolDeficits);
            s.Put(K.EquipmentChanged, c.EquipmentChanged);
            s.Put(K.Turn, c.Turn);
            s[K.Phase] = Js.S(c.Phase);
            s[K.Result] = Js.S(c.Result);
            s.Put(K.HandMax, c.HandMax);
            Put(K.DrawPerTurn, c.DrawPerTurn);
            if (Js.IsInt(f[CK.CharacterLevel])) s[CK.CharacterLevel] = Clone(f[CK.CharacterLevel]);
            var player = (JObject)c.Player.DeepClone();
            // Actions is Stamina (bindTurnStamina): the web's entity holds the pool under its energy names too.
            player[K.Energy] = player[K.Stamina]?.DeepClone();
            player[K.EnergyMax] = player[K.MaxStamina]?.DeepClone();
            s[K.Player] = player;
            s[K.Enemies] = new JArray(c.Enemies.Select(e => e.DeepClone()));
            s[K.Loadout] = c.Loadout?.DeepClone() ?? Js.Null();
            s[K.Attributes] = c.Attributes?.DeepClone() ?? Js.Null();
            s[CK.AttributeMode] = Js.Truthy(f[CK.AttributeMode]) ? f[CK.AttributeMode].DeepClone() : Js.Null();
            s[K.DerivedStatRuleSnapshot] = c.DerivedStatRuleSnapshot?.DeepClone() ?? Js.Null();
            Put(K.SwapCostRule, c.SwapCostRule);
            s.Put(K.SwapsLeft, c.SwapsLeft);
            Truthy(SK.ProposalRewards);
            s[K.Piles] = new JObject
            {
                [K.Draw] = new JArray(c.Piles.Draw.Select(x => x.DeepClone())),
                [K.Hand] = new JArray(c.Piles.Hand.Select(x => x.DeepClone())),
                [K.Discard] = new JArray(c.Piles.Discard.Select(x => x.DeepClone())),
                [K.Exhaust] = new JArray(c.Piles.Exhaust.Select(x => x.DeepClone())),
            };
            Truthy(SK.HiddenCards);
            if (choice || pending)
            {
                if (choice)
                {
                    var key = f[SK.PendingAbilityDiscard] != null ? SK.PendingAbilityDiscard : SK.PendingCardChoice;
                    s[key] = f[key].DeepClone();
                    s[NK.PendingAbilityPlay] = PlayToJson(c.PendingAbilityPlay, c.PendingPlayInstance, c.PendingPlayRef, c.PendingPlayBefore);
                }
                s[NK.AbilityQueue] = new JArray(c.Queue.Select(a => (JToken)CoreSnapshotActions.ActionToJson(a)));
            }
            s[K.EventLog] = new JArray(c.EventLog.Select(x => x.DeepClone()));
            s[K.TriggerState] = new JArray(c.TriggerState.Entries().Select(kv =>
                new JArray(kv.Key, Js.Obj(K.Fires, kv.Value.Fires, K.Turn, kv.Value.Turn, K.TurnFires, kv.Value.TurnFires))));
            s.Put(K.IdCounter, c.IdCounter);
            s.Put(K.EmitDepth, c.EmitDepth);
            s[K.Skills] = c.Skills.DeepClone();
            s[K.SkillXp] = c.SkillXp.DeepClone();
            s[K.CoreTags] = c.CoreTags.DeepClone();
            s[CK.SkillFeats] = f[CK.SkillFeats] is JArray feats ? feats.DeepClone() : new JArray();
            if (f[CK.Consumables] is JObject consumables) s[CK.Consumables] = consumables.DeepClone();
            s[CK.Companions] = f[CK.Companions] is JArray companions ? companions.DeepClone() : new JArray();
            s[CK.SigilSlots] = f[CK.SigilSlots] is JObject slots ? slots.DeepClone() : new JObject();
            s[CK.AttunedSigils] = f[CK.AttunedSigils] is JArray attuned ? attuned.DeepClone() : new JArray();
            var problems = CoreSnapshotProblems.Check(c.CoreData, s);
            if (problems.Count > 0) throw new SnapshotRefusedException(problems);
            return s;
        }

        /// <summary>The stack with every enemy entry's plan whole (the state keeps a compact view of it beside the runtime plan).</summary>
        private static JObject StackToJson(CombatState c, JObject stack)
        {
            var copy = (JObject)stack.DeepClone();
            foreach (var entry in copy.Arr(CK.Entries).OfType<JObject>().Where(e => e.Str(RK.Side) == Ashen.Generated.Core2EnemyValues.SideEnemy))
                entry[Ashen.Generated.Core2EnemyKeys.Plan] = CoreSnapshotActions.PlanToJson(c.EnemyPlans[PlanKey(entry.Str(DK.OwnerId), stack.Str(RK.GroupKey))]);
            return copy;
        }

        private static string PlanKey(string ownerId, string groupKey) => ownerId + DV.KeySeparator + groupKey;

        /// <summary>The play that waits (web <c>{ instance, ref, kind, printedManaCost, targetId, before }</c>): the compact document the state keeps and the three runtime objects beside it.</summary>
        private static JObject PlayToJson(JObject compact, JObject instance, JObject cardRef, JObject before)
        {
            var o = new JObject();
            foreach (var p in compact.Properties().Where(p => p.Name != K.InstanceId && p.Name != K.CardId)) o[p.Name] = p.Value.DeepClone();
            o[NK.Instance] = Clone(instance);
            o[NK.Ref] = Clone(cardRef);
            if (before != null) o[NK.Before] = CoreSnapshotActions.BeforeToJson(before);
            return o;
        }

        /// <summary>
        /// A payload read back through the content parser holds decimals; the engine works in IEEE doubles (D-036), so every non-integer number is converted
        /// once on the way in and a live fight and a restored one hash and play identically.
        /// </summary>
        public static JObject Doubles(JObject payload)
        {
            var copy = (JObject)payload.DeepClone();
            foreach (var v in copy.Descendants().OfType<JValue>().ToList())
                if (v.Value is decimal) v.Value = Js.D(v);
            return copy;
        }

        // ------------------------------------------------------------------ the random streams

        /// <summary>Every stream's counter by its wire name, the 405 set then the Core 2 set (the web's <c>getCounters()</c>): what a save keeps of the generator.</summary>
        public static JObject RngToJson(Rng rng)
        {
            var o = new JObject();
            foreach (var pair in rng.AllCounters().OrderBy(pair => Array.IndexOf(Ashen.Generated.RngStreamNames.All, pair.Key) is var i && i >= 0 ? i : Ashen.Generated.RngStreamNames.All.Length + Array.IndexOf(Ashen.Generated.Core2RngStreamNames.All, pair.Key)))
                o[pair.Key] = pair.Value;
            return o;
        }

        /// <summary>A generator at the saved counters of a seed; a stream this build does not have is refused by name.</summary>
        public static Rng RngFromJson(uint seed, JObject counters)
        {
            var legacy = new Dictionary<Ashen.Generated.RngStream, uint>();
            var core = new Dictionary<Ashen.Generated.Core2RngStream, uint>();
            foreach (var p in (counters ?? new JObject()).Properties())
            {
                var value = (uint)Js.D(p.Value);
                if (Array.IndexOf(Ashen.Generated.RngStreamNames.All, p.Name) >= 0) legacy[Ashen.Generated.RngStreamNames.Parse(p.Name)] = value;
                else if (Array.IndexOf(Ashen.Generated.Core2RngStreamNames.All, p.Name) >= 0) core[Ashen.Generated.Core2RngStreamNames.Parse(p.Name)] = value;
                else throw new SnapshotRefusedException(new[] { string.Format(CultureInfo.InvariantCulture, NM.UnknownStream, p.Name) });
            }
            return new Rng(seed, legacy, core);
        }

        // ------------------------------------------------------------------ restore

        /// <summary>
        /// restoreCombatSnapshot({ registries, rng, snapshot }): a live fight from a snapshot, without replaying the start. The snapshot is validated first and
        /// refused by name; the property mounts are derived again from the restored loadout, relics and enemies, exactly as the fight derives them.
        /// </summary>
        public static CombatState Restore(CoreCombatData d, Rng rng, JObject snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var saved = (JObject)snapshot.DeepClone();
            var pin = saved.Obj(NK.GameProfile);
            if (pin != null && Js.IsStr(pin[NK.Hash]) && Js.IsStr(pin[K.Id]) && (pin.Str(NK.Hash) != d.Run.ProfileHash || pin.Str(K.Id) != d.Run.ProfileId))
                throw new SnapshotRefusedException(new[] { string.Format(CultureInfo.InvariantCulture, NM.ProfileMismatch, pin.Str(NK.Hash), d.Run.ProfileHash) }, true);
            var problems = CoreSnapshotProblems.Check(d, saved);
            if (problems.Count > 0) throw new SnapshotRefusedException(problems);
            var c = new CombatState
            {
                Data = d.Combat, CoreData = d, Rng = rng, RatingsRules = null,
                HandRules = Js.Truthy(saved[K.HandRules]) ? saved.Obj(K.HandRules) : null,
                PendingDiscardDraw = Js.Truthy(saved[K.HandRules]) ? saved.Or0(K.PendingDiscardDraw) : 0,
                EquipmentProfileRuleSnapshot = saved.Obj(K.EquipmentProfileRuleSnapshot),
                RemovedAttackSlotIds = saved[K.RemovedAttackSlotIds] ?? new JArray(),
                EquipmentAttackSlotCount = Js.IsFinite(saved[K.EquipmentAttackSlotCount]) ? saved[K.EquipmentAttackSlotCount] : null,
                ItemUpgradeLevels = saved.Obj(K.ItemUpgradeLevels) ?? new JObject(),
                ItemMounts = saved[K.ItemMounts],
                EquipmentPoolDeficits = saved.Obj(K.EquipmentPoolDeficits),
                EquipmentChanged = saved.Is(K.EquipmentChanged),
                Turn = saved.Num(K.Turn), Phase = saved.Str(K.Phase), Result = saved.Str(K.Result), HandMax = saved.Num(K.HandMax),
                DrawPerTurn = saved[K.DrawPerTurn], Player = saved.Obj(K.Player),
                Enemies = Js.Items(saved[K.Enemies]).OfType<JObject>().ToList(),
                Loadout = saved.Obj(K.Loadout), Attributes = saved.Obj(K.Attributes),
                DerivedStatRuleSnapshot = Js.Truthy(saved[K.DerivedStatRuleSnapshot]) ? saved[K.DerivedStatRuleSnapshot] : null,
                SwapCostRule = saved[K.SwapCostRule], SwapsLeft = saved.Or0(K.SwapsLeft),
                EventLog = Js.Items(saved[K.EventLog]).OfType<JObject>().ToList(),
                IdCounter = saved.Or0(K.IdCounter), EmitDepth = saved.Or0(K.EmitDepth),
                Skills = saved.Obj(K.Skills) ?? new JObject(), SkillXp = saved.Obj(K.SkillXp) ?? new JObject(),
                CoreTags = saved[K.CoreTags] as JArray ?? new JArray(),
                RecordSkillXp = saved.Is(NK.RecordSkillXp),
            };
            // bindTurnStamina: Stamina is the pool, the energy names are the web's view of it.
            c.Player[K.Stamina] = c.Player[K.Energy]?.DeepClone() ?? c.Player[K.Stamina];
            c.Player[K.MaxStamina] = c.Player[K.EnergyMax]?.DeepClone() ?? c.Player[K.MaxStamina];
            c.Player.Remove(K.Energy);
            c.Player.Remove(K.EnergyMax);
            var piles = saved.Obj(K.Piles);
            c.Piles.Draw = Js.Items(piles?[K.Draw]).OfType<JObject>().ToList();
            c.Piles.Hand = Js.Items(piles?[K.Hand]).OfType<JObject>().ToList();
            c.Piles.Discard = Js.Items(piles?[K.Discard]).OfType<JObject>().ToList();
            c.Piles.Exhaust = Js.Items(piles?[K.Exhaust]).OfType<JObject>().ToList();
            // Detach the documents the fight now owns, so moving them between piles never clones them.
            foreach (var doc in c.Enemies.Concat(c.Piles.Draw).Concat(c.Piles.Hand).Concat(c.Piles.Discard).Concat(c.Piles.Exhaust).Concat(c.EventLog).ToList()) doc.Remove();
            foreach (var entry in Js.Items(saved[K.TriggerState]).OfType<JArray>())
            {
                var st = entry.Count > 1 ? entry[1] as JObject : null;
                c.TriggerState[Js.Str(entry[0])] = new TriggerGate { Fires = Js.Or0(st?[K.Fires]), Turn = Js.Coalesce(st?[K.Turn], -1), TurnFires = Js.Or0(st?[K.TurnFires]) };
            }
            var f = c.Fields;
            void Take(string key) { if (saved[key] != null) f[key] = saved[key].DeepClone(); }
            foreach (var key in new[]
            {
                CK.FixCoreRules, CK.RangeState, CK.FormationPolicy, CK.FollowTrace, CK.CombatExpansionVersion, CK.CombatExpansionRules, CK.CombatKey, DK.ExpansionActionSerial,
                DK.PendingExpansionActions, CK.AllocatedAttributes, CK.CombatMatchupRules, CK.CombatIntentRules, CK.EnemyKnowledge, CK.ReactionRulesVersion, NK.FoundationNextSerial,
                DK.ReactionCursor, CK.ReactionHandCleanup, DK.PendingReaction, CK.ReactionRules, CK.EnemyReactionRolls, CK.BreakMeterVersion, CK.CharacterLevel, CK.SideboardedEquipmentCardIds,
                SK.ProposalRewards, SK.HiddenCards, SK.PendingAbilityDiscard, SK.PendingCardChoice, CK.OrderedDraw, CK.SkillFeats, CK.Consumables, CK.Companions, CK.SigilSlots, CK.AttunedSigils,
            }) Take(key);
            f[CK.GameProfileCommands] = Js.N(saved.Or0(NK.GameProfileCommands));
            if (saved.Obj(DK.GameProfileInterrupts)?.HasValues == true) f[DK.GameProfileInterrupts] = saved[DK.GameProfileInterrupts].DeepClone();
            f[CK.AttributeMode] = Js.Truthy(saved[CK.AttributeMode]) ? saved[CK.AttributeMode].DeepClone() : Js.Null();
            if (f[CK.Consumables] == null) f[CK.Consumables] = Js.Null();
            if (f[CK.OrderedDraw] == null) f[CK.OrderedDraw] = Js.Null();
            if (f[CK.SideboardedEquipmentCardIds] == null) f[CK.SideboardedEquipmentCardIds] = new JArray();

            var groups = new Dictionary<string, ExpansionGroup>(StringComparer.Ordinal);
            foreach (var action in Js.Items(saved[NK.AbilityQueue]).OfType<JObject>()) c.Queue.AddLast(CoreSnapshotActions.ActionFromJson(c, action, groups));
            if (saved[DK.ReactionResume] is JObject resume)
            {
                c.ReactionResumeQueue = Js.Items(resume[RK.Queue]).OfType<JObject>().Select(action => CoreSnapshotActions.ActionFromJson(c, action, groups)).ToList();
                f[DK.ReactionResume] = new JObject { [RK.Queue] = c.ReactionResumeQueue.Count, [RK.Foundation] = Clone(resume[RK.Foundation]) ?? Js.Null() };
            }
            if (saved[DK.ReactionStack] is JObject stack)
            {
                var kept = (JObject)stack.DeepClone();
                foreach (var entry in kept.Arr(CK.Entries).OfType<JObject>().Where(e => e.Str(RK.Side) == Ashen.Generated.Core2EnemyValues.SideEnemy))
                {
                    var plan = CoreSnapshotActions.PlanFromJson(entry.Obj(Ashen.Generated.Core2EnemyKeys.Plan));
                    c.EnemyPlans[PlanKey(entry.Str(DK.OwnerId), kept.Str(RK.GroupKey))] = plan;
                    entry[Ashen.Generated.Core2EnemyKeys.Plan] = CoreEnemyReactions.View(plan);
                }
                f[DK.ReactionStack] = kept;
            }
            if (saved[NK.PendingAbilityPlay] is JObject play) PlayFromJson(play, out c.PendingAbilityPlay, out c.PendingPlayInstance, out c.PendingPlayRef, out c.PendingPlayBefore);
            if (saved[DK.ReactionRootPlay] is JObject root)
            {
                PlayFromJson(root, out var compact, out c.StashedPlayInstance, out c.StashedPlayRef, out c.StashedPlayBefore);
                f[DK.ReactionRootPlay] = compact;
            }

            // Property mounts are never saved: they are derived again from the restored loadout, relics, class and enemies.
            Properties.SyncLoadout(c);
            Properties.SyncRelics(c);
            Properties.SyncClass(c);
            CoreEnemies.SyncConfiguredProperties(c);
            return c;
        }

        private static void PlayFromJson(JObject play, out JObject compact, out JObject instance, out JObject cardRef, out JObject before)
        {
            instance = play.Obj(NK.Instance) != null ? (JObject)play.Obj(NK.Instance).DeepClone() : null;
            cardRef = play.Obj(NK.Ref) != null ? (JObject)play.Obj(NK.Ref).DeepClone() : null;
            before = play.Obj(NK.Before) != null ? CoreSnapshotActions.BeforeFromJson((JObject)play.Obj(NK.Before).DeepClone()) : null;
            compact = new JObject { [K.InstanceId] = instance?[K.InstanceId]?.DeepClone(), [K.CardId] = instance?[K.CardId]?.DeepClone() };
            foreach (var prop in play.Properties().Where(x => x.Name != NK.Instance && x.Name != NK.Ref && x.Name != NK.Before)) compact[prop.Name] = prop.Value.DeepClone();
        }
    }
}
