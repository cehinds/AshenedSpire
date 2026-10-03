using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RM = Ashen.Generated.RunMessages;
using RV = Ashen.Generated.RunValues;

namespace Ashen.Domain.Run
{
    /// <summary>One named allocation problem (the shipped { path, msg }).</summary>
    public sealed class Problem
    {
        public Problem(string path, string msg)
        {
            Path = path;
            Msg = msg;
        }

        public string Path { get; }
        public string Msg { get; }

        /// <summary><c>problems.map((p) => `${p.path}: ${p.msg}`).join('; ')</c>.</summary>
        public static string Join(IEnumerable<Problem> problems) => string.Join(RV.ProblemJoiner, problems.Select(p => RunJs.Fmt(RM.PathMessage, p.Path, p.Msg)));
    }

    /// <summary>
    /// The creation-stat reader (shipped model/attributes.js): creation modes, the class presets and the allocation
    /// rules a preset must satisfy.
    /// </summary>
    public static class CreationStats
    {
        /// <summary>orderedAttributes: the attribute rows by their authored order (stable).</summary>
        public static List<JObject> OrderedAttributes(RunData data) =>
            data.Attributes.All.OrderBy(a => a.Num(RK.Order)).ToList();

        public static string DefaultCreationModeId(RunData data)
        {
            var id = data.AttributeRules?[RK.DefaultMode];
            if (!Js.IsStr(id) || !data.CreationModes.All.Any(m => m.Str(K.Id) == Js.Str(id)))
                throw new InvalidOperationException(RunJs.Fmt(RM.DefaultModeUnresolved, RunJs.Show(id)));
            return Js.Str(id);
        }

        public static JObject CreationMode(RunData data, string modeId)
        {
            var mode = data.CreationModes.All.FirstOrDefault(m => m.Str(K.Id) == modeId);
            if (mode == null) throw new InvalidOperationException(RunJs.Fmt(RM.UnknownCreationMode, modeId));
            return mode;
        }

        public static JObject CreationModeSnapshot(RunData data, string modeId) => RunJs.Clone(CreationMode(data, modeId));

        private static JObject RetiredNames(RunData data) => data.AttributeRules?.Obj(RK.Retired) ?? new JObject();

