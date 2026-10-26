using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Run;
using Ashen.Generated;
using Ashen.Presentation.UI;
using Ashen.Presentation.UI.Screens;
using Newtonsoft.Json.Linq;

namespace Ashen.Tests.Play
{
    /// <summary>
    /// Capture fixtures for the Core 2 screens (docs/qa/screen-capture-list.md): a new Core 2 run in slot 1, walked by a bot to the state the fixture names, edited for review, and shown by the climb's
    /// router (RunFlow.Show), then put in the fixture's state (a node selected, the legend, a door over the map). A fixture opts in with a "core2" object:
    ///   seed, class         the run (default 20261025, reaver)
    ///   until               map | combat | rewards | rest | shop | event | runEnd (default map)
    ///   walk                fights/places to clear before the target, each a node of the kind 'walkKind' (default monster)
    ///   prefer              the node kind the target is entered at (monster, event, shrine, elite, merchant, boss); defaults by 'until'
    ///   eventId             with until = event: try seeds (up to 'attempts', default 80) until the run stands at that event
    ///   endTurns            with until = combat: End Turn this many times (the enemy turn, the next player turn)
    ///   result              with until = runEnd: defeat (the default; hit points set to 1 and turns ended until the run is lost)
    ///   hpRatio, cinders    edits for review, applied last
    /// and, for the map: select (true: the first reachable node), legend (true), potionsTray (true: the minis out), modal (potions | pause | armoury) with 'tab' (deck | equipment | stats).
    /// </summary>
    public static class Core2Capture
    {
        public static bool Handles(JObject fixture) => fixture["core2"] is JObject;

        public static void Show(UiHost host, JObject fixture)
        {
            var spec = (JObject)fixture["core2"];
            var ui = host.Context;
            var session = Build(ui, spec);
            ui.Core2Session = session;
            RunFlow.Show(new ScreenContext { Host = host, Navigator = host.Navigator, Ui = ui }, session);
        }

        public static void After(UiHost host, JObject fixture)
        {
            var spec = fixture["core2"] as JObject;
            if (spec == null) return;
            var nav = host.Navigator;
            if (nav.Top?.View is ActMapScreen map && map.Core2 != null)
            {
                var reachable = map.Core2.ReachableNodes();
                if ((bool?)spec["select"] == true && reachable.Count > 0) map.Select(reachable[0]);
                if ((bool?)spec["legend"] == true) map.LegendForReview();
                if ((bool?)spec["potionsTray"] == true) map.MinisForReview();
                switch ((string)spec["modal"])
                {
                    case "potions":
                        nav.OpenModal(ScreenIds.Core2Potions, new Core2PotionsArgs { Session = map.Core2 });
                        break;
                    case "pause":
                        nav.OpenModal(ScreenIds.Pause, new PauseArgs { Session = map.Core2 });
                        break;
                    case "armoury":
                        nav.OpenModal(ScreenIds.Armoury, new ArmouryArgs { Session = map.Core2, Tab = (string)spec["tab"] ?? ArmouryValues.TabEquipment });
                        break;
                }
            }
        }

        private static Core2RunSession Build(UiContext ui, JObject spec)
        {
            var until = (string)spec["until"] ?? "map";
            var wanted = (string)spec["eventId"];
            var attempts = (int?)spec["attempts"] ?? (wanted != null ? 80 : 30);
            var seed = (uint)((long?)spec["seed"] ?? 20261025L);
            Core2RunSession session = null;
            for (var i = 0; i < attempts; i++)
            {
                session = Core2RunSession.New(ui.RunContent, ui.Saves, 1, seed + (uint)i, (string)spec["class"] ?? "reaver", ui.Data.Strings.Get(ui.RunContent.Flow.NameKey));
                var walk = (int?)spec["walk"] ?? 0;
                for (var step = 0; step < walk && Node(session, (string)spec["walkKind"] ?? "monster") is string walked; step++) Clear(session, walked);
                if (until == "map") break;
                var location = ExpectedLocation(until);
                var kind = (string)spec["prefer"] ?? DefaultKind(until);
                // Clear the way to the nearest node of the kind (a shrine or a merchant is not on every row).
                for (var guard = 0; guard < 40 && !session.ReachableNodes().Any(id => KindOf(session, id) == kind) && StepToward(session, kind) is string step; guard++) Clear(session, step);
                if (until == "runEnd") session.EditRunForReview(run => { run["hp"] = 1; });
                var target = Node(session, kind);
                if (target == null) continue;
                session.Travel(target);
                if (until == "rewards") PlayOut(session);
                else if (until == "runEnd") EndOut(session);
                else if (until == "combat") for (var turn = 0; turn < ((int?)spec["endTurns"] ?? 0) && session.IsInCombat; turn++) session.Execute(Intent("endTurn", "discardIds", new string[0]));
                if (session.Location == location && (wanted == null || session.EventId == wanted)) break;
            }
            var ratio = (double?)spec["hpRatio"];
            var cinders = (double?)spec["cinders"];
            if (ratio != null || cinders != null)
                session.EditRunForReview(run =>
                {
                    if (ratio != null) run["hp"] = Math.Max(1d, Math.Floor((double)run["maxHp"] * ratio.Value));
                    if (cinders != null) run["cinders"] = cinders.Value;
                });
            session.Save();
            return session;
        }

