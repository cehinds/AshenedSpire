using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using MK = Ashen.Generated.MapKeys;
using MV = Ashen.Generated.MapValues;

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
        private readonly JObject _map;
        private readonly JObject _run;
        private readonly JObject _events;
        private readonly JArray _tagging;
        private readonly JObject _legacyDungeons;
        private readonly JArray _unlocks;

        private RuntimeRegistries(Dictionary<string, JObject> tables, JArray classTree, JObject equipment, JObject balance, JObject map, JObject run, JObject events,
            JArray tagging, JObject legacyDungeons, JArray unlocks)
        {
            _run = run;
            _events = events;
            _tagging = tagging;
            _legacyDungeons = legacyDungeons;
            _unlocks = unlocks;
            _tables = tables;
            _classTree = classTree;
            _equipment = equipment;
            _balance = balance;
            _map = map;
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
            tables[MK.Seats] = MakeRegistry(MK.Seats, Collection(MK.Seats));
            tables[RunKeys.CreationModes] = MakeRegistry(RunKeys.CreationModes, Collection(RunKeys.CreationModes));
            tables[EventKeys.Speakers] = MakeRegistry(EventKeys.Speakers, Collection(EventKeys.Speakers));
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
            var tagging = (bundle[RegistryKeys.Tagging] as JArray)?.DeepClone() as JArray ?? new JArray();
            var legacyDungeons = content.Contains(ContentFiles.CatalogLegacyDungeons) ? content.Get(ContentFiles.CatalogLegacyDungeons).DeepClone() as JObject : null;
            var unlockTable = content.Contains(ContentFiles.CatalogUnlocks) ? content.Get(ContentFiles.CatalogUnlocks) as JObject : null;
            var unlocks = new JArray((unlockTable ?? new JObject()).Properties().Select(p => p.Value.DeepClone()));
            return new RuntimeRegistries(tables, classTree, equipment, balance, MapDocuments(content), RunDocuments(bundle, stamped), EventDocuments(content),
                tagging, legacyDungeons ?? new JObject(), unlocks);
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

        /// <summary>
        /// The map port's view of these registries (US-4.2): the encounter, event, enemy and seat registries in authoring
        /// order, the per-tier map configs, the quest gates on events, the boss locations, the run-shape limits, the
        /// legacy act bosses and balance.endless, with the map engine rules (rules/mapEngine.json).
        /// </summary>
        public Ashen.Domain.Map.MapData ToMapData(JObject mapEngine)
        {
            Ashen.Domain.Combat.Registry Table(string name) =>
                new Ashen.Domain.Combat.Registry(name, (_tables.TryGetValue(name, out var t) ? t : new JObject()).Properties().Select(p => p.Value.DeepClone()).ToList(), RegistryKeys.Id);
            JObject Doc(string key) => _map[key] is JObject o ? (JObject)o.DeepClone() : new JObject();
            return new Ashen.Domain.Map.MapData(
                Table(RegistryKeys.Encounters), Table(RegistryKeys.Events), Table(RegistryKeys.Enemies), Table(MK.Seats),
                Doc(MK.MapConfigs), Doc(MK.EventHistoryRequirements), Doc(MK.BossLocations), Doc(MK.MapShapeLimits),
                Doc(MK.LegacyActBosses), _balance[MK.Endless] is JObject endless ? (JObject)endless.DeepClone() : new JObject(),
                new Ashen.Domain.Map.MapRules(mapEngine));
        }

        /// <summary>
        /// The run documents createRegistries keeps beside the tables: characterCreation (its keepsakes stamped like
        /// every tagged collection), attributeRules, derivedStatRules, the tagFamilies rows and the tag tree (nodes).
        /// </summary>
        private static JObject RunDocuments(JObject bundle, List<KeyValuePair<string, JArray>> stamped)
        {
            var creation = JsValues.Spread(bundle[RunKeys.CharacterCreation] as JObject);
            var keepsakesPath = string.Join(RegistryKeys.PathSeparator, RunKeys.CharacterCreation, RegistryKeys.Keepsakes);
            var keepsakes = stamped.Where(kv => kv.Key == keepsakesPath).Select(kv => kv.Value).FirstOrDefault();
            if (keepsakes != null) creation[RegistryKeys.Keepsakes] = keepsakes.DeepClone();
            return new JObject
            {
                [RunKeys.CharacterCreation] = creation,
                [RunKeys.AttributeRules] = JsValues.Spread(bundle[RunKeys.AttributeRules] as JObject),
                [RunKeys.DerivedStatRules] = (bundle[RunKeys.DerivedStatRules] as JObject)?.DeepClone() ?? new JObject(),
                [RunKeys.TagFamilies] = (bundle[RegistryKeys.TagFamilies] as JArray)?.DeepClone() ?? new JArray(),
                [RewardsKeys.Nodes] = (bundle[RewardsKeys.Nodes] as JArray)?.DeepClone() ?? new JArray(),
            };
        }

        private Ashen.Domain.Combat.Registry DomainRegistry(string name) =>
            new Ashen.Domain.Combat.Registry(name, (_tables.TryGetValue(name, out var t) ? t : new JObject()).Properties().Select(p => p.Value.DeepClone()).ToList(), RegistryKeys.Id);

        /// <summary>
        /// The run port's view of these registries (US-2.2, D-042): the combat data plus the creation-mode, seat and
        /// encounter registries and the run documents, with the hand rules and the run engine rules. The content version
        /// is the shipped <c>String(bundle.version || bundle.contentVersion || '0')</c>, read from the manifest by the caller.
        /// </summary>
        public Ashen.Domain.Run.RunData ToRunData(JObject mechanics, JObject combatEngine, JObject handRules, JObject runEngine, string contentVersion)
        {
            JObject Doc(string key) => (JObject)_run[key].DeepClone();
            return new Ashen.Domain.Run.RunData(ToCombatData(mechanics, combatEngine),
                DomainRegistry(RunKeys.CreationModes), DomainRegistry(MK.Seats), DomainRegistry(RegistryKeys.Encounters),
                Doc(RunKeys.AttributeRules), Doc(RunKeys.CharacterCreation), Doc(RunKeys.DerivedStatRules),
                (JArray)_run[RunKeys.TagFamilies].DeepClone(),
                string.IsNullOrEmpty(contentVersion) ? RegistryKeys.ContentVersionFallback : contentVersion, handRules, runEngine);
        }

        /// <summary>The post-combat pipeline's view (US-11.1): the run data plus the tag tree, the ascension order and the rewards rules.</summary>
        public Ashen.Domain.Rewards.RewardsData ToRewardsData(JObject mechanics, JObject combatEngine, JObject handRules, JObject runEngine,
            string contentVersion, JArray ascensionOrder, JObject rewardsEngine) =>
            new Ashen.Domain.Rewards.RewardsData(ToRunData(mechanics, combatEngine, handRules, runEngine, contentVersion),
                (JArray)_run[RewardsKeys.Nodes].DeepClone(), ascensionOrder, rewardsEngine);

        /// <summary>The merchant's view (US-9.1): the post-combat data plus the merchant rules (rules/shopEngine.json).</summary>
        public Ashen.Domain.Shop.ShopData ToShopData(JObject mechanics, JObject combatEngine, JObject handRules, JObject runEngine,
            string contentVersion, JArray ascensionOrder, JObject rewardsEngine, JObject shopEngine) =>
            new Ashen.Domain.Shop.ShopData(ToRewardsData(mechanics, combatEngine, handRules, runEngine, contentVersion, ascensionOrder, rewardsEngine), shopEngine);

        /// <summary>The event door's view (US-10.1): the merchant's data, the event and speaker registries, eventMeta, the per-choice requirements and rules/eventsEngine.json.</summary>
        public Ashen.Domain.Events.EventsData ToEventsData(JObject mechanics, JObject combatEngine, JObject handRules, JObject runEngine,
            string contentVersion, JArray ascensionOrder, JObject rewardsEngine, JObject shopEngine, JObject eventsEngine) =>
            new Ashen.Domain.Events.EventsData(ToShopData(mechanics, combatEngine, handRules, runEngine, contentVersion, ascensionOrder, rewardsEngine, shopEngine),
                DomainRegistry(RegistryKeys.Events), DomainRegistry(EventKeys.Speakers),
                (JObject)_events[EventKeys.EventMeta].DeepClone(), (JObject)_events[EventKeys.ChoiceRequirements].DeepClone(), eventsEngine);

        /// <summary>The event documents: catalog/eventMeta.json and catalog/eventChoiceRequirements.json (empty objects when absent).</summary>
        private static JObject EventDocuments(ContentSet content)
        {
            JObject Get(string file) => content.Contains(file) && content.Get(file) is JObject o ? (JObject)o.DeepClone() : new JObject();
            return new JObject
            {
                [EventKeys.EventMeta] = Get(ContentFiles.CatalogEventMeta),
                [EventKeys.ChoiceRequirements] = Get(ContentFiles.CatalogEventChoiceRequirements),
            };
        }

        /// <summary>
        /// The run loop's view (US-8.1, D-100i): the event door's data (with the merchant's and the post-combat data under
        /// it) and the map data, with the tagging rows (a location's tags), the legacy dungeons (catalog/legacyDungeons.json),
        /// the unlock rows (catalog/unlocks.json, in table order) and the loop's own rules (rules/loopEngine.json).
        /// </summary>
        public Ashen.Domain.Loop.LoopData ToLoopData(JObject mechanics, JObject combatEngine, JObject handRules, JObject runEngine, string contentVersion,
            JArray ascensionOrder, JObject rewardsEngine, JObject shopEngine, JObject eventsEngine, JObject mapEngine, JObject loopEngine) =>
            new Ashen.Domain.Loop.LoopData(ToEventsData(mechanics, combatEngine, handRules, runEngine, contentVersion, ascensionOrder, rewardsEngine, shopEngine, eventsEngine),
                ToMapData(mapEngine), (JArray)_tagging.DeepClone(), (JObject)_legacyDungeons.DeepClone(), (JArray)_unlocks.DeepClone(), loopEngine);

        /// <summary>The map documents, in content key order: balance/mapConfigs.json, eventMeta's gates, boss locations, mapShape limits and legacy bosses.</summary>
        private static JObject MapDocuments(ContentSet content)
        {
            JToken Get(string file) => content.Contains(file) ? content.Get(file) : null;
            var shape = Get(ContentFiles.BalanceMapShape) as JObject;
            return new JObject
            {
                [MK.MapConfigs] = Get(ContentFiles.BalanceMapConfigs)?.DeepClone(),
                [MK.EventHistoryRequirements] = (Get(ContentFiles.CatalogEventMeta) as JObject)?[MK.EventHistoryRequirements]?.DeepClone(),
                [MK.BossLocations] = Get(ContentFiles.CatalogBossDestinations)?.DeepClone(),
                [MK.MapShapeLimits] = shape?[MV.MapShapeLimitsKey]?.DeepClone(),
                [MK.LegacyActBosses] = shape?[MV.LegacyActBossesKey]?.DeepClone(),
            };
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
