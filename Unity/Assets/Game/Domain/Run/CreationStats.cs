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

        private static List<Problem> AllocationProblems(RunData data, string classId, string modeId, JToken values, string path, double granted = 0)
        {
            var problems = new List<Problem>();
            var attrs = OrderedAttributes(data);
            var mode = data.CreationModes.All.FirstOrDefault(m => m.Str(K.Id) == modeId);
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
