using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Ashen.App.Combat;
using Ashen.App.Saves;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Loop;
using Ashen.Domain.Random;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using HK = Ashen.Generated.Core2HostKeys;
using HV = Ashen.Generated.Core2HostValues;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using MK = Ashen.Generated.MapKeys;
using NK = Ashen.Generated.Core2SnapshotKeys;
using NM = Ashen.Generated.Core2SnapshotMessages;
using NN = Ashen.Generated.Core2SnapshotNumbers;
using RV = Ashen.Generated.Core2RewardsValues;
using RW = Ashen.Generated.Core2RewardsKeys;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;
using WV = Ashen.Generated.RewardsValues;

namespace Ashen.App.Run
{
    public enum Core2RunLoadStatus
    {
        Resumed,
        ResumedWithWarning,
        Empty,

        /// <summary>A save from a newer build: kept, never loaded.</summary>
        Newer,

        /// <summary>No valid copy, or a payload this build cannot resume.</summary>
        Damaged,

        /// <summary>The slot holds another kind of run (a 405 run, save format 2): the caller loads it with <see cref="RunSession"/>.</summary>
        OtherRuleset,

        /// <summary>The run was made under another profile (D-171): the slot is kept as it is.</summary>
        ProfileMismatch,
    }

    public sealed class Core2RunLoadResult
    {
        public Core2RunLoadStatus Status;
        public Core2RunSession Session;
        public readonly List<string> Warnings = new List<string>();

        public bool Resumed => Session != null;
    }

    /// <summary>What travelling to a node did: refused (with the reason code), or the door the node opened.</summary>
    public sealed class Core2TravelResult
    {
        public string Refusal;

        /// <summary>The door: a fight (with its encounter), or a rest place, shop, treasure room or event.</summary>
        public Core2Door Door;

        public string Location;

        public bool Travelled => Refusal == null;

        /// <summary>Every node kind of a Core 2 map has its rules (WP-19); this stays true only for a place the profile has no module for (a rest place of a profile without the rest module), whose notice with Continue stands in.</summary>
        public bool NotYetPorted;
    }

    /// <summary>How a Core 2 fight ended for the run.</summary>
    public sealed class Core2FightEnd
    {
        /// <summary>'victory' or 'defeat' (CombatValues).</summary>
        public string Outcome;

        /// <summary>The run closed out with this fight: a death, or the last act's boss.</summary>
        public bool RunOver;

        /// <summary>The act advanced (a boss that was not the last).</summary>
        public bool ActAdvanced;

        /// <summary>What a fight paid and where the climb went from it (WP-15): the skill and class awards, the cinders, the level notices, the door.</summary>
        public Core2CombatEndReceipt Receipt;

        /// <summary>What a won fight pays that this build does not roll yet: nothing now that Core 2 rewards are ported (WP-15); kept for the screens that list it.</summary>
        public IReadOnlyList<string> NotYetPorted = Array.Empty<string>();
    }

    /// <summary>What a step of the reward door did: refused (the web's sentence, nothing changed), or the option it took and where the climb goes next.</summary>
    public sealed class Core2RewardResult
    {
        /// <summary>The refusal's sentence, or null when the step was taken.</summary>
        public string Refusal;

        /// <summary>The add-to-deck refusal code (<c>notCard</c>, <c>full</c>, <c>limit</c>) when that is what refused.</summary>
        public string Code;

        /// <summary>The reward option taken (claim, add to deck), or null.</summary>
        public JObject Option;

        /// <summary>Where the door stands after the step.</summary>
        public Core2RewardStep Step;

        public bool Done => Refusal == null;
    }

    /// <summary>
    /// A Core 2 run as the application plays it (docs/design/11 WP-C1a, D-302; the pattern of <see cref="RunSession"/>, which stays the 405 path): <see cref="New"/> makes the
    /// run through the Core 2 profile (<see cref="CoreRunRules.Create"/>, the seat order, the first act's map by <see cref="CoreRoutes"/>) and commits it at the act map. From the map the
    /// player travels to a reachable node (<see cref="Travel"/>, main.js enterNode): a fight rolls its encounter and starts a <see cref="Core2CombatSession"/> on the run's own
    /// random streams; a rest place, shop, treasure room or event opens its door (WP-16 to WP-19). Every combat intent goes through the session
    /// (legality first, rollback on failure) and is autosaved as rules/runFlow.json says; the intent that ends the fight writes it back to the run (runCombatEnd) and returns to
    /// the map, a death or the last act's boss closes the run out into the profile and clears the slot. The save is the Core 2 document (format 3, the profile pinned): it
    /// resumes at the map or in the middle of a fight, under the profile it pinned. Engine-free.
    /// </summary>
    public sealed partial class Core2RunSession : IClimb
    {
        private readonly Func<DateTime> _clock;
        private JObject _run;
        private long _playtimeBase;
        private DateTime _playStarted;
        private Rng _rng;
        private ProfileStore _profile;
        private string _location;
        private string _pendingPort;

