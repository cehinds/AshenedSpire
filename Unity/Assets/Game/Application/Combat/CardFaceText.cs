using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using S = Ashen.Generated.StringKeys;
using T = Ashen.Generated.CardTextKeys;

namespace Ashen.App.Combat
{
    /// <summary>One line of a card's rules: its kind (cost, damage, status, condition, consume, gain, bonus, detail) and its html, every number a val span.</summary>
    public sealed class FaceLine
    {
        public string Kind;
        public string Html;
    }

    /// <summary>What cardFaceLines is told besides the card: whether the stance entry's Precision is its own line, and what the tag rail already says.</summary>
    public sealed class FaceOptions
    {
        public bool StanceEntry = true;
        public List<int> RepresentedEffects = new List<int>();
        public List<string> RepresentedEntry = new List<string>();
        public List<string> RepresentedRules = new List<string>();
        public string EntryLabel;
    }

    /// <summary>
    /// The text a playing card's face says, as lines (web model/cardFaceText.js cardFaceLines, over cardSummaryContext of model/playingCard.js). The numbers come from the
    /// same preview the engine executes; with no preview a number is the card's own and none is marked. The lines are html as the web writes them (the escaped text, every
    /// number <c>&lt;span class="val"&gt;n&lt;/span&gt;</c>, gaining up or down when the preview moved it off its base), so they are compared word for word with the web's; the
    /// board turns them into rich text. Null when a part of the card cannot be phrased (the caller keeps the authored text).
    /// </summary>
    public sealed class CardFaceText
    {
        private sealed class Unphraseable : Exception
        {
        }

        private sealed class Frag
        {
            public string Cat;
            public string Text;
        }

        private sealed class Merge
        {
            public bool Leader;
            public List<int> Indices;
        }

        private sealed class Group
        {
            public string Prefix;
            public List<Frag> Fragments = new List<Frag>();
            public bool Sleeping;
        }

        private readonly FaceEnv _env;
        private readonly CardTextData _t;
        private readonly CombatState _c;
        private JObject _def;
        private readonly JObject _preview;
        private JObject _profile;
        private Dictionary<string, string> _bindings;
        private JObject _tokens;
        private Dictionary<int, Merge> _poiseMerge;

        private CardFaceText(FaceEnv env, JObject def, JObject preview)
        {
            _env = env;
            _t = env.Text;
            _c = env.State;
            _def = def;
            _preview = preview;
        }

        /// <summary>cardFaceLines(def, preview, registries, rules, options): the lines, or null when a part of the card cannot be phrased.</summary>
        public static List<FaceLine> Lines(FaceEnv env, JObject def, JObject preview, FaceOptions options = null)
        {
            var face = new CardFaceText(env, def, preview);
            try
            {
                return face.Build(options ?? new FaceOptions());
            }
            catch (Unphraseable)
            {
                return null;
            }
        }

        /// <summary>withChargedEffects(def, preview): the card with the effect list a live preview priced (an active charge adds a buildup row).</summary>
        public static JObject WithChargedEffects(JObject def, JObject preview)
        {
            if (preview?[T.ChargedEffects] is JArray charged && !ReferenceEquals(charged, def[T.Effects]))
            {
                var copy = Js.Spread(def);
                copy[T.Effects] = charged;
                return copy;
            }
            return def;
        }

        private static Exception Fail() => new Unphraseable();

        /// <summary>The reader of the numbers of a card (cardSummaryContext's value): the preview's, else the card's own.</summary>
        public static Func<JObject, int, string, JToken> Reader(FaceEnv env, JObject def, JObject preview)
        {
            var face = new CardFaceText(env, def, preview);
            face.Prepare();
            return face.Value;
        }

        private void Prepare()
        {
            _profile = CoreStances.ProfileFor(_c, _def);
            _bindings = CombatPreview.TokenBindings(_c.Data, _def[T.Effects]);
            _tokens = StaticTokens(_env, _def);
        }

        // ------------------------------------------------------------------ the shared read (cardSummaryContext)

        private JObject Live(int index) => (_preview?[T.Values] as JArray)?.ElementAtOrDefault(index) as JObject;

        private string TokenOf(int index, string field) =>
            _bindings.TryGetValue(index.ToString(CultureInfo.InvariantCulture) + Ashen.Generated.CombatValues.KeySeparator + field, out var token) ? token : null;

        /// <summary>The number an effect's field has: the preview's token, else the preview's value for the same op, else the static token, else the literal, else '?'.</summary>
        private JToken Value(JObject effect, int index) => Value(effect, index, T.Amount);

        private JToken Value(JObject effect, int index, string field)
        {
            var token = TokenOf(index, field);
            var live = Live(index);
            var fromToken = token != null ? _preview?.Obj(T.Tokens)?[token] : null;
            if (!Js.Nullish(fromToken)) return fromToken;
            if (live != null && live.Str(T.Op) == effect.Str(T.Op) && !Js.Nullish(live[T.Value])) return live[T.Value];
            var fixedToken = token != null ? _tokens[token] : null;
            if (!Js.Nullish(fixedToken)) return fixedToken;
            return Js.IsNum(effect[field]) ? effect[field] : Js.S(_t.Word(S.CardtextUnknown));
        }

        private JObject DefenseValue(JObject effect, int index)
        {
            var live = Live(index);
            if (live?[T.DefenseSplit] is JObject split) return split;
            var token = TokenOf(index, T.Amount);
            var tokens = _preview?.Obj(T.Tokens);
            var block = token != null ? tokens?[token + T.BlockSuffix] : null;
            var ward = token != null ? tokens?[token + T.WardSuffix] : null;
            return Js.IsFinite(block) && Js.IsFinite(ward) ? new JObject { [T.Block] = block.DeepClone(), [T.Ward] = ward.DeepClone() } : null;
        }

        private string Title(string id) => _c.Data.Statuses.Has(id) ? _c.Data.Statuses.Get(id).Str(T.Name) : char.ToUpperInvariant(id[0]) + id.Substring(1);

        private string Condition(JObject pred)
        {
            if (pred == null) return string.Empty;
            var p = pred.Str(T.P);
            var of = pred.Str(T.Of);
            var self = of == T.Self;
            if (p == T.HasStatus)
                return _t.Say(S.CardtextCondHasStatus, self ? _t.Word(S.CardtextYouHave) : of == T.AllEnemies ? _t.Word(S.CardtextAnEnemyHas) : _t.Word(S.CardtextTheTargetHas), Title(pred.Str(T.Status)));
            if (p == T.HasBlock)
                return _t.Say(S.CardtextCondHasBlock, self ? _t.Word(S.CardtextYou) : _t.Word(S.CardtextTheTarget), self ? _t.Word(S.CardtextHave) : _t.Word(S.CardtextHas),
                    pred.Str(T.Snapshot) == T.BeforePlay ? _t.Word(S.CardtextBeforeThisCard) : string.Empty);
            if (p == T.HpBelowPct) return _t.Say(S.CardtextCondHpBelow, self ? _t.Word(S.CardtextYour) : _t.Word(S.CardtextTarget), CardTextData.Show(pred[T.Pct]));
            if (p == T.FirstCardThisTurn) return _t.Word(S.CardtextCondFirstCard);
            if (p == T.InStance) return _t.Say(S.CardtextCondInStance, CardTextData.Show(pred[T.Stance]));
            if (p == T.TurnMetric) return TurnMetric(pred);
            if (p == T.ChargeAvailable) return _t.Word(S.CardtextCondCharge);
            if (p == T.Not)
            {
                var inner = Condition(pred[T.Pred] as JObject);
                return inner.Length > 0 ? _t.Say(S.CardtextCondNot, inner) : string.Empty;
            }
            if (p == T.All || p == T.Any)
            {
                var parts = Js.Items(pred[T.Preds]).Select(x => Condition(x as JObject)).ToList();
                return parts.All(part => part.Length > 0) ? string.Join(p == T.All ? _t.Word(S.CardtextAnd) : _t.Word(S.CardtextOr), parts) : string.Empty;
            }
            return string.Empty;
        }

