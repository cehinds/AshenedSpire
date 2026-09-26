using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Ashen.App.Saves;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Combat
{
    /// <summary>What one command did: refused (with the reason), resolved (with its events), or failed and was rolled back.</summary>
    public sealed class CombatOutcome
    {
        public Refusal Refusal;
        public IReadOnlyList<JObject> Events = new JObject[0];
        public bool RolledBack;

        public bool Accepted => Refusal == null && !RolledBack;
    }

    /// <summary>
    /// A fight as the application runs it (PF-03, PF-06; US-5.1, US-5.9): every command passes the legality service
    /// first (a refusal costs nothing), then the engine; an unexpected engine failure restores the last safe point.
    /// Checkpoints are committed combat states — snapshot, seed and RNG counters — and the save's command log replays
    /// on top of them with a state hash per command (D-017: a mismatch resumes from the snapshot, with a warning).
    /// </summary>
    public sealed class CombatSession
    {
        private readonly CombatData _data;
        private int _gen;
        private int _seq;

        private CombatSession(CombatData data, CombatState state, uint seed)
        {
            _data = data;
            State = state;
            Seed = seed;
        }

        public CombatState State { get; private set; }

        public uint Seed { get; }

        public bool IsOver => State.Result != null;

        /// <summary>Raised after every accepted command, with its events (the presentation timeline reads these).</summary>
        public event Action<CombatOutcome> Resolved;

        public static CombatSession Start(CombatData data, uint seed, JObject createArgs) =>
            new CombatSession(data, CombatStart.Create(data, new Rng(seed), createArgs), seed);

        /// <summary>Restore a checkpoint payload written by <see cref="Checkpoint"/>.</summary>
        public static CombatSession Resume(CombatData data, JObject checkpoint)
        {
            checkpoint = AsDoubles(checkpoint);
            var seed = checkpoint.Value<uint>(SessionKeys.Seed);
            var counters = new Dictionary<RngStream, uint>();
            foreach (var p in ((JObject)checkpoint[SessionKeys.Rng]).Properties()) counters[RngStreamNames.Parse(p.Name)] = p.Value.Value<uint>();
            var state = CombatSnapshot.Restore(data, new Rng(seed, counters), (JObject)checkpoint[SessionKeys.Snapshot]);
            return new CombatSession(data, state, seed);
        }

        /// <summary>
        /// A payload read back through the content parser holds decimals; the engine works in IEEE doubles (D-036), so
        /// every non-integer number is converted once on the way in and live and resumed states hash identically.
        /// </summary>
        private static JObject AsDoubles(JObject payload)
        {
            var copy = (JObject)payload.DeepClone();
            foreach (var v in copy.Descendants().OfType<JValue>().ToList())
                if (v.Value is decimal) v.Value = Js.D(v);
            return copy;
        }

        /// <summary>The committed state: { seed, rng: { stream: counter }, snapshot }.</summary>
        public JObject Checkpoint()
        {
            var rng = new JObject();
            foreach (var kv in State.Rng.Counters().OrderBy(kv => (int)kv.Key)) rng[RngStreamNames.ToWire(kv.Key)] = kv.Value;
            return new JObject
            {
                [SessionKeys.Seed] = Seed,
                [SessionKeys.Rng] = rng,
                [SessionKeys.Snapshot] = CombatSnapshot.Serialize(State),
            };
        }

        /// <summary>SHA-256 of the canonical committed state (hashes the command log's after-states).</summary>
        public string StateHash()
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(ContentSet.Canonical(Checkpoint()))).Select(b => b.ToString(ContentLayout.HexByteFormat)));
        }

        public CombatOutcome Execute(CombatCommand command)
        {
            var refusal = CombatLegality.Check(State, command);
            if (refusal != null) return new CombatOutcome { Refusal = refusal };
            var safe = Checkpoint();
            CombatOutcome outcome;
            try
            {
                outcome = new CombatOutcome { Events = CombatEngine.Dispatch(State, command) };
            }
            catch (Exception)
            {
                State = Resume(_data, safe).State;
                return new CombatOutcome { RolledBack = true, Refusal = new Refusal(CombatStringKeys.CombatErrorUnexpected) };
            }
            Resolved?.Invoke(outcome);
            return outcome;
        }

        // ------------------------------------------------------------------ saves

        /// <summary>Commit a checkpoint to a save slot; later commands append to its log.</summary>
        public void Save(SaveService saves, string slot, string contentHash) => Commit(saves, slot, Checkpoint(), contentHash);

        /// <summary>
        /// Commit a payload that carries this fight's checkpoint (a run save wraps it with the run document) as a new
        /// save generation; later commands append to that generation's log.
        /// </summary>
        public void Commit(SaveService saves, string slot, JToken payload, string contentHash)
        {
            _gen = saves.Checkpoint(slot, payload, contentHash, StateHash());
            _seq = 0;
        }

        /// <summary>Commands appended to the current generation's log since the last commit.</summary>
        public int LoggedCommands => _seq;

        /// <summary>Execute and, when accepted, append the command with its after-state hash to the slot's log.</summary>
        public CombatOutcome ExecuteAndLog(SaveService saves, string slot, CombatCommand command)
        {
            var outcome = Execute(command);
            if (outcome.Accepted && _gen > 0) saves.Append(slot, _gen, ++_seq, command.ToJson(), StateHash());
            return outcome;
        }

        /// <summary>
        /// Resume from a loaded slot: restore the checkpoint, then replay the log while every after-state hash
        /// matches. The first mismatch stops the replay at the last verified state and reports it (D-017).
        /// </summary>
        public static CombatSession Load(CombatData data, LoadResult loaded, out bool logDiverged) =>
            Load(data, (JObject)loaded.Payload, loaded.Gen, loaded.Commands, out logDiverged);

        /// <summary>Resume from a checkpoint inside a larger payload, replaying that generation's verified log records.</summary>
        public static CombatSession Load(CombatData data, JObject checkpoint, int gen, IEnumerable<JObject> commands, out bool logDiverged)
        {
            var session = Resume(data, checkpoint);
            session._gen = gen;
            logDiverged = false;
            foreach (var record in commands)
            {
                var safe = session.Checkpoint();
                var outcome = session.Execute(CombatCommand.FromJson((JObject)record[SaveKeys.Cmd]));
                if (!outcome.Accepted || session.StateHash() != (string)record[SaveKeys.StateHashAfter])
                {
                    session.State = Resume(data, safe).State;
                    logDiverged = true;
                    break;
                }
                session._seq++;
            }
            return session;
        }
    }
}
