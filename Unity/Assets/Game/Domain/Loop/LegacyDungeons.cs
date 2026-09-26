using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using LM = Ashen.Generated.LoopMessages;
using LV = Ashen.Generated.LoopValues;
using MK = Ashen.Generated.MapKeys;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;
using WV = Ashen.Generated.RewardsValues;

namespace Ashen.Domain.Loop
{
    /// <summary>
    /// Legacy dungeons (US-4.5; shipped model/legacyDungeon.js and main.js enterDungeonLocation, showDungeonDialogue's
    /// commit and onDone, leaveLegacyDungeon and openLegacyEntrance): entering a boss node whose encounter owns a dungeon
    /// opens the dungeon in its place; its nodes are rooms (a shrine rests, a cache is a treasure, fights fight) or
    /// dialogue with listen / fight / flee responses (flee a Dexterity roll on 'events'); clearing the boss node lets
    /// the run leave, which ends the run at the summit or advances the act.
    /// </summary>
    public static class LegacyDungeons
    {
        private static IEnumerable<JObject> Defs(LoopData d) => Js.Items(d.LegacyDungeons[LK.Dungeons]).OfType<JObject>();

        /// <summary>dungeonForEncounter(id): the dungeon a boss encounter opens, or null.</summary>
        public static JObject ForEncounter(LoopData d, string encounterId) => Defs(d).FirstOrDefault(def => def.Str(LK.BossEncounter) == encounterId);

        /// <summary>dungeonDefinition(run): the dungeon the run stands in, or null.</summary>
        public static JObject Definition(LoopData d, JObject run)
        {
            var id = run.Obj(RK.LegacyDungeon)?.Str(K.Id);
            return Defs(d).FirstOrDefault(def => def.Str(K.Id) == id);
        }

        /// <summary>dungeonNode(run): the node the run stands on.</summary>
        public static JObject Node(LoopData d, JObject run)
        {
            var current = run.Obj(RK.LegacyDungeon)?[WK.Current];
            return Js.Items(Definition(d, run)?[MK.Nodes]).OfType<JObject>().FirstOrDefault(n => JToken.DeepEquals(n[K.Id], current));
        }

        /// <summary>beginDungeon(run, def, parentNodeId): the run stands at the entrance, nothing resolved.</summary>
        public static void Begin(LoopData d, JObject run, JObject def, string parentNodeId)
        {
            var entrance = def[LK.Entrance];
            run[RK.LegacyDungeon] = Js.Obj(K.Version, d.RuleNum(LK.Dungeons, K.Version), K.Id, def[K.Id], LK.ParentNodeId, parentNodeId, WK.Current, entrance.DeepClone(),
                LK.Previous, entrance.DeepClone(), LK.Visited, new JArray(entrance.DeepClone()), MK.Resolved, new JArray(), LK.Cleared, false, K.Pending, Js.Null());
        }

        /// <summary>openLegacyEntrance(encounterId, nodeId): true when the encounter owns a dungeon and the run now stands in it.</summary>
        public static bool OpenEntrance(LoopContext ctx, string encounterId, string nodeId)
        {
            var def = ForEncounter(ctx.Data, encounterId);
            if (def == null) return false;
            Begin(ctx.Data, ctx.Run, def, nodeId);
            return true;
        }

        private static JObject State(JObject run) => run.Obj(RK.LegacyDungeon) ?? throw new InvalidOperationException(LM.NotInDungeon);

        /// <summary>dungeonNodeAction(run): 'map' once resolved or cleared; else the room the node's kind is.</summary>
        public static string NodeAction(LoopData d, JObject run)
        {
            var s = State(run);
            var n = Node(d, run);
            if (Js.Includes(s[MK.Resolved], n.Str(K.Id)) || s.Is(LK.Cleared)) return WV.MapDoor;
            var kind = n.Str(K.Kind);
            var rooms = d.RuleObj(LK.Dungeons, LK.Rooms);
            return rooms[kind] != null ? rooms.Str(kind) : LV.DialogueAction;
        }

        /// <summary>dungeonNeighbors(run, id): the nodes one edge away.</summary>
        public static List<string> Neighbors(LoopData d, JObject run, string id = null)
        {
            id = id ?? State(run).Str(WK.Current);
            return Js.Items(Definition(d, run)[LK.Edges]).OfType<JObject>()
                .Where(e => e.Str(LK.A) == id || e.Str(LK.B) == id)
                .Select(e => e.Str(LK.A) == id ? e.Str(LK.B) : e.Str(LK.A)).ToList();
        }

        /// <summary>travelDungeon(run, id): one step along an edge, once the node stood on is resolved (or the dungeon cleared).</summary>
        public static bool Travel(LoopData d, JObject run, string id)
        {
            var s = State(run);
            if (Js.Truthy(s[K.Pending]) || (!s.Is(LK.Cleared) && !Js.Includes(s[MK.Resolved], s.Str(WK.Current))) || !Neighbors(d, run).Contains(id)) return false;
            s[LK.Previous] = s[WK.Current].DeepClone();
            s[WK.Current] = id;
            if (!Js.Includes(s[LK.Visited], id)) s.Arr(LK.Visited).Add(id);
            return true;
        }