        private string TurnMetric(JObject pred)
        {
            var thresholds = new List<string>();
            if (!Js.Nullish(pred[T.AtLeast])) thresholds.Add(_t.Say(S.CardtextAtLeast, CardTextData.Show(pred[T.AtLeast])));
            if (!Js.Nullish(pred[T.AtMost])) thresholds.Add(_t.Say(S.CardtextAtMost, CardTextData.Show(pred[T.AtMost])));
            var threshold = string.Join(_t.Word(S.CardtextAnd), thresholds);
            if (threshold.Length == 0) return string.Empty;
            var metric = pred.Str(T.Metric);
            if (metric == T.SameCardPlays && Js.Truthy(pred[T.CardId]) && pred.Str(T.CardId) != _def.Str(T.Id)) return string.Empty;
            if (metric == T.TurnStartHpPct) return _t.Say(S.CardtextCondTurnStartHp, threshold);
            if (metric == T.HpLostSinceTurnStart) return _t.Say(S.CardtextCondHpLost, threshold);
            var name = _t.Map(T.TurnMetricNames, metric);
            if (name == null && (metric == T.TagPlays || metric == T.DistinctTagPlays) && Js.Truthy(pred[T.Tag]))
                name = metric == T.DistinctTagPlays ? _t.Say(S.CardtextDistinctTagPlays, pred.Str(T.Tag)) : _t.Say(S.CardtextTagPlays, pred.Str(T.Tag));
            if (name == null) return string.Empty;
            if (metric == T.PreviousSpell && Js.Nullish(pred[T.AtMost]) && Js.IsNum(pred[T.AtLeast]) && pred.Num(T.AtLeast) == 1) return _t.Word(S.CardtextCondPreviousSpell);
            return _t.Say(S.CardtextCondMetric, name, threshold, metric == T.CardPlaysCombat ? string.Empty : _t.Word(S.CardtextThisTurn));
        }

        // ------------------------------------------------------------------ the static numbers (staticCardTokens)

        /// <summary>staticCardTokens(def): the numbers a card's own template substitutes before any live preview.</summary>
        public static JObject StaticTokens(FaceEnv env, JObject def)
        {
            var t = env.Text;
            var tokens = new JObject();
            var effects = TokenEffects(def);
            foreach (var binding in CombatPreview.TokenBindings(env.State.Data, effects))
            {
                var split = binding.Key.Split(Ashen.Generated.CombatValues.KeySeparator[0]);
                var index = int.Parse(split[0], CultureInfo.InvariantCulture);
                var effect = (JObject)effects[index];
                var raw = effect[split[1]];
                var constant = ConstantSum(raw);
                if (constant.HasValue) { tokens[binding.Value] = Js.N(constant.Value); continue; }
                string configured = null;
                if (raw is JObject metric && metric.Str(T.F) == T.Metric && metric.Str(T.MetricKey) == T.EvadeStackGain && metric.Str(T.Of) == T.Self && metric[T.Per] == null)
                    configured = EvadeStackGainPhrase(env, def);
                var constantValue = configured == null ? ConstantFormulaValue(env, raw) : null;
                var described = configured == null && !constantValue.HasValue ? DescribeFormula(env, raw) : null;
                if (configured != null) tokens[binding.Value] = Js.S(configured);
                else if (constantValue.HasValue) tokens[binding.Value] = Js.N(constantValue.Value);
                else if (described != null) tokens[binding.Value] = Js.S(described);
                else if (raw is JObject weapon && weapon.Str(T.F) == T.Metric && weapon.Str(T.MetricKey) == T.WeaponDamage && weapon.Str(T.Of) == T.Self
                    && Js.IsFinite(weapon[T.Per]) && weapon.Num(T.Per) > 0 && weapon.Properties().All(p => t.In(T.WeaponMetricKeys, p.Name))
                    && effect.Obj(T.Attack)?.Str(T.Source) == T.Weapon)
                    tokens[binding.Value] = Js.S(t.Say(S.CardtextWeaponMultiplier, CardTextData.Num(weapon.Num(T.Per))));
            }
            return tokens;
        }

        /// <summary>cardTokenEffects(card): the play effects, then the hook that runs at the end of the turn in hand.</summary>
        public static JArray TokenEffects(JObject def)
        {
            var effects = def[T.Effects] as JArray ?? new JArray();
            var hook = def[T.OnTurnEndInHand] as JArray;
            if (hook == null || hook.Count == 0) return effects;
            var all = new JArray();
            foreach (var effect in effects) all.Add(effect);
            foreach (var effect in hook) all.Add(effect);
            return all;
        }

        /// <summary>A plain number, or a sum of constant numbers (a rank composition wraps stacks in add formulas).</summary>
        private static double? ConstantSum(JToken value)
        {
            if (Js.IsNum(value)) return Js.D(value);
            if (!(value is JObject formula) || formula.Str(T.F) != T.Add || !(formula[T.Args] is JArray args)) return null;
            double total = 0;
            foreach (var arg in args)
            {
                var part = ConstantSum(arg);
                if (!part.HasValue || double.IsNaN(part.Value) || double.IsInfinity(part.Value)) return null;
                total += part.Value;
            }
            return total;
        }

        private static bool IsFormula(JToken value) => value is JObject o && Js.IsStr(o[T.F]);

        /// <summary>constantFormulaValue(formula): the value of a formula made only of constant adds and multiplications, else null.</summary>
        public static double? ConstantFormulaValue(FaceEnv env, JToken formula)
        {
            bool Constant(JToken node)
            {
                if (Js.IsNum(node)) return Js.IsFinite(node);
                if (!IsFormula(node)) return false;
                var o = (JObject)node;
                return (o.Str(T.F) == T.Add || o.Str(T.F) == T.Mul) && o.Properties().All(p => env.Text.In(T.ConstantFormulaKeys, p.Name))
                    && new[] { T.Min, T.Max }.All(k => o[k] == null || Js.IsFinite(o[k])) && o[T.Args] is JArray args && args.All(Constant);
            }
            if (!Constant(formula)) return null;
            double Eval(JToken node)
            {
                if (Js.IsNum(node)) return Js.D(node);
                var o = (JObject)node;
                double v;
                if (o.Str(T.F) == T.Add) { v = 0; foreach (var arg in (JArray)o[T.Args]) v += Eval(arg); }
                else { v = 1; foreach (var arg in (JArray)o[T.Args]) v *= Eval(arg); }
                if (o[T.Min] != null && v < o.Num(T.Min)) v = o.Num(T.Min);
                if (o[T.Max] != null && v > o.Num(T.Max)) v = o.Num(T.Max);
                return v;
            }
            var value = Math.Floor(Eval(formula));
            return double.IsNaN(value) || double.IsInfinity(value) ? (double?)null : value;
        }

        private static string EvadeStackGainPhrase(FaceEnv env, JObject def)
        {
            var accuracy = CoreFixCore.Family(env.State, T.Accuracy, null, def);
            var baseStacks = accuracy.Num(T.EvadeStackBase);
            var dex = accuracy.Num(T.EvadeStackDex);
            return dex == 0 ? CardTextData.Num(baseStacks)
                : env.Text.Say(S.CardtextEvadeStackPhrase, CardTextData.Num(baseStacks), dex == 1 ? string.Empty : env.Text.Say(S.CardtextDexTimes, CardTextData.Num(dex)));
        }

