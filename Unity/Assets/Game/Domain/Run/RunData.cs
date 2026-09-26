using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RV = Ashen.Generated.RunValues;
using V = Ashen.Generated.CombatValues;
using CombatMath = Ashen.Generated.CombatMath;

namespace Ashen.Domain.Run
{
    /// <summary>
    /// Everything run creation reads (the shipped createRegistries tables createRunState and enterCombat touch): the
    /// combat data (cards, relics, classes, equipment, balance, …) plus the creation tables — creation modes, seats,
    /// encounters, attribute rules, character creation, derived-stat rules, tag families — the content version, the
    /// hand-rule defaults (rules/handRules.json) and the run engine's own rules (rules/runEngine.json). Read-only.
    /// </summary>
    public sealed class RunData
    {
        public RunData(CombatData combat, Registry creationModes, Registry seats, Registry encounters, JObject attributeRules,
            JObject characterCreation, JObject derivedStatRules, JArray tagFamilies, string contentVersion, JObject handRules, JObject engine)
        {
            Combat = combat ?? throw new ArgumentNullException(nameof(combat));
            CreationModes = creationModes ?? throw new ArgumentNullException(nameof(creationModes));
            Seats = seats ?? throw new ArgumentNullException(nameof(seats));
            Encounters = encounters ?? throw new ArgumentNullException(nameof(encounters));
            AttributeRules = attributeRules;
            CharacterCreation = characterCreation;
            DerivedStatRules = derivedStatRules;
            TagFamilies = tagFamilies ?? new JArray();
            ContentVersion = contentVersion;
            HandRules = handRules ?? throw new ArgumentNullException(nameof(handRules));
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
            // The fight's mid-combat equipment intents and createCombat's profile fallback restamp through this data.
            combat.EquipmentPort = new CombatEquipment(this);
        }

        public CombatData Combat { get; }
        public Registry Classes => Combat.Classes;
        public Registry Cards => Combat.Cards;
        public Registry Relics => Combat.Relics;
        public Registry Attributes => Combat.Attributes;
        public Registry CreationModes { get; }
        public Registry Seats { get; }
        public Registry Encounters { get; }
        public JObject Equipment => Combat.Equipment;
        public JObject Balance => Combat.Balance;
        public JObject AttributeRules { get; }
        public JObject CharacterCreation { get; }
        public JObject DerivedStatRules { get; }
        public JArray TagFamilies { get; }
        public string ContentVersion { get; }

        /// <summary>rules/handRules.json: the shipped handRulesDefaults.</summary>
        public JObject HandRules { get; }

        /// <summary>rules/runEngine.json.</summary>
        public JObject Engine { get; }

        /// <summary><c>(registries.equipment || {})[key] || []</c>.</summary>
        public IEnumerable<JObject> EquipmentRows(string key) => Js.Items(Equipment[key]).OfType<JObject>();

        /// <summary><c>(registries.balance.equipment || {})</c>.</summary>
        public JObject EquipmentBalance => Balance.Obj(K.Equipment) ?? new JObject();

        public JToken Rule(string section, string key) => Engine.Obj(section)?[key];

        public double RuleNum(string section, string key) => Js.D(Rule(section, key));

        public string RuleStr(string section, string key) => Js.Str(Rule(section, key));

        public JObject RuleObj(string section, string key) => Rule(section, key) as JObject ?? new JObject();

        public List<string> RuleList(string section, string key) => Js.Items(Rule(section, key)).Select(Js.Str).ToList();

        public List<double> RuleNums(string section, string key) => Js.Items(Rule(section, key)).Select(Js.D).ToList();

