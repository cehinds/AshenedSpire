using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Events;
using Ashen.Domain.Rewards;
using Ashen.Domain.Shop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using LegacyDungeons = Ashen.Domain.Loop.LegacyDungeons;
using LK = Ashen.Generated.LoopKeys;
using LoopContext = Ashen.Domain.Loop.LoopContext;
using LV = Ashen.Generated.LoopValues;
using MK = Ashen.Generated.MapKeys;
using NodeOutcome = Ashen.Domain.Loop.NodeOutcome;
using RK = Ashen.Generated.RunKeys;
using RunLoop = Ashen.Domain.Loop.RunLoop;
using SV = Ashen.Generated.ShopValues;
using WK = Ashen.Generated.RewardsKeys;
using WV = Ashen.Generated.RewardsValues;

namespace Ashen.App.Run
{
    /// <summary>What travelling to a node did: refused (with the reason code) or where the run stands now.</summary>
    public sealed class TravelResult
    {
        public string Refusal;

        /// <summary>The node's kind as the loop resolved it ('fight', 'rest', 'treasure', 'merchant', 'event', 'dungeon').</summary>
        public string Kind;

        /// <summary>RunSession.Location after the step.</summary>
        public string Location;

        public bool Travelled => Refusal == null;
    }

    public sealed partial class RunSession
    {
        private string _eventId;
        private bool _eventDone;

        // ------------------------------------------------------------------ the act map (W-06; US-4.1 to US-4.4)

        /// <summary>The seat climbed at the current tier (currentSeat()).</summary>
        public string SeatId => RunLoop.CurrentSeat(Context());

        /// <summary>The region of the current seat (backgrounds are chosen by region), or null.</summary>
        public string Region
        {
            get
            {
                var seat = SeatId;
                return seat != null && Content.Data.Seats.Has(seat) ? Content.Data.Seats.Get(seat).Str(RunFlowKeys.RegionId) : null;
            }
        }

        /// <summary>The node ids the run may travel to from where it stands (the map's start row before the first step), in graph order.</summary>
        public IReadOnlyList<string> ReachableNodes()
        {
            if (Location != RunFlowValues.LocationMap) return Array.Empty<string>();
            var graph = _run.Obj(RK.MapGraph);
            if (graph == null) return Array.Empty<string>();
            var at = _run[MK.MapNodeId];
            var next = Js.Nullish(at) ? graph.Arr(MK.StartIds) : graph.Obj(MK.Nodes)?.Obj(Js.Str(at))?.Arr(MK.Next);
            return Js.Items(next).Select(Js.Str).ToList();
        }

        /// <summary>
        /// Travel to a reachable node (shipped enterNode): the path bookkeeping, then the node's door — a fight entered on
        /// the run's RNG, a rest place's stay opened (its arrival refill), a treasure room's offer held as a reward
        /// checkpoint so a reload resumes it, a merchant's stock rolled, an event or a legacy dungeon entered. Saved at once
        /// (PF-06 "node entered"); a failed save restores the run and rethrows.
        /// </summary>
        public TravelResult Travel(string nodeId)
        {
            if (Location != RunFlowValues.LocationMap) return new TravelResult { Refusal = RunFlowValues.RefusalNotOnMap, Location = Location };
            if (!ReachableNodes().Contains(nodeId)) return new TravelResult { Refusal = RunFlowValues.RefusalNotReachable, Location = Location };
            var snapshot = Snapshot();
            try
            {
                var ctx = Context();
                var outcome = RunLoop.EnterNode(ctx, nodeId);
                Route(ctx, outcome);
                Commit(snapshot);
                return new TravelResult { Kind = outcome.Kind, Location = Location };
            }
            catch
            {
                Restore(snapshot);
                throw;
            }
        }

