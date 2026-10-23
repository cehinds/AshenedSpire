using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Ashen.App.Saves;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using DK = Ashen.Generated.Core2DamageKeys;
using NK = Ashen.Generated.Core2SnapshotKeys;
using NM = Ashen.Generated.Core2SnapshotMessages;
using NN = Ashen.Generated.Core2SnapshotNumbers;
using RK = Ashen.Generated.Core2ReactionKeys;
using RV = Ashen.Generated.Core2ReactionValues;

namespace Ashen.App.Combat
{
    /// <summary>A command a Core 2 fight refused (nothing changed) or failed on (rolled back to the last safe point): the reason the rules give it.</summary>
    public sealed class Core2Refusal
    {
        public string Message;

        /// <summary>The reason a refused play names (controlled, cost, range, ...), when the rules gave one.</summary>
        public string Reason;

        /// <summary>The rules this command needs are not in this build yet.</summary>
        public bool NotPorted;
    }

    /// <summary>What one intent did: refused (with the reason), resolved (with its events), or failed and was rolled back.</summary>
    public sealed class Core2Outcome
    {
        public Core2Refusal Refusal;
        public IReadOnlyList<JObject> Events = new JObject[0];
        public bool RolledBack;

        public bool Accepted => Refusal == null && !RolledBack;

        /// <summary>The offer the fight is now paused on, if the intent opened one.</summary>
        public JObject Offer;
    }

    public enum Core2LoadStatus
    {
        Empty,
        Loaded,
        RefusedNewer,
        Damaged,
        FormatMismatch,
        ProfileMismatch,
    }

    public sealed class Core2LoadResult
    {
        public Core2LoadStatus Status;
        public Core2CombatSession Session;
        public JObject Run;
        public bool LogDiverged;
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// A Core 2 fight as the application runs it (docs/design/11 WP-13; D-124, D-131; the 405 <see cref="CombatSession"/> pattern): the fight starts from a
    /// run on the run's own random streams (<see cref="CoreRunCombat"/>), every intent goes through the one door the web has (<see cref="CoreDispatch"/>),
    /// a refused or failed intent leaves the fight as it was, and the fight is saved as a checkpoint — the web-shaped snapshot (<see cref="CoreSnapshot"/>)
    /// and the counters of every stream — with the verified command log of the save beside it. A fight paused on a reaction offer or a card choice is a
    /// checkpoint like any other: it resumes at the offer.
    /// It also answers what a screen asks of a fight: the previews of a card and of an enemy's move, the costs of a play, the intents that are legal now.
    /// </summary>
    public sealed class Core2CombatSession
    {
        private readonly CoreCombatData _data;
        private int _gen;
        private int _seq;

        private Core2CombatSession(CoreCombatData data, CombatState state, uint seed)
        {
            _data = data;
            State = state;
            Seed = seed;
        }

        public CombatState State { get; private set; }

        public uint Seed { get; }

        public bool IsOver => State.Result != null;

        /// <summary>Raised after every accepted intent, with its events.</summary>
        public event Action<Core2Outcome> Resolved;

        // ------------------------------------------------------------------ starting

        /// <summary>
        /// A fight on the run's own generator (shipped enterCombat hands createCombat the run's rng, D-131): the run's seed and stream counters, the encounter
        /// it enters, the enemies and settings it was given. The fight's draws continue the run's streams and its checkpoint carries their counters.
        /// </summary>
        public static Core2CombatSession Start(CoreCombatData data, JObject run, string encounterId, JObject settings = null, IReadOnlyList<string> enemyIds = null)
        {
            var seed = (uint)run.Num(Ashen.Generated.RunKeys.Seed);
            var rng = CoreSnapshot.RngFromJson(seed, run.Obj(Ashen.Generated.RunKeys.StreamCounters));
            return Start(data, rng, run, encounterId, settings, enemyIds);
        }

        public static Core2CombatSession Start(CoreCombatData data, Rng rng, JObject run, string encounterId, JObject settings = null, IReadOnlyList<string> enemyIds = null) =>
            new Core2CombatSession(data, CoreRunCombat.Create(data, rng, run, encounterId, settings, enemyIds), rng.Seed);

        // ------------------------------------------------------------------ checkpoints

        /// <summary>The committed state: { seed, rng: { stream: counter }, snapshot }.</summary>
        public JObject Checkpoint() => new JObject
        {
            [SessionKeys.Seed] = Seed,
            [SessionKeys.Rng] = CoreSnapshot.RngToJson(State.Rng),
            [SessionKeys.Snapshot] = CoreSnapshot.Serialize(State),
        };

