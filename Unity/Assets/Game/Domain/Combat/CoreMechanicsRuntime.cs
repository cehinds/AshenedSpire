using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using DK = Ashen.Generated.Core2DamageKeys;
using DM = Ashen.Generated.Core2DamageMessages;
using DN = Ashen.Generated.Core2DamageNumbers;
using DV = Ashen.Generated.Core2DamageValues;

namespace Ashen.Domain.Combat
{
    /// <summary>What a mechanic case produced: the state after, the events raised in order and the value a binding or modifier case read.</summary>
    public sealed class MechanicCaseResult
    {
        public bool Ok;
        public string Error;
        public JObject State;
        public List<string> Events = new List<string>();
        public JToken Value;
    }

    /// <summary>
    /// The portable-op subset of the mechanics runtime the mechanic cases use (web engine/runtime/evaluator.mjs; engine/runtime/SPEC.md): plain-JSON state, a
    /// command or event entering the handlers of the loaded mechanics, the steps (<c>op</c>, <c>if</c>, <c>forEach</c>, <c>emit</c>, <c>call</c>, <c>set</c>), values with
    /// <c>$</c> references and formulas, conditions, and the ops that need no seeded random draw. Core 2's behaviour lives in the combat engine, not here; this is the
    /// reference the cases of <c>engine/mechanics/&#42;/cases.json</c> run on, so a mechanic whose case needs more than the subset fails by name instead of passing wrongly.
    /// </summary>
    public sealed class CoreMechanicsRuntime
    {

        private static HashSet<string> Set(params string[] items) => new HashSet<string>(items, StringComparer.Ordinal);

        private static readonly string[] EntityDefaults = { DK.Hp, DK.MaxHp, DK.Block, DK.Poise, DK.Sp, DK.Mp };
        private static readonly string[] Piles = { DV.PileDraw, DV.PileHand, DV.PileDiscard, DV.PileExhaust };
        private static readonly HashSet<string> ConditionKeys = Set(DV.CondAll, DV.CondAny, DV.CondNot, DV.CondCmp, DV.CondHasTag, DV.CondHasStatus);
        private static readonly HashSet<string> ThenKeys = Set(DK.Entities, DK.Piles, DK.Mechanics, DK.Events, DK.Value, DK.Error);

        private readonly JObject _mechanics;
        private readonly JObject _ops;

        public CoreMechanicsRuntime(JObject mechanics, JObject ops)
        {
            _mechanics = mechanics ?? throw new ArgumentNullException(nameof(mechanics));
            _ops = ops ?? throw new ArgumentNullException(nameof(ops));
        }

        private sealed class Run
        {
            public JObject State;
            public List<(string Name, JObject Payload)> Events = new List<(string, JObject)>();
            public double Depth;
        }

        private sealed class Scope
        {
            public string Slug;
            public JObject Vars = new JObject();
        }

        // ------------------------------------------------------------------ state

        private static double Whole(double n) => Math.Max(0, Math.Floor(n));

        /// <summary>createState(partial): a copy with the defaults of SPEC section 1 filled in.</summary>
        public static JObject CreateState(JObject partial)
        {
            var state = (JObject)(partial ?? new JObject()).DeepClone();
            var entities = state.Obj(DK.Entities) ?? (JObject)(state[DK.Entities] = new JObject());
            foreach (var p in entities.Properties())
            {
                var entity = (JObject)p.Value;
                foreach (var key in EntityDefaults) if (Js.Nullish(entity[key])) entity[key] = 0;
                if (entity[DK.Statuses] == null) entity[DK.Statuses] = new JObject();
                if (entity[DK.Tags] == null) entity[DK.Tags] = new JArray();
            }
            var piles = state.Obj(DK.Piles) ?? (JObject)(state[DK.Piles] = new JObject());
            foreach (var pile in Piles) if (piles[pile] == null) piles[pile] = new JArray();
            if (state[DK.Mechanics] == null) state[DK.Mechanics] = new JObject();
            return state;
        }

