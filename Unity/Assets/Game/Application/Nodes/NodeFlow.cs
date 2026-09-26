using System;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Events;
using Ashen.Domain.Loop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using LK = Ashen.Generated.LoopKeys;
using LV = Ashen.Generated.LoopValues;
using MK = Ashen.Generated.MapKeys;
using RK = Ashen.Generated.RunKeys;
using SK = Ashen.Generated.ShopKeys;

namespace Ashen.App.Nodes
{
    /// <summary>
    /// The start (and resume) payload of a node screen (D-141n): which screen, the NodeOutcome kind that opened it, the
    /// event or dungeon it shows and, once an event's response is taken, that response (the event's resolved state).
    /// It is saved in the run payload's 'node' so Save &amp; quit resumes on the same screen in the same state.
    /// </summary>
    public sealed class NodeEntry
    {
        /// <summary>ScreenIds.Merchant, Event, Dialogue or LegacyDungeon.</summary>
        public string Screen;

        /// <summary>The NodeOutcome kind (merchant, event, dungeon).</summary>
        public string Kind;

        public string EventId;
        public string ChoiceId;
        public string DungeonId;
        public string EncounterId;

        public NodeEntry With(string choiceId) => new NodeEntry { Screen = Screen, Kind = Kind, EventId = EventId, ChoiceId = choiceId, DungeonId = DungeonId, EncounterId = EncounterId };

        public JObject ToJson()
        {
            var o = new JObject { [NodeKeys.Screen] = Screen, [NodeKeys.Kind] = Kind };
            if (EventId != null) o[NodeKeys.EventId] = EventId;
            if (ChoiceId != null) o[NodeKeys.ChoiceId] = ChoiceId;
            if (DungeonId != null) o[NodeKeys.DungeonId] = DungeonId;
            if (EncounterId != null) o[NodeKeys.EncounterId] = EncounterId;
            return o;
        }

        public static NodeEntry FromJson(JToken token) => token is JObject o && o.Str(NodeKeys.Screen) != null
            ? new NodeEntry
            {
                Screen = o.Str(NodeKeys.Screen),
                Kind = o.Str(NodeKeys.Kind),
                EventId = o.Str(NodeKeys.EventId),
                ChoiceId = o.Str(NodeKeys.ChoiceId),
                DungeonId = o.Str(NodeKeys.DungeonId),
                EncounterId = o.Str(NodeKeys.EncounterId),
            }
            : null;
    }

    /// <summary>
    /// Where a node screen leads when it closes (NodeValues exit kinds): the act map, a fight to hand to W-07 (the loop
    /// already entered it: <see cref="Fight"/> carries its createCombat arguments), a rest stay (a dungeon shrine), a
    /// pending reward (a dungeon cache) for W-08, or the run's end (a cleared dungeon left at the summit) or the next act.
    /// </summary>
    public sealed class NodeExit
    {
        public string Kind;

        /// <summary>The fight the loop entered (RunLoop.EnterCombat / EnterEventCombat), for the combat screen.</summary>
        public NodeOutcome Fight;

        /// <summary>The run's close-out when leaving a cleared dungeon ended the climb.</summary>
        public RunEndReceipt End;

        public static NodeExit To(string kind) => new NodeExit { Kind = kind };

        public bool IsMap => Kind == NodeValues.ExitMap;
        public bool IsFight => Kind == NodeValues.ExitFight;
    }

    /// <summary>
    /// What a node session needs from the run's owner (RunSession implements it): the resolved shopSell setting, and a
    /// save door. <see cref="Checkpoint"/> writes the run (and the profile when a step changed it) with the screen's
    /// resume entry, or at the act map when the entry is null; it throws when the save fails, and the session then puts
    /// the run and the profile back as they were.
    /// </summary>
    public interface INodeHost
    {
        /// <summary>settingOn(settings, 'shopSell') (US-9.3; D-081).</summary>
        bool SellOn { get; }

        void Checkpoint(NodeEntry entry);

