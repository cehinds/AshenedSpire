using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Loop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using EK = Ashen.Generated.EventKeys;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using LV = Ashen.Generated.LoopValues;
using MK = Ashen.Generated.MapKeys;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;
using WV = Ashen.Generated.RewardsValues;

namespace Ashen.App.Nodes
{
    /// <summary>
    /// A legacy dungeon (W-13; US-4.5) on the climb's session, over the ported dungeon (<see cref="LegacyDungeons"/>): the run
    /// stands on a node; a node's dialogue offers its responses (<see cref="Choose"/>: listen, fight, flee — a Dexterity roll
    /// on 'events') and the chosen response's <see cref="Continue"/>; a resolved node offers its exits (<see cref="Travel"/>,
    /// one edge at a time, unvisited rooms and the way to the boss first); the room a node is (a shrine's stay, a cache, a
    /// fight) is entered with <see cref="EnterRoom"/> (the W-13 tray's Rest / Open / Fight — D-142n: travel and entering are
    /// two presses; the run ends up exactly where the shipped travel-then-enter puts it); a cleared dungeon can be left
    /// (<see cref="Leave"/>: the summit's victory or the next act). Every step goes through <see cref="RunSession.NodeStep"/>
    /// (saved; the door it opens routed like travel) and returns the run's location after it. Engine-free.
    /// </summary>
    public sealed class DungeonSession
    {
        private JObject _run;

        private DungeonSession(RunSession owner)
        {
            Owner = owner;
            Refresh();
        }

        public RunSession Owner { get; }
        public LoopData Data => Owner.Content.Loop;

        /// <summary>The run copy the readers below see (refreshed after every step).</summary>
        public JObject Run => _run;

        public JObject State => _run.Obj(RK.LegacyDungeon);
        public JObject Definition => LegacyDungeons.Definition(Data, _run);
        public JObject Node => LegacyDungeons.Node(Data, _run);

        /// <summary>The run left the dungeon (it was cleared and left).</summary>
        public bool Left => State == null;

        public static DungeonSession Start(RunSession owner)
        {
            if (owner?.DungeonId == null) throw new InvalidOperationException(NodeMessages.NotInDungeon);
            return new DungeonSession(owner);
        }

        public void Refresh() => _run = Owner.Run;

        /// <summary>The room the node stands for now (dungeonNodeAction): dialogue, map (resolved or cleared), rest, treasure, combat.</summary>
        public string Room => State == null ? WV.MapDoor : LegacyDungeons.NodeAction(Data, _run);

        /// <summary>A chosen response waits for Continue.</summary>
        public JObject Pending => State?.Obj(K.Pending);

        public bool Cleared => State?.Is(LK.Cleared) ?? false;

        /// <summary>A stay at a dungeon shrine is open (the rest screen's).</summary>
        public bool Resting => State?.Is(LK.ActiveRest) ?? false;

        /// <summary>The responses the node's dialogue offers now (none while one is pending).</summary>
        public List<JObject> Choices() => State == null || Pending != null || Room != LV.DialogueAction ? new List<JObject>() : LegacyDungeons.Choices(Data, _run);

        /// <summary>The nodes one edge away when travel is open (the node resolved or the dungeon cleared, nothing pending): unvisited first, then nearer the boss room.</summary>
        public List<string> Exits()
        {
            if (State == null || Pending != null || Resting) return new List<string>();
            var resolved = Cleared || Js.Includes(State[MK.Resolved], State.Str(WK.Current));
            if (!resolved) return new List<string>();
            var def = Definition;
            var boss = def.Str(LK.BossNode);
            var dist = new Dictionary<string, int>(StringComparer.Ordinal) { [boss] = 0 };
            var queue = new Queue<string>();
            queue.Enqueue(boss);
            while (queue.Count > 0)
            {
                var here = queue.Dequeue();
                foreach (var e in Js.Items(def[LK.Edges]).OfType<JObject>())
                {
                    var other = e.Str(LK.A) == here ? e.Str(LK.B) : e.Str(LK.B) == here ? e.Str(LK.A) : null;
                    if (other != null && !dist.ContainsKey(other))
                    {
                        dist[other] = dist[here] + 1;
                        queue.Enqueue(other);
                    }
                }
            }
            var seen = new HashSet<string>(Js.Items(State[LK.Visited]).Select(Js.Str), StringComparer.Ordinal);
            return LegacyDungeons.Neighbors(Data, _run)
                .OrderBy(n => seen.Contains(n) ? 1 : 0)
                .ThenBy(n => dist.TryGetValue(n, out var v) ? v : int.MaxValue)
                .ThenBy(n => n, StringComparer.Ordinal).ToList();
        }

