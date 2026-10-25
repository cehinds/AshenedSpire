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
using V = Ashen.Generated.CombatValues;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using EV = Ashen.Generated.Core2EnemyValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DV = Ashen.Generated.Core2DamageValues;
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
        public Core2Outcome Execute(JObject intent) => Execute(intent, null, null);

        /// <summary>
        /// Run an intent; with a save slot an accepted one is appended to the slot's log before anyone hears of it, and a failed append rolls the fight back
        /// to before the intent (a full disk leaves no fight the log cannot replay, nor a finishing blow the run never settled).
        /// </summary>
        private Core2Outcome Execute(JObject intent, SaveService saves, string slot)
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
                // CoreDispatch restored the fight (counters in place on the caller's generator): nothing is rebuilt.
                var outcome = Refused(e);
                outcome.Refusal.NotPorted = true;
                return outcome;
            }
            catch (InvalidOperationException e)
            {
                return Refused(e);
            }
            catch (Exception)
            {
                RestoreKeepingGenerator(safe);
                return new Core2Outcome { RolledBack = true, Refusal = new Core2Refusal { Message = CombatStringKeys.CombatErrorUnexpected } };
            }
            if (saves != null && _gen > 0)
            {
                try { saves.Append(slot, _gen, _seq + 1, intent, StateHash()); }
                catch (Exception)
                {
                    RestoreKeepingGenerator(safe);
                    return new Core2Outcome { RolledBack = true, Refusal = new Core2Refusal { Message = CombatStringKeys.CombatErrorUnexpected } };
                }
                _seq++;
            }
            var done = new Core2Outcome { Events = State.EventLog.Skip(mark).Select(e => (JObject)e.DeepClone()).ToList(), Offer = PendingOffer() };
            Resolved?.Invoke(done);
            return done;
        }

        /// <summary>The last safe point, on the generator the fight already runs on (a session started on a run's shared generator keeps feeding it).</summary>
        private void RestoreKeepingGenerator(JObject safe)
        {
            var generator = State.Rng;
            var restored = Resume(_data, safe).State;
            generator.RestoreCounters(restored.Rng);
            restored.Rng = generator;
            State = restored;
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
        /// The intents that are legal now, in a stable order: the web player's list (<c>legalIntents</c> of Tools/oracle-core2.mjs with <c>full</c>, D-300). While an
        /// offer waits, its accepted options and Back; while a card choice or discard waits, the selections the rules take; otherwise every card with every
        /// target (when its printed face aims) and stance choice the rules accept, each with the range routes its own preview offers (a route is legal on its
        /// own when the default advance is too dear, as the web's route buttons allow), ending the turn, every flask in the slots and the two charge pools,
        /// the reaction switch, the predictions an enemy's hidden move allows, the formation steps the settings allow and the recovery of each held control
        /// status. A candidate is tried on a private copy of the fight, so it draws nothing from the live streams.
        /// </summary>
        /// <param name="presentation">The player settings the formation step reads (<c>gameConfig.presentation.movementEnabled</c> and its siblings); none means the step is off, as the web's default.</param>
        public IReadOnlyList<JObject> LegalIntents(JObject presentation = null)
        {
            var legal = new List<JObject>();
            if (IsOver) return legal;
            void Keep(JObject intent)
            {
                if (Accepts(intent)) legal.Add(intent);
            }
            var offer = State.Fields.Obj(DK.PendingReaction);
            if (offer != null)
            {
                foreach (var option in Js.Items(offer[RK.Options]).OfType<JObject>()) Keep(Intent(RV.IntentChooseReaction, RK.OfferId, offer[Ashen.Generated.CombatKeys.Id], RK.OptionId, option[Ashen.Generated.CombatKeys.Id]));
                Keep(Intent(RV.IntentChooseReaction, RK.OfferId, offer[Ashen.Generated.CombatKeys.Id], RK.OptionId, Js.Null()));
                return legal;
            }
            var cards = State.Fields.Obj(Core2StatusKeys.PendingCardChoice);
            if (cards != null)
            {
                var ids = Js.Items(cards[Core2StatusKeys.CandidateIds]).ToList();
                foreach (var pick in Selections(ids, (int)cards.Num(Ashen.Generated.CombatKeys.Count))) Keep(Intent(RV.IntentChooseCards, RK.CardInstanceIds, new JArray(pick)));
                return legal;
            }
            var discard = State.Fields.Obj(Core2StatusKeys.PendingAbilityDiscard);
            if (discard != null)
            {
                var ids = State.Piles.Hand.Select(card => card[Ashen.Generated.CombatKeys.InstanceId]).ToList();
                foreach (var pick in Selections(ids, (int)discard.Num(Ashen.Generated.CombatKeys.Count))) Keep(Intent(RV.IntentChooseDiscard, RK.CardInstanceIds, new JArray(pick)));
                return legal;
            }
            var targets = State.Enemies.Where(e => e.Is(Ashen.Generated.CombatKeys.Alive)).Select(e => e.Str(Ashen.Generated.CombatKeys.Id)).ToList();
            foreach (var card in State.Piles.Hand)
            {
                var instanceId = card.Str(Ashen.Generated.CombatKeys.InstanceId);
                List<string> options;
                try { options = CoreCardPlay.ChoiceOptions(CoreCosts.Resolve(State, card)) ?? new List<string>(); }
                catch (NotSupportedException) { options = new List<string>(); }
                catch (InvalidOperationException) { options = new List<string>(); }
                foreach (var choice in options.Count > 0 ? options.Select(o => (JToken)o).ToList() : new List<JToken> { null })
                {
                    var aimed = true;
                    try { aimed = CoreTargets.CardNeedsEnemyTargetNow(State, instanceId); }
                    catch (InvalidOperationException) { }
                    catch (NotSupportedException) { }
                    foreach (var target in aimed ? new string[] { null }.Concat(targets) : new string[] { null })
                    {
                        var intent = Intent(RV.IntentPlayCard, RK.CardInstanceId, instanceId);
                        if (target != null) intent[RK.TargetId] = target;
                        if (choice != null) intent[RK.Choice] = choice;
                        Keep(intent);
                        // Routes beyond the default advance come from the card's own preview; a route is tried even when the advance is not playable.
                        foreach (var route in RoutesOf(instanceId, target))
                        {
                            var routed = (JObject)intent.DeepClone();
                            routed[RK.RangeRoute] = route;
                            Keep(routed);
                        }
                    }
                }
            }
            Keep(Intent(RV.IntentEndTurn, RK.DiscardIds, new JArray()));
            var held = State.Player.Arr(Ashen.Generated.CombatKeys.Flasks);
            for (var slot = 0; held != null && slot < held.Count; slot++)
            {
                var aimedFlask = State.Data.Flasks.Has(Js.Str(Js.Get(held[slot], Ashen.Generated.CombatKeys.FlaskId))) && State.Data.Flasks.Get(Js.Str(Js.Get(held[slot], Ashen.Generated.CombatKeys.FlaskId))).Is(Ashen.Generated.CombatKeys.Targeted);
                foreach (var target in aimedFlask ? targets : new List<string> { null })
                {
                    var use = Intent(V.CommandUseFlask, Ashen.Generated.CombatKeys.Slot, slot);
                    if (target != null) use[RK.TargetId] = target;
                    Keep(use);
                }
            }
            var charges = State.Player.Obj(Ashen.Generated.CombatKeys.FlaskCharges);
            foreach (var kind in Js.Items(State.Data.Engine.Obj(Ashen.Generated.CombatKeys.Flasks)?[Ashen.Generated.CombatKeys.ChargeKinds]).Select(Js.Str))
                if (charges != null && charges.Num(kind + V.CurrentSuffix) > 0) Keep(Intent(V.CommandUseFlask, Ashen.Generated.CombatKeys.ChargeKind, kind));
            Keep(Intent(RV.IntentSetReactions, RK.Enabled, State.Player[NK.ReactionsEnabled] != null && !State.Player.Is(NK.ReactionsEnabled)));
            foreach (var enemy in State.Enemies.Where(e => e.Is(Ashen.Generated.CombatKeys.Alive)))
            {
                var action = enemy.Obj(CK.KnowledgeAction);
                var read = action?.Obj(CK.Reads)?.Obj(Ashen.Generated.CombatValues.Player);
                if (read == null || read.Str(CK.Visibility) != CV.VisibilityUnknown || read[CK.Prediction]?.Type != JTokenType.Null) continue;
                foreach (var maneuver in PredictionChoices)
                    Keep(Intent(EV.IntentPredictIntent, DK.EnemyInstanceId, enemy[Ashen.Generated.CombatKeys.Id], DK.ActionSerial, action[CK.Serial], CK.Maneuver, maneuver));
            }
            // The formation step: every tile the settings allow (the plan says whether), carrying those settings as the intent does.
            foreach (var row in Ashen.Generated.Core2RangeValues.FormationRows)
                for (var column = 1; column <= (int)Ashen.Generated.Core2RangeNumbers.FormationRowsMax; column++)
                {
                    var cell = row.ToString() + column.ToString(CultureInfo.InvariantCulture);
                    if (!CoreFormation.Plan(State, cell, presentation ?? new JObject()).Is(Ashen.Generated.Core2RangeKeys.Ok)) continue;
                    var step = Intent(Ashen.Generated.Core2RangeValues.IntentMoveCharacter, Ashen.Generated.Core2RangeKeys.Cell, cell);
                    if (presentation != null) step[Ashen.Generated.Core2RangeKeys.Settings] = presentation.DeepClone();
                    Keep(step);
                }
            // A control status the player holds is tried for recovery (one stack); the rules accept the ones that can be recovered and paid for.
            foreach (var status in State.Player.Obj(Ashen.Generated.CombatKeys.Statuses)?.Properties().ToList() ?? new List<JProperty>())
            {
                if (!(status.Value is JObject heldStatus) || heldStatus.Num(Ashen.Generated.CombatKeys.Stacks) <= 0) continue;
                Keep(Intent(RV.IntentRecoverControl, RK.Selections, new JArray(new JObject { [Ashen.Generated.CombatKeys.Status] = status.Name, [Ashen.Generated.CombatKeys.Stacks] = Js.N(NN.RecoverStacks) })));
            }
            return legal;
        }

        /// <summary>The selections the web offers for a pause that wants <paramref name="count"/> cards: each card alone for one, otherwise the first and the last that many.</summary>
        private static IEnumerable<List<JToken>> Selections(List<JToken> ids, int count)
        {
            if (count <= 0 || ids.Count < count) yield break;
            if (count == 1)
            {
                foreach (var id in ids) yield return new List<JToken> { id };
                yield break;
            }
            yield return ids.Take(count).ToList();
            yield return ids.Skip(ids.Count - count).ToList();
        }

        /// <summary>The range routes other than advance the card's preview offers against a target, in the preview's order (none when it has no preview).</summary>
        private IEnumerable<string> RoutesOf(string instanceId, string target)
        {
            JObject range;
            try { range = CoreRangePreview.Card(State, instanceId, target); }
            catch (InvalidOperationException) { return Enumerable.Empty<string>(); }
            catch (NotSupportedException) { return Enumerable.Empty<string>(); }
            return Js.Items(range?[Ashen.Generated.Core2RangeKeys.Routes]).OfType<JObject>().Select(row => row.Str(Ashen.Generated.Core2RangeKeys.Id)).Where(id => id != DV.RouteAdvance).ToList();
        }

        /// <summary>What a telegraph the player could not read can be predicted to be (the web's PREDICTION_CHOICES).</summary>
        public static readonly IReadOnlyList<string> PredictionChoices = new[]
        {
            CV.CategoryAttack, CV.CategorySmash, CV.CategorySweep, CV.CategoryRanged, CV.CategoryDefend, CV.CategoryCounter, CV.CategorySpell, CV.CategoryCasting, CV.CategoryPreparing,
        };

        private static JObject Intent(string type, params object[] pairs)
        {
            var o = new JObject { [Ashen.Generated.CombatKeys.Type] = type };
            for (var i = 0; i + 1 < pairs.Length; i += (int)NN.PairLength) o[(string)pairs[i]] = pairs[i + 1] is JToken token ? token.DeepClone() : Js.Token(pairs[i + 1]);
            return o;
        }

        private bool Accepts(JObject intent) => Why(intent) == null;

        /// <summary>
        /// Why the rules would refuse an intent now, in their words, or null when they would take it. The intent is tried on a private copy of the fight (the work a pause
        /// suspended is carried over), so it draws nothing from the live streams and changes nothing.
        /// </summary>
        public string Why(JObject intent)
        {
            if (IsOver) return Ashen.Generated.CombatMessages.CombatOver;
            var copy = CoreTransaction.DetachPaused(State);
            try
            {
                CoreDispatch.Dispatch(copy, (JObject)intent.DeepClone(), false);
                return null;
            }
            catch (InvalidOperationException e) { return e.Message; }
            catch (NotSupportedException e) { return e.Message; }
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
            // A finished fight is not a resume point: restoring it mounts a battlefield with nothing left to kill, and no dispatch reaches the end of the combat.
            if (IsOver) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, NM.EndedFight, State.Result));
            _gen = saves.Checkpoint(slot, payload, contentHash, StateHash());
            _seq = 0;
        }

        /// <summary>Intents appended to the current generation's log since the last commit.</summary>
        public int LoggedCommands => _seq;

        /// <summary>Execute and, when accepted, append the intent with its after-state hash to the slot's log.</summary>
        public Core2Outcome ExecuteAndLog(SaveService saves, string slot, JObject intent) => Execute(intent, saves, slot);

        /// <summary>
        /// Resume a slot: the verified envelope, this build's Core 2 format, the profile the fight is pinned to (D-171: another profile is recoverable, the slot
        /// is kept as it is), then the checkpoint with the log replayed while every after-state hash matches (the first mismatch stops at the last verified
        /// state and reports it, D-017). <paramref name="replayed"/> sees each verified replayed intent's outcome, as a live caller saw it when it ran.
        /// </summary>
        public static Core2LoadResult Load(CoreCombatData data, SaveService saves, string slot, Action<Core2Outcome, Core2CombatSession> replayed = null)
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
                        session.RestoreKeepingGenerator(safe);
                        result.LogDiverged = true;
                        break;
                    }
                    session._seq++;
                    replayed?.Invoke(outcome, session);
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