        /// <summary>A door the loop opened, as this session's location.</summary>
        private void Route(LoopContext ctx, NodeOutcome outcome)
        {
            switch (outcome.Kind)
            {
                case LV.FightOutcome:
                    _location = OnMapOrDungeon();
                    BeginFight(outcome);
                    return;
                case LV.RestOutcome:
                    OpenStay(ctx, outcome.Location);
                    return;
                case MK.Treasure:
                    // A treasure room's offer (shipped: mountRewards with no checkpoint). Held as the run's reward checkpoint so a
                    // reload resumes the same door; Continue closes it as the shipped onDone does (D-125).
                    if (outcome.Rewards != null) CombatEnd.BeginPendingReward(_run, outcome.Rewards, MK.Treasure, WV.MapDoor);
                    _location = OnMapOrDungeon();
                    return;
                case MK.Merchant:
                    Shop.Open(Content.Loop.Shop, _run, _rng);
                    _location = RunFlowValues.LocationMerchant;
                    return;
                case MK.Event:
                    _eventId = outcome.EventId;
                    _eventDone = false;
                    _location = RunFlowValues.LocationEvent;
                    return;
                case LV.DungeonOutcome:
                    _location = RunFlowValues.LocationDungeon;
                    return;
                default:
                    if (outcome.End != null)
                    {
                        CloseOut(outcome.End);
                        return;
                    }
                    _location = OnMapOrDungeon();
                    return;
            }
        }

