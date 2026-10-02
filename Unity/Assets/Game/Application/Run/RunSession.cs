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
using Ashen.Domain.Random;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using FightEntry = Ashen.Domain.Loop.FightEntry;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using LoopContext = Ashen.Domain.Loop.LoopContext;
using LoopOutcome = Ashen.Domain.Loop.CombatOutcome;
using LoopSettings = Ashen.Domain.Loop.LoopSettings;
using LV = Ashen.Generated.LoopValues;
using MK = Ashen.Generated.MapKeys;
using NewRunOptions = Ashen.Domain.Loop.NewRunOptions;
using NodeOutcome = Ashen.Domain.Loop.NodeOutcome;
using RewardDoor = Ashen.Domain.Loop.RewardDoor;
using RK = Ashen.Generated.RunKeys;
using RunEndReceipt = Ashen.Domain.Loop.RunEndReceipt;
using RunLoop = Ashen.Domain.Loop.RunLoop;
using WK = Ashen.Generated.RewardsKeys;
using WV = Ashen.Generated.RewardsValues;

namespace Ashen.App.Run
{
    /// <summary>How a slot came back.</summary>
    public enum RunLoadStatus
    {
        /// <summary>The checkpoint and every logged command replayed and verified.</summary>
        Resumed,

        /// <summary>Resumed at a checkpoint or the last verified command; the log was skipped or diverged (D-017; a warning is shown).</summary>
        ResumedWithWarning,

        Empty,

        /// <summary>A save from a newer build: kept, never loaded.</summary>
        Newer,

        /// <summary>No valid copy, or a payload this build cannot resume (the kit's unreadable-slot path).</summary>
        Damaged,
    }

    public sealed class RunLoadResult
    {
        public RunLoadStatus Status;
        public RunSession Session;
        public readonly List<string> Warnings = new List<string>();

        public bool Resumed => Session != null;
    }

    /// <summary>What a reward claim did at the door: null when the row landed, else the RewardsValues-style refusal code.</summary>
    public sealed class RewardClaimResult
    {
        public string Key;
        public string Refusal;

        public bool Landed => Refusal == null;
    }

    /// <summary>
    /// A run as the application plays it (AF-02 → AF-04 → AF-05 → W-08 → AF-06 … → AF-11, PF-06). <see cref="New"/> makes
    /// the climb through the run loop's newRun (createRunState, the seat order, the keepsake, the first act's map) and
    /// commits it at the act map. From the map the player travels to a reachable node (<see cref="Travel"/>, shipped
    /// enterNode): a fight enters through the loop's enterCombat arguments on the run's own RNG; a rest place opens its
    /// stay; a treasure room's offer becomes a reward checkpoint; a merchant rolls its stock; an event and a legacy dungeon
    /// wait for their screens. Every combat command goes through <see cref="CombatSession.Execute"/> (legality first,
    /// rollback on failure) and is autosaved as rules/runFlow.json says; the command that ends a fight runs the loop's
    /// post-combat door at once (<see cref="RunLoop.EndCombat"/>), and a death or the summit closes the run out into the
    /// profile and clears the slot (permadeath). The reward door claims, skips and continues with a save after every
    /// step; a boss's spoils lead into the next act (<see cref="Ashen.Domain.Loop.Acts.Advance"/>). A save is one
    /// SaveService envelope whose payload holds the format, the preset, the slot summary (D-059), the run document, the
    /// location and what that location needs to resume (the fight's checkpoint, the rest stay, the event); commands between
    /// checkpoints go to the generation's log with their after-state hash. Engine-free.
    /// </summary>
    public sealed partial class RunSession
    {
        private readonly Func<DateTime> _clock;
        private JObject _run;
        private long _playtimeBase;
        private DateTime _playStarted;
        private Rng _rng;
        private ProfileStore _profile;
        private RewardDoor _door;
        private string _location;
        private string _restAnchor;

