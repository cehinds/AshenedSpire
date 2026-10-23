using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.Content
{
    /// <summary>
    /// The Core 2 ruleset's data (WP-2, D-168): the resolved <see cref="CoreProfile"/> and the registries the web builds for the
    /// profile <c>default</c> (<c>applyGameProfileToContent</c> → <c>createRegistries</c> → <c>bindGameProfileRegistries</c>),
    /// read from <c>profile/**</c>. It stands beside <see cref="RuntimeRegistries"/> (the 405 content, which stays until the owner
    /// rules on D-164) and never reads the 405 files. Everything is thin over the JSON: tables are id-keyed objects in registry
    /// order, content keys are rows or objects as shipped, and every read returns a deep clone.
    /// </summary>
    public sealed class Core2Registries
    {
        private Core2Registries(CoreProfile profile, RuntimeRegistries registries)
        {
            Profile = profile;
            Registries = registries;
        }

        public CoreProfile Profile { get; }

        /// <summary>The registries built from profile/bundle (tables, equipment, balance, class tree, ...).</summary>
        public RuntimeRegistries Registries { get; }

        /// <summary>Loads and validates the profile, then builds the registries; throws with the findings when the profile is invalid.</summary>
        public static Core2Registries Load(ContentSet content)
        {
            var profile = CoreProfile.Load(content);
            var report = profile.Validate();
            if (!report.IsValid)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, Core2ProfileMessages.Invalid, report.Summary(report.Issues.Count)));
            return new Core2Registries(profile, RuntimeRegistries.BuildCore2(content));
        }

        /// <summary>An id-keyed registry in registry order: cards, statuses, relics, enemies, stances, keywords, encounters, flasks, classes, events, attributes, resources, sigils, ... or null.</summary>
        public JObject Table(string name) => Registries.Table(name);

        /// <summary>One row of a table by id, or null.</summary>
        public JObject Row(string table, string id) => Table(table)?[id] as JObject;

        /// <summary>A registry that is plain data (nodes, enemyMoves, shops, mapConfigs, tagging, ...), or null.</summary>
        public JToken Object(string name) => Registries.Object(name);

        // The registries the combat port reads (cards also holds the profile's projected cards and the enemy cards).
        public JObject Cards => Table(RegistryKeys.Cards);
        public JObject Statuses => Table(RegistryKeys.Statuses);
        public JObject Relics => Table(RegistryKeys.Relics);
        public JObject Enemies => Table(RegistryKeys.Enemies);
        public JObject Stances => Table(RegistryKeys.Stances);
        public JObject Classes => Table(RegistryKeys.Classes);
        public JObject Resources => Table(Core2BundleKeys.Resources);

        /// <summary>The flat enemy move rows (enemyId, id, ...) in registry order.</summary>
        public JArray EnemyMoves => Object(Core2BundleKeys.EnemyMoves) as JArray ?? new JArray();

        // Profile content (profile/content/*.json). Lists are rows in registry order; the rest are objects as shipped.
        public JArray Characters => Profile.Rows(Core2ProfileContentKeys.Characters);
        public JArray ProfileCards => Profile.Rows(Core2ProfileContentKeys.Cards);
        public JArray EnemyCards => Profile.Rows(Core2ProfileContentKeys.EnemyCards);
        public JArray EnemyRelics => Profile.Rows(Core2ProfileContentKeys.EnemyRelics);
        public JArray Gear => Profile.Rows(Core2ProfileContentKeys.Gear);
        public JToken Stats => Profile.Content(Core2ProfileContentKeys.Stats);
        public JToken Routes => Profile.Content(Core2ProfileContentKeys.Routes);
        public JToken Rests => Profile.Content(Core2ProfileContentKeys.Rests);
        public JToken Rewards => Profile.Content(Core2ProfileContentKeys.Rewards);
        public JToken Shops => Profile.Content(Core2ProfileContentKeys.Shops);
        public JToken ClassUnlocks => Profile.Content(Core2ProfileContentKeys.ClassUnlocks);
        public JToken CardPools => Profile.Content(Core2ProfileContentKeys.CardPools);
        public JToken StarterDeck => Profile.Content(Core2ProfileContentKeys.StarterDeck);
        public JToken StartingHands => Profile.Content(Core2ProfileContentKeys.StartingHands);
        public JToken ClassAttributes => Profile.Content(Core2ProfileContentKeys.ClassAttributes);
        public JToken ResourceVariants => Profile.Content(Core2ProfileContentKeys.ResourceVariants);

        /// <summary>
        /// The data Core 2 run creation reads (WP-3): the run port's view of these registries (the creation tables the profile's bundle
        /// holds), the resolved profile as one document (<c>profile/core.json</c> with its content keys under <c>content</c>), the version
        /// scalars, the creation constants and the rule snapshots a run is stamped with. <paramref name="content"/> is the set this was loaded from.
        /// </summary>
        public CoreRunData ToCoreRunData(ContentSet content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            JObject Rules(string file) => (JObject)content.Get(file);
            var versions = Rules(ContentFiles.RulesVersions);
            var run = Registries.ToRunData(Rules(ContentFiles.RulesMechanics), Rules(ContentFiles.RulesCombatEngine), Rules(ContentFiles.RulesHandRules),
                Rules(ContentFiles.RulesRunEngine), JsValues.Str(versions[Core2VersionKeys.WebContentVersion]));
            var profile = Profile.Core;
            var body = new JObject();
            foreach (var key in Profile.ContentKeys) body[key] = Profile.Content(key);
            profile[Core2RunKeys.Content] = body;
            return new CoreRunData(run, profile, versions, Rules(ContentFiles.RulesCore2Run), Rules(ContentFiles.RulesRangePolicy),
                Rules(ContentFiles.RulesReactionRules), Rules(ContentFiles.RulesCombatExpansionRules));
        }

        /// <summary>
        /// What the Core 2 deck rules read (WP-18): the run data of <see cref="ToCoreRunData"/>, <c>rules/core2Deck.json</c> and the ids of the
        /// card-domain tags (the card schools the equipment gate compares with the worn gear's tags).
        /// </summary>
        public CoreDeckData ToCoreDeckData(ContentSet content)
        {
            var rules = (JObject)content.Get(ContentFiles.RulesCore2Deck);
            var domain = rules.Value<string>(Core2DeckKeys.CardDomain);
            var tags = JsValues.Items(Object(RegistryKeys.TagRegistry)).OfType<JObject>()
                .Where(t => JsValues.Str(t[TagKeys.Domain]) == domain).Select(t => JsValues.Text(t[RegistryKeys.Id]));
            return new CoreDeckData(ToCoreRunData(content), rules, tags);
        }

        /// <summary>What the map port reads (WP-14): encounters, events, enemies and seats in registry order, the quest gates, the boss destinations and the map engine rules.</summary>
        public Ashen.Domain.Map.MapData ToMapData(JObject mapEngine) => Registries.ToMapData(mapEngine);

        /// <summary>The routes the Core 2 map builder reads: profile/content/routes.json, the routes module and the profile hash (shipped verbatim, never recomputed).</summary>
        public Ashen.Domain.Map.CoreRoutesConfig RoutesConfig => new Ashen.Domain.Map.CoreRoutesConfig(Routes as JObject, Profile.Module(Core2ModuleIds.Routes), Profile.Hash);

        /// <summary>The moves of one enemy, keyed by move id, in the order the enemy row lists them.</summary>
        public JObject MovesOf(string enemyId) => Row(RegistryKeys.Enemies, enemyId)?[Core2MoveKeys.Moves] as JObject ?? new JObject();
    }
}