        private string Step(Func<LoopContext, NodeOutcome> step)
        {
            var location = Owner.NodeStep(step);
            Refresh();
            return location;
        }

        /// <summary>A response (dungeonChoose): its receipt stays on the run until Continue; saved. Null when the node offers no such response.</summary>
        public string Choose(string choiceId)
        {
            if (!Choices().Any(c => c.Str(K.Id) == choiceId)) return null;
            return Step(ctx =>
            {
                LegacyDungeons.Choose(ctx.Data, ctx.Run, choiceId, ctx.Rng);
                return null;
            });
        }

        /// <summary>Continue after a response (showDungeonDialogue's onDone): the node's fight, a stay at the shrine, or back on the dungeon.</summary>
        public string Continue() => Pending == null ? null : Step(LegacyDungeons.ContinueDialogue);

        /// <summary>One step along an edge (travelDungeon); refused (nothing changes, nothing saved) unless the node is resolved and the target a neighbour.</summary>
        public bool Travel(string nodeId)
        {
            if (!Exits().Contains(nodeId)) return false;
            Step(ctx =>
            {
                LegacyDungeons.Travel(ctx.Data, ctx.Run, nodeId);
                return null;
            });
            return true;
        }

        /// <summary>Enter the room the node is (enterDungeonLocation): its shrine's stay, its cache (rolled), its fight. Null for a dialogue or a resolved node.</summary>
        public string EnterRoom()
        {
            if (State == null || Pending != null) return null;
            var room = Room;
            if (room != LV.RestAction && room != MK.Treasure && room != LV.CombatAction) return null;
            return Step(LegacyDungeons.EnterLocation);
        }

        /// <summary>Leave a cleared dungeon (leaveLegacyDungeon): the climb's victory at the summit, else the next act. Null unless cleared.</summary>
        public string Leave() => Cleared ? Step(LegacyDungeons.Leave) : null;
    }

    /// <summary>One W-13 tray control.</summary>
    public sealed class DungeonAction
    {
        /// <summary>A NodeValues dungeon action kind: response, travel, room, continue, leave.</summary>
        public string Kind;

        /// <summary>The response id, the target node, or the room kind.</summary>
        public string Id;

        public string Label;
        public bool Boss;
        public bool Visited;
    }

    /// <summary>A node dot on the W-13 scene-graph mini map (percent position on the dungeon's map art).</summary>
    public sealed class DungeonMapNode
    {
        public string Id;
        public double X;
        public double Y;
        public bool Current;
        public bool Visited;
        public bool Resolved;
        public bool Boss;
        public bool Reachable;
    }

    public sealed class DungeonViewState
    {
        public string DungeonName;
        public string SceneTitle;
        public string NodeName;
        public string NodeKind;
        public string Room;
        public string BackgroundId;
        public string FloorId;
        public string MapId;
        public string OccupantArtId;
        public string Line;
        public string Note;

        /// <summary>What leaving does (the summit's victory or the next act), for the Leave door; null until cleared.</summary>
        public string LeaveText;

        public bool Pending;
        public bool Cleared;
        public List<DungeonAction> Actions = new List<DungeonAction>();
        public List<DungeonMapNode> Map = new List<DungeonMapNode>();
    }