        /// <summary>describeFormula(formula): a state-dependent quantity in words, else null.</summary>
        public static string DescribeFormula(FaceEnv env, JToken formula)
        {
            var t = env.Text;
            if (!IsFormula(formula)) return null;
            var f = (JObject)formula;
            var kind = f.Str(T.F);
            bool Bounds() => new[] { T.Min, T.Max }.All(k => f[k] == null || Js.IsFinite(f[k]));
            string Limits(string text, bool rounded)
            {
                if (f[T.Min] != null) text += t.Say(S.CardtextMinimum, CardTextData.Show(f[T.Min]));
                if (f[T.Max] != null) text += t.Say(S.CardtextMaximum, CardTextData.Show(f[T.Max]));
                if (rounded) text += t.Word(S.CardtextRoundedDown);
                return text;
            }
            bool Fractional() => new[] { T.Min, T.Max }.Any(k => f[k] != null && !Js.IsInt(f[k]));
            if (kind == T.Add)
            {
                if (!(f[T.Args] is JArray args) || args.Count == 0 || f.Properties().Any(p => !t.In(T.FormulaKeys, p.Name)) || !Bounds()) return null;
                var parts = args.Select(part => Js.IsFinite(part) ? CardTextData.Show(part) : DescribeFormula(env, part)).ToList();
                if (parts.Any(part => part == null)) return null;
                return Limits(string.Join(t.Word(S.CardtextPlusWord), parts), Fractional());
            }
            if (kind == T.Mul)
            {
                if (!(f[T.Args] is JArray args) || args.Count != Ashen.Generated.P1Count.Pair || f.Properties().Any(p => !t.In(T.FormulaKeys, p.Name)) || !Bounds()) return null;
                var share = args[0];
                var inner = args[1];
                var per = Js.IsNum(share) && Js.D(share) > 0 ? 1 / Js.D(share) : double.NaN;
                var described = DescribeFormula(env, inner);
                if (double.IsNaN(per) || per != Math.Floor(per) || per < Ashen.Generated.P1Count.Pair || described == null) return null;
                return Limits(t.Say(S.CardtextPerGroups, CardTextData.Num(per), described), Fractional());
            }
            if (kind == T.Metric)
            {
                var per = f[T.Per];
                if (!((per == null || (Js.IsFinite(per) && Js.D(per) > 0)) && Bounds() && !(f[T.Min] != null && f[T.Max] != null && f.Num(T.Min) > f.Num(T.Max))
                    && f.Properties().All(p => t.In(T.MetricKeys, p.Name)))) return null;
                var metric = f.Str(T.MetricKey);
                var of = f.Str(T.Of);
                string text = null;
                if (metric == T.NegativeStatusTypes) text = of == T.Target && f[T.Statuses] is JArray list && list.Count > 0 && list.All(s => Js.IsStr(s) && Js.Str(s).Length > 0) ? t.Word(S.CardtextMetricNegative) : null;
                else if (metric == T.DamageTakenThisTurn) text = of == T.Self ? t.Word(S.CardtextMetricHpLost) : null;
                else if (metric == T.DistinctTypes) text = of == T.Self ? t.Word(S.CardtextMetricTypes) : null;
                else if (metric == T.SameCardPlays) text = of == T.Self ? t.Word(S.CardtextMetricSamePlays) : null;
                else if (metric == T.EvadeStackGain) text = of == T.Self ? t.Word(S.CardtextMetricEvade) : null;
                var amount = Js.IsNum(per) ? Js.D(per) : 1;
                if (text != null)
                {
                    var scale = amount == Ashen.Generated.CombatMath.Half && metric == T.DamageTakenThisTurn ? t.Word(S.CardtextHalfOf)
                        : metric == T.EvadeStackGain && amount == 1 ? string.Empty : t.Say(S.CardtextPerScale, CardTextData.Num(amount));
                    return Limits(scale + text, amount != Math.Floor(amount) || Fractional());
                }
            }
            if (kind == T.EnergySpent && Js.IsFinite(f[T.Per]) && f.Num(T.Per) > 0 && Bounds() && !(f[T.Min] != null && f[T.Max] != null && f.Num(T.Min) > f.Num(T.Max))
                && f.Properties().All(p => t.In(T.EnergyKeys, p.Name)))
                return Limits(t.Say(S.CardtextPerSp, CardTextData.Show(f[T.Per])), !Js.IsInt(f[T.Per]) || Fractional());
            if (kind != T.Stacks || f.Properties().Any(p => !t.In(T.StacksKeys, p.Name)) || !Bounds()) return null;
            var places = t.Table(T.StackLocations);
            var where = f.Str(T.Of);
            if (where == null || places[where] == null || !Js.IsStr(f[T.Status]) || f.Str(T.Status).Length == 0 || (f[T.Per] != null && (!Js.IsFinite(f[T.Per]) || f.Num(T.Per) <= 0))) return null;
            var status = Regex.Replace(f.Str(T.Status), t.Word(S.CardtextCamelPattern), t.Word(S.CardtextCamelReplacement));
            status = char.ToUpperInvariant(status[0]) + status.Substring(1);
            var result = t.Say(S.CardtextStacksWhere, status, places.Str(where));
            if (f[T.Per] != null) result = t.Say(S.CardtextPerStacks, CardTextData.Show(f[T.Per]), result);
            return Limits(result, Fractional());
        }

        // ------------------------------------------------------------------ the lines

        private string CounterModeOf()
        {
            var defaults = _c.Fields.Obj(T.CombatExpansionRules)?.Obj(T.Matchups)?.Obj(T.Counter)?.Obj(T.DefaultCoverage);
            if (!Js.Truthy(_def[T.CounterCoverage]))
                return Js.Truthy(_profile[T.CounterMode]) ? _profile.Str(T.CounterMode) : _profile.Str(T.Camp) == T.Spell ? T.Spell : T.Melee;
            string Canonical(JToken value) => Canon(value).ToString(Newtonsoft.Json.Formatting.None);
            foreach (var mode in (defaults?.Properties() ?? Enumerable.Empty<JProperty>()))
                if (Canonical(mode.Value) == Canonical(_def[T.CounterCoverage])) return mode.Name;
            return null;
        }

        /// <summary>JSON.stringify with object keys in localeCompare order (the web's canonical): arrays keep their order.</summary>
        private static JToken Canon(JToken value)
        {
            if (value is JObject o)
            {
                var copy = new JObject();
                foreach (var p in o.Properties().OrderBy(p => p.Name, StringComparer.InvariantCulture)) copy[p.Name] = Canon(p.Value);
                return copy;
            }
            if (value is JArray a) return new JArray(a.Select(Canon));
            return value;
        }

        private string Num(JToken amount, JToken @base = null)
        {
            if (amount == null || Js.Nullish(amount) || (Js.IsStr(amount) && Js.Str(amount) == _t.Word(S.CardtextUnknown)) || (Js.IsNum(amount) && !Js.IsFinite(amount))) throw Fail();
            // A formula described in words is not a number: keep the authored text.
            if (Js.IsStr(amount) && !Regex.IsMatch(Js.Str(amount), _t.Word(S.CardtextDigits))) throw Fail();
            var cls = _t.Word(S.CardtextClassVal);
            if (_preview != null && Js.IsNum(amount) && Js.IsNum(@base))
            {
                if (Js.D(amount) > Js.D(@base)) cls += _t.Word(S.CardtextClassUp);
                else if (Js.D(amount) < Js.D(@base)) cls += _t.Word(S.CardtextClassDown);
            }
            return _t.Say(S.CardtextVal, cls, _t.Esc(CardTextData.Show(amount)));
        }

        private string Num(double amount, double? @base = null) => Num(Js.N(amount), @base.HasValue ? Js.N(@base.Value) : null);

        private JToken BaseOf(JObject effect, int index, string field)
        {
            var token = TokenOf(index, field);
            var fromToken = token != null ? _tokens[token] : null;
            if (Js.IsNum(fromToken)) return fromToken;
            return Js.IsNum(effect[field]) ? effect[field] : null;
        }

        private string TypeWord(string id)
        {
            var label = _env.TagLabel(T.DamageTagPrefix + id);
            return label ?? string.Empty;
        }

        private string Typed(string amountHtml, string label) => !string.IsNullOrEmpty(label) ? _t.Say(S.CardtextTyped, amountHtml, _t.Esc(label)) : amountHtml;

        private string AreaOf(string target) => target == T.AllEnemies ? _t.Word(S.CardtextAreaAll) : target == T.RandomEnemy ? _t.Word(S.CardtextAreaRandom) : string.Empty;

