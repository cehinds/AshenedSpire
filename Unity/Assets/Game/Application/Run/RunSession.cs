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
using RewardDoor = Ashen.Domain.Loop.RewardDoor;
using RK = Ashen.Generated.RunKeys;
using RunLoop = Ashen.Domain.Loop.RunLoop;
using WK = Ashen.Generated.RewardsKeys;

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
    /// A run as the application plays it (F1: AF-02 → AF-05 → W-08 → AF-10, PF-06). New creates the run document through
    /// the shipped createRunState port plus main.js newRun's additions and commits it to a slot; StartEncounter enters
    /// the first fight through the shipped enterCombat arguments; every combat command goes through
    /// <see cref="CombatSession.Execute"/> (legality first, rollback on failure) and is autosaved as rules/runFlow.json
    /// says. When the fight ends the run loop's post-combat door runs at once (<see cref="RunLoop.EndCombat"/>: the
    /// write-back, the ledgers, the reward rolls into run.pendingReward, or the run's close-out on a death) and the slot
    /// is checkpointed at the rewards (a death clears it: permadeath, as shipped saves.clearRun). The reward door
    /// (<see cref="RewardDoor"/>) claims, skips and continues with a save after every step, so a reload resumes the same
    /// claim state. A save is one SaveService envelope whose payload holds the format, the preset, the slot summary
    /// (D-059), the run document, the location and, in a fight, the combat checkpoint; commands between checkpoints go
    /// to the generation's log with their after-state hash. Engine-free.
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

        /// <summary>Where the save resumes: null before the first fight, 'combat', 'rewards' or 'map' (rules/runFlow.json locations).</summary>
        public string Location
        {
            get
            {
                if (IsInCombat) return RunFlowValues.LocationCombat;
                if (HasPendingReward) return RunFlowValues.LocationRewards;
                return _location;
            }
        }

        private string _location;

        /// <summary>Where the last reward door led ('map' or 'advanceAct'); the act map is a later build (D-058).</summary>
        public string After { get; private set; }

        /// <summary>The run document (a copy).</summary>
        public JObject Run => (JObject)_run.DeepClone();

        /// <summary>The profile document behind this run (found armaments, seen records, history, settings).</summary>
        public ProfileStore Profile => _profile ??= ProfileStore.Load(Saves);

        /// <summary>Raised after every accepted combat command (after its autosave), with its outcome.</summary>
        public event Action<CombatOutcome> CommandCommitted;

        // ------------------------------------------------------------------ create

        /// <summary>A new run in a slot (overwriting what was there) from a class and seed, saved at once.</summary>
        public static RunSession New(RunContent content, SaveService saves, int slotIndex, uint seed, string classId, string name, Func<DateTime> clock = null)
        {
            if (classId == null || !content.Data.Classes.Has(classId))
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.UnknownClass, classId));
            var session = new RunSession(content, saves, slotIndex, clock)
            {
                _run = RunState.Create(content.Data, seed, classId),
                Name = name,
                Portrait = content.Portrait(classId),
            };
            session.AddNewRunFields();
            session.Save();
            return session;
        }

        /// <summary>
        /// main.js newRun's additions to a created run, in order (rules/runFlow.json newRun): the seed string, the
        /// customization (the character's name and tint, the data's glyph), stats, path, seenEvents and
        /// lastEncounters. The post-combat pipeline counts wins in stats; the run loop reads the rest.
        /// </summary>
        private void AddNewRunFields()
        {
            _run[RK.SeedString] = SeedText;
            foreach (var p in Content.Flow.NewRun.Properties())
            {
                if (p.Name == LK.Customization)
                {
                    var customization = new JObject { [RunFlowKeys.Name] = Name };
                    foreach (var q in ((JObject)p.Value).Properties()) customization[q.Name] = q.Value.DeepClone();
                    customization[RunFlowKeys.Tint] = Content.Flow.DefaultTint;
                    _run[p.Name] = customization;
                }
                else _run[p.Name] = p.Value.DeepClone();
            }
        }

        /// <summary>Enter a fight (the F1 rule's encounter unless one is named) and checkpoint it.</summary>
        public CombatSession StartEncounter(string encounterId = null)
        {
            EncounterId = encounterId ?? Content.FirstFightEncounter();
            var args = RunCombat.CreateArgs(_run, EncounterId, Content.Data);
            Combat = CombatSession.Start(Content.Combat, Seed, args);
            FightFinished = false;
            LastOutcome = null;
            Save();
            return Combat;
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
                RunOver = true;
                Profile.Save(Content.ContentHash);
                Saves.Delete(Slot);
                return;
            }
            Save();
        }

        // ------------------------------------------------------------------ the reward door (W-08; US-11.1 to US-11.3)

        /// <summary>The run holds a pending reward (resume lands on W-08).</summary>
        public bool HasPendingReward => RewardClaims(_run) != null;

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
                var setting = Js.Str(Profile.Settings[RunFlowKeys.RewardCollect]) ?? Js.Str(Content.Snapshot.PlayerSettings?[RunFlowKeys.RewardCollect]);
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
        /// pending, unskipped row, a choice picked on 'cardRewards'; manual: nothing), the checkpoint leaves the run, and
        /// the slot is saved at the post-reward location. Returns where the door leads ('map' or 'advanceAct').
        /// </summary>
        public string FinishRewards()
        {
            var door = Door ?? throw new InvalidOperationException(RunFlowMessages.NoPendingReward);
            var snapshot = Snapshot();
            var receipt = door.Continue(RewardCollectMode);
            _door = null;
            After = receipt.After;
            _location = RunFlowValues.LocationMap;
            try { Commit(snapshot); }
            catch
            {
                _location = null;
                After = null;
                throw;
            }
            return After;
        }

        private sealed class SessionSnapshot
        {
            public JObject Run;
            public JObject Profile;
            public string ProfileText;
        }

        private SessionSnapshot Snapshot() => new SessionSnapshot
        {
            Run = (JObject)_run.DeepClone(),
            Profile = Profile.Snapshot(),
            ProfileText = ContentSet.Canonical(Profile.Doc),
        };

        private void Restore(SessionSnapshot s)
        {
            _run = s.Run;
            Profile.Restore(s.Profile);
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

        /// <summary>The run loop's context over this run: its RNG, the profile and the settings the loop reads.</summary>
        private LoopContext Context(Rng rng = null)
        {
            _rng = rng ?? _rng ?? RunRng();
            return new LoopContext(Content.Loop, _run, _rng, Profile.Doc, new LoopSettings
            {
                PointsPerLevel = Profile.Settings[RunFlowKeys.LevelUpValue]?.DeepClone() ?? Content.Snapshot.PlayerSettings?[RunFlowKeys.LevelUpValue]?.DeepClone(),
                RewardCollect = RewardCollectMode,
            });
        }

        /// <summary>The run's RNG from its saved stream counters (engine/save.js saveRun writes run.streamCounters).</summary>
        private Rng RunRng()
        {
            var counters = new Dictionary<RngStream, uint>();
            foreach (var p in (_run.Obj(RK.StreamCounters) ?? new JObject()).Properties()) counters[RngStreamNames.Parse(p.Name)] = (uint)Js.D(p.Value);
            return new Rng(Seed, counters);
        }

        private static JObject Counters(Rng rng)
        {
            var o = new JObject();
            foreach (var kv in rng.Counters().OrderBy(kv => (int)kv.Key)) o[RngStreamNames.ToWire(kv.Key)] = (double)kv.Value;
            return o;
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
            if (IsInCombat) payload[RunSaveKeys.Combat] = Combat.Checkpoint();
            if (_node != null && !IsInCombat) payload[NodeKeys.Node] = _node.ToJson();
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
        /// Resume a slot (AF-10): verified envelope, current run format, then the fight from its checkpoint with the log
        /// replayed while every after-state hash matches. A content change skips the log; a divergence stops at the last
        /// verified command. Either way the session re-commits at once so the slot holds one clean generation. A fight
        /// saved already over (an older build, or a crash before the rewards were rolled) runs its post-combat door now.
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
                _node = Ashen.App.Nodes.NodeEntry.FromJson(payload[NodeKeys.Node]),
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
