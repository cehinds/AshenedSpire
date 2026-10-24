using System;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Map;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using HV = Ashen.Generated.Core2HostValues;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using LM = Ashen.Generated.LoopMessages;
using MK = Ashen.Generated.MapKeys;
using RK = Ashen.Generated.RunKeys;
using WK = Ashen.Generated.RewardsKeys;
using WV = Ashen.Generated.RewardsValues;

namespace Ashen.Domain.Loop
{
    /// <summary>What a Core 2 climb is played on (WP-C1a, D-302): the fight's data, the map's data and routes, and the loop rules a new run is stamped with.</summary>
    public sealed class Core2LoopData
    {
        public Core2LoopData(CoreCombatData combat, MapData map, CoreRoutesConfig routes, JObject loopEngine)
        {
            Combat = combat ?? throw new ArgumentNullException(nameof(combat));
            Map = map ?? throw new ArgumentNullException(nameof(map));
            Routes = routes ?? throw new ArgumentNullException(nameof(routes));
            Engine = loopEngine ?? throw new ArgumentNullException(nameof(loopEngine));
        }

        public CoreCombatData Combat { get; }
        public MapData Map { get; }
        public CoreRoutesConfig Routes { get; }

        /// <summary>rules/loopEngine.json, shared with the 405 loop: the ledgers a new run starts with (<c>newRun</c>) and how many recent encounters a pool avoids (<c>map</c>).</summary>
        public JObject Engine { get; }

        /// <summary>newRun.stats: the ledger a run starts with.</summary>
        public JObject StartStats => Engine.Obj(LK.NewRun).Obj(WK.Stats);

        /// <summary>map.recentEncounters: how many normal fights a run remembers.</summary>
        public int RecentEncounters => (int)Js.D(Engine.Obj(LK.Map)[LK.RecentEncounters]);

        /// <summary>The number of acts the routes module climbs (<c>routes.acts</c>).</summary>
        public int Acts => (int)Js.D(Routes.Content?[MK.Acts] ?? Js.N(1));
    }

    /// <summary>The door a node opens (main.js enterNode): a fight in a pool, or one of the nodes whose screens are not part of this build yet.</summary>
    public sealed class Core2Door
    {
        public string Kind;
        public string NodeId;

        /// <summary>The fight's pool ('normal', 'elite', 'boss') and the encounter it was rolled or named as.</summary>
        public string Pool;
        public string EncounterId;

        /// <summary>The event an Unknown node resolved to.</summary>
        public string EventId;

        /// <summary>A rest place reached through an Unknown node is the field camp.</summary>
        public bool Camp;

        public bool IsFight => Kind == HV.DoorFight;
    }

    /// <summary>
    /// The Core 2 run loop's walk (docs/design/11 WP-19, the part WP-C1a needs): the new run and its first map, the node a player travels to, the encounter a fight node
    /// rolls, the act advance. It mirrors <c>main.js</c> (beginPreparedRun/startClimb, enterNode, startFight, advanceAct) line for line, as Tools/oracle-core2-host.mjs
    /// does on the shipped web; the 405 loop (<see cref="RunLoop"/>) is untouched.
    /// </summary>
    public static class Core2RunLoop
    {
        /// <summary>currentSeat(): the seat climbed at the current tier.</summary>
        public static string CurrentSeat(JObject run) => Creation.SeatAtTier(run.Arr(RK.SeatOrder), run.Num(RK.ActNumber));

        /// <summary>
        /// createRunState for the Core 2 profile, then the ledgers beginPreparedRun lays on it (stats, path, seen events, recent encounters), the seat order drawn once
        /// on the <c>seats</c> stream and the first act's map. <paramref name="rng"/> is the run's generator, its streams as that left them.
        /// </summary>
        public static JObject NewClimb(Core2LoopData d, uint seed, string classId, JObject profileMeta, out Rng rng)
        {
            var run = CoreRunRules.Create(d.Combat.Run, seed, classId, CoreRunOptions.FromJson(null, profileMeta));
            run[WK.Stats] = d.StartStats.DeepClone();
            run[LK.Path] = new JArray();
            run[LK.SeenEvents] = new JArray();
            run[LK.LastEncounters] = new JArray();
            rng = new Rng(seed);
            run[RK.SeatOrder] = new JArray(MapSeats.DrawSeatOrder(d.Map, rng).Select(s => (JToken)s));
            run[RK.MapGraph] = BuildMap(d, run, rng);
            return run;
        }