        private string DurationOf(JObject effect)
        {
            if (effect[T.Duration] == null) return string.Empty;
            if (!Js.IsNum(effect[T.Duration])) throw Fail();
            return _t.Say(S.CardtextDuration, Num(effect[T.Duration], effect[T.Duration]));
        }

        private bool _counter;
        private string _damageVerb;
        private string _counterMode;
        private readonly List<KeyValuePair<string, double>> _chances = new List<KeyValuePair<string, double>>();
        private readonly HashSet<string> _dependentStatuses = new HashSet<string>(StringComparer.Ordinal);
        private List<Frag> _unconditional = new List<Frag>();

        private void CollectDependent(JObject predicate)
        {
            if (predicate == null) return;
            if (predicate.Str(T.P) == T.HasStatus && predicate.Str(T.Of) == T.Self) _dependentStatuses.Add(predicate.Str(T.Status));
            CollectDependent(predicate[T.Pred] as JObject);
            foreach (var child in Js.Items(predicate[T.Preds]).OfType<JObject>()) CollectDependent(child);
        }

        private static List<Frag> One(string cat, string text) => new List<Frag> { new Frag { Cat = cat, Text = text } };

        private List<FaceLine> Build(FaceOptions options)
        {
            // A live preview prices the effects an active charge expands (an added buildup row); its values follow that list.
            _def = WithChargedEffects(_def, _preview);
            if (Js.Truthy(_def[T.ComboHook])) return null;
            // A Power's status is its mechanic's carrier ("Gain Goreblood 1" says nothing); its authored text explains it.
            if (_def.Str(T.Type) == T.Power) return null;
            if (_t.In(T.AuthoredRiderCards, _def.Str(T.Id))) return null;
            // A curse's hand trigger and an Ashen Blight price live only in the authored text.
            if ((_def[T.OnTurnEndInHand] is JArray hook && hook.Count > 0) || Js.Truthy(_def[T.AshenBlightCost])) return null;

            Prepare();
            _currentRepresentedRules = options.RepresentedRules;
            _poiseMerge = _env.WardReadsAsPoise ? PoiseMergeGroups(Js.Items(_def[T.Effects]).OfType<JObject>().ToList()) : null;
            foreach (var effect in Js.Items(_def[T.Effects]).OfType<JObject>()) CollectDependent(effect[T.If] as JObject);
            _counter = _profile.Str(T.Maneuver) == T.Counter;
            // A counter is prepared, not dealt: name its mode ("Prepare Melee Counter: return ...").
            _counterMode = _counter ? CounterModeOf() : null;
            if (_counter && _counterMode == null && !(Js.Truthy(_def[T.DefenseRulesVersion]) && _def.Num(T.DefenseRulesVersion) == Ashen.Generated.P1Count.Pair)) return null;
            var defenseVersionTwo = Js.IsNum(_def[T.DefenseRulesVersion]) && _def.Num(T.DefenseRulesVersion) == Ashen.Generated.CombatMath.Two;
            _damageVerb = _counter ? (defenseVersionTwo ? _t.Word(S.CardtextReturn) : _t.Say(S.CardtextPrepareCounterReturn, _t.Word(_counterMode == T.Melee ? S.CardtextModeMelee : _counterMode == T.Ranged ? S.CardtextModeRanged : S.CardtextModeSpell)))
                : _t.Word(S.CardtextInflict);

            // Saved profiles may still carry the old verbose template. Phrase this complete grant from the shared configured/live value on every face.
            if (EvadeGrantOnlyCard(_def))
                return new List<FaceLine> { new FaceLine { Kind = T.Gain, Html = _t.Say(S.CardtextEvadeOnly, Num(Value((JObject)_def.Arr(T.Effects)[0], 0, T.Stacks))) } };

            var groups = new List<KeyValuePair<string, Group>>();
            Group GroupFor(string key, Func<Group> make)
            {
                var found = groups.FirstOrDefault(pair => pair.Key == key);
                if (found.Value != null) return found.Value;
                var made = make();
                groups.Add(new KeyValuePair<string, Group>(key, made));
                return made;
            }
            var effects = Js.Items(_def[T.Effects]).OfType<JObject>().ToList();
            for (var index = 0; index < effects.Count; index++)
            {
                var effect = effects[index];
                if (options.RepresentedEffects.Contains(index)) continue;
                var fragments = PhraseEffect(effect, index);
                if (Js.Truthy(effect[T.OncePerTurn]) && fragments.Count > 0) fragments[fragments.Count - 1].Text += _t.Word(S.CardtextOncePerTurn);
                if (effect[T.If] != null && fragments.Count == 0) continue; // a condition with nothing left to say (a Core return zeroed its row) is not listed
                if (effect[T.If] != null)
                {
                    var key = Canon(effect[T.If]).ToString(Newtonsoft.Json.Formatting.None);
                    var group = GroupFor(key, () => new Group { Prefix = PrefixFor((JObject)effect[T.If]), Sleeping = effect[T.If].ToString(Newtonsoft.Json.Formatting.None).Contains(_t.Word(S.CardtextSleepMark)) });
                    group.Fragments.AddRange(fragments);
                }
                else _unconditional.AddRange(fragments);
            }

            // Counter: what the payload returns, for each part the effects do not already deal.
            var returnRead = _preview?[T.CounterReturn] as JObject;
            var counterUncovered = _counter && returnRead != null && returnRead[T.Uncovered] != null && returnRead.Is(T.Uncovered);
            var coreSpellFace = _counter && !counterUncovered && returnRead == null && Js.Truthy(_def[T.CounterCoverage]) && _profile.Str(T.Camp) == T.Spell
                && CoreFixCore.Enabled(_c) && CoreMatchups.ArmsAsCoreCounter(_c, WithProfile(_def, _profile));
            if (coreSpellFace) _unconditional.Add(new Frag { Cat = T.Damage, Text = _t.Esc(_t.Word(S.CardtextCounterFaceCore)) });
            if (_counter && !counterUncovered && !coreSpellFace)
            {
                var payload = _def.Obj(T.CounterPayload) ?? new JObject();
                double poiseReturn = 0, poiseAuthored = 0;
                foreach (var row in _t.Table(T.CounterReturns).Properties())
                {
                    var key = row.Name;
                    var label = ((JArray)row.Value)[0].Value<string>();
                    var op = ((JArray)row.Value)[1].Value<string>();
                    if (Js.Items(_def[T.Effects]).Any(effect => Js.Str(Js.Get(effect, T.Op)) == op)) continue;
                    // Core 2 (SPEC 17): the computed return against the revealed attack replaces the authored payload; Ward is not part of it.
                    JToken live;
                    if (returnRead != null) live = key == T.Ward ? Js.N(0) : returnRead[key];
                    else live = (_preview?[T.Values] as JArray)?.OfType<JObject>().FirstOrDefault(r => r.Str(T.Op) == op)?[T.Value];
                    var amount = !Js.Nullish(live) ? live : payload[key];
                    if (!Js.IsNum(amount) || !(Js.D(amount) > 0)) continue;
                    if (_poiseMerge != null && key != T.Hp) { poiseReturn += Js.D(amount); poiseAuthored += Js.Or0(payload[key]); continue; }
                    var type = key == T.Hp ? DamageLabel(null) : string.Empty;
                    _unconditional.Add(new Frag { Cat = T.Damage, Text = _t.Say(S.CardtextReturnRow, Typed(Num(amount, payload[key]), type), label) });
                }
                if (poiseReturn > 0) _unconditional.Add(new Frag { Cat = T.Damage, Text = _t.Say(S.CardtextPoiseReturn, Num(poiseReturn, poiseAuthored)) });
                // Beating the revealed hit also adds Block before it lands.
                if (returnRead != null && returnRead.Is(T.Advantaged) && returnRead.Num(T.Block) > 0)
                    _unconditional.Add(new Frag { Cat = T.Bonus, Text = _t.Say(S.CardtextBlockAgainst, Num(returnRead[T.Block])) });
            }
            if (_counter && !counterUncovered && returnRead == null && Js.Truthy(_def.Obj(T.CounterPayload)?[T.SmashPoiseBonus]))
                _unconditional.Add(new Frag { Cat = T.Damage, Text = _t.Say(S.CardtextPoiseAgainstSmash, Num(_def.Obj(T.CounterPayload)[T.SmashPoiseBonus])) });
            // Every card tagged maneuver:smash says the Core rule, from one shared row (model/smashClause.js).
            var smash = _env.SmashClause(_def);
            if (!string.IsNullOrEmpty(smash) && !options.RepresentedRules.Contains(T.SmashRule)) _unconditional.Add(new Frag { Cat = T.Bonus, Text = _t.Esc(smash) });
            if (Js.Truthy(_def[T.PronePoiseBonus]) && !(_counter && returnRead != null)) _unconditional.Add(new Frag { Cat = T.Bonus, Text = _t.Say(S.CardtextPoiseWhileProne, Num(_def[T.PronePoiseBonus])) });
            // Live Precision grant when a preview has an actor, else the 1 + stat mix formula.
            string EntryPrecisionText(double amount) => Js.IsFinite(_preview?[T.EntryPrecision]) ? _t.Say(S.CardtextPlus, Num(_preview[T.EntryPrecision])) : _t.Say(S.CardtextPlusStatMix, Num(amount));
            var coreProfile = CoreFixCore.Enabled(_c);
            var entryLabel = options.EntryLabel ?? _t.Word(S.CardtextEntry);
            var versioned = (Js.IsNum(_def[T.DefenseRulesVersion]) && _def.Num(T.DefenseRulesVersion) == Ashen.Generated.CombatMath.Two)
                || (Js.IsNum(_def[T.MinCombatExpansionVersion]) && _def.Num(T.MinCombatExpansionVersion) == Ashen.Generated.CombatMath.Two);
            if (options.StanceEntry && !versioned && coreProfile)
            {
                var precision = CoreStances.EntryBonuses(_c, _def).Num(T.Precision);
                if (precision > 0 && !options.RepresentedEntry.Contains(T.Precision))
                    _unconditional.Add(new Frag { Cat = T.Bonus, Text = _t.Say(S.CardtextEntryLine, _t.Esc(entryLabel), EntryPrecisionText(precision), _t.Word(S.CardtextEntryPrecision)) });
            }
            if (versioned)
            {
                var entry = CoreStances.EntryBonuses(_c, _def);
                var statMixGrant = entry.Num(T.Precision) > 0 && coreProfile;
                var grants = new List<string>();
                foreach (var id in _t.List(T.EntryOrder))
                {
                    var amount = entry.Num(id);
                    if (!(amount > 0) || options.RepresentedEntry.Contains(id)) continue;
                    grants.Add(_t.Say(S.CardtextEntryGrant, id == T.Precision && statMixGrant ? EntryPrecisionText(amount) : _t.Say(S.CardtextPlus, Num(amount)), _t.Map(T.EntryNames, id)));
                }
                if (options.StanceEntry && grants.Count > 0) _unconditional.Add(new Frag { Cat = T.Bonus, Text = _t.Say(S.CardtextEntryJoined, _t.Esc(entryLabel), string.Join(_t.Word(S.CardtextListJoin), grants)) });
                var stance = CoreStances.StanceOf(_c, _def);
                if (stance != null && stance.Str(T.Maneuver) == T.Defend)
                {
                    var restore = Num(CoreStances.Rules(_c).Num(T.DefenseRestore));
                    _unconditional.Add(new Frag { Cat = T.DefenseRestoreKind, Text = _t.Say(S.CardtextRestoreDefense, restore) });
                }
                var evade = _def.Obj(T.Evade);
                if (evade != null && Js.Truthy(evade[T.WhileStatus]))
                {
                    var key = _t.Say(S.CardtextEvadeGroupKey, evade.Str(T.WhileStatus));
                    var made = new Group
                    {
                        Prefix = _t.Say(S.CardtextWhile, _t.Esc(Title(evade.Str(T.WhileStatus)))),
                        Fragments = One(T.Bonus, PrepareEvade(evade)),
                    };
                    groups.RemoveAll(pair => pair.Key == key);
                    groups.Add(new KeyValuePair<string, Group>(key, made));
                }
            }
            else if (_def.Obj(T.Evade) != null)
            {
                var evade = _def.Obj(T.Evade);
                _unconditional.Add(new Frag { Cat = T.Bonus, Text = (Js.Truthy(evade[T.WhileStatus]) ? _t.Say(S.CardtextWhile, _t.Esc(Title(evade.Str(T.WhileStatus)))) : string.Empty) + PrepareEvade(evade) });
            }

            var keywordList = Js.Items(_def[T.Keywords]).Select(Js.Str).ToList();
            var known = _t.List(T.Keywords);
            if (keywordList.Any(keyword => !known.Contains(keyword))) return null;
            var keywords = known.Where(keyword => keywordList.Contains(keyword)).Select(keyword => char.ToUpperInvariant(keyword[0]) + keyword.Substring(1)).ToList();
            var byKind = _t.List(T.LineOrder).ToDictionary(kind => kind, kind => new List<string>());
            foreach (var segment in Segments(_unconditional, keywords, true)) byKind[segment.Key].Add(segment.Value);
            foreach (var pair in groups)
            {
                var group = pair.Value;
                // Only damage wakes a sleeper; a rider keyed on Sleep that deals none says so, as its authored text does.
                var wakes = group.Fragments.Any(f => f.Cat == T.Damage);
                byKind[T.Condition].Add(group.Prefix + string.Join(_t.Word(S.CardtextSentenceJoin), Segments(group.Fragments, new List<string>(), false).Select(s => s.Value))
                    + (group.Sleeping && !wakes ? _t.Word(S.CardtextDoesNotWake) : string.Empty));
            }
            var byChance = new List<KeyValuePair<double, List<string>>>();
            foreach (var chance in _chances)
            {
                var found = byChance.FirstOrDefault(pair => pair.Key == chance.Value);
                if (found.Value == null) { found = new KeyValuePair<double, List<string>>(chance.Value, new List<string>()); byChance.Add(found); }
                if (!found.Value.Contains(chance.Key)) found.Value.Add(chance.Key);
            }
            foreach (var pair in byChance) byKind[T.Detail].Add(_t.Say(S.CardtextChance, string.Join(_t.Word(S.CardtextListJoin), pair.Value.Select(_t.Esc)), _t.Esc(CardTextData.Num(pair.Key))));
            if (Js.Items(_def[T.UsableWhile]).Any())
                byKind[T.Detail].Add(_t.Say(S.CardtextUsableThrough, string.Join(_t.Word(S.CardtextListJoin), Js.Items(_def[T.UsableWhile]).Select(id => _t.Esc(Title(Js.Str(id)))))));

            var lines = new List<FaceLine>();
            foreach (var kind in _t.List(T.LineOrder))
                foreach (var html in byKind[kind])
                    if (!string.IsNullOrEmpty(html)) lines.Add(new FaceLine { Kind = kind, Html = html });
            // Nothing phrased (a status or curse with no effects) keeps the authored text, as combatCardSummary does.
            return lines.Count > 0 || options.RepresentedEffects.Count > 0 ? lines : null;
        }