        private RunSession(RunContent content, SaveService saves, int slotIndex, Func<DateTime> clock)
        {
            Content = content;
            Saves = saves;
            SlotIndex = slotIndex;
            Slot = saves.Rules.RunSlotName(slotIndex);
            _clock = clock ?? (() => DateTime.UtcNow);
            _playStarted = _clock();
        }

        public RunContent Content { get; }
        public SaveService Saves { get; }
        public int SlotIndex { get; }
        public string Slot { get; }
        public string Name { get; private set; }
        public string ClassId => _run.Str(RK.Class);
        public string Portrait { get; private set; }
        public uint Seed => (uint)_run.Num(RK.Seed);
        public string SeedText => Content.Seeds.Format(Seed);
        public string EncounterId { get; private set; }
        public int Act => (int)_run.Num(RK.ActNumber);
        public int Floor => (int)_run.Num(RK.Floor);

        /// <summary>The fight in progress, or the one that just ended (kept for the W-07 end state until the screen leaves).</summary>
        public CombatSession Combat { get; private set; }

        /// <summary>The fight has ended and the post-combat door has run (the run holds its pending reward or is over).</summary>
        public bool FightFinished { get; private set; }

        /// <summary>A fight is being played (and is checkpointed as the save's location).</summary>
        public bool IsInCombat => Combat != null && !FightFinished;

        /// <summary>What the last fight's end did: 'reward' (a pending reward), 'defeat' or 'victory' (the run is over), or null.</summary>
        public string FightOutcome => LastOutcome?.Outcome;

        /// <summary>The run loop's receipt for the last fight's end (the run's close-out on a death).</summary>
        public LoopOutcome LastOutcome { get; private set; }

        /// <summary>The run was closed out (a death or the summit) and its slot cleared.</summary>
        public bool RunOver { get; private set; }

        /// <summary>The run's close-out (finishRun's receipt: the result record and what was newly earned), once it is over.</summary>
        public RunEndReceipt End { get; private set; }

        /// <summary>
        /// Where the run stands and a save resumes (rules/runFlow.json screens): 'combat', 'rewards', 'map', 'rest',
        /// 'merchant', 'event', 'dungeon', or 'runEnd' once the run is over (never saved: the slot is cleared).
        /// </summary>
        public string Location
        {
            get
            {
                if (RunOver) return RunFlowValues.LocationRunEnd;
                if (IsInCombat) return RunFlowValues.LocationCombat;
                if (HasPendingReward) return RunFlowValues.LocationRewards;
                return _location;
            }
        }

        /// <summary>Where the last reward door led ('map' or 'advanceAct', which the session has already taken).</summary>
        public string After { get; private set; }

        /// <summary>The run document (a copy).</summary>
        public JObject Run => (JObject)_run.DeepClone();

        /// <summary>The profile document behind this run (found armaments, seen records, history, settings).</summary>
        public ProfileStore Profile => _profile ??= ProfileStore.Load(Saves);

        /// <summary>Raised after every accepted combat command (after its autosave), with its outcome.</summary>
        public event Action<CombatOutcome> CommandCommitted;

        /// <summary>
        /// Review and test hook (PlayMode smoke, screen captures): scales each new fight's enemy HP by its pool, as the bot's
        /// assisted policy does (D-103). Null in the game; never set by it.
        /// </summary>
        public static Func<string, double> ReviewEnemyHpScale;

        // ------------------------------------------------------------------ create

