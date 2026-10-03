using System;
using System.Linq;
using Ashen.App.Run;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using MK = Ashen.Generated.MapKeys;
using RK = Ashen.Generated.RunKeys;

namespace Ashen.App.Nodes
{
    /// <summary>
    /// Review and test hooks for the node screens (shared tests, PlayMode smoke, screen captures; never called by the game,
    /// D-145): a climb travelled to a node of a chosen kind — its first start node dressed as a merchant or as an Unknown
    /// node resolved to an event, then <see cref="RunSession.Travel"/> (the loop's own enterNode, the stock rolled, saved) —
    /// or standing at a legacy dungeon's entrance; and the dungeon state edited to stand on a node.
    /// </summary>
    public static class NodeReview
    {
        private static void Dress(RunSession session, Action<JObject> dress)
        {
            session.EditRunForReview(run =>
            {
                var nodeId = Js.Str(Js.Items(run.Obj(RK.MapGraph)?[MK.StartIds]).FirstOrDefault());
                dress(run.Obj(RK.MapGraph).Obj(MK.Nodes).Obj(nodeId));
            });
        }

        private static TravelResult TravelFirst(RunSession session) => session.Travel(session.ReachableNodes().First());

        /// <summary>The climb travels to its first start node, dressed as a merchant.</summary>
        public static TravelResult AtMerchant(RunSession session)
        {
            Dress(session, node => node[K.Type] = MK.Merchant);
            return TravelFirst(session);
        }

        /// <summary>The climb travels to its first start node, dressed as an Unknown node resolved to the event.</summary>
        public static TravelResult AtEvent(RunSession session, string eventId)
        {
            Dress(session, node =>
            {
                node[K.Type] = MK.Event;
                node[MK.Resolved] = Js.Obj(K.Kind, MK.Event, MK.EventId, eventId);
            });
            return TravelFirst(session);
        }

        /// <summary>The climb stands at the entrance of a legacy dungeon (the first one without an id).</summary>
        public static void AtDungeon(RunSession session, string dungeonId = null)
        {
            var d = session.Content.Loop;
            var def = Js.Items(d.LegacyDungeons[LK.Dungeons]).OfType<JObject>().FirstOrDefault(x => dungeonId == null || x.Str(K.Id) == dungeonId)
                      ?? throw new ArgumentException(NodeMessages.NotInDungeon);
            session.ReviewEnterDungeon(def);
        }

        /// <summary>Stand on a dungeon node (resolved or not, the dungeon cleared or not), nothing pending, no stay open; saved.</summary>
        public static void StandAt(RunSession session, string nodeId, bool resolved, bool cleared = false) => session.ReviewStandAt(nodeId, resolved, cleared);
    }
}