        private static JObject WithProfile(JObject def, JObject profile)
        {
            var copy = Js.Spread(def);
            copy[T.CombatProfile] = profile;
            return copy;
        }

        private bool DefenseRulesTwo() => Js.IsNum(_def[T.DefenseRulesVersion]) && _def.Num(T.DefenseRulesVersion) == Ashen.Generated.CombatMath.Two;

        private string PrepareEvade(JObject evade) => _t.Word(S.CardtextPrepareEvade) + (Js.Truthy(evade[T.Advantage]) ? _t.Word(S.CardtextWithAdvantage) : string.Empty);

        /// <summary>A card whose whole authored rule is its configured self Evade grant.</summary>
        public static bool EvadeGrantOnlyCard(JObject def)
        {
            if (!(def[T.Effects] is JArray effects) || effects.Count != 1 || !(effects[0] is JObject effect)) return false;
            if (effect.Str(T.Op) != T.ApplyStatus || effect.Str(T.Status) != T.Evade || effect.Str(T.Target) != T.Self) return false;
            if (effect.Properties().Any(p => p.Name != T.Op && p.Name != T.Target && p.Name != T.Status && p.Name != T.Stacks)) return false;
            return effect[T.Stacks] is JObject stacks && stacks.Str(T.F) == T.Metric && stacks.Str(T.MetricKey) == T.EvadeStackGain && stacks.Str(T.Of) == T.Self
                && stacks.Properties().All(p => p.Name == T.F || p.Name == T.MetricKey || p.Name == T.Of);
        }