        /// <summary>
        /// A new climb in a slot (overwriting what was there), saved at once at the act map: main.js newRun + startClimb
        /// through <see cref="RunLoop.NewRun"/> with the character's name and tint, the data's glyph, the empty advanced
        /// settings' config snapshot and the seed string; <paramref name="custom"/> is a Custom Climb block (null: Classic).
        /// </summary>
        public static RunSession New(RunContent content, SaveService saves, int slotIndex, uint seed, string classId, string name, Func<DateTime> clock = null, JObject custom = null)
        {
            if (classId == null || !content.Data.Classes.Has(classId))
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.UnknownClass, classId));
            var session = new RunSession(content, saves, slotIndex, clock)
            {
                Name = name,
                Portrait = content.Portrait(classId),
            };
            var customization = new JObject { [RunFlowKeys.Name] = name };
            foreach (var p in content.Flow.Customization.Properties()) customization[p.Name] = p.Value.DeepClone();
            customization[RunFlowKeys.Tint] = content.Flow.DefaultTint;
            var ctx = RunLoop.NewRun(content.Loop, session.Profile.Doc, session.Settings(), new NewRunOptions
            {
                ClassId = classId,
                Seed = seed,
                SeedString = content.Seeds.Format(seed),
                Custom = custom?.DeepClone() as JObject,
                Customization = customization,
                AdvancedConfigSnapshot = (JObject)content.Flow.AdvancedConfigSnapshot.DeepClone(),
                Prologue = content.Flow.Prologue,
            });
            session._run = ctx.Run;
            session._rng = ctx.Rng;
            session._location = RunFlowValues.LocationMap;
            session.Save();
            return session;
        }

        /// <summary>
        /// Review and test hook: enter a named fight (the rules/runFlow.json firstFight encounter unless one is named) where
        /// the run stands, through the loop's enterCombat arguments, and checkpoint it. The climb's fights come from
        /// <see cref="Travel"/>.
        /// </summary>
        public CombatSession StartEncounter(string encounterId = null)
        {
            var ctx = Context();
            var outcome = RunLoop.EnterCombat(ctx, Js.Str(_run[MK.MapNodeId]), encounterId ?? Content.FirstFightEncounter());
            BeginFight(outcome);
            Save();
            return Combat;
        }

        /// <summary>A fight entered by the loop: the combat on the run's RNG (its draws continue the run's streams).</summary>
        private void BeginFight(NodeOutcome outcome)
        {
            var args = (JObject)outcome.Args.DeepClone();
            if (ReviewEnemyHpScale != null) args[K.HpMult] = Js.D(args[K.HpMult]) * ReviewEnemyHpScale(outcome.Pool);
            _rng = _rng ?? RunRng();
            EncounterId = outcome.EncounterId;
            Combat = CombatSession.Start(Content.Combat, _rng, args);
            FightFinished = false;
            LastOutcome = null;
        }

        // ------------------------------------------------------------------ play

        /// <summary>
        /// Execute a combat command; an accepted one is autosaved per rules/runFlow.json autosave. The command that ends
        /// the fight runs the post-combat door (<see cref="FinishCombat"/>) before the save.
        /// </summary>
        public CombatOutcome Execute(CombatCommand command)
        {
            if (!IsInCombat) throw new InvalidOperationException(RunFlowMessages.NotInCombat);
            var flow = Content.Flow;
            var outcome = flow.AfterCommand == RunFlowValues.AutosaveAppend
                ? Combat.ExecuteAndLog(Saves, Slot, command)
                : Combat.Execute(command);
            if (!outcome.Accepted) return outcome;
            if (Combat.IsOver) FinishCombat();
            else if (flow.AfterCommand == RunFlowValues.AutosaveCheckpoint || flow.CheckpointOn.Contains(command.Type)) Save();
            CommandCommitted?.Invoke(outcome);
            return outcome;
        }

        /// <summary>
        /// The fight's end (main.js onCombatEnd through <see cref="RunLoop.EndCombat"/>): the run takes the fight back, the
        /// ledgers are paid and the reward rolled onto run.pendingReward on the run's RNG (the one the fight drew from);
        /// a death (or the summit) closes the run out into the profile and clears the slot. Otherwise the slot is
        /// checkpointed at the rewards.
        /// </summary>
        private void FinishCombat()
        {
            var state = Combat.State;
            _rng = state.Rng;
            var ctx = Context(_rng);
            var encounter = (JObject)Content.Data.Encounters.Get(EncounterId).DeepClone();
            ctx.Fight = new FightEntry { EncounterId = EncounterId, Pool = encounter.Str(MK.Pool), Encounter = encounter };
            LastOutcome = RunLoop.EndCombat(ctx, state, state.Result);
            FightFinished = true;
            _door = null;
            if (LastOutcome.End != null)
            {
                CloseOut(LastOutcome.End);
                return;
            }
            Save();
        }

        /// <summary>The run is over (a death, the summit, a cleared dungeon left at the summit): the profile holds its record, the slot is cleared.</summary>
        private void CloseOut(RunEndReceipt end)
        {
            RecordJournal(end);
            End = end;
            RunOver = true;
            _visit = null;
            Profile.Save(Content.ContentHash);
            Saves.Delete(Slot);
        }

        /// <summary>
        /// US-13.1: the run record gains what the shipped record keeps none of (D-148): the last fight's foes by enemy id
        /// (the killer, or the felled keeper on a win), the playtime in seconds and the final deck. Written to the newest
        /// profile history row (the record finishRun just appended) and to the receipt's copy.
        /// </summary>
        private void RecordJournal(RunEndReceipt end)
        {
            var extra = new JObject();
            if (LastOutcome != null && EncounterId != null && Content.Data.Encounters.Has(EncounterId))
                extra[MetaKeys.Killer] = new JArray(Js.Items(Content.Data.Encounters.Get(EncounterId)[K.Enemies]).Select(t => t.DeepClone()));
            extra[MetaKeys.Duration] = PlaytimeSeconds;
            extra[MetaKeys.Deck] = new JArray(Js.Items(_run?[K.Deck]).OfType<JObject>()
                .Select(c => Js.Obj(MetaKeys.CardId, c[K.CardId]?.DeepClone(), MetaKeys.Upgraded, c.Is(K.Upgraded))));
            if (Profile.Doc[LK.Results] is JArray results && results.Count > 0 && results[results.Count - 1] is JObject newest)
                newest.Merge(extra);
            end.Result?.Merge(extra);
        }

        // ------------------------------------------------------------------ the reward door (W-08; US-11.1 to US-11.3)

        /// <summary>The run holds a pending reward (a fight's spoils, a treasure room, a dungeon cache; resume lands on W-08).</summary>
        public bool HasPendingReward => !RunOver && RewardClaims(_run) != null;

        private static JObject RewardClaims(JObject run) => run?.Obj(WK.PendingReward);

        /// <summary>
        /// The open reward door (reward.js mountRewards): opened from the checkpoint on first use, which grants the
        /// offer's cinders on arrival (saved at once when they land).
        /// </summary>
        public RewardDoor Door
        {
            get
            {
                if (_door != null) return _door;
                if (!HasPendingReward) return null;
                var before = RewardClaims(_run).DeepClone();
                _door = RewardDoor.OpenPending(Context());
                if (!JToken.DeepEquals(before, RewardClaims(_run))) Save();
                return _door;
            }
        }

        /// <summary>
        /// The rewardCollect dial (reward.js collectMode): the profile's stored setting, else the preset's player-settings
        /// default, when it is one of balance.ui.rewardCollect's modes; else the dial's default.
        /// </summary>
        public string RewardCollectMode
        {
            get
            {
                var dial = Content.Data.Balance.Obj(RunFlowKeys.Ui)?.Obj(RunFlowKeys.RewardCollect);
                var setting = Js.Str(Setting(RunFlowKeys.RewardCollect));
                if (dial == null) return setting ?? LV.CollectAuto;
                return setting != null && Js.Includes(dial[RunFlowKeys.Modes], setting) ? setting : dial.Str(RunFlowKeys.Def);
            }
        }

        /// <summary>
        /// Take one row (a row press, or the chooser's Confirm with the selected card or node). Refused before the door
        /// is asked when the row is resolved, blocked, or a choice without an offered pick; refused after when its apply
        /// lands nothing (a spent draft, a full or duplicate bag). A landed claim is saved with the claim state (PF-06
        /// "reward claimed"); a failed save restores the run and the profile and rethrows.
        /// </summary>
        public RewardClaimResult ClaimReward(string key, string pickId = null)
        {
            var door = Door;
            var result = new RewardClaimResult { Key = key };
            if (door == null) { result.Refusal = RunFlowValues.RefusalNoReward; return result; }
            var row = door.Rows.OfType<JObject>().FirstOrDefault(r => r.Str(LK.Key) == key);
            result.Refusal = RewardsView.PreRefusal(row, door.States, pickId);
            if (result.Refusal != null) return result;
            var snapshot = Snapshot();
            if (!door.Take(key, pickId))
            {
                Restore(snapshot);
                result.Refusal = RewardsView.ApplyRefusal(row);
                return result;
            }
            Commit(snapshot);
            return result;
        }

        /// <summary>A row's Skip: resolved as skipped (Continue never takes it), saved.</summary>
        public RewardClaimResult SkipReward(string key)
        {
            var door = Door;
            var result = new RewardClaimResult { Key = key };
            if (door == null) { result.Refusal = RunFlowValues.RefusalNoReward; return result; }
            var row = door.Rows.OfType<JObject>().FirstOrDefault(r => r.Str(LK.Key) == key);
            if (row == null) { result.Refusal = RunFlowValues.RefusalNoSuchRow; return result; }
            if (Js.Truthy(door.States[key])) { result.Refusal = RunFlowValues.RefusalClaimed; return result; }
            var snapshot = Snapshot();
            door.Skip(key);
            Commit(snapshot);
            return result;
        }

        /// <summary>
        /// Continue (reward.js finish and main.js onDone): the door takes the rest under the rewardCollect dial (auto: every
        /// pending, unskipped row, a choice picked on 'cardRewards'; manual: nothing), the checkpoint leaves the run, and a
        /// boss's 'advanceAct' runs the act advance (the full heal and the next seat's map, PF-06 "act advanced"). The slot is
        /// saved at the map (or the legacy dungeon the run stands in). Returns where the door led ('map' or 'advanceAct').
        /// </summary>
        public string FinishRewards()
        {
            var door = Door ?? throw new InvalidOperationException(RunFlowMessages.NoPendingReward);
            var snapshot = Snapshot();
            try
            {
                var receipt = door.Continue(RewardCollectMode);
                _door = null;
                After = receipt.After;
                if (After == WV.AdvanceAct) Ashen.Domain.Loop.Acts.Advance(Context());
                _location = OnMapOrDungeon();
                Commit(snapshot);
            }
            catch
            {
                Restore(snapshot);
                throw;
            }
            return After;
        }

        /// <summary>The map, or the legacy dungeon when the run stands in one (its own map).</summary>
        private string OnMapOrDungeon() => _run.Obj(RK.LegacyDungeon) != null ? RunFlowValues.LocationDungeon : RunFlowValues.LocationMap;

        // ------------------------------------------------------------------ steps (snapshot, commit, restore)

        private sealed class SessionSnapshot
        {
            public JObject Run;
            public JObject Profile;
            public string ProfileText;
            public Rng Rng;
            public string Location;
            public string After;
            public string EncounterId;
            public CombatSession Combat;
            public bool FightFinished;
            public LoopOutcome LastOutcome;
            public string RestAnchor;
            public RestStay Stay;
            public string EventId;
            public bool EventDone;
        }

        private SessionSnapshot Snapshot() => new SessionSnapshot
        {
            Run = (JObject)_run.DeepClone(),
            Profile = Profile.Snapshot(),
            ProfileText = ContentSet.Canonical(Profile.Doc),
            Rng = _rng?.Clone(),
            Location = _location,
            After = After,
            EncounterId = EncounterId,
            Combat = Combat,
            FightFinished = FightFinished,
            LastOutcome = LastOutcome,
            RestAnchor = _restAnchor,
            Stay = _stay?.Copy(),
            EventId = _eventId,
            EventDone = _eventDone,
        };

        private void Restore(SessionSnapshot s)
        {
            _run = s.Run;
            Profile.Restore(s.Profile);
            _rng = s.Rng;
            _location = s.Location;
            After = s.After;
            EncounterId = s.EncounterId;
            Combat = s.Combat;
            FightFinished = s.FightFinished;
            LastOutcome = s.LastOutcome;
            _restAnchor = s.RestAnchor;
            _stay = s.Stay;
            _visit = null;
            _eventId = s.EventId;
            _eventDone = s.EventDone;
            _door = null;
        }

        /// <summary>The profile (when the step wrote it) and the run cross one save door; a failure restores both and rethrows.</summary>
        private void Commit(SessionSnapshot s)
        {
            try
            {
                if (ContentSet.Canonical(Profile.Doc) != s.ProfileText) Profile.Save(Content.ContentHash);
                Save();
            }
            catch
            {
                Restore(s);
                throw;
            }
        }

        /// <summary>The run loop's context over this run: its RNG, the profile, the settings the loop reads and the rest place it remembers.</summary>
        private LoopContext Context(Rng rng = null)
        {
            _rng = rng ?? _rng ?? RunRng();
            var ctx = new LoopContext(Content.Loop, _run, _rng, Profile.Doc, Settings());
            if (_restAnchor != null) ctx.RestLocationId = _restAnchor;
            return ctx;
        }

        /// <summary>The run's RNG from its saved stream counters (engine/save.js saveRun writes run.streamCounters).</summary>
        private Rng RunRng() => new Rng(Seed, ParseCounters(_run.Obj(RK.StreamCounters)));

        private static Dictionary<RngStream, uint> ParseCounters(JObject counters)
        {
            var parsed = new Dictionary<RngStream, uint>();
            foreach (var p in (counters ?? new JObject()).Properties()) parsed[RngStreamNames.Parse(p.Name)] = (uint)Js.D(p.Value);
            return parsed;
        }

        private static JObject Counters(Rng rng)
        {
            var o = new JObject();
            foreach (var kv in rng.Counters().OrderBy(kv => (int)kv.Key)) o[RngStreamNames.ToWire(kv.Key)] = (double)kv.Value;
            return o;
        }

        // ------------------------------------------------------------------ settings (the profile, then the preset, then the data default; D-106)

        /// <summary>A stored player setting: the profile's, else the preset's playerSettings value, else null.</summary>
        private JToken Setting(string key)
        {
            var stored = Profile.Settings[key];
            if (stored != null && stored.Type != JTokenType.Null) return stored;
            var preset = Content.Snapshot.PlayerSettings?[key];
            return preset != null && preset.Type != JTokenType.Null ? preset : null;
        }

        /// <summary>settingOn(settings, key): a boolean setting with its data default.</summary>
        private bool SettingOn(FlowSetting setting)
        {
            var value = setting?.Key == null ? null : Setting(setting.Key);
            return value != null && value.Type == JTokenType.Boolean ? (bool)value : setting != null && setting.Default;
        }

        /// <summary>The settings the loop reads (shipped resolveLevelUpValue, settingOn('shrineMultiUse'), the rewardCollect dial).</summary>
        public LoopSettings Settings() => new LoopSettings
        {
            PointsPerLevel = Setting(RunFlowKeys.LevelUpValue)?.DeepClone(),
            RewardCollect = RewardCollectMode,
            MultiUse = SettingOn(Content.Flow.MultiUse),
        };

        /// <summary>settingOn(settings, 'shopSell'): the merchant's sell shelf.</summary>
        public bool ShopSellOn => SettingOn(Content.Flow.ShopSell);

        /// <summary>
        /// The hold-to-confirm dial (balance.ui.holdConfirm): the stored step (the profile's, else the preset's) when the
        /// dial names it, else its default, in ms; 0 means the player turned holds off (a press commits). Falls back to
        /// <paramref name="fallbackMs"/> when the content has no dial.
        /// </summary>
        public int HoldConfirmMs(int fallbackMs)
        {
            var dial = Content.Data.Balance.Obj(RunFlowKeys.Ui)?.Obj(RunFlowKeys.HoldConfirm);
            var steps = dial?.Obj(RunFlowKeys.Steps);
            if (steps == null) return fallbackMs;
            var chosen = Js.Str(Content.Flow.HoldConfirm.Key == null ? null : Setting(Content.Flow.HoldConfirm.Key));
            var step = chosen != null && steps[chosen] != null ? steps[chosen] : steps[dial.Str(RunFlowKeys.Def) ?? string.Empty];
            return step == null ? fallbackMs : (int)Js.D(step);
        }

        // ------------------------------------------------------------------ save

        /// <summary>A full checkpoint: a new save generation holding the run, the fight (while one is on) and the summary.</summary>
        public void Save()
        {
            if (RunOver) return;
            if (!IsInCombat && _rng != null) _run[RK.StreamCounters] = Counters(_rng);
            var payload = Payload();
            if (IsInCombat) Combat.Commit(Saves, Slot, payload, Content.ContentHash);
            else Saves.Checkpoint(Slot, payload, Content.ContentHash, StateHash());
        }

        private JObject Payload()
        {
            var payload = new JObject
            {
                [RunSaveKeys.FormatVersion] = Content.Flow.SaveFormat,
                [RunSaveKeys.PresetId] = Content.PresetId,
                [SlotSummaryKeys.Summary] = Summary(),
                [RunSaveKeys.Name] = Name,
                [RunSaveKeys.Portrait] = Portrait,
                [RunSaveKeys.Playtime] = PlaytimeSeconds,
                [RunSaveKeys.Run] = _run.DeepClone(),
            };
            if (Location != null) payload[RunSaveKeys.Location] = Location;
            if (EncounterId != null) payload[RunSaveKeys.EncounterId] = EncounterId;
            if (After != null) payload[RunSaveKeys.After] = After;
            if (_restAnchor != null) payload[RunSaveKeys.RestAnchor] = _restAnchor;
            if (_stay != null && Location == RunFlowValues.LocationRest) payload[RunSaveKeys.Rest] = _stay.ToJson();
            if (_eventId != null && Location == RunFlowValues.LocationEvent)
            {
                payload[RunSaveKeys.EventId] = _eventId;
                payload[RunSaveKeys.EventDone] = _eventDone;
            }
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
                [SlotSummaryKeys.Act] = (int)_run.Num(RK.ActNumber),
                [SlotSummaryKeys.Floor] = (int)_run.Num(RK.Floor),
                [SlotSummaryKeys.Hp] = (int)Math.Floor(hp),
                [SlotSummaryKeys.HpMax] = (int)Math.Floor(hpMax),
                [SlotSummaryKeys.Seed] = SeedText,
                [SlotSummaryKeys.SavedAt] = _clock().ToUniversalTime().ToString(RunFlowValues.SavedAtFormat, CultureInfo.InvariantCulture),
                [SlotSummaryKeys.Journey] = Content.Flow.Journey,
                [SlotSummaryKeys.Playtime] = PlaytimeSeconds,
            };
        }

        /// <summary>SHA-256 of the canonical run document and, in a fight, the combat checkpoint (resume equivalence, 08 §12 SaveRoundTrip).</summary>
        public string StateHash()
        {
            var state = new JObject { [RunSaveKeys.Run] = _run.DeepClone(), [RunSaveKeys.Combat] = IsInCombat ? Combat.Checkpoint() : null };
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(ContentSet.Canonical(state))).Select(b => b.ToString(ContentLayout.HexByteFormat)));
        }

        // ------------------------------------------------------------------ load

        /// <summary>
        /// Resume a slot (AF-10): verified envelope, current run format, then the place it was saved at — the fight from its
        /// checkpoint with the log replayed while every after-state hash matches (a content change skips the log; a
        /// divergence stops at the last verified command; either way the session re-commits at once so the slot holds one
        /// clean generation), the rest stay re-opened on the streams it opened on, or the map, reward door, merchant, event
        /// or dungeon as the run holds them. A fight saved already over (a crash before the rewards were rolled) runs its
        /// post-combat door now.
        /// </summary>
        public static RunLoadResult Load(RunContent content, SaveService saves, int slotIndex, Func<DateTime> clock = null)
        {
            var result = new RunLoadResult();
            var slot = saves.Rules.RunSlotName(slotIndex);
            LoadResult loaded;
            try { loaded = saves.Load(slot); }
            catch (Exception e)
            {
                result.Status = RunLoadStatus.Damaged;
                result.Warnings.Add(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.LoadRefused, slot, e.Message));
                return result;
            }
            result.Warnings.AddRange(loaded.Warnings);
            switch (loaded.Status)
            {
                case LoadStatus.Empty:
                    result.Status = RunLoadStatus.Empty;
                    return result;
                case LoadStatus.RefusedNewer:
                    result.Status = RunLoadStatus.Newer;
                    return result;
                case LoadStatus.Corrupt:
                    result.Status = RunLoadStatus.Damaged;
                    return result;
            }
            var payload = loaded.Payload as JObject;
            var format = (int?)payload?[RunSaveKeys.FormatVersion];
            if (format != content.Flow.SaveFormat || !(payload[RunSaveKeys.Run] is JObject run))
            {
                result.Status = RunLoadStatus.Damaged;
                result.Warnings.Add(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.FormatMismatch, slot, format, content.Flow.SaveFormat));
                return result;
            }
            var session = new RunSession(content, saves, slotIndex, clock)
            {
                _run = AsDoubles(run),
                Name = (string)payload[RunSaveKeys.Name],
                Portrait = (string)payload[RunSaveKeys.Portrait],
                _playtimeBase = (long?)payload[RunSaveKeys.Playtime] ?? 0L,
                EncounterId = (string)payload[RunSaveKeys.EncounterId],
                After = (string)payload[RunSaveKeys.After],
                _location = (string)payload[RunSaveKeys.Location],
                _restAnchor = (string)payload[RunSaveKeys.RestAnchor],
                _stay = RestStay.From(payload[RunSaveKeys.Rest] as JObject),
                _eventId = (string)payload[RunSaveKeys.EventId],
                _eventDone = payload[RunSaveKeys.EventDone]?.Type == JTokenType.Boolean && (bool)payload[RunSaveKeys.EventDone],
            };
            var warned = false;
            try
            {
                if (payload[RunSaveKeys.Combat] is JObject checkpoint)
                {
                    var sameContent = loaded.ContentHash == content.ContentHash;
                    if (!sameContent)
                    {
                        warned = true;
                        result.Warnings.Add(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.ContentChanged, loaded.ContentHash, content.ContentHash));
                    }
                    session.Combat = CombatSession.Load(content.Combat, checkpoint, loaded.Gen, sameContent ? loaded.Commands : Enumerable.Empty<JObject>(), out var diverged);
                    if (diverged)
                    {
                        warned = true;
                        result.Warnings.Add(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.LogDiverged, session.Combat.LoggedCommands));
                    }
                    if (session.Combat.IsOver && !session.HasPendingReward)
                    {
                        session.FinishCombat();
                        if (session.RunOver)
                        {
                            result.Status = RunLoadStatus.Empty;
                            return result;
                        }
                    }
                }
                else if (session._location == RunFlowValues.LocationRest && session.ReopenStay(result.Warnings)) warned = true;
            }
            catch (Exception e)
            {
                result.Status = RunLoadStatus.Damaged;
                result.Warnings.Add(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.LoadRefused, slot, e.Message));
                return result;
            }
            if (warned) session.Save();
            result.Session = session;
            result.Status = warned ? RunLoadStatus.ResumedWithWarning : RunLoadStatus.Resumed;
            return result;
        }

        /// <summary>
        /// Review and test hook (screen captures, smoke tests): edits the live run document in place, e.g. to queue a
        /// skill draft before a fight ends so W-08 shows the draft variant. Never called by the game.
        /// </summary>
        public void EditRunForReview(Action<JObject> edit) => edit?.Invoke(_run);

        /// <summary>A document read back through the content parser holds decimals; the engine works in doubles (D-036).</summary>
        private static JObject AsDoubles(JObject doc)
        {
            var copy = (JObject)doc.DeepClone();
            foreach (var v in copy.Descendants().OfType<JValue>().ToList())
                if (v.Value is decimal) v.Value = Js.D(v);
            return copy;
        }
    }
}
