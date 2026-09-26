using System;
using System.Collections.Generic;
using System.Globalization;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// One id-keyed registry table in authoring order (the shipped makeRegistry): <see cref="Get"/> throws on an
    /// unknown id, exactly as the shipped getter does.
    /// </summary>
    public sealed class Registry
    {
        private readonly Dictionary<string, JObject> _byId = new Dictionary<string, JObject>(StringComparer.Ordinal);
        private readonly List<JObject> _all = new List<JObject>();
        private readonly string _name;

        public Registry(string name, IEnumerable<JToken> rows, string key)
        {
            _name = name;
            foreach (var token in rows)
            {
                var row = (JObject)token;
                var id = row.Str(key);
                if (id == null || _byId.ContainsKey(id)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.BadRegistryRow, name, id));
                _byId[id] = row;
                _all.Add(row);
            }
        }

        public IReadOnlyList<JObject> All => _all;

        public int Count => _all.Count;

        public bool Has(string id) => id != null && _byId.ContainsKey(id);

        public JObject Get(string id)
        {
            if (id != null && _byId.TryGetValue(id, out var row)) return row;
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnknownRegistryId, _name, id));
        }

        public IEnumerable<string> Ids
        {
            get { foreach (var row in _all) yield return row.Str(K.Id); }
        }
    }

    /// <summary>
    /// Everything the combat engine reads that is not the fight itself: the runtime registry tables (as the shipped
    /// createRegistries builds them), the framework mechanics (rules/mechanics.json) and the engine's own rules
    /// (rules/combatEngine.json — limits, defaults and the card-property vocabulary maps). Read-only.
    /// </summary>
    public sealed class CombatData
    {
        private readonly Dictionary<string, JObject> _resolveCache = new Dictionary<string, JObject>(StringComparer.Ordinal);

        public CombatData(IReadOnlyDictionary<string, Registry> tables, JArray classTree, JObject equipment, JObject balance, JObject mechanics, JObject engine)
        {
            Tables = tables ?? throw new ArgumentNullException(nameof(tables));
            ClassTree = classTree ?? new JArray();
            Equipment = equipment ?? new JObject();
            Balance = balance ?? new JObject();
            Mechanics = mechanics ?? throw new ArgumentNullException(nameof(mechanics));
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public IReadOnlyDictionary<string, Registry> Tables { get; }

        public Registry Cards => Tables[K.Cards];
        public Registry Relics => Tables[K.Relics];
        public Registry Statuses => Tables[K.Statuses];
        public Registry Stances => Tables[K.Stances];
        public Registry Enemies => Tables[K.Enemies];
        public Registry Flasks => Tables[K.Flasks];
        public Registry Classes => Tables[K.Classes];
        public Registry PropertyRules => Tables[K.PropertyRules];
        public Registry Attributes => Tables[K.Attributes];

        public JArray ClassTree { get; }
        public JObject Equipment { get; }
        public JObject Balance { get; }
        public JObject Mechanics { get; }

        /// <summary>rules/combatEngine.json.</summary>
        public JObject Engine { get; }

        internal Dictionary<string, JObject> ResolveCache => _resolveCache;

        /// <summary>An engine rule number (rules/combatEngine.json section.key).</summary>
        public double Rule(string section, string key) => Js.D(Engine.Obj(section)?[key]);

        /// <summary>An engine vocabulary map (rules/combatEngine.json cardProperties.*, grip, ...).</summary>
        public JObject Map(string section, string key) => Engine.Obj(section)?.Obj(key) ?? new JObject();

        /// <summary>
        /// The registry dump layout written by Tools/oracle-combat.mjs: every table an array of rows in authoring
        /// order, plus classTree, equipment and balance.
        /// </summary>
        public static CombatData FromRegistryDump(JObject dump, JObject mechanics, JObject engine)
        {
            var tables = new Dictionary<string, Registry>(StringComparer.Ordinal);
            foreach (var name in TableNames)
                tables[name] = new Registry(name, Js.Items(dump[name]), name == K.PropertyRules ? K.Tag : K.Id);
            return new CombatData(tables, dump[K.ClassTree] as JArray, dump.Obj(K.Equipment), dump.Obj(K.Balance), mechanics, engine);
        }

        /// <summary>The registry tables the engine reads.</summary>
        public static readonly string[] TableNames =
        {
            K.Attributes, K.Cards, K.Relics, K.Statuses, K.Stances, K.Keywords, K.Enemies, K.Flasks, K.Classes, K.PropertyRules,
        };
    }
}