        private static JObject BuildMap(Core2LoopData d, JObject run, Rng rng) =>
            CoreRoutes.BuildActMap(d.Map, d.Routes, rng, CurrentSeat(run), (int)run.Num(RK.ActNumber), run[MK.History] as JArray).ToJson();

        /// <summary>
        /// enterNode(nodeId): the path bookkeeping (mapNodeId, path, floor), then the node's door. An Unknown node resolved at map birth is its resolution (an event is marked
        /// seen; a rest there is the field camp). A fight rolls its encounter on the seat's pool away from the last two (<c>enemyAI</c> stream), a boss names its own.
        /// </summary>
        public static Core2Door EnterNode(Core2LoopData d, JObject run, Rng rng, string nodeId)
        {
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
                    (run.Arr(LK.SeenEvents) ?? throw new InvalidOperationException(LM.RunHasNoSeenEvents)).Add(res[MK.EventId]?.DeepClone());
                    return new Core2Door { Kind = HV.DoorEvent, NodeId = nodeId, EventId = res.Str(MK.EventId) };
                }
                kind = res.Str(K.Kind);
            }
            switch (kind)
            {
                case MK.Monster:
                case MK.Fight: return StartFight(d, run, rng, WV.Normal, nodeId);
                case MK.Elite: return StartFight(d, run, rng, WV.Elite, nodeId);
                case MK.Boss: return StartFight(d, run, rng, WV.Boss, nodeId);
                case MK.Shrine: return new Core2Door { Kind = HV.DoorRest, NodeId = nodeId, Camp = node.Str(K.Type) == MK.Event };
                case MK.Merchant: return new Core2Door { Kind = HV.DoorShop, NodeId = nodeId };
                case MK.Treasure: return new Core2Door { Kind = HV.DoorTreasure, NodeId = nodeId };
                default: throw new InvalidOperationException(RunJs.Fmt(LM.UnknownNodeKind, kind));
            }
        }

        private static Core2Door StartFight(Core2LoopData d, JObject run, Rng rng, string pool, string nodeId)
        {
            var recent = run.Arr(LK.LastEncounters) ?? throw new InvalidOperationException(LM.RunHasNoLastEncounters);
            var seat = CurrentSeat(run);
            var encounterId = pool == WV.Boss
                ? ActMap.BossEncounterForNode(d.Map, ActMapGraph.FromJson(run.Obj(RK.MapGraph)), nodeId, seat, (int)run.Num(RK.ActNumber))
                : MapEncounters.RollEncounter(d.Map, rng, pool, seat, recent.Select(Js.Str).ToList());
            if (pool == WV.Normal)
            {
                recent.Add(encounterId);
                if (recent.Count > d.RecentEncounters) recent.RemoveAt(0);
            }
            return new Core2Door { Kind = HV.DoorFight, NodeId = nodeId, Pool = pool, EncounterId = encounterId };
        }

        /// <summary>advanceAct(): the next act, a full heal, a fresh map on the run's generator.</summary>
        public static void AdvanceAct(Core2LoopData d, JObject run, Rng rng)
        {
            run.Put(RK.ActNumber, run.Num(RK.ActNumber) + 1);
            run.Put(MK.Floor, 0);
            run[MK.MapNodeId] = Js.Null();
            run[LK.Path] = new JArray();
            run[LK.LastEncounters] = new JArray();
            run[K.Hp] = run[K.MaxHp]?.DeepClone();
            run[RK.MapGraph] = BuildMap(d, run, rng);
        }
    }
}