        /// <summary>poiseMergeGroups(effects): effect index to its group, for every Poise or Ward damage effect that shares a target, condition and once-per-turn flag with a Ward damage effect.</summary>
        private Dictionary<int, Merge> PoiseMergeGroups(List<JObject> effects)
        {
            var groups = new List<KeyValuePair<string, List<int>>>();
            for (var index = 0; index < effects.Count; index++)
            {
                var effect = effects[index];
                var op = effect.Str(T.Op);
                if (op != T.PoiseDamage && op != T.WardDamage) continue;
                var key = _t.Say(S.CardtextMergeKey, effect.Str(T.Target) ?? string.Empty, (effect[T.If] != null ? Canon(effect[T.If]).ToString(Newtonsoft.Json.Formatting.None) : string.Empty), Js.Truthy(effect[T.OncePerTurn]) ? 1 : 0);
                var found = groups.FirstOrDefault(pair => pair.Key == key);
                if (found.Value == null) { found = new KeyValuePair<string, List<int>>(key, new List<int>()); groups.Add(found); }
                found.Value.Add(index);
            }
            var result = new Dictionary<int, Merge>();
            foreach (var group in groups)
            {
                if (!group.Value.Any(index => effects[index].Str(T.Op) == T.WardDamage)) continue;
                for (var i = 0; i < group.Value.Count; i++) result[group.Value[i]] = new Merge { Leader = i == 0, Indices = group.Value };
            }
            return result;
        }

        private string DamageLabel(int? index) => _env.DamageLabel(_def, _preview, index);

        // ------------------------------------------------------------------ one effect

        private List<Frag> PhraseEffect(JObject effect, int index)
        {
            var n = Value(effect, index);
            var @base = BaseOf(effect, index, T.Amount);
            var area = AreaOf(effect.Str(T.Target));
            switch (effect.Str(T.Op))
            {
                case var op when op == T.Damage: return PhraseDamage(effect, index, n, @base, area);
                case var op when op == T.PoiseDamage || op == T.WardDamage:
                {
                    // Core 2: the computed return stands for every authored Poise row; a row the return zeroes is not listed.
                    if (_counter && _preview?[T.CounterReturn] != null && !(Js.IsNum(n) && Js.D(n) > 0)) return new List<Frag>();
                    // Ward off (Core 2): Ward damage drains Poise, so the face reads one combined Poise figure.
                    if (_poiseMerge != null && _poiseMerge.TryGetValue(index, out var merged))
                    {
                        if (!merged.Leader) return new List<Frag>();
                        var effects = Js.Items(_def[T.Effects]).OfType<JObject>().ToList();
                        var live = merged.Indices.Select(i => Value(effects[i], i)).ToList();
                        var authored = merged.Indices.Select(i => BaseOf(effects[i], i, T.Amount)).ToList();
                        double Sum(IEnumerable<JToken> list) => list.Sum(x => Js.IsNum(x) ? Js.D(x) : double.NaN);
                        var allNumbers = authored.All(Js.IsNum);
                        var total = live.Any(x => !Js.IsNum(x)) ? Js.S(_t.Word(S.CardtextUnknown)) : Js.N(Sum(live));
                        return One(T.Damage, _t.Say(S.CardtextPoiseDamageArea, Num(total, allNumbers ? Js.N(Sum(authored)) : null), area));
                    }
                    return One(T.Damage, _t.Say(op == T.PoiseDamage ? S.CardtextPoiseDamage : S.CardtextWardDamage, Num(n, @base), area));
                }
                case var op when op == T.Block: return PhraseBlock(effect, index, n, @base);
                case var op when op == T.GainBarrier: return PhraseBarrier(effect, index, n, @base);
                case var op when op == T.GainWard:
                    return One(T.Gain, _t.Say(S.CardtextWardGain, Num(n, @base)) + (Js.Truthy(effect[T.OncePerCombat]) ? _t.Word(S.CardtextOncePerCombat) : string.Empty));
                case var op when op == T.GainPoise: return One(T.Gain, _t.Say(S.CardtextPoiseGuard, Num(n, @base)));
                case var op when op == T.RestoreMana: return One(T.Gain, _t.Say(S.CardtextManaGain, Num(n, @base)));
                case var op when op == T.RestoreStamina || op == T.GainEnergy: return One(T.Gain, _t.Say(S.CardtextSpGain, Num(n, @base)));
                case var op when op == T.Heal:
                    return One(T.Bonus, _t.Say(S.CardtextHeal, effect.Str(T.Target) == T.Ally ? _t.Word(S.CardtextAnAllyFor) : string.Empty, Num(n, @base)));
                case var op when op == T.Buildup:
                {
                    // The face names the status and amount; a partial chance is kept for the detail line.
                    if (Js.IsNum(effect[T.Chance]) && effect.Num(T.Chance) < Ashen.Generated.CombatMath.Percent) _chances.Add(new KeyValuePair<string, double>(Title(effect.Str(T.Status)), effect.Num(T.Chance)));
                    return One(T.Status, _t.Say(S.CardtextNameAmountArea, _t.Esc(Title(effect.Str(T.Status))), Num(n, @base), area));
                }
                case var op when op == T.ApplyStatus: return PhraseStatus(effect, index, area);
                case var op when op == T.RemoveStatus:
                {
                    var amount = effect[T.Amount] == null ? _t.Word(S.CardtextAll) : Num(n, @base);
                    var suffix = effect.Str(T.Target) == T.Enemy ? _t.Word(S.CardtextFromTarget) : effect.Str(T.Target) == T.Ally ? _t.Word(S.CardtextFromAlly) : area;
                    var cat = effect.Str(T.Target) == T.Self && _dependentStatuses.Contains(effect.Str(T.Status)) ? T.Consume : T.Remove;
                    return One(cat, _t.Say(S.CardtextRemoveRow, amount, _t.Esc(Title(effect.Str(T.Status))), suffix));
                }
                case var op when op == T.MoveEngagement:
                {
                    if (!(effect.Str(T.Target) == T.Enemy || effect.Str(T.Target) == T.PendingAttacker) || !Js.IsNum(effect[T.Bands]) || Math.Abs(effect.Num(T.Bands)) != 1
                        || effect.Properties().Any(p => !_t.In(T.MoveKeys, p.Name))) throw Fail();
                    var close = effect.Num(T.Bands) < 0;
                    var target = effect.Str(T.Target) == T.PendingAttacker ? _t.Word(S.CardtextAttacker) : _t.Word(S.CardtextTargetWord);
                    return One(T.Bonus, _t.Say(close ? S.CardtextClose : S.CardtextRetreat, Num(Math.Abs(effect.Num(T.Bands))), target));
                }
                case var op when op == T.Draw: return One(T.Bonus, _t.Say(S.CardtextDraw, Num(n, @base)));
                case var op when op == T.Discard:
                    return One(T.Bonus, _t.Say(Js.Truthy(effect[T.Choose]) ? S.CardtextChooseDiscard : S.CardtextDiscard, Num(n, @base), Js.Truthy(effect[T.Random]) ? _t.Word(S.CardtextAtRandom) : string.Empty));
                case var op when op == T.LoseHp:
                    return One(index == 0 && effect[T.If] == null ? T.Cost : T.Bonus,
                        _t.Say(Js.Truthy(effect[T.Offering]) ? S.CardtextOffer : S.CardtextPay, Num(n, @base), Js.Truthy(effect[T.Nonlethal]) ? _t.Word(S.CardtextLeaveOne) : string.Empty));
                case var op when op == T.Retain: return One(T.Bonus, _t.Say(S.CardtextRetain, Num(n, @base)));
                case var op when op == T.GrantRollMode:
                    return One(T.Bonus, _t.Say(S.CardtextNextRoll, _t.Esc(CardTextData.Show(effect[T.Roll])), Js.Truthy(effect[T.Advantage]) ? _t.Word(S.CardtextAdvantage) : _t.Word(S.CardtextDisadvantage)));
                case var op when op == T.EnterStance:
                    return One(T.Bonus, Js.Truthy(effect[T.Choose]) ? _t.Word(S.CardtextChooseStance)
                        : _t.Say(S.CardtextEnter, _t.Esc(_c.Data.Stances.Has(effect.Str(T.Stance)) ? _c.Data.Stances.Get(effect.Str(T.Stance)).Str(T.Name) : effect.Str(T.Stance))));
                // A generated card's own cost and keywords are only in the authored text.
                case var op when op == T.AddCard: throw Fail();
                case var op when op == T.GrantCardCharge: return PhraseCharge(effect, index);
                default: throw Fail(); // Complex effects keep their authored text.
            }
        }

