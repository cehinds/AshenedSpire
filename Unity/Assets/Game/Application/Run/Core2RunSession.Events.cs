using System;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Events;
using Ashen.Domain.Loop;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using HV = Ashen.Generated.Core2HostValues;
using K = Ashen.Generated.CombatKeys;
using LM = Ashen.Generated.Core2LoopMessages;
using MK = Ashen.Generated.MapKeys;
using RK = Ashen.Generated.RunKeys;
using RV = Ashen.Generated.Core2RewardsValues;
using SK = Ashen.Generated.ShopKeys;

namespace Ashen.App.Run
{
    /// <summary>What a response of an event did: refused (the web's reason, nothing changed), failed (the effects threw, as the web's do), or the outcome it had.</summary>
    public sealed class Core2EventResult
    {
        /// <summary>The domain result: the outcome (choice id, result text, the history row, quest completions, the effects' events, the fight it started) or the refusal.</summary>
        public EventResult Result;

        /// <summary>The message of an effect that threw (the Turncoat Mirror under a configured profile); the web leaves the run as the throw left it and the screen stays open.</summary>
        public string Error;

        public bool Ok => Result != null && Result.Ok;

        /// <summary>The refusal's string key (<c>events.refusal.*</c>), or null.</summary>
        public string RefusalKey => Result?.Refusal?.Key;

        /// <summary>The response started a fight: Continue enters it.</summary>
        public string Fight => Result?.Fight;
    }

    /// <summary>Where the event's Continue went: the fight a response started, else the map.</summary>
    public sealed class Core2EventEnd
    {
        public string Fight;
        public string Location;
    }

    public sealed partial class Core2RunSession
    {
        private string _eventId;
        private bool _eventDone;
        private Core2EventResult _eventResult;

        // ------------------------------------------------------------------ the treasure node (WP-19)

        // main.js enterNode's treasure branch for a configured run: the combat door's offer, opened at the node (offer id <act>:<node>:combat) and followed by the map. A
        // profile without the rewards module has no door: the climb stays at the map.
        private void EnterTreasure(Core2Door door)
        {
            _pendingPort = null;
            Core2RewardDoor.Begin(Core.Rewards, _run, _rng, RV.SourceCombat, RV.AfterMap, 0, null, false);
        }

        // ------------------------------------------------------------------ the event node (WP-19)

        /// <summary>The event the run stands at (an Unknown node that resolved to one, entered by <see cref="Travel"/>), or null. A save does not keep it: a resume stands at the map.</summary>
        public string EventId => _eventId;

        /// <summary>A response was taken (the event's Continue is live).</summary>
        public bool EventDone => _eventId != null && _eventDone;

        /// <summary>The result of the response taken here, or null.</summary>
        public Core2EventResult EventResult => _eventResult;

        /// <summary>showEvent(eventId): what the event (or a quest chain's dialogue) shows before a response — the visible responses with their price, binding and refusal, the hidden ones, the speaker.</summary>
        public JObject EventDomainView() => _eventId == null ? null : Events.Open(Core.Events, _run, _eventId);

        private void EnterEvent(Core2Door door)
        {
            _pendingPort = null;
            _eventId = door.EventId;
            _eventDone = false;
            _eventResult = null;
        }

        /// <summary>
        /// commitEventChoice: the response's effects, the history row and the quest completions (engine/quests.js). A response the event lacks, one the history has not opened or one the
        /// purse cannot pay is refused and changes nothing. An effect that throws (the class swap of a configured profile) leaves the run as the throw left it, as the web does, and
        /// the event stays open. A response taken is not saved: the web persists when the event's Continue is pressed.
        /// </summary>
        public Core2EventResult ChooseEvent(string choiceId)
        {
            if (_eventId == null) return new Core2EventResult { Error = LM.NoEventOpen };
            if (_eventDone) return new Core2EventResult { Error = LM.EventAnswered };
            try
            {
                var result = Events.Choose(Core.Events, _run, _eventId, choiceId, _rng);
                var outcome = new Core2EventResult { Result = result };
                if (result.Ok)
                {
                    _eventDone = true;
                    _eventResult = outcome;
                }
                return outcome;
            }
            catch (InvalidOperationException e) { return new Core2EventResult { Error = e.Message }; }
        }

        /// <summary>
        /// showEvent's onDone (the event's Continue, live once a response is taken): a fight the response started is entered where the run stands, otherwise the climb is back at the
        /// map. Saved.
        /// </summary>
        public Core2EventEnd FinishEvent()
        {
            if (!EventDone) throw new InvalidOperationException(LM.NoResponseTaken);
            var snapshot = Snapshot();
            try
            {
                var encounter = Events.Finish(_run).Str(MK.Fight);
                _eventId = null;
                _eventDone = false;
                _eventResult = null;
                _location = Ashen.Generated.RunFlowValues.LocationMap;
                if (encounter != null)
                    BeginFight(new Core2Door { Kind = HV.DoorFight, NodeId = Js.Str(_run[MK.MapNodeId]), Pool = Core.Loop.Map.Encounters.Get(encounter).Str(Ashen.Generated.RewardsKeys.Pool), EncounterId = encounter });
                Save();
                return new Core2EventEnd { Fight = encounter, Location = Location };
            }
            catch
            {
                Restore(snapshot);
                throw;
            }
        }

        /// <summary>
        /// The response a player with no screen takes: the first one the purse can pay, as the shipped screen's smart default lands there (a test or a bot without the event screen).
        /// </summary>
        public string EventStandInChoice()
        {
            var view = EventDomainView();
            var choice = Js.Items(view?[RK.Choices]).OfType<JObject>().FirstOrDefault(c => c.Is(SK.Affordable));
            return choice?.Str(MK.ChoiceId) ?? throw new InvalidOperationException(string.Format(System.Globalization.CultureInfo.InvariantCulture, LM.NoAffordableResponse, _eventId));
        }
    }
}
