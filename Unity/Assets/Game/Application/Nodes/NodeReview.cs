using System;
using System.Linq;
using Ashen.App.Run;
using Ashen.Domain.Combat;
using Ashen.Domain.Loop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using MK = Ashen.Generated.MapKeys;
using RK = Ashen.Generated.RunKeys;
using WK = Ashen.Generated.RewardsKeys;

namespace Ashen.App.Nodes
{
    /// <summary>
    /// Review and test hooks for the node screens (shared tests, PlayMode smoke, screen captures), used until the act-map
    /// router exists: a run made by the run loop's newRun in place of the session's first-fight run, and that run travelled
    /// to a node of a chosen kind — its first start node dressed as a merchant or as an event, or standing in a legacy
    /// dungeon's entrance — through the loop's own travel (<see cref="RunLoop.EnterNode"/>). Never called by the game.
    /// </summary>
    public static class NodeReview
    {
        /// <summary>
        /// Replace the session's run with one made by <see cref="RunLoop.NewRun"/> (the map, seat order and travel ledgers the
        /// loop needs), its RNG counters stamped on it, and save.
        /// </summary>
        public static void UseLoopRun(RunSession session, uint seed, string classId = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var content = session.Content;
            var ctx = RunLoop.NewRun(content.Loop, new JObject(), new LoopSettings(), new NewRunOptions
            {
                ClassId = classId ?? session.ClassId,
                Seed = seed,
                SeedString = content.Seeds.Format(seed),
                AdvancedConfigSnapshot = new JObject(),
            });
            var counters = new JObject();
            foreach (var kv in ctx.Rng.Counters().OrderBy(kv => (int)kv.Key)) counters[RngStreamNames.ToWire(kv.Key)] = (double)kv.Value;
            ctx.Run[RK.StreamCounters] = counters;
            session.EditRunForReview(run => Docs.Restore(run, ctx.Run));
            session.Save();
        }

        private static string FirstStart(JObject run) => Js.Str(Js.Items(run.Obj(RK.MapGraph)?[MK.StartIds]).FirstOrDefault());

        private static NodeEntry Travel(RunSession session, Action<JObject> dress)
        {
            string nodeId = null;
            session.EditRunForReview(run =>
            {
                nodeId = FirstStart(run);
                dress(run.Obj(RK.MapGraph).Obj(MK.Nodes).Obj(nodeId));
            });
            var outcome = RunLoop.EnterNode(session.NodeContext(), nodeId);
            return NodeScreens.For(session.Content.Loop, outcome);
        }

        /// <summary>The run travels to its first start node, dressed as a merchant.</summary>
        public static NodeEntry AtMerchant(RunSession session) => Travel(session, node => node[K.Type] = MK.Merchant);

        /// <summary>The run travels to its first start node, dressed as an Unknown node resolved to the event.</summary>
        public static NodeEntry AtEvent(RunSession session, string eventId) => Travel(session, node =>
        {
            node[K.Type] = MK.Event;
            node[MK.Resolved] = Js.Obj(K.Kind, MK.Event, MK.EventId, eventId);
        });

        /// <summary>The run stands at the entrance of a legacy dungeon (the first one without an id), opened at its first start node.</summary>
        public static NodeEntry AtDungeon(RunSession session, string dungeonId = null)
        {
            var d = session.Content.Loop;
            var def = Js.Items(d.LegacyDungeons[LK.Dungeons]).OfType<JObject>().FirstOrDefault(x => dungeonId == null || x.Str(K.Id) == dungeonId)
                      ?? throw new ArgumentException(NodeMessages.NotInDungeon);
            session.EditRunForReview(run =>
            {
                var nodeId = FirstStart(run);
                run[MK.MapNodeId] = nodeId;
                run[MK.Floor] = run.Obj(RK.MapGraph).Obj(MK.Nodes).Obj(nodeId)?[MK.Floor]?.DeepClone();
                LegacyDungeons.Begin(d, run, def, nodeId);
            });
            return NodeScreens.Resume(session.Run, null);
        }

        /// <summary>Edit the dungeon state for review: stand on a node (resolved or not, the dungeon cleared or not), nothing pending, no stay open.</summary>
        public static void StandAt(RunSession session, string nodeId, bool resolved, bool cleared = false)
        {
            session.EditRunForReview(run =>
            {
                var s = run.Obj(RK.LegacyDungeon);
                if (s == null) return;
                s[LK.Previous] = s[WK.Current]?.DeepClone();
                s[WK.Current] = nodeId;
                if (!Js.Includes(s[LK.Visited], nodeId)) s.Arr(LK.Visited).Add(nodeId);
                if (resolved && !Js.Includes(s[MK.Resolved], nodeId)) s.Arr(MK.Resolved).Add(nodeId);
                s[LK.Cleared] = cleared;
                s[K.Pending] = Js.Null();
                s.Remove(LK.ActiveRest);
            });
        }
    }
}