        private List<Frag> PhraseDamage(JObject effect, int index, JToken n, JToken @base, string area)
        {
            var live = Live(index);
            var hitsToken = live != null && !Js.Nullish(live[T.Hits]) ? live[T.Hits] : !Js.Nullish(effect[T.Hits]) ? effect[T.Hits] : Js.N(1);
            if (!Js.IsNum(hitsToken)) throw Fail();
            var hits = Js.D(hitsToken);
            // Core 2: one row carries the computed return; every other damage row of the Counter is zeroed and not listed.
            if (_counter && _preview?[T.CounterReturn] != null && live != null && hits == 0 && Js.IsNum(live[T.TotalDamage]) && live.Num(T.TotalDamage) == 0) return new List<Frag>();
            var sequence = (live?[T.HitDamages] as JArray)?.Select(x => Js.D(x)).ToList();
            var unequal = sequence != null && sequence.Count > 1 && sequence.Any(amount => amount != sequence[0]);
            // The face names the hit's leading type only ("Lightning", not "Lightning . Piercing"); inspection keeps the full label.
            var type = DamageLabel(index).Split(new[] { _t.Word(S.CardtextTypeSeparator) }, StringSplitOptions.None)[0];
            var word = !string.IsNullOrEmpty(type) ? _t.Say(S.CardtextTypedDamage, _t.Esc(type)) : _t.Word(S.CardtextDamageWord);
            var hitsHtml = hits != 1 ? _t.Say(S.CardtextTimes, Num(hitsToken, Js.IsNum(effect[T.Hits]) ? effect[T.Hits] : null)) : string.Empty;
            // A hit part magic and part physical names both parts, the physical first; equal repeated hits split each hit.
            var split = !unequal ? _env.SplitOf(_def, _profile, n, live) : null;
            string text;
            if (split != null)
            {
                // Each part compares with the same split of the static base, so a boosted hit colours both parts.
                var baseSplit = Js.IsNum(@base) ? _env.SplitOf(_def, _profile, @base, null) : null;
                text = _t.Say(S.CardtextSplitDamage, Typed(Num(split.Physical, baseSplit?.Physical), TypeWord(split.PhysicalType)), Typed(Num(split.Magic, baseSplit?.Magic), TypeWord(split.MagicType))) + hitsHtml;
            }
            else if (unequal)
                text = _t.Say(S.CardtextSequence, string.Join(_t.Word(S.CardtextPlusJoin), sequence.Select(amount => Num(Js.N(amount), @base))), word, Num(live[T.TotalDamage], Js.IsNum(@base) ? Js.N(Js.D(@base) * sequence.Count) : null), sequence.Count);
            else text = _t.Say(S.CardtextAmountWord, Num(n, @base), word) + hitsHtml;
            return One(T.Damage, text + area);
        }

        private List<Frag> PhraseBlock(JObject effect, int index, JToken n, JToken @base)
        {
            var versioned = DefenseRulesTwo() || (Js.IsNum(_def[T.MinCombatExpansionVersion]) && _def.Num(T.MinCombatExpansionVersion) == Ashen.Generated.CombatMath.Two);
            var ally = effect.Str(T.Target) == T.Ally;
            var defense = versioned ? DefenseValue(effect, index) ?? _env.SpellDefenseSplit(WithProfile(_def, _profile), n) : null;
            if (defense != null)
            {
                var baseSplit = Js.IsNum(@base) ? _env.SpellDefenseSplit(WithProfile(_def, _profile), @base) : null;
                return One(ally ? T.Bonus : T.Gain, (ally ? _t.Word(S.CardtextAllyGains) : string.Empty)
                    + _t.Say(S.CardtextBlockWard, Num(defense[T.Block], baseSplit?[T.Block]), Num(defense[T.Ward], baseSplit?[T.Ward])));
            }
            if (ally) return One(T.Bonus, _t.Word(S.CardtextAllyGains) + _t.Say(S.CardtextBlockOnly, Num(n, @base)));
            var split = _env.SplitOf(_def, _profile, n, null);
            if (split != null)
            {
                // Each part compares with the same split of the static base, as a split hit does.
                var baseSplit = Js.IsNum(@base) ? _env.SplitOf(_def, _profile, @base, null) : null;
                return One(T.Gain, _t.Say(S.CardtextBlockWard, Num(split.Physical, baseSplit?.Physical), Num(split.Magic, baseSplit?.Magic)));
            }
            return One(T.Gain, _t.Say(S.CardtextBlockOnly, Num(n, @base)));
        }

        private List<Frag> PhraseBarrier(JObject effect, int index, JToken n, JToken @base)
        {
            // A Spell's gain refills Ward first, so it reads Ward.
            var ally = effect.Str(T.Target) == T.Ally;
            var split = DefenseValue(effect, index) ?? _env.SpellDefenseSplit(WithProfile(_def, _profile), n);
            if (split != null)
            {
                var baseSplit = Js.IsNum(@base) ? _env.SpellDefenseSplit(WithProfile(_def, _profile), @base) : null;
                return One(ally ? T.Bonus : T.Gain, (ally ? _t.Word(S.CardtextAllyGains) : string.Empty) + _t.Say(S.CardtextBlockWard, Num(split[T.Block], baseSplit?[T.Block]), Num(split[T.Ward], baseSplit?[T.Ward])));
            }
            return One(T.Gain, _t.Say(CoreStances.IsSpellCard(WithProfile(_def, _profile)) ? S.CardtextWardGain : S.CardtextBarrierGain, Num(n, @base)));
        }

        private List<Frag> PhraseStatus(JObject effect, int index, string area)
        {
            // Unmodelled per-application tags (for example Persistent) may carry authored rules. Preserve that prose rather than silently dropping it.
            if (effect[T.Tags] is JArray tags && tags.Count > 0) throw Fail();
            var status = effect.Str(T.Status);
            if (_t.In(T.NeverPhrased, status) && !(_counter && DefenseRulesTwo() && status == T.Grounded)) throw Fail();
            if (effect[T.Chance] != null)
            {
                if (!Js.IsNum(effect[T.Chance])) throw Fail();
                if (effect.Num(T.Chance) < Ashen.Generated.CombatMath.Percent) _chances.Add(new KeyValuePair<string, double>(Title(status), effect.Num(T.Chance)));
            }
            var stacks = Value(effect, index, T.Stacks);
            var stacksBase = BaseOf(effect, index, T.Stacks);
            var name = _t.Say(S.CardtextNameAmount, _t.Esc(Title(status)), Num(stacks, stacksBase));
            var duration = DurationOf(effect);
            var output = new List<Frag>();
            var target = effect.Str(T.Target);
            if (target == T.Self) output.Add(new Frag { Cat = T.Gain, Text = name + duration });
            else if (_t.In(T.EnemyTargets, target)) output.Add(new Frag { Cat = T.Status, Text = name + duration + area });
            else if (target == T.Ally) output.Add(new Frag { Cat = T.Bonus, Text = _t.Say(S.CardtextApplyToAlly, name, duration) });
            else output.Add(new Frag { Cat = T.Bonus, Text = _t.Say(S.CardtextApply, name, duration, area) });
            // Statuses whose name is not their effect: the face adds the status's own tooltip line.
            return AddMechanic(output, status);
        }

