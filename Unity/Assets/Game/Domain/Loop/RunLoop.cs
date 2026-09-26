using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Map;
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
    /// Where travelling to a node led (the shipped enterNode's door): a fight (its encounter and the createCombat
    /// arguments), a rest place, a treasure room's offer, a merchant or an event (the shop and event streams' screens),
    /// or a legacy dungeon opened in place of a boss fight.
    /// </summary>
    public sealed class NodeOutcome
    {
        public string Kind;
        public string EncounterId;
        public string Pool;
        public string EventId;
        public string Location;
        public string DungeonId;

        /// <summary>A fight's createCombat arguments (without registries/rng, JSON-plain).</summary>
        public JObject Args;

        /// <summary>A treasure room's offer: { relicId, armamentId, title } (a dungeon cache's is its pending reward instead).</summary>
        public JObject Rewards;

        /// <summary>A door that ended the run (a cleared dungeon left at the summit): the run's close-out.</summary>
        public RunEndReceipt End;

        /// <summary>
        /// The outcome as a document: { kind, … }. A fight's arguments are shown without the player block (the run's own
        /// fields) and the two settings-only documents (ratings rules, hand rules).
        /// </summary>
        public JObject ToJson()
        {
            var o = Js.Obj(K.Kind, Kind);
            if (Kind == LV.FightOutcome)
            {
                o[MK.EncounterId] = EncounterId;
                o[WK.Pool] = Pool;
                var shown = (JObject)Args.DeepClone();
                foreach (var key in new[] { K.Player, K.RatingsRules, K.HandRules }) shown.Remove(key);
                o[K.Args] = shown;
            }
            else if (Kind == LV.DungeonOutcome)
            {
                o[LK.DungeonId] = DungeonId;
                o[MK.EncounterId] = EncounterId;
            }
            else if (Kind == LV.RestOutcome) o[LK.Location] = Js.S(Location);
            else if (Kind == MK.Event) o[MK.EventId] = EventId;
            else if (Kind == MK.Treasure && Rewards != null) o[WK.Rewards] = Rewards.DeepClone();
            if (End != null) foreach (var p in End.ToJson().Properties()) o[p.Name] = p.Value.DeepClone();
            return o;
        }
    }

    /// <summary>
    /// The run loop's map half (shipped main.js enterNode, startFight, enterCombat's arguments, combatMods, showEvent's
    /// onDone, and the endlessOn/contentAct/currentSeat/runMapShape readers): travelling to a node writes the path
    /// bookkeeping and resolves what the node is; a fight's encounter is rolled (or read off the boss destination) and
    /// its arguments carry the Custom Climb rules and the seat tier's HP ratio. Journeys are deferred (D-091).
    /// </summary>
    public static partial class RunLoop
    {
        // ------------------------------------------------------------------ readers

        /// <summary>endlessOn(): the run climbs the Endless Spire.</summary>
        public static bool EndlessOn(LoopContext ctx) => ctx.ModOn(RK.Endless);

        /// <summary>endlessActInfo(actNumber): the content act (1..cycle) and the completed cycles.</summary>
        public static (double ContentAct, double Loop) EndlessActInfo(LoopData d, double actNumber)
        {
            var per = d.Balance.Obj(RK.Endless).Num(RK.ActsPerCycle);
            return (((actNumber - 1) % per) + 1, Math.Floor((actNumber - 1) / per));
        }

        /// <summary>contentAct(): the act whose content the run climbs (Endless loops acts 1..cycle).</summary>
        public static double ContentAct(LoopContext ctx) =>
            EndlessOn(ctx) ? EndlessActInfo(ctx.Data, ctx.Run.Num(RK.ActNumber)).ContentAct : ctx.Run.Num(RK.ActNumber);

        /// <summary>currentSeat(): the seat climbed at the current tier.</summary>
        public static string CurrentSeat(LoopContext ctx) => Creation.SeatAtTier(ctx.Run.Arr(RK.SeatOrder), ContentAct(ctx));

        /// <summary>runMapShape(): the Custom Climb map shape, or null.</summary>
        public static JToken RunMapShape(LoopContext ctx)
        {
            var shape = ctx.Run.Obj(RK.Custom)?[MK.MapShape];
            return Js.Truthy(shape) ? shape : null;
        }

        /// <summary>The act map for the current seat and content act (engine/actmap.js buildActMap), as its document.</summary>
        internal static JObject BuildMap(LoopContext ctx) =>
            ActMap.BuildActMap(ctx.Data.Map, ctx.Rng, CurrentSeat(ctx), (int)ContentAct(ctx), RunMapShape(ctx), ctx.Run[MK.History] as JArray).ToJson();

        // ------------------------------------------------------------------ travel

        /// <summary>
        /// enterNode(nodeId): the path bookkeeping (mapNodeId, path, floor), then the node's door. An Unknown node
        /// resolved at map birth is its resolution (an event is marked seen; a rest there is the field camp).
        /// </summary>
        public static NodeOutcome EnterNode(LoopContext ctx, string nodeId)
        {
            var run = ctx.Run;
            if (run.Is(RK.Journey)) throw new NotSupportedException(LM.JourneyDeferred);
            var node = run.Obj(RK.MapGraph)?.Obj(MK.Nodes)?.Obj(nodeId) ?? throw new InvalidOperationException(RunJs.Fmt(LM.UnknownMapNode, nodeId));
            run[MK.MapNodeId] = nodeId;
            var path = run.Arr(LK.Path) ?? throw new InvalidOperationException(LM.RunHasNoPath);
            if (!Js.Includes(path, nodeId)) path.Add(nodeId);
            run[MK.Floor] = node[MK.Floor]?.DeepClone();

            var kind = node.Str(K.Type);
            if (kind == MK.Event)
            {
                var res = node.Obj(MK.Resolved) ?? Js.Obj(K.Kind, MK.Fight);
                if (res.Str(K.Kind) == MK.Event)
                {
                    var seen = run.Arr(LK.SeenEvents) ?? throw new InvalidOperationException(LM.RunHasNoSeenEvents);
                    seen.Add(res[MK.EventId]?.DeepClone());
                    return new NodeOutcome { Kind = MK.Event, EventId = res.Str(MK.EventId) };
                }
                kind = res.Str(K.Kind);
            }
            switch (kind)
            {
                case MK.Monster:
                case MK.Fight:
                    return StartFight(ctx, WV.Normal, nodeId);
                case MK.Elite:
                    return StartFight(ctx, WV.Elite, nodeId);
                case MK.Boss:
                    return StartFight(ctx, WV.Boss, nodeId);
                case MK.Shrine:
                    return new NodeOutcome
                    {
                        Kind = LV.RestOutcome,
                        Location = node.Str(K.Type) == MK.Event ? ctx.Data.RuleStr(LK.Locations, LK.Camp) : ctx.Data.RuleStr(LK.Locations, MK.Shrine),
                    };
                case MK.Merchant:
                    return new NodeOutcome { Kind = MK.Merchant };
                case MK.Treasure:
                {
                    var relicId = RewardRolls.RelicReward(ctx.Data.Rewards, ctx.Rng, run[K.Relics], ctx.Data.Rewards.RuleList(WK.Rewards, WK.RelicRarities));
                    var armamentId = RollDrop(ctx, MK.Treasure);
                    return new NodeOutcome
                    {
                        Kind = MK.Treasure,
                        Rewards = Js.Obj(K.RelicId, Js.S(relicId), WK.ArmamentId, Js.S(armamentId), WK.Title, ctx.Data.RuleStr(LK.Map, LK.TreasureTitle)),
                    };
                }
                default:
                    throw new InvalidOperationException(RunJs.Fmt(LM.UnknownNodeKind, kind));
            }
        }

        /// <summary>rollDrop(source): a pure armament roll against the profile's found set and what the run carries.</summary>
        internal static string RollDrop(LoopContext ctx, string source) =>
            RewardRolls.ArmamentDrop(ctx.Data.Rewards, ctx.Rng, source, ctx.Found(), RewardRolls.CarriedIds(ctx.Run.Obj(K.Loadout)));

        /// <summary>
        /// startFight(pool, nodeId): Elite Gauntlet promotes a monster node; a boss is its destination's encounter (and may
        /// open a legacy dungeon instead), anything else is rolled on the seat's pool away from the last two fights.
        /// </summary>
        public static NodeOutcome StartFight(LoopContext ctx, string pool, string nodeId)
        {
            var run = ctx.Run;
            var d = ctx.Data;
            if (run.Is(RK.Journey)) throw new NotSupportedException(LM.JourneyDeferred);
            if (pool == WV.Normal && ctx.ModOn(LV.AllElite)) pool = WV.Elite;
            var recent = run.Arr(LK.LastEncounters) ?? throw new InvalidOperationException(LM.RunHasNoLastEncounters);
            var encounterId = pool == WV.Boss
                ? ActMap.BossEncounterForNode(d.Map, ActMapGraph.FromJson(run.Obj(RK.MapGraph)), nodeId, CurrentSeat(ctx), (int)ContentAct(ctx))
                : MapEncounters.RollEncounter(d.Map, ctx.Rng, pool, CurrentSeat(ctx), recent.Select(Js.Str).ToList());
            if (pool == WV.Boss && LegacyDungeons.OpenEntrance(ctx, encounterId, nodeId))
                return new NodeOutcome { Kind = LV.DungeonOutcome, DungeonId = run.Obj(RK.LegacyDungeon).Str(K.Id), EncounterId = encounterId };
            if (pool == WV.Normal)
            {
                recent.Add(encounterId);
                if (recent.Count > d.RuleNum(LK.Map, LK.RecentEncounters)) recent.RemoveAt(0);
            }
            return EnterCombat(ctx, nodeId, encounterId);
        }

        /// <summary>
        /// enterCombat(nodeId, encounterId) for a fresh entry: the combat receipt on the run, then the createCombat
        /// arguments under combatMods(pool). The fight is the caller's (CombatStart.Create on the run's RNG).
        /// </summary>
        public static NodeOutcome EnterCombat(LoopContext ctx, string nodeId, string encounterId)
        {
            var run = ctx.Run;
            var d = ctx.Data;
            run[RK.CombatEntered] = Js.Obj(K.NodeId, nodeId, MK.EncounterId, encounterId);
            if (run.Is(RK.Journey) && !run.Is(RK.LegacyDungeon)) throw new NotSupportedException(LM.JourneyDeferred);
            var enc = d.Run.Encounters.Get(encounterId);
            var pool = enc.Str(WK.Pool);
            var mods = CombatMods(ctx, pool);
            var args = RunCombat.Arguments(run, enc, d.Run, ctx.ProfileSettings(), mods.HpMult, mods.EnemyStatuses, mods.PlayerStatuses);
            ctx.Fight = new FightEntry { EncounterId = encounterId, Pool = pool, Encounter = enc, Args = args };
            return new NodeOutcome { Kind = LV.FightOutcome, EncounterId = encounterId, Pool = pool, Args = args };
        }

        /// <summary>
        /// An event's startCombat (showEvent's onDone): the encounter the choice stored enters as a fight at the node the
        /// run stands on. Null when the event started none.
        /// </summary>
        public static NodeOutcome EnterEventCombat(LoopContext ctx)
        {
            var run = ctx.Run;
            if (!run.Is(RK.CombatEntered)) return null;
            var entered = run[RK.CombatEntered];
            var encounterId = Js.IsStr(entered) ? Js.Str(entered) : ((JObject)entered).Str(MK.EncounterId);
            run[RK.CombatEntered] = Js.Null();
            return EnterCombat(ctx, run.Str(MK.MapNodeId), encounterId);
        }

        /// <summary>combatMods(pool): the Custom Climb rules and the seat tier as createCombat options.</summary>
        public static (double HpMult, JArray EnemyStatuses, JArray PlayerStatuses) CombatMods(LoopContext ctx, string pool)
        {
            var run = ctx.Run;
            var d = ctx.Data;
            double hpMult = 1;
            var enemyStatuses = new JArray();
            var playerStatuses = new JArray();
            var cm = d.Balance.Obj(LK.CustomMods) ?? new JObject();
            if ((pool == WV.Elite || pool == WV.Boss) && ctx.ModOn(LV.ToughElites)) hpMult *= cm.Num(LK.ToughElitesHpMult);
            if (pool == WV.Boss && ctx.ModOn(LV.BigBosses)) hpMult *= cm.Num(LK.BigBossesHpMult);
            if (!run.Is(RK.Journey) && run[RK.SeatOrder] is JArray) hpMult *= Creation.SeatTierHpMult(d.Run, CurrentSeat(ctx), ContentAct(ctx));
            if (ctx.ModOn(LV.DeadlyEnemies)) enemyStatuses.Add(d.RuleObj(LK.CustomMods, LV.DeadlyEnemies).DeepClone());
            if (ctx.ModOn(LV.GlassCannon)) playerStatuses.Add(d.RuleObj(LK.CustomMods, LV.GlassCannon).DeepClone());
            if (ctx.ModOn(RK.Endless))
            {
                var loop = EndlessActInfo(d, run.Num(RK.ActNumber)).Loop;
                if (loop > 0)
                {
                    var endless = d.Balance.Obj(RK.Endless);
                    hpMult *= 1 + endless.Num(LK.HpPerLoop) * loop;
                    enemyStatuses.Add(Js.Obj(K.Status, d.RuleStr(LK.CustomMods, LK.EndlessStatus), K.Stacks, endless.Num(LK.StrPerLoop) * loop));
                }
            }
            return (hpMult, enemyStatuses, playerStatuses);
        }
    }
}