        /// <summary>escapeChance(run): the flee odds, the Dexterity bonus clamped to the table's bounds.</summary>
        public static double EscapeChance(LoopData d, JObject run)
        {
            var f = d.LegacyDungeons.Obj(LK.Flee);
            var dex = run.Obj(K.Attributes)?[K.Dexterity];
            var dexterity = Js.Nullish(dex) ? f.Num(LK.DexBaseline) : Js.D(dex);
            return Math.Max(f.Num(K.Min), Math.Min(f.Num(K.Max), f.Num(K.Base) + f.Num(LK.BonusPerPoint) * (dexterity - f.Num(LK.DexBaseline))));
        }

        /// <summary>dungeonChoices(run): the responses open at the node — { id, resultText } (labels are the screen's).</summary>
        public static List<JObject> Choices(LoopData d, JObject run)
        {
            var s = State(run);
            var n = Node(d, run);
            var texts = d.RuleObj(LK.Dungeons, LK.Texts);
            if (Js.Includes(s[MK.Resolved], n.Str(K.Id)) || s.Is(LK.Cleared))
                return new List<JObject> { Js.Obj(K.Id, LV.LeaveChoice, LK.ResultText, n[LK.Reply]?.DeepClone(), K.Effects, new JArray()) };
            var kind = n.Str(K.Kind);
            var hostile = d.RuleList(LK.Dungeons, LK.HostileKinds).Contains(kind);
            var fighting = d.RuleList(LK.Dungeons, LK.FightKinds).Contains(kind);
            var rows = new List<JObject>();
            if (!fighting) rows.Add(Js.Obj(K.Id, LV.ListenChoice, LK.ResultText, n[LK.Reply]?.DeepClone(), K.Effects, new JArray()));
            if (hostile)
            {
                rows.Add(Js.Obj(K.Id, LV.FightChoice, LK.ResultText, texts[LV.FightChoice], K.Effects, new JArray()));
                rows.Add(Js.Obj(K.Id, LV.FleeChoice, LK.ResultText, texts[LV.FleeChoice], K.Effects, new JArray()));
            }
            return rows;
        }

        /// <summary>resolveDungeonNode(run): the node stood on is resolved (the boss node clears the dungeon); a pending response and a stay end.</summary>
        public static void ResolveNode(LoopData d, JObject run)
        {
            var s = State(run);
            var current = s[WK.Current];
            if (!Js.Items(s[MK.Resolved]).Any(t => JToken.DeepEquals(t, current))) s.Arr(MK.Resolved).Add(current.DeepClone());
            if (JToken.DeepEquals(current, Definition(d, run)[LK.BossNode])) s[LK.Cleared] = true;
            s[K.Pending] = Js.Null();
            s.Remove(LK.ActiveRest);
        }

        /// <summary>
        /// chooseDungeon(run, choiceId, rng): the response's persisted receipt { choiceId, action, text, roll, nodeId }. A reload
        /// continues the receipt rather than rolling the escape or awarding a cache again.
        /// </summary>
        public static JObject Choose(LoopData d, JObject run, string choiceId, Rng rng)
        {
            var s = State(run);
            var n = Node(d, run);
            if (Js.Truthy(s[K.Pending])) return s.Obj(K.Pending);
            var choice = Choices(d, run).FirstOrDefault(c => c.Str(K.Id) == choiceId) ?? throw new InvalidOperationException(RunJs.Fmt(LM.UnavailableDungeonResponse, choiceId));
            var texts = d.RuleObj(LK.Dungeons, LK.Texts);
            var action = WV.MapDoor;
            var text = RunJs.Key(choice[LK.ResultText]);
            JToken roll = Js.Null();
            if (choiceId == LV.FightChoice) action = LV.CombatAction;
            else if (choiceId == LV.FleeChoice)
            {
                var range = d.RuleObj(LK.Dungeons, LK.FleeRoll);
                var r = rng.Int(Ashen.Generated.RngStream.Events, (int)range.Num(K.Min), (int)range.Num(K.Max));
                roll = Js.N(r);
                var chance = EscapeChance(d, run);
                var success = r <= chance;
                action = success ? LV.RetreatAction : LV.CombatAction;
                text = RunJs.Fmt(texts.Str(LK.Escape), RunJs.NumStr(r), RunJs.NumStr(chance), texts.Str(success ? LK.Escaped : LK.Caught));
            }
            else if (choiceId == LV.ListenChoice)
            {
                var kind = n.Str(K.Kind);
                if (kind == d.RuleStr(LK.Dungeons, LK.ShrineKind)) action = LV.RestAction;
                if (kind == d.RuleStr(LK.Dungeons, LK.CacheKind))
                {
                    var cinders = d.RuleNum(LK.Dungeons, LK.CacheCinders);
                    run.Put(RK.Cinders, run.Num(RK.Cinders) + cinders);
                    text += RunJs.Fmt(texts.Str(LK.Cache), RunJs.NumStr(cinders));
                }
                if (action != LV.RestAction) ResolveNode(d, run);
            }
            s[K.Pending] = Js.Obj(MK.ChoiceId, choiceId, LK.Action, action, LK.Text, text, K.Roll, roll, K.NodeId, n[K.Id]?.DeepClone());
            return s.Obj(K.Pending);
        }