        private List<Frag> AddMechanic(List<Frag> output, string status)
        {
            if (!_t.In(T.MechanicStatuses, status)) return output;
            if (_currentRepresentedRules.Contains(_t.Say(S.CardtextStatusRule, status))) return output;
            var explanation = _c.Data.Statuses.Has(status) ? _c.Data.Statuses.Get(status).Str(T.Tooltip) : null;
            if (string.IsNullOrEmpty(explanation)) throw Fail();
            output.Add(new Frag { Cat = T.Bonus, Text = _t.Esc(Regex.Replace(explanation, _t.Word(S.CardtextTrailingPeriod), string.Empty)) });
            return output;
        }

        private List<string> _currentRepresentedRules = new List<string>();

        private List<Frag> PhraseCharge(JObject effect, int index)
        {
            var bonuses = new List<string>();
            foreach (var row in _t.Table(T.ChargeLabels).Properties())
            {
                var key = row.Name;
                if (effect[key] == null) continue;
                var value = Value(effect, index, key);
                var @base = BaseOf(effect, index, key);
                if (key == T.ManaDiscount) bonuses.Add(_t.Say(S.CardtextLessMana, Num(value, @base)));
                else
                {
                    var label = key == T.Buildup ? _t.Say(S.CardtextBuildupLabel, effect.Str(T.BuildupStatus) ?? string.Empty) : row.Value.Value<string>();
                    bonuses.Add(_t.Say(S.CardtextPlusLabel, Num(value, @base), _t.Esc(label)));
                }
            }
            var match = string.Join(_t.Word(S.CardtextSpace), new[] { effect.Str(T.CardType), effect.Str(T.AbilityKind), effect.Str(T.CardTag) }.Where(x => !string.IsNullOrEmpty(x)));
            return One(T.Bonus, _t.Say(S.CardtextNextCharge, _t.Esc(string.IsNullOrEmpty(match) ? _t.Word(S.CardtextMatchingCard) : match), string.Join(_t.Word(S.CardtextListJoin), bonuses)));
        }

        // ------------------------------------------------------------------ assembling

        /// <summary>Comma-join phrases; one that already ends a sentence drops its period first (no ".,"), as playingCard does.</summary>
        private string JoinList(List<string> parts) =>
            string.Join(_t.Word(S.CardtextListJoin), parts.Select((part, i) => i < parts.Count - 1 ? Regex.Replace(part, _t.Word(S.CardtextTrailingPeriod), string.Empty) : part));

        /// <summary>The line a set of fragments reads as, one segment per kind, in fixed order.</summary>
        private List<KeyValuePair<string, string>> Segments(List<Frag> fragments, List<string> extraBonus, bool isUnconditional)
        {
            var output = new List<KeyValuePair<string, string>>();
            List<string> Of(string cat) => fragments.Where(f => f.Cat == cat).Select(f => f.Text).ToList();
            var cost = Of(T.Cost);
            var damage = Of(T.Damage);
            var status = Of(T.Status);
            var remove = Of(T.Remove);
            var consume = Of(T.Consume);
            var gain = Of(T.Gain);
            var bonus = Of(T.Bonus).Concat(extraBonus).ToList();
            var defenseRestore = Of(T.DefenseRestoreKind);
            void Add(string kind, string text) => output.Add(new KeyValuePair<string, string>(kind, text));
            if (cost.Count > 0) Add(T.Cost, JoinList(cost));
            if (damage.Count > 0) Add(T.Damage, _t.Say(S.CardtextVerbRow, _damageVerb, JoinList(damage)));
            else if (_counter && isUnconditional && _def.Obj(T.Evade) == null)
                Add(T.Damage, DefenseRulesTwo() ? _t.Word(S.CardtextPrepareCounter) : Regex.Replace(_damageVerb, _t.Word(S.CardtextReturnSuffix), string.Empty));
            if (status.Count > 0) Add(T.Status, _t.Say(S.CardtextInflictRow, JoinList(status)));
            if (remove.Count > 0) Add(T.Status, _t.Say(S.CardtextRemoveRows, JoinList(remove)));
            if (consume.Count > 0) Add(T.Consume, _t.Say(S.CardtextRemoveRows, JoinList(consume)));
            if (gain.Count > 0) Add(T.Gain, _t.Say(S.CardtextGainRow, JoinList(gain)));
            if (bonus.Count > 0) Add(T.Bonus, defenseRestore.Count > 0 ? JoinList(defenseRestore) + _t.Word(S.CardtextSentenceJoin) + JoinList(bonus) : JoinList(bonus));
            else if (defenseRestore.Count > 0) Add(T.Bonus, JoinList(defenseRestore));
            return output;
        }

        private bool ThresholdInside(JObject pred)
        {
            if (pred == null) return false;
            if (pred.Str(T.P) == T.HasStatus && pred[T.AtLeast] != null) return true;
            return ThresholdInside(pred[T.Pred] as JObject) || Js.Items(pred[T.Preds]).OfType<JObject>().Any(ThresholdInside);
        }

        private bool StatusSnapshotInside(JObject pred)
        {
            if (pred == null) return false;
            if (pred.Str(T.P) != T.HasBlock && pred.Str(T.Snapshot) == T.BeforePlay) return true;
            return StatusSnapshotInside(pred[T.Pred] as JObject) || Js.Items(pred[T.Preds]).OfType<JObject>().Any(StatusSnapshotInside);
        }

        /// <summary>A predicate's label: the status name for the card's own status, else "If ...".</summary>
        private string PrefixFor(JObject predicate)
        {
            if (predicate.Str(T.P) == T.HasStatus)
            {
                if (predicate.Properties().Any(p => !_t.In(T.PredicateKeys, p.Name)) || (predicate[T.AtLeast] != null && !Js.IsNum(predicate[T.AtLeast]))) throw Fail();
                var name = _t.Esc(Title(predicate.Str(T.Status)));
                var at = predicate[T.AtLeast];
                if (predicate.Str(T.Of) == T.Self) return at != null ? _t.Say(S.CardtextPrefixSelfAt, name, Num(at)) : _t.Say(S.CardtextPrefixSelf, name);
                if (at != null) return _t.Say(S.CardtextIfHasAtLeast, predicate.Str(T.Of) == T.AllEnemies ? _t.Word(S.CardtextAnEnemyHas) : _t.Word(S.CardtextTheTargetHas), Num(at), name);
            }
            if (ThresholdInside(predicate)) throw Fail();
            // "No Starstone Charge:" reads cleaner than "If not (you have Starstone Charge):".
            if (predicate.Str(T.P) == T.Not && predicate[T.Pred] is JObject inner && inner.Str(T.P) == T.HasStatus && inner.Str(T.Of) == T.Self
                && inner.Properties().All(p => _t.In(T.NotKeys, p.Name))) return _t.Say(S.CardtextNo, _t.Esc(Title(inner.Str(T.Status))));
            var text = Condition(predicate);
            if (text.Length == 0) throw Fail();
            return _t.Say(S.CardtextIf, _t.Esc(text), StatusSnapshotInside(predicate) ? _t.Word(S.CardtextBeforeThisCard) : string.Empty);
        }
    }
}