        private Core2RunSession(RunContent content, SaveService saves, int slotIndex, Func<DateTime> clock)
        {
            Content = content;
            Core = content.Core2;
            Saves = saves;
            SlotIndex = slotIndex;
            Slot = saves.Rules.RunSlotName(slotIndex);
            _clock = clock ?? (() => DateTime.UtcNow);
            _playStarted = _clock();
        }

        public RunContent Content { get; }
        public Core2Content Core { get; }
        public SaveService Saves { get; }
        public int SlotIndex { get; }
        public string Slot { get; }
        public string Name { get; private set; }
        public string Portrait { get; private set; }
        public string ClassId => _run.Str(RK.Class);
        public uint Seed => (uint)_run.Num(RK.Seed);
        public string SeedText => Content.Seeds.Format(Seed);
        public int Act => (int)_run.Num(RK.ActNumber);
        public int Floor => (int)_run.Num(MK.Floor);
        public string EncounterId { get; private set; }

        /// <summary>The fight in progress, or the one that just ended (kept for the end state until the screen leaves).</summary>
        public Core2CombatSession Combat { get; private set; }

        public bool FightFinished { get; private set; }

        public bool IsInCombat => Combat != null && !FightFinished;

        /// <summary>How the last fight ended for the run, or null.</summary>
        public Core2FightEnd LastEnd { get; private set; }

        public bool RunOver { get; private set; }

        /// <summary>The run's close-out (its result record and what was newly earned), once it is over.</summary>
        public RunEndReceipt End { get; private set; }

