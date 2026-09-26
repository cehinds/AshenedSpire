using System;
using System.Collections.Generic;
using System.Linq;
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
    /// A legacy dungeon as the application plays it (W-13; US-4.5), over the ported dungeon (<see cref="LegacyDungeons"/>):
    /// the run stands on a node; a node's dialogue offers its responses (<see cref="Choose"/>: listen, fight, flee — a
    /// Dexterity roll on 'events') and the chosen response's <see cref="Continue"/>; a resolved node offers its exits
    /// (<see cref="Travel"/>, one edge at a time); the room a node is (a shrine's stay, a cache, a fight) is entered with
    /// <see cref="EnterRoom"/> (the W-13 tray's Rest / Open / Fight — D-142n: travel and entering are two presses; the run
    /// ends up exactly where the shipped travel-then-enter puts it); a cleared dungeon can be left (<see cref="Leave"/>:
    /// the summit's victory or the next act). Every step that changes the run is saved; a fight is handed to combat (the
    /// owner saves it as it starts). Engine-free.
    /// </summary>
    public sealed class DungeonSession
    {
        private DungeonSession(LoopContext ctx, INodeHost host, NodeEntry entry)
        {
            Context = ctx;
            Host = host;
            Entry = entry;
        }

        public LoopContext Context { get; }
        public INodeHost Host { get; }
        public NodeEntry Entry { get; }
        public LoopData Data => Context.Data;
        public JObject Run => Context.Run;
        public JObject State => Run.Obj(RK.LegacyDungeon);
        public JObject Definition => LegacyDungeons.Definition(Data, Run);
        public JObject Node => LegacyDungeons.Node(Data, Run);

        /// <summary>The run left the dungeon (it was cleared and left).</summary>
        public bool Left => State == null;

        public static DungeonSession Start(LoopContext ctx, INodeHost host, NodeEntry entry = null)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            if (!(ctx.Run[RK.LegacyDungeon] is JObject state)) throw new InvalidOperationException(NodeMessages.NotInDungeon);
            var session = new DungeonSession(ctx, host, entry ?? new NodeEntry { Screen = ScreenIds.LegacyDungeon, Kind = LV.DungeonOutcome, DungeonId = state.Str(K.Id) });
            new NodeStep(ctx).Commit(host, session.Entry);
            return session;
        }

        /// <summary>The room the node stands for now (dungeonNodeAction): dialogue, map (resolved or cleared), rest, treasure, combat.</summary>
        public string Room => State == null ? WV.MapDoor : LegacyDungeons.NodeAction(Data, Run);

        /// <summary>A chosen response waits for Continue.</summary>
        public JObject Pending => State?.Obj(K.Pending);

        public bool Cleared => State?.Is(LK.Cleared) ?? false;

        /// <summary>A stay at a dungeon shrine is open (the rest screen's).</summary>
        public bool Resting => State?.Is(LK.ActiveRest) ?? false;

        /// <summary>The responses the node's dialogue offers now (none while one is pending).</summary>
        public List<JObject> Choices() => State == null || Pending != null || Room != LV.DialogueAction ? new List<JObject>() : LegacyDungeons.Choices(Data, Run);

        /// <summary>The nodes one edge away, when travel is open (the node resolved or the dungeon cleared, nothing pending).</summary>
        public List<string> Exits()
        {
            if (State == null || Pending != null || Resting) return new List<string>();
            var resolved = Cleared || Js.Includes(State[MK.Resolved], State.Str(WK.Current));
            return resolved ? LegacyDungeons.Neighbors(Data, Run) : new List<string>();
        }

        /// <summary>A response (dungeonChoose): its receipt stays on the run until Continue; saved.</summary>
        public JObject Choose(string choiceId)
        {
            if (!Choices().Any(c => c.Str(K.Id) == choiceId)) return null;
            var step = new NodeStep(Context);
            var pending = LegacyDungeons.Choose(Data, Run, choiceId, Context.Rng);
            step.Commit(Host, Entry);
            return pending;
        }

        /// <summary>
        /// Continue after a response (showDungeonDialogue's onDone): a fight (entered now, for the owner to start and save),
        /// a stay at the shrine (the rest screen), or back on the dungeon (resolved, or stepped back after an escape).
        /// </summary>
        public NodeExit Continue()
        {
            if (Pending == null) return null;
            var step = new NodeStep(Context);
            var outcome = LegacyDungeons.ContinueDialogue(Context);
            if (outcome.Kind == LV.FightOutcome) return new NodeExit { Kind = NodeValues.ExitFight, Fight = outcome };
            step.Commit(Host, Entry);
            return outcome.Kind == LV.RestAction ? NodeExit.To(NodeValues.ExitRest) : NodeExit.To(NodeValues.ExitStay);
        }

        /// <summary>One step along an edge (travelDungeon); refused (nothing changes) unless the node is resolved and the target a neighbour.</summary>
        public bool Travel(string nodeId)
        {
            if (!Exits().Contains(nodeId)) return false;
            var step = new NodeStep(Context);
            if (!LegacyDungeons.Travel(Data, Run, nodeId)) return false;
            step.Commit(Host, Entry);
            return true;
        }

        /// <summary>
        /// Enter the room the node is (enterDungeonLocation): a stay at its shrine (the rest screen), a cache (rolled; the
        /// reward screen), its fight (entered now, for the owner to start and save); a dialogue or a resolved node stays.
        /// </summary>
        public NodeExit EnterRoom()
        {
            if (State == null || Pending != null) return null;
            var room = Room;
            if (room != LV.RestAction && room != MK.Treasure && room != LV.CombatAction) return null;
            var step = new NodeStep(Context);
            var outcome = LegacyDungeons.EnterLocation(Context);
            if (outcome.Kind == LV.FightOutcome) return new NodeExit { Kind = NodeValues.ExitFight, Fight = outcome };
            step.Commit(Host, Entry);
            if (outcome.Kind == MK.Treasure) return NodeExit.To(NodeValues.ExitRewards);
            if (outcome.Kind == LV.RestOutcome) return NodeExit.To(NodeValues.ExitRest);
            return NodeExit.To(NodeValues.ExitStay);
        }

        /// <summary>Leave a cleared dungeon (leaveLegacyDungeon): the climb's victory at the summit, else the next act (saved at its map).</summary>
        public NodeExit Leave()
        {
            if (!Cleared) return null;
            var step = new NodeStep(Context);
            var outcome = LegacyDungeons.Leave(Context);
            if (outcome.Kind == LV.RefusedOutcome) return null;
            if (outcome.End != null)
            {
                Host?.RunEnded(outcome.End);
                return new NodeExit { Kind = NodeValues.ExitVictory, End = outcome.End };
            }
            step.Commit(Host, null);
            return NodeExit.To(NodeValues.ExitMap);
        }
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
                var final = session.Run.Num(RK.ActNumber) >= d.Rewards.RuleNum(WK.Summit, WK.FinalAct) && !RunLoop.EndlessOn(session.Context);
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
                var visited = Js.Items(s[LK.Visited]).Select(Js.Str).ToList();
                foreach (var exit in session.Exits())
                {
                    var seen = visited.Contains(exit);
                    var label = exit == boss
                        ? strings.Format(NodeStringKeys.NodesDungeonBossDoor, new StringArgs().Add(NodePlaceholders.Name, NameOf(exit)))
                        : strings.Format(seen ? NodeStringKeys.NodesDungeonTravelSeen : NodeStringKeys.NodesDungeonTravel, new StringArgs().Add(NodePlaceholders.Name, NameOf(exit)));
                    state.Actions.Add(new DungeonAction { Kind = NodeValues.ActionTravel, Id = exit, Label = label, Boss = exit == boss, Visited = seen });
                }
                if (session.Cleared)
                    state.Actions.Add(new DungeonAction { Kind = NodeValues.ActionLeave, Label = strings.Get(NodeStringKeys.NodesDungeonLeave) });
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