        private static List<Problem> AllocationProblems(RunData data, string classId, string modeId, JToken values, string path, double granted = 0, JToken modeSnapshot = null)
        {
            var problems = new List<Problem>();
            var attrs = OrderedAttributes(data);
            var mode = Js.Truthy(modeSnapshot) ? modeSnapshot as JObject : data.CreationModes.All.FirstOrDefault(m => m.Str(K.Id) == modeId);
            if (Js.Truthy(modeSnapshot) && (!(modeSnapshot is JObject snap) || snap.Str(K.Id) != modeId))
                problems.Add(new Problem(RK.AttributeModeSnapshot, RunJs.Fmt(RM.ModeSnapshotMismatch, modeId)));
            if (mode == null) problems.Add(new Problem(RK.AttributeMode, RunJs.Fmt(RM.UnknownCreationModeProblem, modeId)));
            if (!data.Classes.All.Any(c => c.Str(K.Id) == classId)) problems.Add(new Problem(RK.Class, RunJs.Fmt(RM.UnknownClassProblem, classId)));
            if (!(values is JObject cells))
            {
                problems.Add(new Problem(path, RM.AllocationNotObject));
                return problems;
            }
            var ids = attrs.Select(a => a[K.Id]).Where(Js.IsStr).Select(Js.Str).ToList();
            var retired = RetiredNames(data);
            foreach (var id in ids)
                if (cells[id] == null) problems.Add(new Problem(path + RV.PathDot + id, RM.MissingAttributeCell));
            foreach (var p in cells.Properties())
            {
                if (ids.Contains(p.Name)) continue;
                problems.Add(new Problem(path + RV.PathDot + p.Name, retired[p.Name] != null
                    ? RunJs.Fmt(RM.RetiredAttribute, p.Name, RunJs.Show(retired[p.Name]))
                    : RunJs.Fmt(RM.UnknownAttribute, p.Name)));
            }
            if (mode == null) return problems;
            double total = 0;
            var allIntegers = true;
            foreach (var id in ids)
            {
                var value = cells[id];
                if (!Js.IsInt(value))
                {
                    allIntegers = false;
                    problems.Add(new Problem(path + RV.PathDot + id, RM.MustBeInteger));
                    continue;
                }
                var floor = mode.Str(RK.BelowBaseline) == RV.Forbid ? Math.Max(mode.Num(K.Minimum), mode.Num(K.Baseline)) : mode.Num(K.Minimum);
                var ceiling = mode.Num(K.Maximum) + granted;
                var v = Js.D(value);
                if (v < floor || v > ceiling)
                    problems.Add(new Problem(path + RV.PathDot + id, granted != 0
                        ? RunJs.Fmt(RM.CellOutOfRangeGranted, RunJs.NumStr(floor), RunJs.NumStr(ceiling), mode.Str(K.Id), RunJs.NumStr(granted))
                        : RunJs.Fmt(RM.CellOutOfRange, RunJs.NumStr(floor), RunJs.NumStr(ceiling), mode.Str(K.Id))));
                total += v;
            }
            if (allIntegers && mode.Str(RK.Redistribution) == RV.FixedTotal)
            {
                var expected = mode.Num(K.Baseline) * ids.Count + mode.Num(RK.BonusPool) + granted;
                if (total != expected)
                    problems.Add(new Problem(path, granted != 0
                        ? RunJs.Fmt(RM.TotalMismatchGranted, RunJs.NumStr(total), RunJs.NumStr(expected), mode.Str(K.Id), RunJs.NumStr(granted))
                        : RunJs.Fmt(RM.TotalMismatch, RunJs.NumStr(total), RunJs.NumStr(expected), mode.Str(K.Id))));
            }
            return problems;
        }

        /// <summary>grantedAttributePoints(run): the attribute points a run's levels granted (levelPoints, else the legacy levelUps).</summary>
        public static double GrantedAttributePoints(JObject run)
        {
            if (Js.IsInt(run?[RK.LevelPoints])) return Math.Max(0, run.Num(RK.LevelPoints));
            return Js.IsInt(run?[RK.LevelUps]) ? Math.Max(0, run.Num(RK.LevelUps)) : 0;
        }

        /// <summary>
        /// migrateRetiredAttributeNames(run, source): carry every retired attribute id (attributeRules.retired) to its
        /// heir, in the allocation and in the rule snapshot's sourceStat rows; a run holding both refuses by name.
        /// </summary>
        public static void MigrateRetiredAttributeNames(RunData data, JObject run)
        {
            foreach (var p in RetiredNames(data).Properties())
            {
                var dead = p.Name;
                var heir = Js.Str(p.Value) ?? RunJs.Key(p.Value);
                var rules = run.Obj(K.DerivedStatRuleSnapshot)?.Obj(K.Rules)?.Obj(K.Rules);
                var attributes = run.Obj(K.Attributes);
                var allocationDead = attributes != null && attributes[dead] != null;
                var allocationHeir = attributes != null && attributes[heir] != null;
                var rows = rules != null ? rules.Properties().Where(r => r.Value is JObject).ToList() : new List<JProperty>();
                var deadPaths = new List<string>();
                if (allocationDead) deadPaths.Add(K.Attributes + RV.PathDot + dead);
                deadPaths.AddRange(rows.Where(r => ((JObject)r.Value).Str(RK.SourceStat) == dead).Select(r => RunJs.Fmt(RM.SnapshotSourceStatPath, r.Name)));
                var heirPaths = new List<string>();
                if (allocationHeir) heirPaths.Add(K.Attributes + RV.PathDot + heir);
                heirPaths.AddRange(rows.Where(r => ((JObject)r.Value).Str(RK.SourceStat) == heir).Select(r => RunJs.Fmt(RM.SnapshotSourceStatPath, r.Name)));
                if (deadPaths.Count > 0 && heirPaths.Count > 0)
                    throw new InvalidOperationException(RunJs.Fmt(RM.MixedRetiredAttribute, dead, heir, string.Join(RV.ListJoiner, deadPaths.Concat(heirPaths))));
                if (allocationDead)
                {
                    attributes[heir] = attributes[dead].DeepClone();
                    attributes.Remove(dead);
                }
                foreach (var r in rows)
                    if (((JObject)r.Value).Str(RK.SourceStat) == dead) ((JObject)r.Value)[RK.SourceStat] = heir;
            }
        }