        /// <summary>
        /// The seam the node screens (merchant, event, legacy dungeon) build on: run a step of the loop over this run and
        /// commit it (the run and the profile cross one save; a failure restores both and rethrows).
        /// </summary>
        public T CommitStep<T>(Func<LoopContext, T> step)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));
            var snapshot = Snapshot();
            try
            {
                var result = step(Context());
                Commit(snapshot);
                return result;
            }
            catch
            {
                Restore(snapshot);
                throw;
            }
        }

        private void Require(string location)
        {
            if (Location != location) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.NotAtNode, location));
        }

        // ------------------------------------------------------------------ merchant (W-09 is the node screens' stream; the seam)

        /// <summary>The merchant's Leave (shop.js onLeave): the stock leaves the run, the slot is saved at the map.</summary>
        public void LeaveMerchant()
        {
            Require(RunFlowValues.LocationMerchant);
            CommitStep(ctx =>
            {
                var left = Shop.Execute(Content.Loop.Shop, _run, new ShopAction { Kind = SV.ActionLeave }, ShopSellOn);
                if (!left.Ok) throw new InvalidOperationException(left.Refusal?.Key);
                _location = RunFlowValues.LocationMap;
                return left.Receipt;
            });
        }

        // ------------------------------------------------------------------ events (W-11 is the node screens' stream; the seam)

        /// <summary>The event the run stands at, or null.</summary>
        public string EventId => Location == RunFlowValues.LocationEvent ? _eventId : null;

        /// <summary>A response was taken (the event's Continue is live); the event door has not closed yet.</summary>
        public bool EventDone => Location == RunFlowValues.LocationEvent && _eventDone;

        /// <summary>showEvent(eventId): the view the event screen draws (visible choices with prices, bindings and refusals).</summary>
        public JObject EventView() => EventId == null ? null : Events.Open(Content.Loop.Events, _run, _eventId);

        /// <summary>
        /// The response the planned event door takes: the first visible, affordable choice — the shipped screen's smart
        /// default (focusFirst on the first available choice); the shipped event cannot be left without a response (D-126).
        /// </summary>
        public string EventStandInChoice()
        {
            var view = EventView();
            var choice = Js.Items(view?[RunKeys.Choices]).OfType<JObject>().FirstOrDefault(c => c.Is(ShopKeys.Affordable));
            return choice?.Str(MK.ChoiceId) ?? throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.NoEventChoice, _eventId));
        }

        /// <summary>commitEventChoice: the response's effects, history row and quest completions; saved with the event still open.</summary>
        public EventResult ChooseEvent(string choiceId)
        {
            Require(RunFlowValues.LocationEvent);
            if (_eventDone) return new EventResult { Refusal = new Ashen.Domain.Shop.Refusal(EventStringKeys.EventsRefusalUnknownChoice) };
            var snapshot = Snapshot();
            try
            {
                var result = Events.Choose(Content.Loop.Events, _run, _eventId, choiceId, _rng ?? (_rng = RunRng()));
                if (!result.Ok)
                {
                    Restore(snapshot);
                    return result;
                }
                _eventDone = true;
                Commit(snapshot);
                return result;
            }
            catch
            {
                Restore(snapshot);
                throw;
            }
        }

        /// <summary>showEvent's onDone: a fight the response started enters where the run stands; otherwise the map. Saved.</summary>
        public string FinishEvent()
        {
            Require(RunFlowValues.LocationEvent);
            if (!_eventDone) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.NoEventChoice, _eventId));
            return CommitStep(ctx =>
            {
                var fight = RunLoop.EnterEventCombat(ctx);
                _eventId = null;
                _eventDone = false;
                _location = RunFlowValues.LocationMap;
                if (fight != null) BeginFight(fight);
                return Location;
            });
        }

        // ------------------------------------------------------------------ legacy dungeons (W-13 is the node screens' stream; the planned door's walk)

        /// <summary>The dungeon the run stands in (its definition id), or null.</summary>
        public string DungeonId => _run.Obj(RK.LegacyDungeon)?.Str(K.Id);

        /// <summary>
        /// The planned dungeon door's one step (D-127), the legal walk the loop oracle's harness takes: a pending response
        /// continues; a stay at the dungeon shrine opens; a cleared dungeon is left (the summit or the next act); a dialogue
        /// takes its first response; on the dungeon map the run steps one edge toward the boss room (unvisited rooms first);
        /// otherwise the room is entered (its fight, shrine or cache). Saved; returns the location after the step.
        /// </summary>
        public string AdvanceDungeon()
        {
            Require(RunFlowValues.LocationDungeon);
            var snapshot = Snapshot();
            try
            {
                var ctx = Context();
                var d = Content.Loop;
                var s = _run.Obj(RK.LegacyDungeon);
                if (Js.Truthy(s[LK.ActiveRest])) OpenStay(ctx, null);
                else if (Js.Truthy(s[K.Pending])) Route(ctx, LegacyDungeons.ContinueDialogue(ctx));
                else if (Js.Truthy(s[LK.Cleared]))
                {
                    var left = LegacyDungeons.Leave(ctx);
                    if (left.End != null)
                    {
                        CloseOut(left.End);
                        return Location;
                    }
                    _location = OnMapOrDungeon();
                }
                else
                {
                    var action = LegacyDungeons.NodeAction(d, _run);
                    if (action == LV.DialogueAction)
                    {
                        var first = LegacyDungeons.Choices(d, _run).First();
                        LegacyDungeons.Choose(d, _run, first.Str(K.Id), _rng);
                    }
                    else if (action == WV.MapDoor) Route(ctx, LegacyDungeons.TravelTo(ctx, TowardBoss(d, s)));
                    else Route(ctx, LegacyDungeons.EnterLocation(ctx));
                }
                Commit(snapshot);
                return Location;
            }
            catch
            {
                Restore(snapshot);
                throw;
            }
        }

        /// <summary>The neighbour one step nearer the boss room (breadth-first over the dungeon's edges), unvisited rooms first, then by id.</summary>
        private string TowardBoss(Ashen.Domain.Loop.LoopData d, JObject state)
        {
            var def = LegacyDungeons.Definition(d, _run);
            var boss = def.Str(LK.BossNode);
            var dist = new Dictionary<string, int>(StringComparer.Ordinal) { [boss] = 0 };
            var queue = new Queue<string>();
            queue.Enqueue(boss);
            while (queue.Count > 0)
            {
                var here = queue.Dequeue();
                foreach (var e in Js.Items(def[LK.Edges]).OfType<JObject>())
                {
                    var a = e.Str(LK.A);
                    var b = e.Str(LK.B);
                    var other = a == here ? b : b == here ? a : null;
                    if (other != null && !dist.ContainsKey(other))
                    {
                        dist[other] = dist[here] + 1;
                        queue.Enqueue(other);
                    }
                }
            }
            var seen = new HashSet<string>(Js.Items(state[LK.Visited]).Select(Js.Str), StringComparer.Ordinal);
            return LegacyDungeons.Neighbors(d, _run)
                .OrderBy(n => seen.Contains(n) ? 1 : 0)
                .ThenBy(n => dist.TryGetValue(n, out var v) ? v : int.MaxValue)
                .ThenBy(n => n, StringComparer.Ordinal)
                .First();
        }
    }
}