    /// <summary>
    /// W-13 as display data: the scene (background and floor of the catalog scene holding the node, the occupant of a
    /// hostile node), the tray (the node's name, its line — the speaker's words, or the pending response's text — and its
    /// controls: responses, exits with the boss door marked ☠, the room's Rest / Open / Fight, Continue, Leave), and the
    /// scene-graph mini map. Engine-free.
    /// </summary>
    public static class DungeonView
    {
        public static DungeonViewState Build(DungeonSession session, UiData ui)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var strings = ui.Strings;
            var state = new DungeonViewState();
            var def = session.Definition;
            var node = session.Node;
            if (def == null || node == null) return state;
            var d = session.Data;
            var s = session.State;
            var nodes = Js.Items(def[MK.Nodes]).OfType<JObject>().ToList();
            string NameOf(string id) => nodes.FirstOrDefault(n => n.Str(K.Id) == id)?.Str(K.Name) ?? id;
            state.DungeonName = def.Str(K.Name);
            state.NodeName = node.Str(K.Name);
            state.NodeKind = node.Str(K.Kind);
            state.Room = session.Room;
            state.Cleared = session.Cleared;
            state.SceneTitle = strings.Format(NodeStringKeys.NodesDungeonScene, new StringArgs().Add(NodePlaceholders.Node, state.NodeName).Add(NodePlaceholders.Dungeon, state.DungeonName));
            var scene = SceneOf(d, def, node, nodes);
            state.BackgroundId = NodeRules.Fill(ui.Nodes.DungeonBackground, NodePlaceholders.Scene, scene);
            state.FloorId = NodeRules.Fill(ui.Nodes.DungeonFloor, NodePlaceholders.Scene, scene);
            state.MapId = NodeRules.Fill(ui.Nodes.DungeonMap, NodePlaceholders.Asset, def.Str(NodeKeys.AssetId));
            if (d.RuleList(LK.Dungeons, LK.HostileKinds).Contains(state.NodeKind))
            {
                var enc = d.Run.Encounters.Has(node.Str(LK.Encounter)) ? d.Run.Encounters.Get(node.Str(LK.Encounter)) : null;
                var enemy = Js.Str(Js.Items(enc?[K.Enemies]).FirstOrDefault());
                state.OccupantArtId = NodeRules.Fill(ui.Nodes.DungeonOccupantArt, NodePlaceholders.Id, enemy);
            }
            var pending = session.Pending;
            state.Pending = pending != null;
            var resolved = Js.Includes(s[MK.Resolved], node.Str(K.Id));
            if (pending != null) state.Line = pending.Str(LK.Text) ?? string.Empty;
            else if (resolved || session.Cleared) state.Line = node.Str(LK.Reply) ?? strings.Get(NodeStringKeys.NodesDungeonResolved);
            else
                state.Line = strings.Format(NodeStringKeys.NodesDungeonLine, new StringArgs()
                    .Add(NodePlaceholders.Speaker, node.Str(EK.Speaker) ?? strings.Get(NodeStringKeys.NodesDialoguePlayer)).Add(NodePlaceholders.Line, node.Str(NodeKeys.Lore) ?? string.Empty));
            var boss = def.Str(LK.BossNode);
            state.Note = session.Cleared
                ? strings.Get(NodeStringKeys.NodesDungeonCleared)
                : strings.Format(NodeStringKeys.NodesDungeonBossNote, new StringArgs().Add(NodePlaceholders.Boss, def.Str(NodeKeys.Boss) ?? NameOf(boss)).Add(NodePlaceholders.Node, NameOf(boss)));

            if (session.Cleared)
            {
                var final = session.Run.Num(RK.ActNumber) >= d.Rewards.RuleNum(WK.Summit, WK.FinalAct) && !Ashen.Domain.Rewards.CombatEnd.ModOn(d.Rewards, session.Run, RK.Endless);
                state.LeaveText = strings.Get(final ? NodeStringKeys.NodesDungeonLeaveFinal : NodeStringKeys.NodesDungeonLeaveNext);
            }