        private static JObject SchemaDefaults(JObject schema)
        {
            var result = new JObject();
            var properties = schema?.Obj(DK.Properties);
            if (properties == null) return result;
            foreach (var p in properties.Properties())
            {
                var prop = p.Value as JObject;
                if (prop == null) continue;
                if (prop[DK.Default] != null) result[p.Name] = prop[DK.Default].DeepClone();
                else if (prop.Obj(DK.Properties) != null)
                {
                    var nested = SchemaDefaults(prop);
                    if (nested.Count > 0) result[p.Name] = nested;
                }
            }
            return result;
        }

        private static JToken MergeDeep(JToken baseValue, JToken over)
        {
            if (!(baseValue is JObject b) || !(over is JObject o)) return over ?? baseValue;
            var result = (JObject)b.DeepClone();
            foreach (var p in o.Properties()) result[p.Name] = b[p.Name] != null ? MergeDeep(b[p.Name], p.Value) : p.Value.DeepClone();
            return result;
        }

        private JObject Mechanic(string slug) => _mechanics.Obj(slug);

        // ------------------------------------------------------------------ values

        private JToken Lookup(Run rt, Scope scope, string reference)
        {
            var path = reference.Substring(DV.ReferencePrefix.Length).Split(new[] { DV.ReferenceSeparator }, StringSplitOptions.None);
            JToken cur;
            if (path[0] == DK.State) cur = rt.State.Obj(DK.Mechanics)?[scope.Slug];
            else if (path[0] == DK.Config) cur = null;
            else if (path[0] == DK.Model) cur = MergeDeep(SchemaDefaults(Mechanic(scope.Slug)?.Obj(DK.ModelSchema)), rt.State.Obj(DK.Mechanics)?[scope.Slug] ?? new JObject());
            else
            {
                if (scope.Vars[path[0]] == null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.UnresolvedNamed, reference));
                cur = scope.Vars[path[0]];
                foreach (var key in path.Skip(1))
                {
                    if (cur == null || cur.Type == JTokenType.Null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.UnresolvedNamed, reference));
                    cur = Js.IsStr(cur) && rt.State.Obj(DK.Entities)?[Js.Str(cur)] is JObject entity ? entity[key] : (cur as JObject)?[key];
                }
                if (cur == null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.UnresolvedNamed, reference));
                return cur;
            }
            foreach (var key in path.Skip(1)) cur = cur == null || cur.Type == JTokenType.Null ? null : (cur as JObject)?[key];
            if (cur == null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.UnresolvedNamed, reference));
            return cur;
        }

        private JToken Substitute(Run rt, Scope scope, JToken node)
        {
            if (Js.IsStr(node)) return Js.Str(node).StartsWith(DV.ReferencePrefix, StringComparison.Ordinal) ? Lookup(rt, scope, Js.Str(node)) : node;
            if (node is JArray a) return new JArray(a.Select(x => Substitute(rt, scope, x)));
            if (node is JObject o)
            {
                var result = new JObject();
                foreach (var p in o.Properties()) result[p.Name] = Substitute(rt, scope, p.Value);
                return result;
            }
            return node;
        }

        private FormulaContext FormulaContextFor(Run rt, Scope scope)
        {
            var ctx = new FormulaContext();
            var entities = rt.State.Obj(DK.Entities);
            JObject View(JObject entity)
            {
                var statuses = new JObject();
                foreach (var s in entity.Obj(DK.Statuses).Properties()) statuses[s.Name] = new JObject { [DK.Stacks] = s.Value };
                return Js.Obj(DK.Hp, entity[DK.Hp], DK.MaxHp, entity[DK.MaxHp], DK.Block, entity[DK.Block], DK.Mana, entity[DK.Mp],
                    DK.MaxMana, Js.Nullish(entity[DK.MpMax]) ? entity[DK.Mp] : entity[DK.MpMax], DK.Statuses, statuses);
            }
            foreach (var p in entities.Properties()) ctx.Entities[p.Name] = View((JObject)p.Value);
            foreach (var v in scope.Vars.Properties())
                if (Js.IsStr(v.Value) && ctx.Entities.TryGetValue(Js.Str(v.Value), out var view)) ctx.Entities[v.Name] = view;
            ctx.AllEnemies = entities.Properties().Where(p => Js.Includes(((JObject)p.Value)[DK.Tags], DV.TagEnemy)).Select(p => ctx.Entities[p.Name]).ToList();
            if (Js.IsNum(scope.Vars[DK.EnergySpent])) ctx.EnergySpent = Js.D(scope.Vars[DK.EnergySpent]);
            if (Js.IsNum(scope.Vars[DK.CardsPlayedThisTurn])) ctx.CardsPlayedThisTurn = Js.D(scope.Vars[DK.CardsPlayedThisTurn]);
            return ctx;
        }

        private static bool IsCondition(JToken node) =>
            node is JObject o && o[DK.F] == null && o.Count == 1 && ConditionKeys.Contains(o.Properties().First().Name);

        private JToken Value(Run rt, Scope scope, JToken node)
        {
            if (Js.IsNum(node) && !Js.IsFinite(node)) throw new InvalidOperationException(DM.ValueNotFinite);
            if (node == null || node.Type == JTokenType.Null || node.Type == JTokenType.Boolean || Js.IsNum(node)) return node;
            if (Js.IsStr(node)) return Js.Str(node).StartsWith(DV.ReferencePrefix, StringComparison.Ordinal) ? Lookup(rt, scope, Js.Str(node)) : node;
            if (node is JArray array) return new JArray(array.Select(x => Value(rt, scope, x)));
            var obj = (JObject)node;
            if (Js.IsStr(obj[DK.F]))
            {
                var substituted = Substitute(rt, scope, obj);
                var ctx = FormulaContextFor(rt, scope);
                return Js.N(Js.Str(obj[DK.Numeric]) == DV.NumericScalar ? Formulas.EvaluateRaw(substituted, ctx) : Formulas.Evaluate(substituted, ctx));
            }
            if (IsCondition(obj)) return new JValue(Condition(rt, scope, obj));
            var result = new JObject();
            foreach (var p in obj.Properties()) result[p.Name] = Value(rt, scope, p.Value);
            return result;
        }

        private bool Condition(Run rt, Scope scope, JToken c) =>
            CoreMechanicsKernel.EvaluateCondition(c, new Dictionary<string, JToken> { [DK.Entities] = rt.State.Obj(DK.Entities) }, node => Value(rt, scope, node));

        private double NumberValue(Run rt, Scope scope, JToken node, string what)
        {
            var v = Value(rt, scope, node);
            if (!Js.IsFinite(v)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.MustBeNumber, what, v));
            return Js.D(v);
        }

        private string EntityKey(Run rt, Scope scope, JToken reference)
        {
            var v = Js.IsStr(reference) && Js.Str(reference).StartsWith(DV.ReferencePrefix, StringComparison.Ordinal) ? Lookup(rt, scope, Js.Str(reference)) : reference;
            if (!Js.IsStr(v) || rt.State.Obj(DK.Entities)?[Js.Str(v)] == null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.UnknownEntityRef, Js.IsStr(reference) ? Js.Str(reference) : reference?.ToString()));
            return Js.Str(v);
        }

        private List<string> EntityKeys(Run rt, Scope scope, JToken reference)
        {
            var v = Js.IsStr(reference) && Js.Str(reference).StartsWith(DV.ReferencePrefix, StringComparison.Ordinal) ? Lookup(rt, scope, Js.Str(reference)) : reference;
            if (v is JArray array) return array.SelectMany(item => EntityKeys(rt, scope, item)).ToList();
            if (Js.IsStr(v) && Js.Str(v) == DV.TagAllEnemies && rt.State.Obj(DK.Entities)[DV.TagAllEnemies] == null)
                return rt.State.Obj(DK.Entities).Properties().Where(p => Js.Includes(((JObject)p.Value)[DK.Tags], DV.TagEnemy)).Select(p => p.Name).ToList();
            return new List<string> { EntityKey(rt, scope, v) };
        }

        // ------------------------------------------------------------------ handlers and steps

        private void DispatchEvent(Run rt, string name, JObject payload)
        {
            rt.Events.Add((name, payload));
            foreach (var m in _mechanics.Properties().ToList())
                foreach (var handler in Js.Items(((JObject)m.Value).Obj(DK.Service)?[DK.Handlers]).OfType<JObject>().ToList())
                    if (handler.Str(DK.On) == DV.EventPrefix + name) RunHandler(rt, m.Name, handler, (JObject)payload.DeepClone());
        }

        private void RunHandler(Run rt, string slug, JObject handler, JObject parameters)
        {
            if (rt.Depth >= DN.MaxDepth) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.RecursionDepth, DN.MaxDepth));
            rt.Depth++;
            try
            {
                var scope = new Scope { Slug = slug, Vars = parameters };
                if (handler[DK.When] != null && !Condition(rt, scope, handler[DK.When])) return;
                RunSteps(rt, scope, handler[DK.Do]);
            }
            finally { rt.Depth--; }
        }

        private void RunSteps(Run rt, Scope scope, JToken steps)
        {
            foreach (var step in Js.Items(steps).OfType<JObject>().ToList()) RunStep(rt, scope, step);
        }

        private void RunStep(Run rt, Scope scope, JObject step)
        {
            if (step[DK.Op] != null) { RunOp(rt, scope, step); return; }
            if (step[DK.If] != null) { RunSteps(rt, scope, Condition(rt, scope, step[DK.If]) ? step[DK.Then] : step[DK.Else]); return; }
            if (step[DK.ForEach] != null)
            {
                var raw = step[DK.ForEach] is JArray list ? list.ToList() : new List<JToken> { step[DK.ForEach] };
                var items = new List<JToken>();
                foreach (var item in raw)
                {
                    if (Js.IsStr(item))
                    {
                        var v = Js.Str(item).StartsWith(DV.ReferencePrefix, StringComparison.Ordinal) ? Lookup(rt, scope, Js.Str(item)) : item;
                        items.AddRange(Expand(rt, scope, v));
                    }
                    else items.Add(Value(rt, scope, item));
                }
                foreach (var item in items)
                {
                    var inner = new Scope { Slug = scope.Slug, Vars = (JObject)scope.Vars.DeepClone() };
                    inner.Vars[step.Str(DK.As)] = item.DeepClone();
                    RunSteps(rt, inner, step[DK.Do]);
                }
                return;
            }
            if (step[DK.Emit] != null)
            {
                DispatchEvent(rt, step.Str(DK.Emit), (JObject)Value(rt, scope, step[DK.With] ?? new JObject()).DeepClone());
                return;
            }
            if (step[DK.Call] != null)
            {
                InvokeCommand(rt, step.Str(DK.Call), (JObject)Value(rt, scope, step[DK.With] ?? new JObject()).DeepClone());
                return;
            }
            if (step[DK.Set] != null)
            {
                var path = step.Str(DK.Set).Split(new[] { DV.ReferenceSeparator }, StringSplitOptions.None);
                var mechanics = rt.State.Obj(DK.Mechanics);
                var cur = mechanics.Obj(scope.Slug) ?? (JObject)(mechanics[scope.Slug] = new JObject());
                foreach (var key in path.Take(path.Length - 1))
                {
                    if (!(cur[key] is JObject next)) cur[key] = next = new JObject();
                    cur = next;
                }
                cur[path[path.Length - 1]] = Value(rt, scope, step[DK.Value]).DeepClone();
                return;
            }
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.UnknownStep, step.ToString(Newtonsoft.Json.Formatting.None)));
        }

        private IEnumerable<JToken> Expand(Run rt, Scope scope, JToken v)
        {
            if (v is JArray array) return array.SelectMany(x => Expand(rt, scope, x));
            if (Js.IsStr(v) && (Js.Str(v) == DV.TagAllEnemies || rt.State.Obj(DK.Entities)?[Js.Str(v)] != null)) return EntityKeys(rt, scope, v).Select(k => (JToken)new JValue(k));
            return new[] { v };
        }

        private void InvokeCommand(Run rt, string full, JObject parameters)
        {
            var (slug, name) = SplitName(full);
            var handlers = Js.Items(Mechanic(slug).Obj(DK.Service)?[DK.Handlers]).OfType<JObject>().Where(h => h.Str(DK.On) == DV.CommandPrefix + name).ToList();
            if (handlers.Count == 0) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.UnknownCommand, full));
            foreach (var handler in handlers) RunHandler(rt, slug, handler, (JObject)parameters.DeepClone());
        }

        private (string Slug, string Name) SplitName(string full)
        {
            for (var i = full.LastIndexOf(DV.NameSeparator, StringComparison.Ordinal); i > 0; i = full.LastIndexOf(DV.NameSeparator, i - 1, StringComparison.Ordinal))
            {
                var slug = full.Substring(0, i);
                if (_mechanics[slug] != null) return (slug, full.Substring(i + 1));
            }
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.UnknownName, full));
        }

        // ------------------------------------------------------------------ ops

        private void RunOp(Run rt, Scope scope, JObject step)
        {
            var op = step.Str(DK.Op);
            var def = _ops.Obj(DK.Ops)?.Obj(op);
            if (def == null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.UnknownOp, op));
            if (def.Str(DK.Reference) != DV.PortabilityPortable) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.LegacyOp, op));
            var declared = def.Obj(DK.Params) ?? new JObject();
            foreach (var p in step.Properties())
                if (p.Name != DK.Op && declared[p.Name] == null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.OpUnknownParameter, op, p.Name));
            foreach (var p in declared.Properties())
            {
                var param = (JObject)p.Value;
                if (param.Is(DK.Required) && step[p.Name] == null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.OpNeeds, op, p.Name));
                if (step[p.Name] == null) continue;
                var resolved = Value(rt, scope, step[p.Name]);
                var type = param.Str(DK.Type);
                bool good;
                switch (type)
                {
                    case DV.TypeNumber:
                    case DV.TypeValue: good = Js.IsFinite(resolved); break;
                    case DV.TypeInteger: good = Js.IsInt(resolved); break;
                    case DV.TypeEntityRef: good = Js.IsStr(resolved) || (resolved is JArray refs && refs.All(Js.IsStr)); break;
                    case DV.TypeString:
                    case DV.TypeCardRef: good = Js.IsStr(resolved); break;
                    case DV.TypeBoolean: good = resolved != null && resolved.Type == JTokenType.Boolean; break;
                    case DV.TypeObject: good = resolved is JObject; break;
                    default: good = true; break;
                }
                if (!good) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.OpParameterType, op, p.Name, type));
            }
            var pileOp = op == DV.OpDraw || op == DV.OpDiscard || op == DV.OpExhaust;
            var targets = pileOp ? new List<string>() : EntityKeys(rt, scope, step[DK.Target] ?? (scope.Vars[DK.Target] != null ? (JToken)(DV.ReferencePrefix + DK.Target) : DV.TargetSelf));
            double Number(JToken node, double fallback, JObject vars, string what)
            {
                if (node == null) return fallback;
                var inner = scope;
                if (vars != null)
                {
                    inner = new Scope { Slug = scope.Slug, Vars = (JObject)scope.Vars.DeepClone() };
                    foreach (var p in vars.Properties()) inner.Vars[p.Name] = p.Value.DeepClone();
                }
                return NumberValue(rt, inner, node, string.Format(CultureInfo.InvariantCulture, DM.OpWhat, op, what));
            }
            var entities = rt.State.Obj(DK.Entities);
            foreach (var key in OpTargets(op, targets))
            {
                var e = key == null ? null : entities.Obj(key);
                var vars = key == null ? null : new JObject { [DK.Target] = key };
                switch (op)
                {
                    case DV.OpDamage:
                    {
                        var hits = Whole(Number(step[DK.Hits], 1, vars, DK.Hits));
                        for (var h = 0; h < hits; h++)
                        {
                            var amount = Whole(Number(step[DK.Amount], 0, vars, DK.Amount));
                            var absorbed = Math.Min(e.Num(DK.Block), amount);
                            e.Put(DK.Block, e.Num(DK.Block) - absorbed);
                            e.Put(DK.Hp, Math.Max(0, e.Num(DK.Hp) - (amount - absorbed)));
                        }
                        break;
                    }
                    case DV.OpBlock: e.Put(DK.Block, e.Num(DK.Block) + Whole(Number(step[DK.Amount], 0, vars, DK.Amount))); break;
                    case DV.OpLoseHp:
                    {
                        var amount = Whole(Number(step[DK.Amount], 0, vars, DK.Amount));
                        if (step.Is(DK.Nonlethal) && amount >= e.Num(DK.Hp)) throw new InvalidOperationException(DM.NotEnoughHp);
                        e.Put(DK.Hp, Math.Max(0, e.Num(DK.Hp) - amount));
                        break;
                    }
                    case DV.OpHeal: e.Put(DK.Hp, Math.Min(e.Num(DK.MaxHp), e.Num(DK.Hp) + Whole(Number(step[DK.Amount], 0, vars, DK.Amount)))); break;
                    case DV.OpApplyStatus:
                    {
                        var id = Js.Str(Value(rt, Inner(scope, vars), step[DK.Status]));
                        var statuses = e.Obj(DK.Statuses);
                        var next = Js.Or0(statuses[id]) + Math.Floor(Number(step[DK.Stacks], 1, vars, DK.Stacks));
                        if (next > 0) statuses.Put(id, next); else statuses.Remove(id);
                        break;
                    }
                    case DV.OpRemoveStatus:
                    {
                        var id = Js.Str(Value(rt, Inner(scope, vars), step[DK.Status]));
                        var statuses = e.Obj(DK.Statuses);
                        if (step[DK.Amount] == null) { statuses.Remove(id); break; }
                        var next = Js.Or0(statuses[id]) - Whole(Number(step[DK.Amount], 0, vars, DK.Amount));
                        if (next > 0) statuses.Put(id, next); else statuses.Remove(id);
                        break;
                    }
                    case DV.OpGainEnergy:
                    {
                        var amount = Whole(Number(step[DK.Amount], 1, vars, DK.Amount) - (step[DK.Funded] == null ? 0 : Number(step[DK.Funded], 0, vars, DK.Funded)));
                        e.Put(DK.Sp, e.Num(DK.Sp) + amount);
                        if (e[DK.SpMax] != null) e.Put(DK.Sp, Math.Min(e.Num(DK.SpMax), e.Num(DK.Sp)));
                        break;
                    }
                    case DV.OpRestoreStamina:
                        e.Put(DK.Sp, Math.Max(0, e.Num(DK.Sp) + Math.Floor(Number(step[DK.Amount], 1, vars, DK.Amount))));
                        if (e[DK.SpMax] != null) e.Put(DK.Sp, Math.Min(e.Num(DK.SpMax), e.Num(DK.Sp)));
                        break;
                    case DV.OpRestoreMana:
                    {
                        if (step[DK.ToFloorPct] != null)
                        {
                            var cap = Js.Nullish(e[DK.MpMax]) ? e.Num(DK.Mp) : e.Num(DK.MpMax);
                            var floor = Math.Min(cap, Math.Floor(cap * Whole(Number(step[DK.ToFloorPct], 0, vars, DK.ToFloorPct)) / Ashen.Generated.CombatMath.Percent));
                            e.Put(DK.Mp, e.Num(DK.Mp) >= floor ? cap : floor);
                            break;
                        }
                        e.Put(DK.Mp, e.Num(DK.Mp) + Whole(Number(step[DK.Amount], 1, vars, DK.Amount) - (step[DK.Funded] == null ? 0 : Number(step[DK.Funded], 0, vars, DK.Funded))));
                        if (e[DK.MpMax] != null) e.Put(DK.Mp, Math.Min(e.Num(DK.MpMax), e.Num(DK.Mp)));
                        break;
                    }
                    case DV.OpPoiseDamage:
                    {
                        var before = e.Num(DK.Poise);
                        e.Put(DK.Poise, Math.Max(0, before - Whole(Number(step[DK.Amount], 0, vars, DK.Amount))));
                        if (before > 0 && e.Num(DK.Poise) == 0) DispatchEvent(rt, DV.EventPoiseBroken, new JObject { [DK.Target] = key });
                        break;
                    }
                    case DV.OpGainPoise: e.Put(DK.Poise, e.Num(DK.Poise) + Whole(Number(step[DK.Amount], 0, vars, DK.Amount))); break;
                    case DV.OpRestorePoise:
                        e.Put(DK.Poise, e.Num(DK.Poise) + Whole(Number(step[DK.Amount], 0, vars, DK.Amount)));
                        if (e[DK.PoiseMax] != null) e.Put(DK.Poise, Math.Min(e.Num(DK.PoiseMax), e.Num(DK.Poise)));
                        break;
                    case DV.OpStagger: DispatchEvent(rt, DV.EventStaggered, new JObject { [DK.Target] = key }); break;
                    case DV.OpDraw:
                    {
                        var piles = rt.State.Obj(DK.Piles);
                        var amount = Whole(Number(step[DK.Amount], 1, null, DK.Amount));
                        for (var i = 0; i < amount && piles.Arr(DV.PileDraw).Count > 0; i++) { piles.Arr(DV.PileHand).Add(piles.Arr(DV.PileDraw)[0]); piles.Arr(DV.PileDraw).RemoveAt(0); }
                        break;
                    }
                    case DV.OpDiscard:
                    case DV.OpExhaust:
                    {
                        if (step.Is(DK.Random)) throw new InvalidOperationException(DM.RandomNotInSubset);
                        var piles = rt.State.Obj(DK.Piles);
                        var hand = piles.Arr(DV.PileHand);
                        var to = piles.Arr(op == DV.OpDiscard ? DV.PileDiscard : DV.PileExhaust);
                        var amount = Whole(Number(step[DK.Amount], 1, null, DK.Amount));
                        for (var i = 0; i < amount && hand.Count > 0; i++) { to.Add(hand[0]); hand.RemoveAt(0); }
                        break;
                    }
                    default: throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.OpNotInSubset, op));
                }
            }
        }

        private static Scope Inner(Scope scope, JObject vars)
        {
            if (vars == null) return scope;
            var inner = new Scope { Slug = scope.Slug, Vars = (JObject)scope.Vars.DeepClone() };
            foreach (var p in vars.Properties()) inner.Vars[p.Name] = p.Value.DeepClone();
            return inner;
        }

        /// <summary>The ops that act on the piles (draw, discard, exhaust) run once; the rest once per target.</summary>
        private static IEnumerable<string> OpTargets(string op, List<string> targets) =>
            op == DV.OpDraw || op == DV.OpDiscard || op == DV.OpExhaust ? new string[] { null } : (IEnumerable<string>)targets;

        // ------------------------------------------------------------------ cases

        private static void PartialMatch(JToken actual, JToken expected, string path)
        {
            if (expected is JObject want)
            {
                if (!(actual is JObject got)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.ExpectedObjectAt, path, actual));
                foreach (var p in want.Properties()) PartialMatch(got[p.Name], p.Value, path + DV.PathSeparator + p.Name);
                return;
            }
            if (!JToken.DeepEquals(actual, expected)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.ExpectedAt, path, expected, actual));
        }

        /// <summary>
        /// runCases for one case (web engine/runtime/interpreter.mjs runCases): the given state, the command, event, binding query or modifier evaluation, and the
        /// then-assertions. Returns the state after, the event names and the value the web's harness would have compared.
        /// </summary>
        public MechanicCaseResult RunCase(JObject mechanicDoc, JObject testCase)
        {
            var result = new MechanicCaseResult();
            try
            {
                var state = CreateState(testCase.Obj(DK.Given));
                var when = testCase.Obj(DK.When) ?? new JObject();
                var then = testCase.Obj(DK.Then) ?? new JObject();
                foreach (var p in then.Properties())
                    if (!ThenKeys.Contains(p.Name)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.ThenUnknownKey, p.Name));
                var run = new Run { State = state };
                JToken value = null;
                void Execute()
                {
                    if (when[DK.Command] != null) InvokeCommand(run, when.Str(DK.Command), (JObject)(when.Obj(DK.With) ?? new JObject()).DeepClone());
                    else if (when[DK.Event] != null) DispatchEvent(run, when.Str(DK.Event), (JObject)(when.Obj(DK.With) ?? new JObject()).DeepClone());
                    else if (when[DK.Query] != null) throw new InvalidOperationException(DM.QueryNotInSubset);
                    else if (when.Obj(DK.BindingQuery) != null)
                    {
                        var q = when.Obj(DK.BindingQuery);
                        var bindings = CoreMechanicsKernel.CompileBindings(SingleMechanic(mechanicDoc), Js.Items(q[DK.Attachments]).OfType<JObject>(), q.Obj(DK.ModuleParameters));
                        if (bindings.Count != 1) throw new InvalidOperationException(DM.QueryNeedsOneBinding);
                        value = CoreMechanicsKernel.QueryBinding(bindings[0], q.Str(DK.Query), q.Obj(DK.Params));
                    }
                    else if (when.Obj(DK.Modifiers) != null)
                    {
                        var m = when.Obj(DK.Modifiers);
                        var bindings = CoreMechanicsKernel.CompileBindings(SingleMechanic(mechanicDoc), Js.Items(m[DK.Attachments]).OfType<JObject>(), m.Obj(DK.ModuleParameters));
                        var evaluated = CoreMechanicsKernel.EvaluateModifiers(bindings, m.Str(DK.Phase), m.Str(DK.SubjectId), m.Str(DK.ActorId), ScopeOf(m.Obj(DK.Context)), m.Num(DK.Amount), m.Is(DK.Quantity));
                        value = evaluated[DK.Amount];
                    }
                    else throw new InvalidOperationException(DM.WhenNeeds);
                }
                if (then[DK.Error] != null)
                {
                    string message = null;
                    try { Execute(); } catch (Exception e) when (e is InvalidOperationException || e is MechanicsException) { message = e.Message; }
                    if (message == null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.ExpectedErrorNone, then.Str(DK.Error)));
                    if (!message.Contains(then.Str(DK.Error))) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.ExpectedErrorGot, then.Str(DK.Error), message));
                    result.Ok = true;
                    result.State = run.State;
                    result.Error = message;
                    return result;
                }
                Execute();
                foreach (var section in new[] { DK.Entities, DK.Piles, DK.Mechanics })
                    if (then[section] != null) PartialMatch(run.State[section], then[section], section);
                result.Events = run.Events.Select(e => e.Name).ToList();
                if (then[DK.Events] != null)
                {
                    var at = 0;
                    foreach (var wanted in Js.Items(then[DK.Events]).Select(Js.Str))
                    {
                        var found = result.Events.IndexOf(wanted, at);
                        if (found < 0) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.EventsExpected, wanted, string.Join(DV.ListSeparator, result.Events)));
                        at = found + 1;
                    }
                }
                if (then[DK.Value] != null && !JToken.DeepEquals(value, then[DK.Value]))
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.ValueExpected, then[DK.Value], value));
                result.Ok = true;
                result.State = run.State;
                result.Value = value;
            }
            catch (Exception e) when (e is InvalidOperationException || e is MechanicsException || e is InvalidCastException)
            {
                result.Ok = false;
                result.Error = e.Message;
            }
            return result;
        }

        private static Dictionary<string, JToken> ScopeOf(JObject context)
        {
            var scope = new Dictionary<string, JToken>(StringComparer.Ordinal);
            if (context != null) foreach (var p in context.Properties()) scope[p.Name] = p.Value;
            return scope;
        }

        private JObject SingleMechanic(JObject mechanicDoc)
        {
            var id = mechanicDoc.Obj(DK.Mechanic).Str(DK.Id);
            return new JObject { [id] = mechanicDoc };
        }
    }
}
