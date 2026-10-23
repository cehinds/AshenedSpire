using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
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
        /// <summary>initializeCombatKnowledge(combat, {rules}): the knowledge state of a fight with no encounter bank, and the observer's Perception track.</summary>
        public static void Initialize(CombatState c, JObject rules)
        {
            var state = new JObject
            {
                [CK.Version] = 1,
                [CK.Rules] = rules.DeepClone(),
                [CK.Encounter] = Js.Null(),
                [CK.Bankable] = false,
                [CK.NextSerial] = 0,
                [CK.Owners] = new JObject(),
            };
            c.Fields[CK.EnemyKnowledge] = state;
            var skills = c.Skills;
            if (skills[CK.PerceptionSkill] == null) skills[CK.PerceptionSkill] = Js.Obj(Ashen.Generated.Core2DamageKeys.Xp, 0, K.Level, 0, CK.PendingDrafts, 0);
            state.Obj(CK.Owners)[V.Player] = new JObject
            {
                [CK.Knowledge] = Empty(),
                [CK.Pending] = Empty(),
                [CK.EarnedXp] = 0,
                [CK.Feedback] = new JArray(),
                [CK.CounterEnemyIds] = new JArray(),
            };
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

        /// <summary>rollEnemyKnowledge(combat, enemy, {charging}): a new numbered action for the intent the enemy just showed.</summary>
        public static void Roll(CombatState c, JObject enemy, bool charging)
        {
            var state = c.Fields.Obj(CK.EnemyKnowledge);
            if (state == null) return;
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
            return true;
        }

        /// <summary>resolveKnowledgeAction(combat, enemy): the telegraphed action starts; every read resolves, a correct prediction pays Perception (WP-11).</summary>
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
                var predicted = !Js.Nullish(read[CK.Prediction]);
                read[CK.Correct] = predicted ? (JToken)(read.Str(CK.Prediction) == action.Str(CK.Category)) : Js.Null();
                if (read.Is(CK.Correct)) PayPerception(c, enemy, pair.Name, Ashen.Generated.Core2DamageValues.ReasonPrediction);
            }
        }

        /// <summary>xpStepCost(curve, step): what one step of the Perception curve costs.</summary>
        private static double StepCost(JObject curve, double step)
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
            var owner = state?.Obj(Ashen.Generated.Core2DamageKeys.Owners)?.Obj(ownerId);
            if (read == null || read.Str(CK.Visibility) != CV.VisibilityUnknown || read.Is(CK.Credited) || c.Skills == null || owner == null) return false;
            read[CK.Credited] = true;
            var skill = c.Skills.Obj(Ashen.Generated.Core2DamageKeys.Perception);
            if (skill == null) c.Skills[Ashen.Generated.Core2DamageKeys.Perception] = skill = new JObject { [Ashen.Generated.Core2DamageKeys.Xp] = 0, [Ashen.Generated.Core2DamageKeys.Level] = 0, [Ashen.Generated.Core2DamageKeys.PendingDrafts] = 0 };
            var rules = state.Obj(CK.Rules).Obj(Ashen.Generated.Core2DamageKeys.Perception);
            var amount = rules.Num(Ashen.Generated.Core2DamageKeys.CorrectPredictionXp);
            var before = skill.Num(Ashen.Generated.Core2DamageKeys.Level);
            skill.Put(Ashen.Generated.Core2DamageKeys.Xp, skill.Num(Ashen.Generated.Core2DamageKeys.Xp) + amount);
            while (skill.Num(Ashen.Generated.Core2DamageKeys.Level) < rules.Num(Ashen.Generated.Core2DamageKeys.MaxLevel) && skill.Num(Ashen.Generated.Core2DamageKeys.Xp) >= StepCost(rules, skill.Num(Ashen.Generated.Core2DamageKeys.Level)))
            {
                skill.Put(Ashen.Generated.Core2DamageKeys.Xp, skill.Num(Ashen.Generated.Core2DamageKeys.Xp) - StepCost(rules, skill.Num(Ashen.Generated.Core2DamageKeys.Level)));
                skill.Put(Ashen.Generated.Core2DamageKeys.Level, skill.Num(Ashen.Generated.Core2DamageKeys.Level) + 1);
            }
            owner.Put(Ashen.Generated.Core2DamageKeys.EarnedXp, owner.Num(Ashen.Generated.Core2DamageKeys.EarnedXp) + amount);
            c.Emit(Ashen.Generated.Core2DamageKeys.PerceptionGained, Js.Obj(Ashen.Generated.Core2DamageKeys.OwnerId, ownerId, Ashen.Generated.Core2DamageKeys.EnemyInstanceId, enemy[K.Id], Ashen.Generated.Core2DamageKeys.ActionSerial, enemy.Obj(CK.KnowledgeAction)[CK.Serial],
                K.Reason, reason, Ashen.Generated.Core2DamageKeys.SkillId, Ashen.Generated.Core2DamageValues.SkillPerception, Ashen.Generated.Core2DamageKeys.Before, before, Ashen.Generated.Core2DamageKeys.After, skill[Ashen.Generated.Core2DamageKeys.Level], Ashen.Generated.Core2DamageKeys.LevelUps, skill.Num(Ashen.Generated.Core2DamageKeys.Level) - before, Ashen.Generated.Core2DamageKeys.Gained, amount));
            return true;
        }
    }
}
