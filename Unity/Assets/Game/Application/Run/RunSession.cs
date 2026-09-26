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
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;

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

    /// <summary>
    /// A run as the application plays it (F1: AF-02 → AF-05 → AF-10, PF-06). New creates the run document through the
    /// shipped createRunState port and commits it to a slot; StartEncounter enters the first fight through the shipped
    /// enterCombat arguments; every combat command goes through <see cref="CombatSession.Execute"/> (legality first,
    /// rollback on failure) and is autosaved as rules/runFlow.json says. A save is one SaveService envelope whose
    /// payload holds the format, the preset, the slot summary (D-059), the run document and the combat checkpoint;
    /// commands between checkpoints go to the generation's log with their after-state hash. Load verifies the stored
    /// hash, replays the log while every after-state matches and falls back to the checkpoint with a warning when the
    /// content changed or the log diverged (D-017). Engine-free.
    /// </summary>
    public sealed class RunSession
    {
        private readonly Func<DateTime> _clock;
        private JObject _run;
        private long _playtimeBase;
        private DateTime _playStarted;

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
        public CombatSession Combat { get; private set; }

        public bool IsInCombat => Combat != null;

        /// <summary>The run document (a copy).</summary>
        public JObject Run => (JObject)_run.DeepClone();

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
            session.Save();
            return session;
        }

        /// <summary>Enter a fight (the F1 rule's encounter unless one is named) and checkpoint it.</summary>
        public CombatSession StartEncounter(string encounterId = null)
        {
            EncounterId = encounterId ?? Content.FirstFightEncounter();
            var args = RunCombat.CreateArgs(_run, EncounterId, Content.Data);
            Combat = CombatSession.Start(Content.Combat, Seed, args);
            Save();
            return Combat;
        }

        // ------------------------------------------------------------------ play

        /// <summary>Execute a combat command; an accepted one is autosaved per rules/runFlow.json autosave.</summary>
        public CombatOutcome Execute(CombatCommand command)
        {
            if (Combat == null) throw new InvalidOperationException(RunFlowMessages.NotInCombat);
            var flow = Content.Flow;
            var outcome = flow.AfterCommand == RunFlowValues.AutosaveAppend
                ? Combat.ExecuteAndLog(Saves, Slot, command)
                : Combat.Execute(command);
            if (!outcome.Accepted) return outcome;
            if (flow.AfterCommand == RunFlowValues.AutosaveCheckpoint || flow.CheckpointOn.Contains(command.Type) || Combat.IsOver) Save();
            CommandCommitted?.Invoke(outcome);
            return outcome;
        }

        // ------------------------------------------------------------------ save

        /// <summary>A full checkpoint: a new save generation holding the run, the fight and the summary.</summary>
        public void Save()
        {
            var payload = Payload();
            if (Combat != null) Combat.Commit(Saves, Slot, payload, Content.ContentHash);
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
            if (Combat != null)
            {
                payload[RunSaveKeys.Location] = RunFlowValues.LocationCombat;
                payload[RunSaveKeys.EncounterId] = EncounterId;
                payload[RunSaveKeys.Combat] = Combat.Checkpoint();
            }
            return payload;
        }

        public long PlaytimeSeconds => _playtimeBase + Math.Max(0L, (long)(_clock() - _playStarted).TotalSeconds);

        /// <summary>The slot summary W-02 and W-03 read (D-059): identity, progress, live HP in a fight, seed and save time.</summary>
        public JObject Summary()
        {
            var hp = Combat != null ? Combat.State.Player.Num(K.Hp) : _run.Num(K.Hp);
            var hpMax = Combat != null ? Combat.State.Player.Num(K.MaxHp) : _run.Num(K.MaxHp);
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

        /// <summary>SHA-256 of the canonical run document and combat checkpoint (resume equivalence, 08 §12 SaveRoundTrip).</summary>
        public string StateHash()
        {
            var state = new JObject { [RunSaveKeys.Run] = _run.DeepClone(), [RunSaveKeys.Combat] = Combat?.Checkpoint() };
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(ContentSet.Canonical(state))).Select(b => b.ToString(ContentLayout.HexByteFormat)));
        }

        // ------------------------------------------------------------------ load

        /// <summary>
        /// Resume a slot (AF-10): verified envelope, current run format, then the fight from its checkpoint with the log
        /// replayed while every after-state hash matches. A content change skips the log; a divergence stops at the last
        /// verified command. Either way the session re-commits at once so the slot holds one clean generation.
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