            if (pending != null)
                state.Actions.Add(new DungeonAction { Kind = NodeValues.ActionContinue, Label = strings.Get(NodeStringKeys.NodesDungeonContinue) });
            else
            {
                foreach (var choice in session.Choices())
                {
                    var id = choice.Str(K.Id);
                    var label = id == LV.FleeChoice
                        ? strings.Format(NodeStringKeys.NodesDungeonChoiceFlee, new StringArgs().Add(NodePlaceholders.Chance, NodeText.Number(LegacyDungeons.EscapeChance(d, session.Run))))
                        : strings.Get(ChoiceKey(id));
                    state.Actions.Add(new DungeonAction { Kind = NodeValues.ActionResponse, Id = id, Label = label });
                }
                var room = session.Room;
                if (room == LV.RestAction || room == MK.Treasure || room == LV.CombatAction)
                    state.Actions.Add(new DungeonAction { Kind = NodeValues.ActionRoom, Id = room, Label = strings.Get(RoomKey(room)) });
                if (session.Cleared)
                    state.Actions.Add(new DungeonAction { Kind = NodeValues.ActionLeave, Label = strings.Get(NodeStringKeys.NodesDungeonLeave) });
                var visited = Js.Items(s[LK.Visited]).Select(Js.Str).ToList();
                foreach (var exit in session.Exits())
                {
                    var seen = visited.Contains(exit);
                    var label = exit == boss
                        ? strings.Format(NodeStringKeys.NodesDungeonBossDoor, new StringArgs().Add(NodePlaceholders.Name, NameOf(exit)))
                        : strings.Format(seen ? NodeStringKeys.NodesDungeonTravelSeen : NodeStringKeys.NodesDungeonTravel, new StringArgs().Add(NodePlaceholders.Name, NameOf(exit)));
                    state.Actions.Add(new DungeonAction { Kind = NodeValues.ActionTravel, Id = exit, Label = label, Boss = exit == boss, Visited = seen });
                }
            }

            var exits = new HashSet<string>(session.Exits());
            var visitedSet = new HashSet<string>(Js.Items(s[LK.Visited]).Select(Js.Str));
            var resolvedSet = new HashSet<string>(Js.Items(s[MK.Resolved]).Select(Js.Str));
            foreach (var n in nodes)
            {
                var id = n.Str(K.Id);
                state.Map.Add(new DungeonMapNode
                {
                    Id = id,
                    X = n.Num(NodeKeys.X),
                    Y = n.Num(NodeKeys.Y),
                    Current = id == node.Str(K.Id),
                    Visited = visitedSet.Contains(id),
                    Resolved = resolvedSet.Contains(id),
                    Boss = id == boss,
                    Reachable = exits.Contains(id),
                });
            }
            return state;
        }

        /// <summary>The catalog scene whose node range (nodeStart..nodeEnd, in the dungeon's node order) holds the node.</summary>
        private static string SceneOf(LoopData d, JObject def, JObject node, List<JObject> nodes)
        {
            var at = nodes.FindIndex(n => n.Str(K.Id) == node.Str(K.Id));
            foreach (var scene in Js.Items(d.LegacyDungeons[NodeKeys.Scenes]).OfType<JObject>())
            {
                if (scene.Str(NodeKeys.Region) != def.Str(K.Id)) continue;
                var start = nodes.FindIndex(n => n.Str(K.Id) == scene.Str(NodeKeys.NodeStart));
                var end = nodes.FindIndex(n => n.Str(K.Id) == scene.Str(NodeKeys.NodeEnd));
                if (start >= 0 && end >= 0 && at >= start && at <= end) return scene.Str(K.Id);
            }
            return null;
        }

        private static string ChoiceKey(string id) =>
            id == LV.FightChoice ? NodeStringKeys.NodesDungeonChoiceFight
            : id == LV.LeaveChoice ? NodeStringKeys.NodesDungeonChoiceLeave
            : NodeStringKeys.NodesDungeonChoiceListen;

        private static string RoomKey(string room) =>
            room == LV.RestAction ? NodeStringKeys.NodesDungeonRoomRest
            : room == MK.Treasure ? NodeStringKeys.NodesDungeonRoomTreasure
            : NodeStringKeys.NodesDungeonRoomCombat;
    }
}