        /// <summary>The run was closed out by a node (a cleared dungeon left at the summit): write the profile, clear the slot.</summary>
        void RunEnded(RunEndReceipt end);
    }

    /// <summary>
    /// The router's entry point for the node screens (D-141n): the screen and start payload a travel outcome opens —
    /// keyed by NodeOutcome kind: 'merchant' → merchant, 'event' → event (a quest chain's step, which has a speaker, →
    /// dialogue), 'dungeon' → legacyDungeon — and where a loaded or returning run stands among them.
    /// </summary>
    public static class NodeScreens
    {
        /// <summary>The node screen a travel outcome opens, or null (fights, rests and treasure are other screens).</summary>
        public static NodeEntry For(LoopData d, NodeOutcome outcome)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            if (outcome == null) return null;
            if (outcome.Kind == MK.Merchant) return new NodeEntry { Screen = ScreenIds.Merchant, Kind = outcome.Kind };
            if (outcome.Kind == MK.Event)
                return new NodeEntry
                {
                    Screen = Quests.QuestChainForEvent(d.Events, outcome.EventId) != null ? ScreenIds.Dialogue : ScreenIds.Event,
                    Kind = outcome.Kind,
                    EventId = outcome.EventId,
                };
            if (outcome.Kind == LV.DungeonOutcome)
                return new NodeEntry { Screen = ScreenIds.LegacyDungeon, Kind = outcome.Kind, DungeonId = outcome.DungeonId, EncounterId = outcome.EncounterId };
            return null;
        }

        /// <summary>
        /// Where a run resumes among the node screens: inside a legacy dungeon it is the dungeon (whatever was saved); a
        /// saved merchant only while its stock is still on the run; a saved event as saved. Null means none (the map,
        /// or the fight and reward screens, which the caller checks first).
        /// </summary>
        public static NodeEntry Resume(JObject run, NodeEntry saved)
        {
            if (run == null) return null;
            if (run[RK.LegacyDungeon] is JObject dungeon)
                return new NodeEntry { Screen = ScreenIds.LegacyDungeon, Kind = LV.DungeonOutcome, DungeonId = dungeon.Str(CombatKeys.Id), EncounterId = saved?.EncounterId };
            if (saved == null) return null;
            if (saved.Screen == ScreenIds.Merchant) return run[SK.ShopStock] is JObject ? saved : null;
            if (saved.Screen == ScreenIds.Event || saved.Screen == ScreenIds.Dialogue) return saved.EventId != null ? saved : null;
            return null;
        }

        /// <summary>The screens this module builds.</summary>
        public static bool IsNodeScreen(string screen) =>
            screen == ScreenIds.Merchant || screen == ScreenIds.Event || screen == ScreenIds.Dialogue || screen == ScreenIds.LegacyDungeon;
    }

    /// <summary>Put a document back as it was, in place (sessions share the run and profile objects with their owner).</summary>
    internal static class Docs
    {
        public static void Restore(JObject target, JObject snapshot)
        {
            if (target == null || snapshot == null) return;
            target.RemoveAll();
            foreach (var p in snapshot.Properties().ToList()) target.Add(p.Name, p.Value.DeepClone());
        }
    }

    /// <summary>One step's before-image (run and profile) and the save through the host; a failed save restores both and rethrows.</summary>
    internal sealed class NodeStep
    {
        private readonly LoopContext _ctx;
        private readonly JObject _run;
        private readonly JObject _profile;

        public NodeStep(LoopContext ctx)
        {
            _ctx = ctx;
            _run = (JObject)ctx.Run.DeepClone();
            _profile = (JObject)ctx.Profile.DeepClone();
        }

        public void Undo()
        {
            Docs.Restore(_ctx.Run, _run);
            Docs.Restore(_ctx.Profile, _profile);
        }

        public void Commit(INodeHost host, NodeEntry entry)
        {
            if (host == null) return;
            try { host.Checkpoint(entry); }
            catch
            {
                Undo();
                throw;
            }
        }
    }
}