        /// <summary>The classes the run's close-out opened (the profile's class ledger), once it is over.</summary>
        public IReadOnlyList<string> ClassesUnlocked { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// The kind of node the run stands at whose door has no screen yet ('event' while it is open, or a 'rest' place of a profile without the rest module), until the player moves on; or null.
        /// The rest site and the shop have screens (VP-8, VP-9) and report <see cref="Location"/> instead. Every node's rules are part of the build (WP-19): the act map's notice
        /// (<see cref="DismissPort"/> takes the stand-in action) is the placeholder until the event screen is registered.
        /// </summary>
        public string PendingPort => _eventId != null ? HV.DoorEvent : _pendingPort;

        /// <summary>Where the run stands and a save resumes: 'combat', 'map', or 'runEnd' once the run is over (never saved: the slot is cleared).</summary>
        public string Location
        {
            get
            {
                if (RunOver) return RunFlowValues.LocationRunEnd;
                if (IsInCombat) return RunFlowValues.LocationCombat;
                if (HasPendingReward) return RunFlowValues.LocationRewards;
                if (HasOpenShop) return RunFlowValues.LocationMerchant;
                if (IsResting) return RunFlowValues.LocationRest;
                if (_eventId != null) return RunFlowValues.LocationEvent;
                return _location;
            }
        }

        public JObject Run => (JObject)_run.DeepClone();

        /// <summary>The run's random streams as they stand now, by name (a fight in progress draws on its own state's generator): what the oracle's <c>counters</c> hold.</summary>
        public IReadOnlyDictionary<string, uint> StreamCounters => (IsInCombat ? Combat.State.Rng : _rng).AllCounters();

        public ProfileStore Profile => _profile ??= ProfileStore.Load(Saves);

        /// <summary>Raised after every accepted combat intent (after its autosave), with its outcome.</summary>
        public event Action<Core2Outcome> CommandCommitted;

        // ------------------------------------------------------------------ create

        /// <summary>
        /// A new Core 2 climb in a slot (overwriting what was there), saved at once at the act map: main.js newRun + startClimb for the configured profile. The class must be open
        /// in the profile's ledger (the first run opens the first class).
        /// </summary>
        public static Core2RunSession New(RunContent content, SaveService saves, int slotIndex, uint seed, string classId, string name, Func<DateTime> clock = null, CreationChoices choices = null)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            var core = content.Core2;
            if (classId == null || !core.Combat.Run.Run.Classes.Has(classId))
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.UnknownClass, classId));
            var tint = choices?.Tint ?? content.Flow.DefaultTint;
            var session = new Core2RunSession(content, saves, slotIndex, clock) { Name = name, Portrait = content.Portrait(classId, tint) };
            _ = session.Profile;
            var run = Core2RunLoop.NewClimb(core.Loop, seed, classId, session.Profile.Doc, out var rng);
            var customization = new JObject { [RunFlowKeys.Name] = name };
            foreach (var p in content.Flow.Customization.Properties()) customization[p.Name] = p.Value.DeepClone();
            customization[RunFlowKeys.Tint] = tint;
            if (choices?.Glyph != null) customization[RunFlowKeys.Glyph] = choices.Glyph;
            run[LK.Customization] = customization;
            run[RK.SeedString] = content.Seeds.Format(seed);
            run[RK.Custom] = core.Loop.Engine.Obj(LK.NewRun).Obj(RK.Custom).DeepClone();
            session._run = run;
            session._rng = rng;
            session._location = RunFlowValues.LocationMap;
            session.Save();
            return session;
        }

        // ------------------------------------------------------------------ the act map

        /// <summary>The node ids the run may travel to from where it stands (the map's start row before the first step), in graph order.</summary>
        public string PinnedMapMode => Core.Loop.Routes.PinnedMapMode;

        public IReadOnlyList<string> ReachableNodes()
        {
            if (Location != RunFlowValues.LocationMap) return Array.Empty<string>();
            var graph = _run.Obj(RK.MapGraph);
            if (graph == null) return Array.Empty<string>();
            var at = _run[MK.MapNodeId];
            var next = Js.Nullish(at) ? graph.Arr(MK.StartIds) : graph.Obj(MK.Nodes)?.Obj(Js.Str(at))?.Arr(MK.Next);
            var reachable = Js.Items(next).Select(Js.Str).ToList();
            // A left rest site stays enterable until a new node is chosen (restRevisit.js, main.js showMap revisitId).
            var revisit = Core2Rest.RevisitNodeId(_run);
            if (revisit != null && !reachable.Contains(revisit)) reachable.Add(revisit);
            return reachable;
        }

        /// <summary>The seat climbed at the current tier.</summary>
        public string SeatId => Core2RunLoop.CurrentSeat(_run);

        /// <summary>The region of the current seat (backgrounds are chosen by region), or null.</summary>
        public string Region => RegionOfSeat(SeatId);

        /// <summary>The region of the seat an encounter is fought in (the fight's background), or null.</summary>
        public string RegionOfEncounter(string encounterId) =>
            encounterId != null && Core.Loop.Map.Encounters.Has(encounterId) ? RegionOfSeat(Core.Loop.Map.Encounters.Get(encounterId).Str(MK.Seat)) : null;

        private string RegionOfSeat(string seat) => seat != null && Content.Data.Seats.Has(seat) ? Content.Data.Seats.Get(seat).Str(RunFlowKeys.RegionId) : null;

        private Ashen.App.Settings.SettingsSession _playerSettings;

        /// <summary>The settings over this session's profile (settings/defaults.json): the combat pacing, the map mode, reduced motion.</summary>
        public Ashen.App.Settings.SettingsSession PlayerSettings => _playerSettings ??= new Ashen.App.Settings.SettingsSession(Content, Profile, true);

        public Ashen.Domain.Combat.CombatData CombatData => Core.Combat.Combat;

        public bool ProloguePending => false;

        public string EncounterPool => EncounterId != null && Core.Loop.Map.Encounters.Has(EncounterId) ? Core.Loop.Map.Encounters.Get(EncounterId).Str(MK.Pool) : null;

        /// <summary>The names of the last fight's foes, empty when the run did not end in a fight.</summary>
        public IReadOnlyList<string> LastFoes()
        {
            if (EncounterId == null || Combat == null || !Core.Loop.Map.Encounters.Has(EncounterId)) return Array.Empty<string>();
            var enemies = Core.Combat.Combat.Enemies;
            return Js.Items(Core.Loop.Map.Encounters.Get(EncounterId)[K.Enemies]).Select(Js.Str)
                .Select(id => enemies.Has(id) ? enemies.Get(id).Str(K.Name) ?? id : id).Distinct().ToList();
        }

        /// <summary>
        /// Travel to a reachable node (main.js enterNode): the path bookkeeping, then the node's door — a fight rolls its encounter and starts on the run's streams; a rest place, shop, treasure room
        /// or event opens its own door (the run stands at it). Saved at once; a failure restores the run and rethrows.
        /// </summary>
        public Core2TravelResult Travel(string nodeId)
        {
            if (Location != RunFlowValues.LocationMap) return new Core2TravelResult { Refusal = RunFlowValues.RefusalNotOnMap, Location = Location };
            if (!ReachableNodes().Contains(nodeId)) return new Core2TravelResult { Refusal = RunFlowValues.RefusalNotReachable, Location = Location };
            var snapshot = Snapshot();
            try
            {
                var door = Core2RunLoop.EnterNode(Core.Loop, _run, _rng, nodeId);
                _pendingPort = door.IsFight ? null : door.Kind;
                if (door.IsFight) BeginFight(door);
                else if (door.Kind == HV.DoorShop) EnterShop(door);
                else if (door.Kind == HV.DoorRest) EnterRest(door);
                else if (door.Kind == HV.DoorTreasure) EnterTreasure(door);
                else if (door.Kind == HV.DoorEvent) EnterEvent(door);
                Save();
                return new Core2TravelResult { Door = door, Location = Location, NotYetPorted = _pendingPort != null };
            }
            catch
            {
                Restore(snapshot);
                throw;
            }
        }

        /// <summary>
        /// The player moves past a place whose rules are not part of this build. A reward door still open (the spoils screen, VP-7, is where its offer is worked) is
        /// left as it stands too, as a test or a bot that has no screen does: the notices are read and the offer is left (<see cref="LeaveRewards"/>).
        /// </summary>
        public void DismissPort()
        {
            _pendingPort = null;
            if (HasOpenShop) LeaveShop();
            if (IsResting) LeaveRest();
            if (_eventId != null)
            {
                // A player with no event screen takes the first response the purse can pay and goes on (a fight it starts is entered).
                if (!_eventDone) ChooseEvent(EventStandInChoice());
                FinishEvent();
            }
            // Every step reads the notices or spends a level offer, so the loop ends; a refusal ends it too.
            while (HasPendingReward)
            {
                var step = ProgressionNotices.Count > 0 ? AcknowledgeProgression() : LeaveRewards();
                if (!step.Done) break;
            }
        }

        // ------------------------------------------------------------------ the fight

        private void BeginFight(Core2Door door)
        {
            EncounterId = door.EncounterId;
            _run[RK.CombatEntered] = new JObject { [HK.NodeId] = door.NodeId, [RunSaveKeys.EncounterId] = door.EncounterId };
            Combat = Core2CombatSession.Start(Core.Combat, _rng, _run, door.EncounterId, Profile.Settings);
            FightFinished = false;
            LastEnd = null;
            if (Combat.IsOver) FinishCombat();
        }

        /// <summary>
        /// Review and test hook: enter a named fight where the run stands (the encounter by id), as a node would, and checkpoint it. The climb's fights come from
        /// <see cref="Travel"/>.
        /// </summary>
        public Core2CombatSession StartEncounter(string encounterId)
        {
            BeginFight(new Core2Door { Kind = HV.DoorFight, NodeId = Js.Str(_run[MK.MapNodeId]), Pool = Core.Loop.Map.Encounters.Get(encounterId).Str(MK.Pool), EncounterId = encounterId });
            if (IsInCombat) Save();
            return Combat;
        }

        /// <summary>
        /// Execute a combat intent; an accepted one is autosaved per rules/runFlow.json autosave. The intent that ends the fight runs the fight's end
        /// (<see cref="FinishCombat"/>) before the save.
        /// </summary>
        public Core2Outcome Execute(JObject intent)
        {
            if (!IsInCombat) throw new InvalidOperationException(RunFlowMessages.NotInCombat);
            var flow = Content.Flow;
            var outcome = flow.AfterCommand == RunFlowValues.AutosaveAppend ? Combat.ExecuteAndLog(Saves, Slot, intent) : Combat.Execute(intent);
            if (!outcome.Accepted) return outcome;
            if (Combat.IsOver) FinishCombat();
            else if (flow.AfterCommand == RunFlowValues.AutosaveCheckpoint || flow.CheckpointOn.Contains(intent.Str(K.Type))) Save();
            CommandCommitted?.Invoke(outcome);
            return outcome;
        }

        /// <summary>
        /// The fight's end (main.js onCombatEnd for a configured run, WP-15): <see cref="Core2CombatEnd.Settle"/> writes the fight back to the run (runCombatEnd),
        /// pays the skill and class tracks once (a death pays too), then a death ends the run, and a win pays its cinders, records a boss and either ends the run
        /// (the last act's boss) or opens the reward door with a persisted offer. The climb goes on from the door (<see cref="LeaveRewards"/>): the map, or the next
        /// act after a boss. A death or the summit with level notices still to be read waits for them first (<see cref="AcknowledgeProgression"/>).
        /// </summary>
        private void FinishCombat()
        {
            var state = Combat.State;
            _rng = state.Rng;
            FightFinished = true;
            var receipt = Core2CombatEnd.Settle(Core.Rewards, _run, _rng, state, Core.Loop.Map.Encounters.Get(EncounterId));
            LastEnd = new Core2FightEnd { Outcome = receipt.Result, Receipt = receipt };
            Continue(receipt.Next);
        }

        /// <summary>Goes where the reward door says the climb goes (<see cref="Core2RewardDoor"/>): the door stays open, the map, the next act, or the run's end.</summary>
        private void Continue(string next)
        {
            switch (next)
            {
                case Core2RewardDoor.NextDoor:
                    _location = RunFlowValues.LocationRewards;
                    Save();
                    return;
                case RV.AfterVictory:
                case RV.AfterDefeat:
                    if (LastEnd != null) LastEnd.RunOver = true;
                    CloseOut(next == RV.AfterVictory);
                    return;
                case RV.AfterAdvanceAct:
                    Core2RunLoop.AdvanceAct(Core.Loop, _run, _rng);
                    if (LastEnd != null) LastEnd.ActAdvanced = true;
                    break;
            }
            _location = RunFlowValues.LocationMap;
            Save();
        }

        // ------------------------------------------------------------------ the reward door (WP-15)

        /// <summary>A won fight's offer (or a death's level notices) is waiting: <c>gameProfilePendingReward</c> is on the run, and a save resumes at it.</summary>
        public bool HasPendingReward => Core2RewardDoor.Pending(_run) != null;

        /// <summary>The pending reward record (the offer's id and source, what comes after it, the notices still to be read), or null.</summary>
        public JObject PendingReward => Core2RewardDoor.Pending(_run) is JObject pending ? (JObject)pending.DeepClone() : null;

        /// <summary>The level notices the door still has to show (the skill and class levels the fight crossed), in order.</summary>
        public IReadOnlyList<JObject> ProgressionNotices => Js.Items(Core2RewardDoor.Pending(_run)?[RW.Progression]).OfType<JObject>().Select(n => (JObject)n.DeepClone()).ToList();

        /// <summary>The door has no offer, only notices: a death, or the last act's boss.</summary>
        public bool NoticeOnlyDoor => Core2RewardDoor.Pending(_run)?.Is(RW.NoticeOnly) == true;

        /// <summary>The offer the door shows (id, source, options, take count, taken, claimed), or null when there is none.</summary>
        public JObject RewardOffer
        {
            get
            {
                var pending = Core2RewardDoor.Pending(_run);
                return pending != null && _run.Obj(RW.GameProfileRewardOffers)?[pending.Str(RW.OfferId) ?? string.Empty] is JObject offer ? (JObject)offer.DeepClone() : null;
            }
        }

        /// <summary>The settings the deck rules read for "Add to deck" (the player's deck bounds).</summary>
        private JObject DeckSettings => Profile.Settings;

        /// <summary>The refusal of "Add to deck" for an option: empty when legal, else <c>notCard</c>, <c>full</c> or <c>limit</c> (rewardDeckAddRefusal).</summary>
        public string RewardDeckRefusal(int index)
        {
            var pending = Core2RewardDoor.Pending(_run) ?? throw new InvalidOperationException(Ashen.Generated.Core2RewardsMessages.NoDoor);
            return Core2Rewards.DeckAddRefusal(Core.Rewards, _run, pending.Str(RW.OfferId), index, DeckSettings);
        }

        /// <summary>The progression modal's Continue: the notices are read once (a death or the summit then ends the climb).</summary>
        public Core2RewardResult AcknowledgeProgression()
        {
            var snapshot = Snapshot();
            try
            {
                var step = Core2RewardDoor.Acknowledge(_run);
                Continue(step.Next);
                return new Core2RewardResult { Step = step };
            }
            catch (InvalidOperationException e)
            {
                Restore(snapshot);
                return new Core2RewardResult { Refusal = e.Message };
            }
            catch
            {
                Restore(snapshot);
                throw;
            }
        }

        /// <summary>Take the offered option at <paramref name="index"/> (claimGameProfileReward): a card lands in the sideboard, a weapon in storage, and so on. A refusal changes nothing.</summary>
        public Core2RewardResult TakeReward(int index) => Reward(pending => Core2Rewards.Claim(Core.Rewards, _run, pending.Str(RW.OfferId), index));

        /// <summary>Take the offered card and put it straight into the deck (claimGameProfileRewardToDeck), obeying the deck rules; a refusal changes nothing and carries its code.</summary>
        public Core2RewardResult AddRewardToDeck(int index) => Reward(pending => Core2Rewards.ClaimToDeck(Core.Rewards, _run, pending.Str(RW.OfferId), index, DeckSettings));

        // A claim is all-or-nothing in the domain (Core2Rewards restores the run it was handed when it refuses), so no copy of the run is kept here.
        private Core2RewardResult Reward(Func<JObject, JObject> act)
        {
            var pending = Core2RewardDoor.Pending(_run);
            if (pending == null) return new Core2RewardResult { Refusal = Ashen.Generated.Core2RewardsMessages.NoDoor };
            try
            {
                var option = act(pending);
                Save();
                return new Core2RewardResult { Option = option };
            }
            catch (DeckAddRefusalException e) { return new Core2RewardResult { Refusal = e.Message, Code = e.Code }; }
            catch (InvalidOperationException e) { return new Core2RewardResult { Refusal = e.Message }; }
        }

        /// <summary>The offer screen's Continue: the offer is left as it stands, a level offer still owed opens the door again, otherwise the climb goes on to the map or the next act.</summary>
        public Core2RewardResult LeaveRewards()
        {
            var snapshot = Snapshot();
            try
            {
                if (!HasPendingReward) return new Core2RewardResult { Refusal = Ashen.Generated.Core2RewardsMessages.NoDoor };
                var step = Core2RewardDoor.Leave(Core.Rewards, _run, _rng);
                Continue(step.Next);
                return new Core2RewardResult { Step = step };
            }
            catch
            {
                Restore(snapshot);
                throw;
            }
        }

        /// <summary>The run is over (a death or the summit): the profile holds its record and the class ledger moved, the slot is cleared.</summary>
        private void CloseOut(bool victory)
        {
            // finishRun: the run's record and the profile's unlock ledger, as a configured run writes them (Core2RunEnd).
            var closed = Core2RunEnd.Finish(Core.Rewards, Core.Loop, _run, Profile.Doc, victory);
            ClassesUnlocked = closed.ClassesUnlocked;
            FinishRun(closed.Receipt, closed.Recorded);
        }

        /// <summary>W-20 Abandon run: the run ends here, recorded as abandoned (it counts as a run, earns no unlock), the profile is saved and the slot cleared.</summary>
        public RunEndReceipt Abandon()
        {
            if (RunOver) return End;
            var end = RunEnd.Abandon(new LoopContext(Content.Loop, _run, _rng, Profile.Doc));
            FinishRun(end, true);
            return end;
        }

        public bool Abandoned => End?.Result?.Is(MetaKeys.Abandoned) == true;

        private void FinishRun(RunEndReceipt end, bool recorded)
        {
            var extra = new JObject();
            if (!(end.Result?.Is(MetaKeys.Abandoned) ?? false) && EncounterId != null && Core.Loop.Map.Encounters.Has(EncounterId))
                extra[MetaKeys.Killer] = new JArray(Js.Items(Core.Loop.Map.Encounters.Get(EncounterId)[K.Enemies]).Select(t => t.DeepClone()));
            extra[MetaKeys.Duration] = PlaytimeSeconds;
            if (CoreRunRules.PinOf(_run) is JObject pin)
            {
                extra[MetaKeys.ProfileId] = pin[Core2ProfileKeys.Id]?.DeepClone();
                extra[MetaKeys.ProfileHash] = pin[Core2ProfileKeys.Hash]?.DeepClone();
            }
            extra[MetaKeys.Deck] = new JArray(Js.Items(_run[K.Deck]).OfType<JObject>().Select(c => Js.Obj(MetaKeys.CardId, c[K.CardId]?.DeepClone(), MetaKeys.Upgraded, c.Is(K.Upgraded))));
            if (recorded && Profile.Doc[LK.Results] is JArray results && results.Count > 0 && results[results.Count - 1] is JObject newest) newest.Merge(extra);
            end.Result?.Merge(extra);
            End = end;
            RunOver = true;
            Profile.Save(Content.ContentHash);
            Saves.Delete(Slot);
        }

        // ------------------------------------------------------------------ the deck editor (WP-18)

        /// <summary>
        /// The reason the deck editor stays shut, as a string key, or null when it may open (model/armouryRules.js armouryOpensDeckEditor, editingLocked): the climb is over,
        /// a fight has not resolved (Core edits deck and equipment outside combat only), or the profile or the player's Deck editing setting turns manual editing off.
        /// </summary>
        public string DeckEditorRefusal()
        {
            if (RunOver) return StringKeys.Core2DeckRunOver;
            if (Ashen.Domain.Run.DeckRules.EditingLocked(_run, IsInCombat)) return StringKeys.Core2DeckLocked;
            if (!Ashen.Domain.Run.DeckRules.ArmouryOpensEditor(Core.Deck, _run, Profile.Settings)) return StringKeys.Core2DeckDisabled;
            return null;
        }

        /// <summary>Opens the deck editor over the live run (main.js showDeckEditor): its words come from <paramref name="strings"/>; a refusal names the key to say.</summary>
        public Core2DeckOpen OpenDeckEditor(Ashen.App.Ui.StringTable strings)
        {
            var refusal = DeckEditorRefusal();
            if (refusal != null) return new Core2DeckOpen { Refusal = refusal };
            var session = Ashen.Domain.Run.DeckEditor.Open(Core.Deck, new DeckEditorText(strings), _run, Profile.Settings);
            return new Core2DeckOpen { Editor = new Core2DeckEditor(this, session) };
        }

        /// <summary>
        /// The draft door (main.js showCombatDeckEditor, commitDeckEditDraft): an editor that worked on a copy of the run (<see cref="Run"/>) commits its deck and sideboard
        /// here. The rules check the whole draft (bounds, identities, known cards, range, no modified, invented or deleted owned card, copy limits) and write it atomically;
        /// a refusal carries the web's sentence and leaves the run as it was. A committed draft is saved.
        /// </summary>
        public Core2DeckCommit CommitDeckDraft(JObject draft)
        {
            var refusal = DeckEditorRefusal();
            if (refusal != null) return new Core2DeckCommit { Ok = false, Message = refusal };
            try
            {
                // The rules write the draft only after every check passed, so a refusal leaves the run (and the editors holding it) untouched.
                var size = Ashen.Domain.Run.DeckRules.CommitDraft(Core.Deck, _run, draft, Profile.Settings);
                Save();
                return new Core2DeckCommit { Ok = true, DeckSize = size };
            }
            catch (InvalidOperationException e)
            {
                return new Core2DeckCommit { Ok = false, Message = e.Message };
            }
        }

        /// <summary>Review and test hook (smoke tests, screen captures): edits the live run document in place. Never called by the game.</summary>
        public void EditRunForReview(Action<JObject> edit) => edit?.Invoke(_run);

        // ------------------------------------------------------------------ steps (snapshot, restore)

        private sealed class SessionSnapshot
        {
            public JObject Run;
            public JObject Profile;
            public Rng Rng;
            public string Location;
            public string EncounterId;
            public Core2CombatSession Combat;
            public bool FightFinished;
            public Core2FightEnd LastEnd;
            public string PendingPort;
            public string EventId;
            public bool EventDone;
            public Core2EventResult EventResult;
        }

        private SessionSnapshot Snapshot() => new SessionSnapshot
        {
            Run = (JObject)_run.DeepClone(), Profile = Profile.Snapshot(), Rng = _rng?.Clone(), Location = _location, EncounterId = EncounterId, Combat = Combat,
            FightFinished = FightFinished, LastEnd = LastEnd, PendingPort = _pendingPort, EventId = _eventId, EventDone = _eventDone, EventResult = _eventResult,
        };

        private void Restore(SessionSnapshot s)
        {
            _run = s.Run;
            Profile.Restore(s.Profile);
            _rng = s.Rng;
            _location = s.Location;
            EncounterId = s.EncounterId;
            Combat = s.Combat;
            FightFinished = s.FightFinished;
            LastEnd = s.LastEnd;
            _pendingPort = s.PendingPort;
            _eventId = s.EventId;
            _eventDone = s.EventDone;
            _eventResult = s.EventResult;
        }

        // ------------------------------------------------------------------ save

        /// <summary>A full checkpoint: a new save generation holding the run, the fight (while one is on) and the summary.</summary>
        public void Save()
        {
            if (RunOver) return;
            if (!IsInCombat && _rng != null) _run[RK.StreamCounters] = CoreSnapshot.RngToJson(_rng);
            var payload = Payload();
            if (IsInCombat) Combat.Commit(Saves, Slot, payload, Content.ContentHash);
            else Saves.Checkpoint(Slot, payload, Content.ContentHash, StateHash());
        }

        private JObject Payload()
        {
            var payload = new JObject
            {
                [RunSaveKeys.FormatVersion] = NN.SaveFormat,
                [NK.GameProfile] = new JObject { [K.Id] = Core.ProfileId, [NK.Hash] = Core.ProfileHash },
                [RunSaveKeys.PresetId] = Content.PresetId,
                [SlotSummaryKeys.Summary] = Summary(),
                [RunSaveKeys.Name] = Name,
                [RunSaveKeys.Portrait] = Portrait,
                [RunSaveKeys.Playtime] = PlaytimeSeconds,
                [RunSaveKeys.Run] = _run.DeepClone(),
            };
            // An event is not kept by a save (the web persists at the node and resumes at the map): the save stands at the map.
            if (Location != null) payload[RunSaveKeys.Location] = Location == RunFlowValues.LocationEvent ? RunFlowValues.LocationMap : Location;
            if (EncounterId != null) payload[RunSaveKeys.EncounterId] = EncounterId;
            if (_pendingPort != null) payload[HK.PendingPort] = _pendingPort;
            if (IsInCombat) payload[RunSaveKeys.Combat] = Combat.Checkpoint();
            return payload;
        }

        public long PlaytimeSeconds => _playtimeBase + Math.Max(0L, (long)(_clock() - _playStarted).TotalSeconds);

        /// <summary>The slot summary W-02 and W-03 read (D-059): identity, progress, live HP in a fight, seed and save time.</summary>
        public JObject Summary()
        {
            var hp = IsInCombat ? Combat.State.Player.Num(K.Hp) : _run.Num(K.Hp);
            var hpMax = IsInCombat ? Combat.State.Player.Num(K.MaxHp) : _run.Num(K.MaxHp);
            return new JObject
            {
                [SlotSummaryKeys.Name] = Name,
                [SlotSummaryKeys.ClassId] = ClassId,
                [SlotSummaryKeys.Portrait] = Portrait,
                [SlotSummaryKeys.Act] = Act,
                [SlotSummaryKeys.Floor] = Floor,
                [SlotSummaryKeys.Hp] = (int)Math.Floor(hp),
                [SlotSummaryKeys.HpMax] = (int)Math.Floor(hpMax),
                [SlotSummaryKeys.Seed] = SeedText,
                [SlotSummaryKeys.SavedAt] = _clock().ToUniversalTime().ToString(RunFlowValues.SavedAtFormat, CultureInfo.InvariantCulture),
                [SlotSummaryKeys.Journey] = Content.Flow.Journey,
                [SlotSummaryKeys.Playtime] = PlaytimeSeconds,
            };
        }

        /// <summary>SHA-256 of the canonical run document and, in a fight, the combat checkpoint (resume equivalence).</summary>
        public string StateHash()
        {
            var state = new JObject { [RunSaveKeys.Run] = _run.DeepClone(), [RunSaveKeys.Combat] = IsInCombat ? Combat.Checkpoint() : null };
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(ContentSet.Canonical(state))).Select(b => b.ToString(ContentLayout.HexByteFormat)));
        }

        // ------------------------------------------------------------------ load

        /// <summary>The save format of a slot (null when it holds nothing readable): the caller picks the session kind by it.</summary>
        public static int? FormatOf(SaveService saves, int slotIndex)
        {
            try
            {
                var loaded = saves.Load(saves.Rules.RunSlotName(slotIndex));
                return loaded.Status == LoadStatus.Loaded ? (int?)(loaded.Payload as JObject)?[RunSaveKeys.FormatVersion] : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Resume a slot: the verified envelope, the Core 2 save format, the profile the run pinned (D-171: another profile is recoverable, the slot is kept), then the place
        /// it was saved at — the fight from its checkpoint with the log replayed while every after-state hash matches, or the act map. A fight saved already over (a crash
        /// before its end was settled) runs its end now.
        /// </summary>
        public static Core2RunLoadResult Load(RunContent content, SaveService saves, int slotIndex, Func<DateTime> clock = null)
        {
            var result = new Core2RunLoadResult();
            var core = content.Core2;
            var slot = saves.Rules.RunSlotName(slotIndex);
            LoadResult loaded;
            try { loaded = saves.Load(slot); }
            catch (Exception e)
            {
                result.Status = Core2RunLoadStatus.Damaged;
                result.Warnings.Add(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.LoadRefused, slot, e.Message));
                return result;
            }
            result.Warnings.AddRange(loaded.Warnings);
            switch (loaded.Status)
            {
                case LoadStatus.Empty: result.Status = Core2RunLoadStatus.Empty; return result;
                case LoadStatus.RefusedNewer: result.Status = Core2RunLoadStatus.Newer; return result;
                case LoadStatus.Corrupt: result.Status = Core2RunLoadStatus.Damaged; return result;
            }
            var payload = loaded.Payload as JObject;
            var format = (int?)payload?[RunSaveKeys.FormatVersion];
            if (format != (int)NN.SaveFormat || !(payload[RunSaveKeys.Run] is JObject run))
            {
                result.Status = format == content.Flow.SaveFormat ? Core2RunLoadStatus.OtherRuleset : Core2RunLoadStatus.Damaged;
                result.Warnings.Add(string.Format(CultureInfo.InvariantCulture, NM.FormatMismatch, slot, format, NN.SaveFormat));
                return result;
            }
            var pin = payload.Obj(NK.GameProfile);
            if (pin != null && pin.Str(NK.Hash) != core.ProfileHash)
            {
                result.Status = Core2RunLoadStatus.ProfileMismatch;
                result.Warnings.Add(string.Format(CultureInfo.InvariantCulture, NM.ProfileMismatch, pin.Str(NK.Hash), core.ProfileHash));
                return result;
            }
            var session = new Core2RunSession(content, saves, slotIndex, clock)
            {
                _run = CoreSnapshot.Doubles(run),
                Name = (string)payload[RunSaveKeys.Name],
                Portrait = (string)payload[RunSaveKeys.Portrait],
                _playtimeBase = (long?)payload[RunSaveKeys.Playtime] ?? 0L,
                EncounterId = (string)payload[RunSaveKeys.EncounterId],
                _location = (string)payload[RunSaveKeys.Location] is string saved && saved != RunFlowValues.LocationRewards && saved != RunFlowValues.LocationMerchant && saved != RunFlowValues.LocationRest ? saved : RunFlowValues.LocationMap,
                _pendingPort = (string)payload[HK.PendingPort],
            };
            var warned = false;
            try
            {
                if (payload[RunSaveKeys.Combat] is JObject)
                {
                    var sameContent = loaded.ContentHash == content.ContentHash;
                    if (!sameContent)
                    {
                        warned = true;
                        result.Warnings.Add(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.ContentChanged, loaded.ContentHash, content.ContentHash));
                    }
                    var fight = Core2CombatSession.Load(core.Combat, saves, slot);
                    if (fight.Status != Core2LoadStatus.Loaded)
                    {
                        result.Status = fight.Status == Core2LoadStatus.ProfileMismatch ? Core2RunLoadStatus.ProfileMismatch : Core2RunLoadStatus.Damaged;
                        result.Warnings.AddRange(fight.Warnings);
                        return result;
                    }
                    session.Combat = fight.Session;
                    session._rng = session.Combat.State.Rng;
                    if (fight.LogDiverged)
                    {
                        warned = true;
                        result.Warnings.Add(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.LogDiverged, session.Combat.LoggedCommands));
                    }
                    if (session.Combat.IsOver)
                    {
                        session.FinishCombat();
                        if (session.RunOver)
                        {
                            result.Status = Core2RunLoadStatus.Empty;
                            return result;
                        }
                    }
                }
                else session._rng = CoreSnapshot.RngFromJson(session.Seed, session._run.Obj(RK.StreamCounters));
            }
            catch (Exception e)
            {
                result.Status = Core2RunLoadStatus.Damaged;
                result.Warnings.Add(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.LoadRefused, slot, e.Message));
                return result;
            }
            if (warned) session.Save();
            result.Session = session;
            result.Status = warned ? Core2RunLoadStatus.ResumedWithWarning : Core2RunLoadStatus.Resumed;
            return result;
        }
    }
}
