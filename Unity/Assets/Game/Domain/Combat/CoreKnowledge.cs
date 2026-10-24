using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using EK = Ashen.Generated.Core2EnemyKeys;
using EM = Ashen.Generated.Core2EnemyMessages;
using EN = Ashen.Generated.Core2EnemyNumbers;
using EV = Ashen.Generated.Core2EnemyValues;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;
using Core2RngStream = Ashen.Generated.Core2RngStream;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// What the player reads of an enemy's telegraphed action (web engine/enemyKnowledge.js, model/enemyIntentKnowledge.js): every
    /// intent is a numbered knowledge action with a category, and the observer rolls a perception check against it, exact or hidden.
    /// Under FixCore the check is a d20 plus the observer's Wisdom against a DC from the target's Intelligence and Wisdom
    /// (<c>perception.baseDC</c>); both read streams are drawn every time (<c>enemyIntentVisibility</c> for the die,
    /// <c>enemyIntentClue</c> kept in step) so an exact success never shifts a later clue. Predictions, feedback and the bestiary
    /// (WP-11) are not here.
    /// </summary>
    public static class CoreKnowledge
    {
        /// <summary>
        /// initializeCombatKnowledge(combat, { rules, encounter, bankable, profiles }): the knowledge state of a fight and the observer's Perception track. A
        /// bankable fight is an accepted encounter: the observer enters with a receipt for each of its enemies, which it keeps for good once earned.
        /// </summary>
        public static void Initialize(CombatState c, JObject rules, JObject encounter = null, bool bankable = false, JObject profiles = null)
        {
            var state = new JObject
            {
                [CK.Version] = 1,
                [CK.Rules] = rules.DeepClone(),
                [CK.Encounter] = encounter != null ? encounter.DeepClone() : Js.Null(),
                [CK.Bankable] = bankable,
                [CK.NextSerial] = 0,
                [CK.Owners] = new JObject(),
            };
            c.Fields[CK.EnemyKnowledge] = state;
            var skills = c.Skills;
            if (skills[CK.PerceptionSkill] == null) skills[CK.PerceptionSkill] = Js.Obj(Ashen.Generated.Core2DamageKeys.Xp, 0, K.Level, 0, CK.PendingDrafts, 0);
            state.Obj(CK.Owners)[V.Player] = NewOwner(state, profiles?.Obj(V.Player));
        }

        /// <summary>newKnowledgeOwner(state, profile): an observer's profile, what the fight adds to it, what it earned and the feedback it was given.</summary>
        private static JObject NewOwner(JObject state, JObject profile)
        {
            var owner = new JObject
            {
                [CK.Knowledge] = (profile ?? Empty()).DeepClone(),
                [CK.Pending] = Empty(),
                [CK.EarnedXp] = 0,
                [CK.Feedback] = new JArray(),
                [CK.CounterEnemyIds] = new JArray(),
            };
            var encounter = state.Obj(CK.Encounter);
            if (state.Is(CK.Bankable) && encounter != null)
            {
                var entered = new JObject { [EK.Version] = 1, [EK.Enemies] = new JObject() };
                foreach (var enemyId in Js.Items(encounter[EK.EnemyIds]).Select(Js.Str))
                    entered.Obj(EK.Enemies)[enemyId] = new JObject { [EK.Target] = Js.N(CoreKnowledgeLedger.MasteryTarget(state.Obj(CK.Rules), enemyId)), [EK.Receipts] = new JObject { [encounter.Str(K.Id)] = new JObject { [EK.Bonus] = false } } };
                var accepted = CoreKnowledgeLedger.Merge(entered, owner.Obj(CK.Knowledge));
                var pending = new JObject();
                foreach (var pair in accepted.Obj(EK.Enemies).Properties())
                {
                    var row = (JObject)pair.Value;
                    if (row.Obj(EK.Receipts)[encounter.Str(K.Id)] == null) continue;
                    pending[pair.Name] = new JObject { [EK.Target] = row[EK.Target].DeepClone(), [EK.Receipts] = new JObject { [encounter.Str(K.Id)] = new JObject { [EK.Bonus] = row.Obj(EK.Receipts).Obj(encounter.Str(K.Id))[EK.Bonus].DeepClone() } } };
                }
                owner.Obj(CK.Pending)[EK.Enemies] = pending;
                owner[CK.Knowledge] = accepted;
            }
            return owner;
        }

        private static JObject Empty() => new JObject { [CK.Version] = 1, [CK.Enemies] = new JObject() };

        /// <summary>predictionCategory(intent, profile, {charging}): the maneuver the intent reads as.</summary>
        public static string Category(JObject intent, JObject profile, bool charging)
        {
            if (intent.Str(K.Kind) == V.IntentStaggered) return CV.CategoryStaggered;
            var maneuver = Js.Truthy(profile?[CK.Maneuver]) ? profile.Str(CK.Maneuver) : intent.Str(CK.Maneuver);
            var kind = intent.Str(K.Kind);
            if (maneuver == CV.StanceDefend || kind == CV.IntentBlock || kind == CV.StanceDefend) return CV.CategoryDefend;
            if (maneuver == CV.StanceCounter || kind == CV.StanceCounter) return CV.CategoryCounter;
            if (charging || kind == CV.StanceCasting) return CV.CategoryCasting;
            if (profile != null && Js.Includes(new JArray(CV.CampSpell, CV.CampSpells, CV.CampMagic), profile.Str(CK.Camp))) return CV.CategorySpell;
            if (maneuver == CV.StanceAttack) return CV.CategoryAttack;
            if (maneuver == CV.StanceSmash) return CV.CategorySmash;
            if (maneuver == CV.StanceSweep) return CV.CategorySweep;
            if (maneuver == CV.StanceRanged) return CV.CategoryRanged;
            if (kind == CV.IntentAttack || kind == CV.IntentAttackDebuff) return CV.CategoryAttack;
            return CV.CategoryPreparing;
        }

        /// <summary>broadIntentLabel(category): the clue a partial read shows.</summary>
        public static string BroadLabel(string category)
        {
            if (category == CV.CategoryAttack || category == CV.CategorySmash || category == CV.CategorySweep || category == CV.CategoryRanged) return Ashen.Generated.Core2EnemyValues.LabelAttackish;
            return category == CV.CategorySpell ? Ashen.Generated.Core2EnemyValues.LabelMagic : Ashen.Generated.Core2EnemyValues.LabelPreparing;
        }

        /// <summary>rollKnowledgeRead (FixCore branch): the d20 perception check against one enemy's action.</summary>
        private static JObject Read(CombatState c, JObject target)
        {
            if (!CoreFixCore.Enabled(c)) throw new NotSupportedException(Ashen.Generated.Core2CombatMessages.LegacyKnowledgeNotPorted);
            var cfg = CoreFixCore.Family(c, CK.Perception, c.Player);
            var die = c.CoreData.Section(CK.Perception).Num(CK.Die);
            var roll = 1 + Math.Floor(c.Rng.Float(Core2RngStream.EnemyIntentVisibility) * die);
            c.Rng.Float(Core2RngStream.EnemyIntentClue);
            var wisdom = Js.Or0(c.Attributes?[CK.AttrWisdom]);
            var bonus = Js.Items(cfg[CK.Bonuses]).Sum(v => Js.IsFinite(v) ? Js.D(v) : 0);
            var dc = cfg.Num(CK.BaseDc) + Js.Or0(target.Obj(K.Attributes)?[CK.AttrIntelligence]) + Js.Or0(target.Obj(K.Attributes)?[CK.AttrWisdom]) + bonus;
            var total = roll + wisdom;
            var passed = total >= dc;
            return new JObject
            {
                [CK.Visibility] = passed ? CV.VisibilityExact : CV.VisibilityUnknown,
                [CK.Label] = passed ? Js.Null() : Js.S(CV.LabelUnknown),
                [CK.Perception] = new JObject
                {
                    [CK.Roll] = Js.N(roll), [CK.Wisdom] = Js.N(wisdom), [CK.Dc] = Js.N(dc), [CK.Total] = Js.N(total), [CK.Passed] = passed,
                },
                [CK.Prediction] = Js.Null(),
                [CK.Resolved] = false,
                [CK.Correct] = Js.Null(),
                [CK.Credited] = false,
            };
        }

        /// <summary>feedback(combat, enemy, outcome): what became of an action the player predicted, kept for the last few.</summary>
        private static void Feedback(CombatState c, JObject enemy, string outcome)
        {
            var action = enemy.Obj(CK.KnowledgeAction);
            foreach (var pair in (action?.Obj(CK.Reads) ?? new JObject()).Properties())
            {
                var read = (JObject)pair.Value;
                if (!Js.Truthy(read[CK.Prediction])) continue;
                var owner = c.Fields.Obj(CK.EnemyKnowledge).Obj(CK.Owners).Obj(pair.Name);
                if (owner == null) continue;
                var rows = owner.Arr(CK.Feedback);
                rows.Add(new JObject
                {
                    [DK.EnemyInstanceId] = enemy[K.Id].DeepClone(), [K.EnemyId] = enemy[K.EnemyId].DeepClone(), [DK.ActionSerial] = action[CK.Serial].DeepClone(),
                    [CK.Prediction] = read[CK.Prediction].DeepClone(), [CK.Correct] = read[CK.Correct]?.DeepClone(), [EK.Outcome] = outcome,
                });
                if (rows.Count > EN.FeedbackMax) rows.RemoveAt(0);
            }
        }

        /// <summary>rollEnemyKnowledge(combat, enemy, {charging}): a new numbered action for the intent the enemy just showed.</summary>
        public static void Roll(CombatState c, JObject enemy, bool charging)
        {
            var state = c.Fields.Obj(CK.EnemyKnowledge);
            if (state == null) return;
            var previous = enemy.Obj(CK.KnowledgeAction);
            if (previous != null && !previous.Is(CK.Executed) && !previous.Is(CK.Cancelled)) Feedback(c, enemy, EV.OutcomeCancelled);
            var category = Category(enemy.Obj(K.Intent), enemy.Obj(K.Intent)?.Obj(CK.CombatProfile), charging);
            var serial = state.Num(CK.NextSerial) + 1;
            state.Put(CK.NextSerial, serial);
            var action = new JObject { [CK.Serial] = Js.N(serial), [CK.Category] = category, [CK.Executed] = false, [CK.Reads] = new JObject() };
            if (c.Player.Is(K.Alive)) action.Obj(CK.Reads)[V.Player] = Read(c, enemy);
            enemy[CK.KnowledgeAction] = action;
        }

        /// <summary>cancelKnowledgeAction(combat, enemy): the telegraphed action will not happen; every read resolves unpredicted.</summary>
        public static bool Cancel(CombatState c, JObject enemy)
        {
            var action = enemy?.Obj(CK.KnowledgeAction);
            if (c.Fields[CK.EnemyKnowledge] == null || action == null || action.Is(CK.Executed) || action.Is(CK.Cancelled)) return false;
            action[CK.Cancelled] = true;
            foreach (var read in action.Obj(CK.Reads).Properties().Select(p => p.Value).OfType<JObject>())
            {
                read[CK.Resolved] = true;
                read[CK.Correct] = Js.Null();
            }
            Feedback(c, enemy, EV.OutcomeCancelled);
            return true;
        }

        /// <summary>resolveKnowledgeAction(combat, enemy): the telegraphed action starts; every read resolves, a correct prediction pays Perception.</summary>
        public static void Resolve(CombatState c, JObject enemy)
        {
            var action = enemy?.Obj(CK.KnowledgeAction);
            if (c.Fields[CK.EnemyKnowledge] == null || action == null || action.Is(CK.Executed) || action.Is(CK.Cancelled) || !enemy.Is(K.Alive)) return;
            if (enemy.Obj(K.Intent)?.Str(K.Kind) == V.IntentStaggered) { Cancel(c, enemy); return; }
            action[CK.Executed] = true;
            foreach (var pair in action.Obj(CK.Reads).Properties().ToList())
            {
                var read = (JObject)pair.Value;
                read[CK.Resolved] = true;
                var predicted = !(read[CK.Prediction] == null || read[CK.Prediction].Type == JTokenType.Null);
                read[CK.Correct] = predicted ? (JToken)(read.Str(CK.Prediction) == action.Str(CK.Category)) : Js.Null();
                if (read.Is(CK.Correct)) PayPerception(c, enemy, pair.Name, Ashen.Generated.Core2DamageValues.ReasonPrediction);
            }
            Feedback(c, enemy, EV.OutcomeExecuted);
        }

        /// <summary>
        /// predictEnemyIntent(combat, ownerId, enemyId, serial, maneuver): the observer commits to what a telegraph it could not read will be. Refused for an
        /// action already predicted, resolved or cancelled, an enemy that is gone or staggered, a read that was exact and a category outside the choices.
        /// </summary>
        public static void Predict(CombatState c, string ownerId, string enemyId, JToken serial, string maneuver)
        {
            var enemy = c.Enemies.FirstOrDefault(row => row.Str(K.Id) == enemyId);
            var action = enemy?.Obj(CK.KnowledgeAction);
            var read = action?.Obj(CK.Reads)?.Obj(ownerId);
            var choices = new[] { CV.CategoryAttack, CV.CategorySmash, CV.CategorySweep, CV.CategoryRanged, CV.CategoryDefend, CV.CategoryCounter, CV.CategorySpell, CV.CategoryCasting, CV.CategoryPreparing };
            if (c.Fields[CK.EnemyKnowledge] == null || c.Phase != V.PhasePlayer || c.Result != null || ownerId != V.Player || !c.Player.Is(K.Alive)
                || enemy == null || !enemy.Is(K.Alive) || enemy.Obj(K.Intent)?.Str(K.Kind) == V.IntentStaggered || !Js.IsSafeInteger(serial) || Js.D(serial) < 1 || action == null || Js.D(serial) != action.Num(CK.Serial)
                || action.Is(CK.Executed) || action.Is(CK.Cancelled) || read == null || read.Str(CK.Visibility) != CV.VisibilityUnknown || read[CK.Prediction]?.Type != JTokenType.Null || read.Is(CK.Resolved)
                || !choices.Contains(maneuver))
                throw new InvalidOperationException(EM.PredictionUnavailable);
            read[CK.Prediction] = maneuver;
            c.Emit(EV.EventIntentPredictionAccepted, Js.Obj(DK.OwnerId, ownerId, DK.EnemyInstanceId, enemyId, DK.ActionSerial, serial.DeepClone(), CK.Prediction, maneuver));
        }

        /// <summary>
        /// creditKnowledgeResponse(combat, ownerId, enemyId, receipt): a response that answered a telegraph the observer could not read (an Evade that turned
        /// it, a Counter that returned it, a realized matchup) pays Perception once, and a bankable fight keeps the enemy for the encounter's ledger.
        /// </summary>
        public static bool CreditResponse(CombatState c, string ownerId, string enemyId, string kind, double amount, JToken actionSerial)
        {
            var state = c.Fields.Obj(CK.EnemyKnowledge);
            if (state == null || !(amount > 0) || (kind != EV.KindEvade && kind != EV.KindCounter && kind != EV.KindMatchup)) return false;
            var enemy = c.Enemies.FirstOrDefault(row => row.Str(K.Id) == enemyId);
            var owner = state.Obj(CK.Owners)?.Obj(ownerId);
            var action = enemy?.Obj(CK.KnowledgeAction);
            if (action?.Obj(CK.Reads)?.Obj(ownerId) == null || owner == null || action.Is(CK.Cancelled) || actionSerial == null || actionSerial.Type == JTokenType.Null || Js.D(actionSerial) != action.Num(CK.Serial)) return false;
            var paid = PayPerception(c, enemy, ownerId, Ashen.Generated.Core2DamageValues.ReasonResponse);
            if (state.Is(CK.Bankable) && state.Is(CK.Encounter) && !Js.Includes(owner[CK.CounterEnemyIds], enemy.Str(K.EnemyId)))
            {
                owner.Arr(CK.CounterEnemyIds).Add(enemy[K.EnemyId].DeepClone());
                owner[CK.Pending] = CoreKnowledgeLedger.AddCounterBonus(owner.Obj(CK.Pending), state.Obj(CK.Encounter), enemy.Str(K.EnemyId), CoreKnowledgeLedger.MasteryTarget(state.Obj(CK.Rules), enemy.Str(K.EnemyId)), owner.Obj(CK.Knowledge));
                owner[CK.Knowledge] = CoreKnowledgeLedger.Merge(owner.Obj(CK.Pending), owner.Obj(CK.Knowledge));
            }
            return paid;
        }

        /// <summary>creditKnowledgeBenefit(combat, source, target, ...): the player's own action realized a benefit against an enemy.</summary>
        public static bool CreditBenefit(CombatState c, JObject source, JObject target, double amount, string kind, JToken actionSerial)
        {
            if (source?.Str(K.Kind) != V.Player || target?.Str(K.Kind) != V.Enemy) return false;
            var serial = kind == EV.KindCounter ? actionSerial : (actionSerial == null || actionSerial.Type == JTokenType.Null ? target.Obj(CK.KnowledgeAction)?[CK.Serial] : actionSerial);
            return CreditResponse(c, V.Player, target.Str(K.Id), kind, amount, serial);
        }

        /// <summary>creditKnowledgeImpact(combat, target, amount, matchup): an executed Counter reply (or a realized authored advantage) reaches the credit.</summary>
        public static bool CreditImpact(CombatState c, JObject target, double amount, bool matchup = false)
        {
            var action = c.CurrentAction;
            var counter = action?.Meta?.CombatCounterReaction == true;
            if (!counter && !matchup) return false;
            return CreditBenefit(c, action?.Source, target, amount, counter ? EV.KindCounter : EV.KindMatchup, counter ? action.Meta.EnemyKnowledgeActionSerial : target?.Obj(CK.KnowledgeAction)?[CK.Serial]);
        }

        /// <summary>reconcileCombatKnowledge(run, combat): the bank the fight earned joins the run's pending ledger and its Perception is paid into the run, once.</summary>
        public static bool Reconcile(JObject run, CombatState c)
        {
            if (run.Obj(EK.EnemyKnowledgeState) == null || c.Fields[CK.EnemyKnowledge] == null) return false;
            var state = c.Fields.Obj(CK.EnemyKnowledge);
            var owner = state.Obj(CK.Owners).Obj(V.Player);
            if (owner == null || state.Obj(CK.Encounter) == null || state.Obj(CK.Encounter).Str(K.Id) != run.Obj(EK.EnemyKnowledgeState).Obj(EK.CurrentEncounter)?.Str(K.Id)) throw new InvalidOperationException(EM.LearningMismatch);
            run.Obj(EK.EnemyKnowledgeState)[EK.Pending] = CoreKnowledgeLedger.Merge(owner.Obj(CK.Pending), run.Obj(EK.EnemyKnowledgeState).Obj(EK.Pending));
            CoreKnowledgeLedger.ReconcilePerception(run, state.Obj(CK.Encounter).Str(K.Id), owner.Num(CK.EarnedXp));
            return true;
        }

        /// <summary>xpStepCost(curve, step): what one step of the Perception curve costs.</summary>
        public static double StepCost(JObject curve, double step)
        {
            var linear = curve.Is(Ashen.Generated.Core2DamageKeys.Linear) && curve[Ashen.Generated.Core2DamageKeys.Linear].Type == JTokenType.Boolean;
            var baseCost = curve.Num(Ashen.Generated.Core2DamageKeys.Base);
            var raw = linear ? baseCost + step * baseCost * curve.Num(Ashen.Generated.Core2DamageKeys.MultScaler) : baseCost * Math.Pow(curve.Num(Ashen.Generated.Core2DamageKeys.Growth), step);
            var roundTo = curve.Num(Ashen.Generated.Core2DamageKeys.RoundTo);
            var unit = Js.IsInt(curve[Ashen.Generated.Core2DamageKeys.RoundTo]) && roundTo > 0 ? roundTo : 1;
            return Math.Max(unit, Math.Floor(raw / unit + Ashen.Generated.CombatMath.Epsilon + Ashen.Generated.CombatMath.Half) * unit);
        }

        /// <summary>payPerception(combat, enemy, ownerId, reason): a player who failed the read is paid Perception once for the action it answered.</summary>
        public static bool PayPerception(CombatState c, JObject enemy, string ownerId, string reason)
        {
            var state = c.Fields.Obj(CK.EnemyKnowledge);
            var read = enemy.Obj(CK.KnowledgeAction)?.Obj(CK.Reads)?.Obj(ownerId);
            var owner = state?.Obj(DK.Owners)?.Obj(ownerId);
            if (read == null || read.Str(CK.Visibility) != CV.VisibilityUnknown || read.Is(CK.Credited) || c.Skills == null || owner == null) return false;
            read[CK.Credited] = true;
            var skill = c.Skills.Obj(DK.Perception);
            if (skill == null) c.Skills[DK.Perception] = skill = new JObject { [DK.Xp] = 0, [DK.Level] = 0, [DK.PendingDrafts] = 0 };
            var amount = state.Obj(CK.Rules).Obj(DK.Perception).Num(DK.CorrectPredictionXp);
            var award = CoreKnowledgeLedger.AdvancePerception(skill, state.Obj(CK.Rules).Obj(DK.Perception), amount);
            owner.Put(DK.EarnedXp, owner.Num(DK.EarnedXp) + amount);
            var payload = Js.Obj(DK.OwnerId, ownerId, DK.EnemyInstanceId, enemy[K.Id], DK.ActionSerial, enemy.Obj(CK.KnowledgeAction)[CK.Serial], K.Reason, reason);
            foreach (var p in award.Properties()) payload[p.Name] = p.Value;
            c.Emit(DK.PerceptionGained, payload);
            return true;
        }
    }
}
