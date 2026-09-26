using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using F = Ashen.Generated.FormulaOps;

namespace Ashen.Domain.Combat
{
    /// <summary>Formula evaluation context (shipped formulaCtxFor): the entity refs a formula's <c>of</c> may name.</summary>
    public sealed class FormulaContext
    {
        public readonly Dictionary<string, JObject> Entities = new Dictionary<string, JObject>(StringComparer.Ordinal);
        public List<JObject> AllEnemies = new List<JObject>();
        public double EnergySpent;
        public double CardsPlayedThisTurn;
    }

    /// <summary>
    /// The structured formula evaluator (shipped model/formulas.js, SPEC §3.5): a plain number or
    /// <c>{ f: op, ... }</c> from the closed op set; optional min/max clamps on any node; floored once at the end.
    /// </summary>
    public static class Formulas
    {
        public static bool IsFormula(JToken v) => v is JObject o && Js.IsStr(o[K.F]);

        public static double Evaluate(JToken formula, FormulaContext ctx) => Math.Floor(EvaluateRaw(formula, ctx));

        public static double EvaluateRaw(JToken formula, FormulaContext ctx)
        {
            var raw = EvalNode(formula, ctx ?? new FormulaContext());
            if (double.IsNaN(raw)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.FormulaNaN, formula));
            return raw;
        }

        private static double EvalNode(JToken node, FormulaContext ctx)
        {
            if (Js.IsNum(node)) return Js.D(node);
            if (!IsFormula(node)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.NotAFormula, node));
            var o = (JObject)node;
            var op = o.Str(K.F);
            double v;
            switch (op)
            {
                case F.Add:
                    v = 0;
                    foreach (var a in ArgsOf(o)) v += EvalNode(a, ctx);
                    break;
                case F.Mul:
                    v = 1;
                    foreach (var a in ArgsOf(o)) v *= EvalNode(a, ctx);
                    break;
                case F.PercentMaxHp:
                    v = One(ctx, o).Num(K.MaxHp) * NumField(o, K.Pct) / Ashen.Generated.CombatMath.Percent;
                    break;
                case F.MissingHp:
                {
                    var ent = One(ctx, o);
                    v = Math.Max(0, ent.Num(K.MaxHp) - ent.Num(K.Hp));
                    break;
                }
                case F.MissingMana:
                {
                    var ent = One(ctx, o);
                    v = Math.Max(0, ent.Or0(K.MaxMana) - ent.Or0(K.Mana));
                    break;
                }
                case F.Stacks:
                {
                    double total = 0;
                    foreach (var ent in Many(ctx, o)) total += StacksOf(ent, o[K.Status]);
                    v = Js.Nullish(o[K.Per]) ? total : Math.Floor(total / o.Num(K.Per));
                    break;
                }
                case F.EnergySpent:
                    v = ctx.EnergySpent * Js.Coalesce(o[K.Per], 1);
                    break;
                case F.BlockOf:
                    v = One(ctx, o).Num(K.Block);
                    break;
                case F.HpOf:
                    v = One(ctx, o).Num(K.Hp);
                    break;
                case F.CardsPlayedThisTurn:
                    v = ctx.CardsPlayedThisTurn * Js.Coalesce(o[K.Per], 1);
                    break;
                default:
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnknownFormulaOp, op));
            }
            if (!Js.Nullish(o[K.Min]) && v < o.Num(K.Min)) v = o.Num(K.Min);
            if (!Js.Nullish(o[K.Max]) && v > o.Num(K.Max)) v = o.Num(K.Max);
            return v;
        }

        private static JArray ArgsOf(JObject node) =>
            node[K.Args] as JArray ?? throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.FormulaNeedsArgs, node.Str(K.F)));

        private static double NumField(JObject node, string field) =>
            Js.IsNum(node[field]) ? node.Num(field) : throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.FormulaNeedsField, node.Str(K.F), field));

        private static JObject One(FormulaContext ctx, JObject node)
        {
            var of = node.Str(K.Of);
            if (of == K.AllEnemies) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.FormulaGroupRef, node.Str(K.F), of));
            if (of == null || !ctx.Entities.TryGetValue(of, out var ent) || ent == null)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.FormulaUnresolved, node.Str(K.F), of));
            return ent;
        }

        private static IEnumerable<JObject> Many(FormulaContext ctx, JObject node)
        {
            var of = node.Str(K.Of);
            if (of == K.AllEnemies) return ctx.AllEnemies;
            return new[] { One(ctx, node) };
        }

        /// <summary>A status's stacks on an entity; meter statuses report their meter value.</summary>
        public static double StacksOf(JObject entity, JToken statusId)
        {
            var id = Js.Str(statusId) ?? throw new InvalidOperationException(M.FormulaNeedsStatus);
            var inst = entity?.Obj(K.Statuses)?.Obj(id);
            if (inst == null) return 0;
            var meter = inst.Obj(K.Meter);
            return meter != null ? meter.Num(K.Value) : inst.Or0(K.Stacks);
        }
    }
}