        /// <summary>Where the run stands once the target is reached; a seed whose map never offers the kind (a merchant is rare) is tried again with the next.</summary>
        private static string ExpectedLocation(string until)
        {
            switch (until)
            {
                case "rest": return RunFlowValues.LocationRest;
                case "shop": return RunFlowValues.LocationMerchant;
                case "event": return RunFlowValues.LocationEvent;
                case "rewards": return RunFlowValues.LocationRewards;
                case "runEnd": return RunFlowValues.LocationRunEnd;
                default: return RunFlowValues.LocationCombat;
            }
        }

        private static string DefaultKind(string until)
        {
            switch (until)
            {
                case "rest": return "shrine";
                case "shop": return "merchant";
                case "event": return "event";
                default: return "monster";
            }
        }

        /// <summary>The first reachable node of the kind, else the first reachable one.</summary>
        private static string Node(Core2RunSession session, string kind)
        {
            var nodes = session.ReachableNodes();
            return nodes.FirstOrDefault(id => KindOf(session, id) == kind) ?? nodes.FirstOrDefault();
        }

        private static string KindOf(Core2RunSession session, string id) => (string)session.Run["mapGraph"]["nodes"][id]["type"];

        /// <summary>The reachable node that starts the shortest walk to a node of the kind (breadth first over the graph), or null when no such node lies ahead.</summary>
        private static string StepToward(Core2RunSession session, string kind)
        {
            var graph = session.Run["mapGraph"];
            foreach (var first in session.ReachableNodes())
            {
                var seen = new HashSet<string> { first };
                var queue = new Queue<string>();
                queue.Enqueue(first);
                while (queue.Count > 0)
                {
                    var id = queue.Dequeue();
                    if ((string)graph["nodes"][id]["type"] == kind) return first;
                    foreach (var next in (graph["nodes"][id]["next"] as JArray ?? new JArray()).Select(t => (string)t))
                        if (seen.Add(next)) queue.Enqueue(next);
                }
            }
            return null;
        }

        /// <summary>Enters a node and plays it out to the map: a fight is won by the bot, a door is left as a player with no screen would.</summary>
        private static void Clear(Core2RunSession session, string nodeId)
        {
            session.Travel(nodeId);
            if (session.IsInCombat) PlayOut(session);
            session.DismissPort();
        }

        private static JObject Intent(string type, params object[] pairs)
        {
            var o = new JObject { ["type"] = type };
            for (var i = 0; i + 1 < pairs.Length; i += 2) o[(string)pairs[i]] = JToken.FromObject(pairs[i + 1] ?? JValue.CreateNull());
            return o;
        }

        /// <summary>Ends every turn without playing a card until the fight is over (a run at one hit point is lost).</summary>
        private static void EndOut(Core2RunSession session)
        {
            for (var guard = 0; guard < 400 && session.IsInCombat; guard++)
            {
                if (session.Combat.PendingOffer() is JObject offer) { session.Execute(Intent("chooseReaction", "offerId", offer["id"], "optionId", null)); continue; }
                if (session.Combat.PendingChoice() != null) { session.Execute(session.Combat.LegalIntents().First()); continue; }
                session.Execute(Intent("endTurn", "discardIds", new string[0]));
            }
        }

        /// <summary>Plays every card it can and ends the turn, until the fight is over (the run's hit points decide a defeat).</summary>
        private static void PlayOut(Core2RunSession session)
        {
            for (var guard = 0; guard < 400 && session.IsInCombat; guard++)
            {
                if (session.Combat.PendingOffer() is JObject offer) { session.Execute(Intent("chooseReaction", "offerId", offer["id"], "optionId", null)); continue; }
                if (session.Combat.PendingChoice() != null) { session.Execute(session.Combat.LegalIntents().First()); continue; }
                var played = false;
                foreach (var card in session.Combat.State.Piles.Hand.ToList())
                {
                    var target = session.Combat.State.Enemies.FirstOrDefault(e => e.Value<bool>("alive"))?.Value<string>("id");
                    var intent = Intent("playCard", "cardInstanceId", card.Value<string>("instanceId"));
                    if (target != null) intent["targetId"] = target;
                    if (!session.IsInCombat) return;
                    if (session.Execute(intent).Accepted) { played = true; break; }
                }
                if (!played) session.Execute(Intent("endTurn", "discardIds", new string[0]));
            }
        }
    }
}
