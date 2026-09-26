using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.Content
{
    /// <summary>
    /// The runtime registries: the content tables as the engine sees them, after the transforms the shipped
    /// <c>createRegistries(contentBundle)</c> (src/model/registries.js) applies before freezing — card value
    /// bonuses materialized into balance.damage, attack/Block/poise damage projected from the cost formulas
    /// (with the school variant a profile can move a card into), and every tagged collection stamped with its tags.
    /// Tables are id-keyed objects in authoring order; <see cref="Equipment"/> and <see cref="Balance"/> are the
    /// frozen equipment tables and balance constants. Reads return deep clones.
    /// </summary>
    public sealed class RuntimeRegistries
    {
        /// <summary>The id-keyed registry tables, in the shipped REGISTRY_TYPES order used by the parity oracle.</summary>
        public static readonly IReadOnlyList<string> TableNames = new[]
        {
            RegistryKeys.Cards, RegistryKeys.Relics, RegistryKeys.Statuses, RegistryKeys.Stances, RegistryKeys.Keywords,
            RegistryKeys.Enemies, RegistryKeys.Encounters, RegistryKeys.Flasks, RegistryKeys.Classes, RegistryKeys.Events,
        };

        /// <summary>The further tables the combat engine reads (attributes by id, property rules by tag).</summary>
        public static readonly IReadOnlyList<string> CombatTableNames = new[] { RegistryKeys.Attributes, RegistryKeys.PropertyRules };

        private readonly Dictionary<string, JObject> _tables;
        private readonly JArray _classTree;
        private readonly JObject _equipment;
        private readonly JObject _balance;

        private RuntimeRegistries(Dictionary<string, JObject> tables, JArray classTree, JObject equipment, JObject balance)
        {
            _tables = tables;
            _classTree = classTree;
            _equipment = equipment;
            _balance = balance;
        }

        /// <summary>Builds the registries from an effective content set (e.g. <c>RunSnapshot.Content</c>) and its display strings.</summary>
        public static RuntimeRegistries Build(ContentSet content, JObject stringsEn)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            var bundle = AttackCardDamage.ProjectBundle(RegistryBundle.Load(content, stringsEn));
            var stamped = TagStamp.Stamp(bundle);
            JArray Collection(string source) =>
                stamped.Where(kv => kv.Key == source).Select(kv => kv.Value).FirstOrDefault() ?? bundle[source] as JArray ?? new JArray();

            var tables = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var name in TableNames) tables[name] = MakeRegistry(name, Collection(name));
            tables[RegistryKeys.Attributes] = MakeRegistry(RegistryKeys.Attributes, Collection(RegistryKeys.Attributes));
            tables[RegistryKeys.PropertyRules] = MakeRegistry(RegistryKeys.PropertyRules, Collection(RegistryKeys.PropertyRules), CombatKeys.Tag);
            var classTree = (JArray)Collection(RegistryKeys.ClassTree).DeepClone();

            var equipment = JsValues.Spread(bundle[RegistryKeys.Equipment] as JObject);
            foreach (var kv in stamped) WriteEquipment(equipment, kv.Key, kv.Value);
            // The card-tag index equipment fit reads, folded from THIS bundle's stamped cards (never a stale copy).
            if (bundle[RegistryKeys.Tagging] is JArray)
            {
                var cardTagging = new JArray();
                foreach (var card in tables[RegistryKeys.Cards].Properties().Select(p => p.Value))
                    if (JsValues.Get(card, TagKeys.Tags) is JArray tags && tags.Count > 0)
                        cardTagging.Add(new JObject { [TagKeys.CardTaggingCardId] = card[RegistryKeys.Id]?.DeepClone(), [TagKeys.Tags] = tags.DeepClone() });
                equipment[RegistryKeys.CardTagging] = cardTagging;
            }
            else equipment.Remove(RegistryKeys.CardTagging);

            var balance = JsValues.Spread(bundle[RegistryKeys.Balance] as JObject);
            return new RuntimeRegistries(tables, classTree, equipment, balance);
        }

        /// <summary>An id-keyed registry table ("cards", "relics", ...), or null for a name that is not one.</summary>
        public JObject Table(string name) => name != null && _tables.TryGetValue(name, out var t) ? (JObject)t.DeepClone() : null;

        /// <summary>The equipment tables (armaments, armour, slots, profiles, ... with tags stamped, plus cardTagging).</summary>
        public JObject Equipment => (JObject)_equipment.DeepClone();

        /// <summary>The class tree rows (classId, nodeId, ...) in authoring order.</summary>
        public JArray ClassTree => (JArray)_classTree.DeepClone();

        /// <summary>
        /// The combat engine's view of these registries (D-041): every table it reads as an ordered registry, plus the
        /// class tree, equipment and balance, with the framework mechanics and the engine rules.
        /// </summary>
        public Ashen.Domain.Combat.CombatData ToCombatData(JObject mechanics, JObject engine)
        {
            var tables = new Dictionary<string, Ashen.Domain.Combat.Registry>(StringComparer.Ordinal);
            foreach (var name in Ashen.Domain.Combat.CombatData.TableNames)
            {
                if (!_tables.TryGetValue(name, out var table)) continue;
                var key = name == RegistryKeys.PropertyRules ? CombatKeys.Tag : RegistryKeys.Id;
                tables[name] = new Ashen.Domain.Combat.Registry(name, table.Properties().Select(p => p.Value.DeepClone()).ToList(), key);
            }
            return new Ashen.Domain.Combat.CombatData(tables, ClassTree, Equipment, Balance, mechanics, engine);
        }

        /// <summary>The balance constants, with balance.damage's statusMultipliers and cardBonuses materialized.</summary>
        public JObject Balance => (JObject)_balance.DeepClone();

        /// <summary>The shipped makeRegistry: every def has a string id, unique within its table.</summary>
        private static JObject MakeRegistry(string name, JArray defs, string key = RegistryKeys.Id)
        {
            var byId = new JObject();
            foreach (var def in defs)
            {
                var id = JsValues.Str(JsValues.Get(def, key));
                if (id == null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, RegistryMessages.MissingId, name));
                if (byId.ContainsKey(id)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, RegistryMessages.DuplicateId, name, id));
                byId[id] = def.DeepClone();
            }
            return byId;
        }

        /// <summary>Writes a stamped equipment.* collection back at its whole dotted path, cloning parents so siblings survive.</summary>
        private static void WriteEquipment(JObject equipment, string source, JArray rows)
        {
            var parts = source.Split(RegistryKeys.PathSeparator[0]);
            if (parts[0] != RegistryKeys.Equipment || parts.Length == 1) return;
            var node = equipment;
            for (var i = 1; i < parts.Length - 1; i++)
            {
                var child = node[parts[i]] is JObject existing ? JsValues.Spread(existing) : new JObject();
                node[parts[i]] = child;
                node = child;
            }
            node[parts[parts.Length - 1]] = rows.DeepClone();
        }
    }
}