        /// <summary>continueDungeon(run): the pending response's action ('map', 'rest', 'combat', 'retreat'); a rest opens a stay, a retreat steps back.</summary>
        public static string Continue(JObject run)
        {
            var s = State(run);
            var p = s.Obj(K.Pending);
            if (p == null) return WV.MapDoor;
            var action = p.Str(LK.Action);
            if (action == LV.RestAction) s[LK.ActiveRest] = Js.Obj(K.NodeId, s[WK.Current]?.DeepClone(), LK.Refilled, false);
            if (action == LV.RetreatAction) s[WK.Current] = s[LK.Previous]?.DeepClone();
            s[K.Pending] = Js.Null();
            return action;
        }

        // ------------------------------------------------------------------ the controller (main.js)

        /// <summary>
        /// enterDungeonLocation(): the room the node is — a stay at its shrine (the Rest screen at the last rest place), a
        /// cache's treasure (rolled, the node resolved, a pending reward to claim), its fight, or its dialogue.
        /// </summary>
        public static NodeOutcome EnterLocation(LoopContext ctx)
        {
            var d = ctx.Data;
            var run = ctx.Run;
            var s = State(run);
            switch (NodeAction(d, run))
            {
                case LV.RestAction:
                    if (!s.Is(LK.ActiveRest)) s[LK.ActiveRest] = Js.Obj(K.NodeId, s[WK.Current]?.DeepClone(), LK.Refilled, false);
                    return new NodeOutcome { Kind = LV.RestOutcome };
                case MK.Treasure:
                {
                    var relicId = RewardRolls.RelicReward(d.Rewards, ctx.Rng, run[K.Relics], d.Rewards.RuleList(WK.Rewards, WK.RelicRarities));
                    var armamentId = RunLoop.RollDrop(ctx, MK.Treasure);
                    ResolveNode(d, run);
                    CombatEnd.BeginPendingReward(run, Js.Obj(K.RelicId, Js.S(relicId), WK.ArmamentId, Js.S(armamentId), WK.Title, d.RuleStr(LK.Map, LK.TreasureTitle)),
                        MK.Treasure, WV.MapDoor);
                    return new NodeOutcome { Kind = MK.Treasure };
                }
                case LV.CombatAction:
                    return RunLoop.EnterCombat(ctx, s.Str(LK.ParentNodeId), Node(d, run).Str(LK.Encounter));
                case LV.DialogueAction:
                    return new NodeOutcome { Kind = LV.DialogueAction };
                default:
                    return new NodeOutcome { Kind = WV.MapDoor };
            }
        }

        /// <summary>
        /// travelDungeon then enterDungeonLocation (the dungeon map's travel); a step the dungeon refuses is 'refused'.
        /// </summary>
        public static NodeOutcome TravelTo(LoopContext ctx, string nodeId) =>
            Travel(ctx.Data, ctx.Run, nodeId) ? EnterLocation(ctx) : new NodeOutcome { Kind = LV.RefusedOutcome };

        /// <summary>The dialogue's Continue (showDungeonDialogue onDone): the pending action — a stay, the node's fight, or the map.</summary>
        public static NodeOutcome ContinueDialogue(LoopContext ctx)
        {
            var encounterId = Node(ctx.Data, ctx.Run).Str(LK.Encounter);
            var action = Continue(ctx.Run);
            if (action == LV.CombatAction) return RunLoop.EnterCombat(ctx, State(ctx.Run).Str(LK.ParentNodeId), encounterId);
            return new NodeOutcome { Kind = action };
        }

        /// <summary>
        /// leaveLegacyDungeon(): only a cleared dungeon can be left; leaving ends the run at the summit (the final act,
        /// Endless off) or advances the act.
        /// </summary>
        public static NodeOutcome Leave(LoopContext ctx)
        {
            var run = ctx.Run;
            if (!(run.Obj(RK.LegacyDungeon)?.Is(LK.Cleared) ?? false)) return new NodeOutcome { Kind = LV.RefusedOutcome };
            if (run.Is(RK.Journey)) throw new NotSupportedException(LM.JourneyDeferred);
            run.Remove(RK.LegacyDungeon);
            if (run.Num(RK.ActNumber) >= ctx.Data.Rewards.RuleNum(WK.Summit, WK.FinalAct) && !RunLoop.EndlessOn(ctx))
                return new NodeOutcome { Kind = V.Victory, End = RunEnd.Finish(ctx, true) };
            Acts.Advance(ctx);
            return new NodeOutcome { Kind = LV.AdvancedOutcome };
        }
    }
}