        /// <summary>
        /// normalizeRunAttributes(run, registries): the allocation door (a custom creation allocation and the load door
        /// share it). Mode and values are both present or both absent (absent refills the class preset), retired ids
        /// are carried to their heirs, the mode snapshot is captured once, and the allocation must satisfy the mode (with
        /// the points the run's levels granted); the result is written in attribute order.
        /// </summary>
        public static JObject NormalizeRunAttributes(RunData data, JObject run)
        {
            var modeAbsent = run[RK.AttributeMode] == null;
            var valuesAbsent = run[K.Attributes] == null;
            if (modeAbsent != valuesAbsent) throw new InvalidOperationException(RM.ModeAndAttributesTogether);
            if (modeAbsent && run[RK.AttributeModeSnapshot] != null) throw new InvalidOperationException(RM.ModeSnapshotNeedsMode);
            MigrateRetiredAttributeNames(data, run);
            if (modeAbsent)
            {
                run[RK.AttributeMode] = DefaultCreationModeId(data);
                run[K.Attributes] = ClassAttributePreset(data, run.Str(RK.Class), run.Str(RK.AttributeMode));
                run[RK.AttributeModeSnapshot] = CreationModeSnapshot(data, run.Str(RK.AttributeMode));
                return run;
            }
            if (run[RK.AttributeModeSnapshot] == null) run[RK.AttributeModeSnapshot] = CreationModeSnapshot(data, Js.Str(run[RK.AttributeMode]) ?? RunJs.Key(run[RK.AttributeMode]));
            var problems = AllocationProblems(data, run.Str(RK.Class), Js.Str(run[RK.AttributeMode]) ?? RunJs.Key(run[RK.AttributeMode]), run[K.Attributes], K.Attributes,
                GrantedAttributePoints(run), run[RK.AttributeModeSnapshot]);
            if (problems.Count > 0) throw new InvalidOperationException(Problem.Join(problems));
            var ordered = new JObject();
            foreach (var def in OrderedAttributes(data))
                if (run.Obj(K.Attributes)[def.Str(K.Id)] != null) ordered[def.Str(K.Id)] = run.Obj(K.Attributes)[def.Str(K.Id)].DeepClone();
            run[K.Attributes] = ordered;
            return run;
        }

        /// <summary>classAttributePreset(source, classId, modeId): the class's authored allocation, validated.</summary>
        public static JObject ClassAttributePreset(RunData data, string classId, string modeId)
        {
            var values = data.AttributeRules?.Obj(RK.Presets)?.Obj(modeId)?[classId];
            var path = string.Join(RV.PathDot, RK.AttributeRules, RK.Presets, modeId, classId);
            var problems = AllocationProblems(data, classId, modeId, values, path);
            if (problems.Count > 0) throw new InvalidOperationException(Problem.Join(problems));
            var result = new JObject();
            foreach (var def in OrderedAttributes(data)) result[def.Str(K.Id)] = values[def.Str(K.Id)]?.DeepClone();
            return result;
        }
    }
}
