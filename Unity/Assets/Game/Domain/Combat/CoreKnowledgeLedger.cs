using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DK = Ashen.Generated.Core2DamageKeys;
using EK = Ashen.Generated.Core2EnemyKeys;
using EM = Ashen.Generated.Core2EnemyMessages;
using EN = Ashen.Generated.Core2EnemyNumbers;
using EV = Ashen.Generated.Core2EnemyValues;
using K = Ashen.Generated.CombatKeys;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// What a run keeps of the player's knowledge of its enemies (web model/enemyKnowledgeProfile.js, enemyKnowledgeRun.js, enemyKnowledgeRules.js,
    /// perception.js): the ledger of receipts per enemy (one per accepted encounter, a bonus for a response that answered the enemy), the encounter a fight
    /// opens, the Perception the fight earned reconciled into the run once, and the bank a profile keeps. A Core 2 run opens none of it (a configured run
    /// carries no enemy knowledge version), so these are the pure functions the legacy ruleset and the bankable fight call, held by the oracle's units.
    /// </summary>
    public static class CoreKnowledgeLedger
    {
        private static bool Object(JToken t) => t is JObject;

        private static bool Count(JToken t) => Js.IsInt(t) && SafeInteger(t) && Js.D(t) >= 0;

        private static bool SafeInteger(JToken t) => Js.IsSafeInteger(t);

        /// <summary>knowledgeKey(key): a usable identity: a non-empty string of a bounded length that is not an object-prototype name.</summary>
        public static bool KnowledgeKey(JToken key) => Js.IsStr(key) && key.Value<string>().Length > 0 && key.Value<string>().Length <= EN.KeyLengthMax && !Forbidden(key.Value<string>());

        private static bool Forbidden(string key) => key == EV.ForbiddenProto || key == EV.ForbiddenPrototype || key == EV.ForbiddenConstructor;

        private static readonly string[] Stages = { EV.StageUnencountered, EV.StageEncountered, EV.StageStudied, EV.StageFamiliar, EV.StageUnderstood, EV.StageMastered };

        private static readonly string[] ReadKinds = { EV.KindExact, EV.KindClue };

        public static JObject Empty() => new JObject { [EK.Version] = 1, [EK.Enemies] = new JObject() };

        // ------------------------------------------------------------------ rules

        /// <summary>enemyMasteryTarget(rules, enemyId): the encounters it takes to master an enemy.</summary>
        public static double MasteryTarget(JObject rules, string enemyId)
        {
            var own = rules.Obj(EK.Bestiary)?.Obj(EK.PerEnemy)?.Obj(enemyId)?[EK.EncountersToMaster];
            return Js.Nullish(own) ? rules.Obj(EK.Bestiary).Num(EK.EncountersToMaster) : Js.D(own);
        }

        private static bool Bounded(JToken t) => Js.IsFinite(t) && Js.D(t) >= 0 && Js.D(t) <= 1;

        private static bool Target(JToken t) => Js.IsInt(t) && Js.D(t) >= EN.MasteryTargetMin && Js.D(t) <= EN.MasteryTargetMax;

        /// <summary>enemyKnowledgeRuleProblems(rules, enemyIds): why a rule set is unusable.</summary>
        public static List<string> RuleProblems(JObject rules, ISet<string> enemyIds = null)
        {
            if (rules == null || rules.Num(EK.Version) != 1 || !Object(rules[EK.Reads]) || !Object(rules[DK.Perception]) || !Object(rules[EK.Bestiary]) || !Object(rules.Obj(EK.Bestiary)[EK.PerEnemy]))
                return new List<string> { EM.RulesShape };
            var problems = new List<string>();
            var reads = rules.Obj(EK.Reads);
            foreach (var key in reads.Properties().Select(p => p.Name))
                if (!Bounded(reads[key])) problems.Add(string.Format(CultureInfo.InvariantCulture, EM.ReadCoefficient, key));
            foreach (var kind in ReadKinds)
                if (reads.Num(EV.ReadMinimum + kind) > reads.Num(EV.ReadMaximum + kind)) problems.Add(string.Format(CultureInfo.InvariantCulture, EM.ReadMinimumAbove, kind, kind));
            var curve = rules.Obj(DK.Perception);
            if (!Js.IsFinite(curve[DK.Base]) || !(curve.Num(DK.Base) > 0) || !Js.IsFinite(curve[DK.Growth]) || curve.Num(DK.Growth) < 1 || curve.Num(DK.Growth) > EN.GrowthMax
                || !Js.IsInt(curve[DK.RoundTo]) || curve.Num(DK.RoundTo) < 1 || !Js.IsInt(curve[DK.MaxLevel]) || curve.Num(DK.MaxLevel) < 1 || curve.Num(DK.MaxLevel) > EN.MaxLevelMax
                || !SafeInteger(curve[DK.CorrectPredictionXp]) || curve.Num(DK.CorrectPredictionXp) < 1) problems.Add(EM.PerceptionCurve);
            if (!SafeInteger(Js.N(CoreKnowledge.StepCost(curve, curve.Num(DK.MaxLevel) - 1)))) problems.Add(EM.PerceptionCosts);
            var bestiary = rules.Obj(EK.Bestiary);
            if (!Target(bestiary[EK.EncountersToMaster])) problems.Add(EM.MasteryTarget);
            foreach (var pair in bestiary.Obj(EK.PerEnemy).Properties())
            {
                var row = pair.Value as JObject;
                if (string.IsNullOrEmpty(pair.Name) || Forbidden(pair.Name) || (enemyIds != null && !enemyIds.Contains(pair.Name)) || row == null
                    || !Target(row[EK.EncountersToMaster]) || row.Properties().Any(p => p.Name != EK.EncountersToMaster))
                    problems.Add(string.Format(CultureInfo.InvariantCulture, EM.PerEnemyTarget, pair.Name));
            }
            return problems;
        }

        // ------------------------------------------------------------------ the ledger

        /// <summary>knowledgePoints(row): the receipts a row holds (a bonus counts twice), up to its target.</summary>
        public static double Points(JObject row)
        {
            double sum = 0;
            foreach (var receipt in (row?.Obj(EK.Receipts) ?? new JObject()).Properties().Select(p => p.Value as JObject))
                sum += 1 + (receipt != null && Js.Truthy(receipt[EK.Bonus]) ? 1 : 0);
            return Math.Min(Js.Or0(row?[EK.Target]), sum);
        }

        /// <summary>knowledgeThresholds(target): the points each stage after the first needs.</summary>
        public static double[] Thresholds(double target) => new[] { 1, Math.Ceiling(target * EN.StageTwoShare), Math.Ceiling(target * EN.StageThreeShare), Math.Ceiling(target * EN.StageFourShare), target };

        /// <summary>knowledgeProgress(row, fallbackTarget): the stage, its name, the points and what the next stage needs.</summary>
        public static JObject Progress(JObject row, double fallbackTarget)
        {
            var target = Js.Nullish(row?[EK.Target]) ? fallbackTarget : row.Num(EK.Target);
            var points = Points(row);
            var thresholds = Thresholds(target);
            var stage = thresholds.Count(value => points >= value);
            var names = Stages;
            return new JObject
            {
                [EK.Stage] = stage, [EK.Label] = names[stage], [EK.Points] = Js.N(points), [EK.Target] = Js.N(target),
                [EK.NextStage] = stage < names.Length - 1 ? new JObject { [EK.Stage] = stage + 1, [EK.Label] = names[stage + 1], [EK.Points] = Js.N(thresholds[stage]) } : Js.Null(),
            };
        }

        /// <summary>enemyKnowledgeProblems(state, enemyIds): why a ledger is unusable.</summary>
        public static List<string> Problems(JToken state, ISet<string> enemyIds = null)
        {
            var problems = new List<string>();
            if (!(state is JObject s) || s.Num(EK.Version) != 1 || !Object(s[EK.Enemies])) return new List<string> { EM.LedgerShape };
            foreach (var pair in s.Obj(EK.Enemies).Properties())
            {
                var row = pair.Value as JObject;
                if (!KnowledgeKey(Js.S(pair.Name)) || (enemyIds != null && !enemyIds.Contains(pair.Name)) || row == null || !Target(row[EK.Target]) || !Object(row[EK.Receipts]))
                {
                    problems.Add(string.Format(CultureInfo.InvariantCulture, EM.LedgerRow, pair.Name));
                    continue;
                }
                var entries = row.Obj(EK.Receipts).Properties().ToList();
                if (entries.Count == 0 || entries.Count > row.Num(EK.Target)) problems.Add(string.Format(CultureInfo.InvariantCulture, EM.LedgerBounds, pair.Name));
                foreach (var entry in entries)
                    if (!KnowledgeKey(Js.S(entry.Name)) || !(entry.Value is JObject receipt) || receipt[EK.Bonus]?.Type != JTokenType.Boolean || receipt.Properties().Any(p => p.Name != EK.Bonus))
                        problems.Add(string.Format(CultureInfo.InvariantCulture, EM.LedgerReceipt, pair.Name));
            }
            return problems;
        }

        /// <summary>mergeEnemyKnowledge(incoming, current): the receipts of both, a bonus kept once earned, new encounters accepted until the target is funded.</summary>
        public static JObject Merge(JObject incoming, JObject current)
        {
            foreach (var state in new[] { incoming, current })
            {
                var problems = Problems(state);
                if (problems.Count > 0) throw new InvalidOperationException(string.Join(EM.ProblemSeparator, problems));
            }
            var next = (JObject)current.DeepClone();
            var enemies = next.Obj(EK.Enemies);
            foreach (var pair in incoming.Obj(EK.Enemies).Properties())
            {
                var earned = (JObject)pair.Value;
                if (enemies[pair.Name] == null) enemies[pair.Name] = new JObject { [EK.Target] = earned[EK.Target].DeepClone(), [EK.Receipts] = new JObject() };
                var row = enemies.Obj(pair.Name);
                var receipts = row.Obj(EK.Receipts);
                foreach (var receipt in earned.Obj(EK.Receipts).Properties())
                    if (receipts[receipt.Name] != null) receipts.Obj(receipt.Name)[EK.Bonus] = receipts.Obj(receipt.Name).Is(EK.Bonus) || ((JObject)receipt.Value).Is(EK.Bonus);
                foreach (var receipt in earned.Obj(EK.Receipts).Properties())
                {
                    if (Points(row) >= row.Num(EK.Target)) break;
                    if (receipts[receipt.Name] == null) receipts[receipt.Name] = new JObject { [EK.Bonus] = ((JObject)receipt.Value)[EK.Bonus].DeepClone() };
                }
            }
            return next;
        }

        /// <summary>bankEnemyKnowledgeProfile(meta, receipts): the profile with the receipts merged in, and whether that changed it.</summary>
        public static JObject Bank(JObject meta, JObject receipts)
        {
            var current = meta.Obj(EK.EnemyKnowledge) ?? Empty();
            var merged = Merge(receipts, current);
            var next = Js.Spread(meta);
            next[EK.EnemyKnowledge] = merged;
            return new JObject { [EK.Meta] = next, [EK.Changed] = current.ToString(Formatting.None) != merged.ToString(Formatting.None) };
        }

        // ------------------------------------------------------------------ Perception

        /// <summary>advancePerception(row, curve, amount): the XP added, the levels it pays.</summary>
        public static JObject AdvancePerception(JObject row, JObject curve, double amount)
        {
            var gained = SafeInteger(Js.N(amount)) && amount > 0 ? amount : 0;
            if (!SafeInteger(Js.N(row.Num(DK.Xp) + gained))) throw new InvalidOperationException(EM.PerceptionOverflow);
            var before = row.Num(DK.Level);
            row.Put(DK.Xp, row.Num(DK.Xp) + gained);
            while (row.Num(DK.Level) < curve.Num(DK.MaxLevel) && row.Num(DK.Xp) >= CoreKnowledge.StepCost(curve, row.Num(DK.Level)))
            {
                row.Put(DK.Xp, row.Num(DK.Xp) - CoreKnowledge.StepCost(curve, row.Num(DK.Level)));
                row.Put(DK.Level, row.Num(DK.Level) + 1);
            }
            return new JObject { [DK.SkillId] = EV.SkillPerception, [DK.Before] = Js.N(before), [DK.After] = Js.N(row.Num(DK.Level)), [DK.LevelUps] = Js.N(row.Num(DK.Level) - before), [DK.Gained] = Js.N(gained) };
        }

        /// <summary>perceptionProblems(row, curve): why a Perception row is unusable.</summary>
        public static List<string> PerceptionProblems(JObject row, JObject curve)
        {
            if (row == null || !SafeInteger(row[DK.Xp]) || row.Num(DK.Xp) < 0 || !Js.IsInt(row[DK.Level]) || row.Num(DK.Level) < 0 || row.Num(DK.Level) > curve.Num(DK.MaxLevel)
                || row.Num(DK.PendingDrafts) != 0 || row.Properties().Any(p => p.Name != DK.Xp && p.Name != DK.Level && p.Name != DK.PendingDrafts))
                return new List<string> { EM.PerceptionRow };
            if (row.Num(DK.Level) < curve.Num(DK.MaxLevel) && row.Num(DK.Xp) >= CoreKnowledge.StepCost(curve, row.Num(DK.Level))) return new List<string> { EM.PerceptionUnclaimed };
            return new List<string>();
        }

        // ------------------------------------------------------------------ the run

        /// <summary>knowledgeEncounterId(receiptId, visit, nodeId, encounterId): the receipt an accepted encounter is known by.</summary>
        public static string EncounterId(string receiptId, double visit, string nodeId, string encounterId) =>
            EV.EncounterPrefix + new JArray(Js.Truthy(Js.S(receiptId)) ? receiptId : EV.SandboxReceipt, Js.N(visit), nodeId, encounterId).ToString(Formatting.None);

        /// <summary>openRunEnemyKnowledge(run, { receiptId, bankable }): the run's learning state and Perception track. False for a run with no knowledge rules.</summary>
        public static bool OpenRun(JObject run, string receiptId, bool bankable)
        {
            if (run.Obj(EK.EnemyKnowledgeRules)?.Num(EK.Version) != 1) return false;
            if (bankable && (!KnowledgeKey(Js.S(receiptId)) || receiptId.Length > EN.ReceiptLengthMax)) throw new InvalidOperationException(EM.ReceiptRequired);
            run[EK.EnemyKnowledgeState] = new JObject
            {
                [EK.Version] = 1, [EK.ReceiptId] = Js.S(receiptId), [EK.Bankable] = bankable, [EK.VisitOrdinal] = 0, [EK.CurrentEncounter] = Js.Null(),
                [EK.Pending] = Empty(), [EK.RecoveryTargets] = new JObject(), [EK.AppliedXp] = new JObject(),
            };
            if (run[K.Skills] == null) run[K.Skills] = new JObject();
            run.Obj(K.Skills)[DK.Perception] = new JObject { [DK.Xp] = 0, [DK.Level] = 0, [DK.PendingDrafts] = 0 };
            return true;
        }

        /// <summary>openKnowledgeEncounter(run, nodeId, encounterId, enemyIds, current): the encounter a fight is accepted as, with the receipts it can earn.</summary>
        public static JObject OpenEncounter(JObject run, string nodeId, string encounterId, IReadOnlyList<string> enemyIds, JObject current = null)
        {
            var state = run.Obj(EK.EnemyKnowledgeState);
            if (state == null) return null;
            current ??= Empty();
            if (!KnowledgeKey(Js.S(nodeId)) || !KnowledgeKey(Js.S(encounterId)) || enemyIds == null || enemyIds.Count == 0 || enemyIds.Any(id => !KnowledgeKey(Js.S(id)))) throw new InvalidOperationException(EM.EncounterRequired);
            var visit = state.Num(EK.VisitOrdinal) + 1;
            if (!Count(Js.N(visit))) throw new InvalidOperationException(EM.VisitOverflow);
            var id = EncounterId(state.Str(EK.ReceiptId), visit, nodeId, encounterId);
            if (!KnowledgeKey(Js.S(id))) throw new InvalidOperationException(EM.EncounterIdLength);
            var distinct = enemyIds.Distinct().ToList();
            var encounter = new JObject { [K.Id] = id, [EK.NodeId] = nodeId, [EK.EncounterId] = encounterId, [EK.Visit] = Js.N(visit), [EK.EnemyIds] = new JArray(distinct) };
            var pending = state.Obj(EK.Pending);
            var rules = run.Obj(EK.EnemyKnowledgeRules);
            if (state.Is(EK.Bankable))
            {
                var earned = new JObject { [EK.Version] = 1, [EK.Enemies] = new JObject() };
                foreach (var enemyId in distinct)
                    earned.Obj(EK.Enemies)[enemyId] = new JObject { [EK.Target] = Js.N(MasteryTarget(rules, enemyId)), [EK.Receipts] = new JObject { [id] = new JObject { [EK.Bonus] = false } } };
                var funded = Merge(pending, current);
                var accepted = Merge(earned, funded);
                foreach (var enemyId in distinct)
                    if (accepted.Obj(EK.Enemies).Obj(enemyId).Obj(EK.Receipts)[id] != null)
                        pending = Merge(new JObject
                        {
                            [EK.Version] = 1,
                            [EK.Enemies] = new JObject { [enemyId] = new JObject { [EK.Target] = accepted.Obj(EK.Enemies).Obj(enemyId)[EK.Target].DeepClone(), [EK.Receipts] = new JObject { [id] = new JObject { [EK.Bonus] = false } } } },
                        }, pending);
            }
            state[EK.VisitOrdinal] = Js.N(visit);
            state[EK.CurrentEncounter] = encounter;
            state[EK.Pending] = pending;
            return encounter;
        }

        /// <summary>addKnowledgeCounterBonus(pending, encounter, enemyId, target, current): the response that answered the enemy earns the encounter's receipt its bonus.</summary>
        public static JObject AddCounterBonus(JObject pending, JObject encounter, string enemyId, double target, JObject current = null)
        {
            if (!Js.Items(encounter?[EK.EnemyIds]).Select(Js.Str).Contains(enemyId)) return pending;
            var funded = Merge(pending, current ?? Empty());
            var row = funded.Obj(EK.Enemies).Obj(enemyId);
            if (row != null && Points(row) >= row.Num(EK.Target) && row.Obj(EK.Receipts)[encounter.Str(K.Id)] == null) return pending;
            return Merge(new JObject
            {
                [EK.Version] = 1,
                [EK.Enemies] = new JObject { [enemyId] = new JObject { [EK.Target] = Js.N(row != null && Js.Truthy(row[EK.Target]) ? row.Num(EK.Target) : target), [EK.Receipts] = new JObject { [encounter.Str(K.Id)] = new JObject { [EK.Bonus] = true } } } },
            }, pending);
        }

        /// <summary>reconcilePerception(run, encounterId, earnedXp): the Perception a fight earned is paid into the run once, whatever is retried.</summary>
        public static JObject ReconcilePerception(JObject run, string encounterId, double earnedXp)
        {
            var state = run.Obj(EK.EnemyKnowledgeState);
            if (state == null || !KnowledgeKey(Js.S(encounterId)) || !Count(Js.N(earnedXp))) throw new InvalidOperationException(EM.ReconcileIdentity);
            var applied = state.Obj(EK.AppliedXp);
            var paid = applied[encounterId] != null ? applied.Num(encounterId) : 0;
            var delta = Math.Max(0, earnedXp - paid);
            var award = AdvancePerception(run.Obj(K.Skills).Obj(DK.Perception), run.Obj(EK.EnemyKnowledgeRules).Obj(DK.Perception), delta);
            applied[encounterId] = Js.N(Math.Max(paid, earnedXp));
            return award;
        }

        /// <summary>acknowledgeKnowledgeBank(run, captured): what a successful write banked is no longer pending; a bonus earned meanwhile stays queued.</summary>
        public static void AcknowledgeBank(JObject run, JObject captured)
        {
            var state = run.Obj(EK.EnemyKnowledgeState);
            var pending = state?.Obj(EK.Pending);
            if (pending == null) return;
            foreach (var pair in captured.Obj(EK.Enemies).Properties())
            {
                var live = pending.Obj(EK.Enemies).Obj(pair.Name);
                if (live == null) continue;
                foreach (var receipt in ((JObject)pair.Value).Obj(EK.Receipts).Properties().ToList())
                {
                    var held = live.Obj(EK.Receipts)[receipt.Name] as JObject;
                    if (held != null && (((JObject)receipt.Value).Is(EK.Bonus) || !held.Is(EK.Bonus))) live.Obj(EK.Receipts).Remove(receipt.Name);
                }
                if (!live.Obj(EK.Receipts).Properties().Any())
                {
                    pending.Obj(EK.Enemies).Remove(pair.Name);
                    state.Obj(EK.RecoveryTargets)?.Remove(pair.Name);
                }
            }
        }

        /// <summary>enemyKnowledgeRunProblems(run, enemyIds): why a run's learning state is unusable.</summary>
        public static List<string> RunProblems(JObject run, ISet<string> enemyIds = null)
        {
            if (run[EK.EnemyKnowledgeRules] == null && run[EK.EnemyKnowledgeState] == null) return new List<string>();
            var problems = RuleProblems(run.Obj(EK.EnemyKnowledgeRules), enemyIds);
            var state = run.Obj(EK.EnemyKnowledgeState);
            if (state == null || state.Num(EK.Version) != 1 || state[EK.Bankable]?.Type != JTokenType.Boolean || !Count(state[EK.VisitOrdinal])
                || (state.Is(EK.Bankable) && (!KnowledgeKey(state[EK.ReceiptId]) || state.Str(EK.ReceiptId).Length > EN.ReceiptLengthMax)) || !Object(state[EK.AppliedXp])) { problems.Add(EM.RunState); return problems; }
            problems.AddRange(Problems(state[EK.Pending], enemyIds));
            if (state[EK.RecoveryTargets] != null)
            {
                if (!Object(state[EK.RecoveryTargets])) problems.Add(EM.RecoveryLedger);
                else
                    foreach (var pair in state.Obj(EK.RecoveryTargets).Properties())
                        if (!KnowledgeKey(Js.S(pair.Name)) || (enemyIds != null && !enemyIds.Contains(pair.Name)) || !Js.IsInt(pair.Value) || pair.Value.Value<double>() < EN.MasteryTargetMin || pair.Value.Value<double>() > EN.MasteryTargetMax
                            || state.Obj(EK.Pending)?.Obj(EK.Enemies)?.Obj(pair.Name)?[EK.Target] == null || state.Obj(EK.Pending).Obj(EK.Enemies).Obj(pair.Name).Num(EK.Target) != pair.Value.Value<double>())
                            problems.Add(EM.RecoveryTarget);
            }
            foreach (var pair in state.Obj(EK.AppliedXp).Properties())
                if (!KnowledgeKey(Js.S(pair.Name)) || !Count(pair.Value)) problems.Add(EM.AppliedXp);
            var encounter = state[EK.CurrentEncounter];
            if (encounter != null && encounter.Type != JTokenType.Null)
            {
                var e = encounter as JObject;
                var ids = Js.Items(e?[EK.EnemyIds]).Select(Js.Str).ToList();
                if (e == null || !KnowledgeKey(e[K.Id]) || !KnowledgeKey(e[EK.NodeId]) || !KnowledgeKey(e[EK.EncounterId]) || !Count(e[EK.Visit]) || e.Num(EK.Visit) < 1 || e.Num(EK.Visit) > state.Num(EK.VisitOrdinal)
                    || !(e[EK.EnemyIds] is JArray) || ids.Count == 0 || ids.Distinct().Count() != ids.Count || ids.Any(id => !KnowledgeKey(Js.S(id)) || (enemyIds != null && !enemyIds.Contains(id)))
                    || e.Str(K.Id) != EncounterId(state.Str(EK.ReceiptId), e.Num(EK.Visit), e.Str(EK.NodeId), e.Str(EK.EncounterId))) problems.Add(EM.CurrentEncounter);
            }
            if (problems.Count == 0) problems.AddRange(PerceptionProblems(run.Obj(K.Skills)?.Obj(DK.Perception), run.Obj(EK.EnemyKnowledgeRules).Obj(DK.Perception)));
            return problems;
        }
    }
}