        /// <summary>A fight restored from a checkpoint payload; a snapshot the rules refuse (or one saved under another profile) throws <see cref="SnapshotRefusedException"/>.</summary>
        public static Core2CombatSession Resume(CoreCombatData data, JObject checkpoint)
        {
            checkpoint = CoreSnapshot.Doubles(checkpoint);
            var seed = checkpoint.Value<uint>(SessionKeys.Seed);
            var rng = CoreSnapshot.RngFromJson(seed, checkpoint.Obj(SessionKeys.Rng));
            return new Core2CombatSession(data, CoreSnapshot.Restore(data, rng, checkpoint.Obj(SessionKeys.Snapshot)), seed);
        }

        /// <summary>SHA-256 of the canonical committed state (hashes the command log's after-states).</summary>
        public string StateHash()
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(ContentSet.Canonical(Checkpoint()))).Select(b => b.ToString(ContentLayout.HexByteFormat)));
        }

        // ------------------------------------------------------------------ intents

        /// <summary>
        /// dispatch(combat, intent): the intent is applied and the queue drained, or the fight is paused on a reaction offer or a card choice. A refused
        /// intent (the rules throw) changes nothing; an unexpected failure restores the last safe point.
        /// </summary>
        public Core2Outcome Execute(JObject intent)
        {
            if (IsOver) return Refused(new InvalidOperationException(Ashen.Generated.CombatMessages.CombatOver));
            var safe = Checkpoint();
            var mark = State.EventLog.Count;
            try
            {
                CoreDispatch.Dispatch(State, intent);
            }
            catch (NotSupportedException e)
            {
                State = Resume(_data, safe).State;
                var outcome = Refused(e);
                outcome.Refusal.NotPorted = true;
                return outcome;
            }
            catch (InvalidOperationException e)
            {
                State = Resume(_data, safe).State;
                return Refused(e);
            }
            catch (Exception)
            {
                State = Resume(_data, safe).State;
                return new Core2Outcome { RolledBack = true, Refusal = new Core2Refusal { Message = CombatStringKeys.CombatErrorUnexpected } };
            }
            var done = new Core2Outcome { Events = State.EventLog.Skip(mark).Select(e => (JObject)e.DeepClone()).ToList(), Offer = PendingOffer() };
            Resolved?.Invoke(done);
            return done;
        }

        private static Core2Outcome Refused(Exception e) =>
            new Core2Outcome { Refusal = new Core2Refusal { Message = e.Message, Reason = (e as PlayRefusalException)?.Reason } };

        /// <summary>The reaction offer the fight is paused on (its options with their prices), or null.</summary>
        public JObject PendingOffer() => State.Fields.Obj(DK.PendingReaction) != null ? (JObject)State.Fields.Obj(DK.PendingReaction).DeepClone() : null;

        /// <summary>The card choice the fight is paused on (<c>pendingCardChoice</c> or <c>pendingAbilityDiscard</c>), or null.</summary>
        public JObject PendingChoice()
        {
            var choice = State.Fields.Obj(Core2StatusKeys.PendingCardChoice) ?? State.Fields.Obj(Core2StatusKeys.PendingAbilityDiscard);
            return choice == null ? null : (JObject)choice.DeepClone();
        }

        /// <summary>The fight ended: what the run keeps of what the player learned about the enemies (reconcileCombatKnowledge); false when the run holds no ledger.</summary>
        public bool ReconcileKnowledge(JObject run) => CoreKnowledge.Reconcile(run, State);

        // ------------------------------------------------------------------ what a screen asks

        /// <summary>previewCard(combat, instanceId, targetId, route): the card's face as it would play now, with its range routes.</summary>
        public JObject PreviewCard(string instanceId, string targetId = null, string route = null) => CoreRangePreview.Card(State, instanceId, targetId, route);

        /// <summary>previewCardHit(combat, instanceId, targetId, route): the hit a card would make on a target, with its chance.</summary>
        public JObject PreviewHit(string instanceId, string targetId, string route = null) => CoreRangePreview.Hit(State, instanceId, targetId, route);

        /// <summary>cardPlayCosts(combat, instanceId): what playing the card would pay now.</summary>
        public JObject CardCosts(string instanceId) => CoreCosts.CardPlayCosts(State, instanceId);

        /// <summary>playRefusal(combat, instanceId): why the card cannot be played now, or null.</summary>
        public string PlayRefusal(string instanceId) => CoreCosts.PlayRefusal(State, instanceId);

        /// <summary>previewEnemyAction: what an enemy's telegraphed move would do (its committed intent when it has one).</summary>
        public JObject PreviewEnemy(string enemyId, string moveId = null)
        {
            var enemy = State.Enemies.FirstOrDefault(e => e.Str(Ashen.Generated.CombatKeys.Id) == enemyId) ?? throw new ArgumentException(enemyId);
            return CoreEnemyPreview.Preview(State, enemy, moveId ?? enemy.Obj(Ashen.Generated.CombatKeys.Intent)?.Str(Ashen.Generated.CombatKeys.MoveId));
        }

        /// <summary>
        /// The intents that are legal now, in a stable order (the web's <c>legalIntents</c>): while an offer waits, its options and Back; while a card choice
        /// waits, the first valid selection; otherwise every card with every target and stance choice the rules accept, ending the turn, the reaction switch
        /// and the predictions an enemy's hidden move allows. A candidate is tried on a private copy of the fight, so it draws nothing from the live streams.
        /// </summary>
        public IReadOnlyList<JObject> LegalIntents()
        {
            var legal = new List<JObject>();
            if (IsOver) return legal;
            var offer = State.Fields.Obj(DK.PendingReaction);
            if (offer != null)
            {
                foreach (var option in Js.Items(offer[RK.Options]).OfType<JObject>()) legal.Add(Intent(RV.IntentChooseReaction, RK.OfferId, offer[Ashen.Generated.CombatKeys.Id], RK.OptionId, option[Ashen.Generated.CombatKeys.Id]));
                legal.Add(Intent(RV.IntentChooseReaction, RK.OfferId, offer[Ashen.Generated.CombatKeys.Id], RK.OptionId, Js.Null()));
                return legal;
            }
            var cards = State.Fields.Obj(Core2StatusKeys.PendingCardChoice);
            if (cards != null)
            {
                var ids = Js.Items(cards[Core2StatusKeys.CandidateIds]).Take((int)cards.Num(Ashen.Generated.CombatKeys.Count)).ToList();
                legal.Add(Intent(RV.IntentChooseCards, RK.CardInstanceIds, new JArray(ids)));
                return legal;
            }
            var discard = State.Fields.Obj(Core2StatusKeys.PendingAbilityDiscard);
            if (discard != null)
            {
                legal.Add(Intent(RV.IntentChooseDiscard, RK.CardInstanceIds, new JArray(State.Piles.Hand.Take((int)discard.Num(Ashen.Generated.CombatKeys.Count)).Select(card => card[Ashen.Generated.CombatKeys.InstanceId]))));
                return legal;
            }
            var targets = State.Enemies.Where(e => e.Is(Ashen.Generated.CombatKeys.Alive)).Select(e => e.Str(Ashen.Generated.CombatKeys.Id)).ToList();
            foreach (var card in State.Piles.Hand)
            {
                var instanceId = card.Str(Ashen.Generated.CombatKeys.InstanceId);
                List<string> options;
                try { options = CoreCardPlay.ChoiceOptions(CoreCosts.Resolve(State, card)) ?? new List<string>(); }
                catch (NotSupportedException) { continue; }
                foreach (var choice in options.Count > 0 ? options.Select(o => (JToken)o).ToList() : new List<JToken> { null })
                    foreach (var target in new string[] { null }.Concat(targets))
                    {
                        var intent = Intent(RV.IntentPlayCard, RK.CardInstanceId, instanceId);
                        if (target != null) intent[RK.TargetId] = target;
                        if (choice != null) intent[RK.Choice] = choice;
                        if (Accepts(intent)) legal.Add(intent);
                    }
            }
            var end = Intent(RV.IntentEndTurn, RK.DiscardIds, new JArray());
            if (Accepts(end)) legal.Add(end);
            var reactions = Intent(RV.IntentSetReactions, RK.Enabled, State.Player[NK.ReactionsEnabled] != null && !State.Player.Is(NK.ReactionsEnabled));
            if (Accepts(reactions)) legal.Add(reactions);
            return legal;
        }

        private static JObject Intent(string type, params object[] pairs)
        {
            var o = new JObject { [Ashen.Generated.CombatKeys.Type] = type };
            for (var i = 0; i + 1 < pairs.Length; i += (int)NN.PairLength) o[(string)pairs[i]] = pairs[i + 1] is JToken token ? token.DeepClone() : Js.Token(pairs[i + 1]);
            return o;
        }

        private bool Accepts(JObject intent)
        {
            var copy = State.Detach();
            try
            {
                CoreDispatch.Dispatch(copy, (JObject)intent.DeepClone());
                return true;
            }
            catch (InvalidOperationException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        // ------------------------------------------------------------------ saves

        /// <summary>The document a save holds: the format, the profile the fight is pinned to, the run it belongs to (when it has one) and the fight's checkpoint.</summary>
        public JObject Payload(JObject run = null)
        {
            var payload = new JObject
            {
                [RunSaveKeys.FormatVersion] = NN.SaveFormat,
                [NK.GameProfile] = new JObject { [Ashen.Generated.CombatKeys.Id] = _data.Run.ProfileId, [NK.Hash] = _data.Run.ProfileHash },
            };
            if (run != null) payload[RunSaveKeys.Run] = run.DeepClone();
            payload[RunSaveKeys.Combat] = Checkpoint();
            return payload;
        }

        /// <summary>Commit a checkpoint to a save slot; later intents append to its log.</summary>
        public void Save(SaveService saves, string slot, string contentHash, JObject run = null) => Commit(saves, slot, Payload(run), contentHash);

        /// <summary>Commit a payload that carries this fight's checkpoint as a new save generation; later intents append to that generation's log.</summary>
        public void Commit(SaveService saves, string slot, JToken payload, string contentHash)
        {
            _gen = saves.Checkpoint(slot, payload, contentHash, StateHash());
            _seq = 0;
        }

        /// <summary>Intents appended to the current generation's log since the last commit.</summary>
        public int LoggedCommands => _seq;

        /// <summary>Execute and, when accepted, append the intent with its after-state hash to the slot's log.</summary>
        public Core2Outcome ExecuteAndLog(SaveService saves, string slot, JObject intent)
        {
            var outcome = Execute(intent);
            if (outcome.Accepted && _gen > 0) saves.Append(slot, _gen, ++_seq, intent, StateHash());
            return outcome;
        }

        /// <summary>
        /// Resume a slot: the verified envelope, this build's Core 2 format, the profile the fight is pinned to (D-171: another profile is recoverable, the slot
        /// is kept as it is), then the checkpoint with the log replayed while every after-state hash matches (the first mismatch stops at the last verified
        /// state and reports it, D-017).
        /// </summary>
        public static Core2LoadResult Load(CoreCombatData data, SaveService saves, string slot)
        {
            var result = new Core2LoadResult();
            LoadResult loaded;
            try { loaded = saves.Load(slot); }
            catch (Exception e)
            {
                result.Status = Core2LoadStatus.Damaged;
                result.Warnings.Add(e.Message);
                return result;
            }
            result.Warnings.AddRange(loaded.Warnings);
            switch (loaded.Status)
            {
                case LoadStatus.Empty: result.Status = Core2LoadStatus.Empty; return result;
                case LoadStatus.RefusedNewer: result.Status = Core2LoadStatus.RefusedNewer; return result;
                case LoadStatus.Corrupt: result.Status = Core2LoadStatus.Damaged; return result;
            }
            var payload = loaded.Payload as JObject;
            if ((int?)payload?[RunSaveKeys.FormatVersion] != (int)NN.SaveFormat || !(payload[RunSaveKeys.Combat] is JObject checkpoint))
            {
                result.Status = Core2LoadStatus.FormatMismatch;
                result.Warnings.Add(string.Format(CultureInfo.InvariantCulture, NM.FormatMismatch, slot, payload?[RunSaveKeys.FormatVersion], NN.SaveFormat));
                return result;
            }
            var pin = payload.Obj(NK.GameProfile);
            if (pin != null && pin.Str(NK.Hash) != data.Run.ProfileHash)
            {
                result.Status = Core2LoadStatus.ProfileMismatch;
                result.Warnings.Add(string.Format(CultureInfo.InvariantCulture, NM.ProfileMismatch, pin.Str(NK.Hash), data.Run.ProfileHash));
                return result;
            }
            try
            {
                var session = Resume(data, checkpoint);
                session._gen = loaded.Gen;
                foreach (var record in loaded.Commands)
                {
                    var safe = session.Checkpoint();
                    var outcome = session.Execute((JObject)CoreSnapshot.Doubles((JObject)record[SaveKeys.Cmd]));
                    if (!outcome.Accepted || session.StateHash() != (string)record[SaveKeys.StateHashAfter])
                    {
                        session.State = Resume(data, safe).State;
                        result.LogDiverged = true;
                        break;
                    }
                    session._seq++;
                }
                result.Session = session;
                result.Run = payload[RunSaveKeys.Run] is JObject run ? CoreSnapshot.Doubles(run) : null;
                result.Status = Core2LoadStatus.Loaded;
            }
            catch (SnapshotRefusedException e)
            {
                result.Status = e.ProfileMismatch ? Core2LoadStatus.ProfileMismatch : Core2LoadStatus.Damaged;
                result.Warnings.Add(e.Message);
            }
            return result;
        }
    }
}