        /// <summary>
        /// The run registry dump written by Tools/oracle-run.mjs: the combat dump's tables plus creationModes, seats,
        /// encounters, attributeRules, characterCreation, derivedStatRules, tagFamilies and contentVersion.
        /// </summary>
        public static RunData FromRegistryDump(JObject dump, JObject mechanics, JObject combatEngine, JObject handRules, JObject runEngine)
        {
            var combat = CombatData.FromRegistryDump(dump, mechanics, combatEngine);
            return new RunData(combat,
                new Registry(RK.CreationModes, Js.Items(dump[RK.CreationModes]), K.Id),
                new Registry(RK.Seats, Js.Items(dump[RK.Seats]), K.Id),
                new Registry(RK.Encounters, Js.Items(dump[RK.Encounters]), K.Id),
                dump.Obj(RK.AttributeRules), dump.Obj(RK.CharacterCreation), dump.Obj(RK.DerivedStatRules),
                dump[RK.TagFamilies] as JArray, dump.Str(RK.ContentVersion), handRules, runEngine);
        }
    }

    /// <summary>JS semantics the run port needs beyond <see cref="Js"/>.</summary>
    public static class RunJs
    {
        /// <summary>JS <c>Math.round</c>: halves round toward +∞.</summary>
        public static double Round(double x) => Math.Floor(x + CombatMath.Half);

        /// <summary><c>String(n)</c> for the numbers content carries (integers and short decimals).</summary>
        public static string NumStr(double n)
        {
            if (double.IsNaN(n)) return RV.NaN;
            if (n == Math.Floor(n) && Math.Abs(n) < long.MaxValue) return ((long)n).ToString(CultureInfo.InvariantCulture);
            return n.ToString(RV.RoundTripFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>JS <c>Number(v)</c>: undefined → NaN, null → 0, booleans → 0/1, strings parsed (blank → 0).</summary>
        public static double Number(JToken v)
        {
            if (v == null || v.Type == JTokenType.Undefined) return double.NaN;
            switch (v.Type)
            {
                case JTokenType.Null:
                    return 0;
                case JTokenType.Boolean:
                    return v.Value<bool>() ? 1 : 0;
                case JTokenType.Integer:
                case JTokenType.Float:
                    return Js.D(v);
                case JTokenType.String:
                    var s = v.Value<string>().Trim();
                    if (s.Length == 0) return 0;
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
                default:
                    return double.NaN;
            }
        }

        /// <summary>A plain JS object (not null, not an array).</summary>
        public static bool IsPlain(JToken v) => v is JObject;

        /// <summary>structuredClone for a JSON value (null stays null).</summary>
        public static T Clone<T>(T v) where T : JToken => v == null ? null : (T)v.DeepClone();

        /// <summary><c>JSON.stringify(v)</c> as a comparison key (undefined → empty).</summary>
        public static string Json(JToken v) => v == null ? string.Empty : v.ToString(Formatting.None);

        /// <summary>A developer-facing message from a generated template.</summary>
        public static string Fmt(string template, params object[] args) => string.Format(CultureInfo.InvariantCulture, template, args);

        /// <summary>The string items of a JSON array (non-strings as null).</summary>
        public static List<string> Strs(JToken array) => Js.Items(array).Select(Js.Str).ToList();

        /// <summary><c>JSON.stringify(value)</c> of a scalar for a message.</summary>
        public static string Show(JToken v) => v == null ? V.Undefined : v.ToString(Formatting.None);

        /// <summary>The property key JS uses for a value (<c>obj[value]</c>): undefined, null, numbers and booleans stringify.</summary>
        public static string Key(JToken v)
        {
            if (v == null || v.Type == JTokenType.Undefined) return V.Undefined;
            switch (v.Type)
            {
                case JTokenType.Null:
                    return RV.Null;
                case JTokenType.String:
                    return v.Value<string>();
                case JTokenType.Integer:
                case JTokenType.Float:
                    return NumStr(Js.D(v));
                case JTokenType.Boolean:
                    return v.Value<bool>() ? RV.True : RV.False;
                default:
                    return v.ToString(Formatting.None);
            }
        }

        /// <summary><c>a ?? b</c>: the first token that is neither undefined nor null (or null).</summary>
        public static JToken Coalesce(params JToken[] tokens)
        {
            foreach (var t in tokens) if (!Js.Nullish(t)) return t;
            return null;
        }
    }
}
