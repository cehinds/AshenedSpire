using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// VP-2: art is found by name through ui/art.json over the registry (suite: Presentation, engine-free). No Core 2 card, enemy or
    /// icon the UI reads may lack art, except the explicit lists below (each is a later phase, see docs/ASSET-GAPS.md).
    /// </summary>
    [TestFixture, Category("Presentation")]
    public class ArtLookupTests
    {
        private static UiData _ui;
        private static UiData Ui => _ui ?? (_ui = UiData.Load(TestContent.Source));
        private static ArtLookup Art => Ui.Art;

        /// <summary>Core 2 events with no painting in the art release (their screen keeps the reused enemy art of ui/nodes.json event.art).</summary>
        private static readonly string[] EventsWithoutPainting = { "graveOfTheNameless", "namelessKeeper", "namelessRest", "lastLantern", "lanternCaravan" };

        private static IEnumerable<JObject> Core2Cards() => TestContent.ContentJson(ContentFiles.ProfileContentCards).Properties().Select(p => (JObject)p.Value);

        private static string[] Tags(JObject card) => (card["tags"] as JArray ?? new JArray()).Select(t => (string)t).ToArray();

        [Test]
        public void EveryGroupMatchesImportedIdsAndEveryReleaseIdIsInOneCatalog()
        {
            var registry = TestContent.ContentJson(ContentFiles.AssetsRegistry);
            foreach (var g in Art.Config.Groups)
                Assert.That(Art.Config.Groups.Count(o => o.Id == g.Id), Is.EqualTo(1), g.Id);
            foreach (var g in Art.Config.Groups)
                foreach (var entry in g.Include)
                    Assert.That(registry.Properties().Any(p => p.Value[ArtRegistryKeys.UnityPath].Type == JTokenType.String && ComponentDefaults.ArtMatches(entry, p.Name)), Is.True, g.Id + " " + entry);
            var unplaced = registry.Properties()
                .Where(p => p.Value[ArtRegistryKeys.Release] != null && p.Value[ArtRegistryKeys.UnityPath].Type == JTokenType.String)
                .Select(p => p.Name)
                .Where(id => Art.Config.GroupOf(id) == null && !Ui.Components.ArtIncludes(id))
                .ToList();
            Assert.That(unplaced, Is.Empty, "imported art-release ids that no catalog builds:\n" + string.Join("\n", unplaced.Take(20)));
        }

        [Test]
        public void GroupOfRoutesAnIdToItsCatalog()
        {
            var cardId = Art.Card(Core2Cards().First(c => (string)c["id"] != null).Value<string>("id"), null, null).AssetId;
            Assert.That(Art.Config.GroupOf(cardId).Id, Is.EqualTo(Art.Config.Groups.First().Id));
            Assert.That(Art.Config.GroupOf(Art.Icon(IconNamesOf(Core2Cards()).First())).Id, Is.Not.Null);
            Assert.That(Art.Config.GroupOf(Ui.Components.CombatFallbackBackground), Is.Null);
        }

        [Test]
        public void EveryCoreTwoCardResolvesToArt()
        {
            var counts = new Dictionary<CardArtKind, int>();
            foreach (var card in Core2Cards())
            {
                var r = Art.Card(card.Value<string>("id"), card.Value<string>("type"), Tags(card));
                Assert.That(r.Kind, Is.Not.EqualTo(CardArtKind.None), card.Value<string>("id"));
                Assert.That(Art.Has(r.AssetId), Is.True, r.AssetId);
                counts[r.Kind] = counts.TryGetValue(r.Kind, out var n) ? n + 1 : 1;
            }
            Assert.That(counts[CardArtKind.Library], Is.GreaterThan(80), "attacks, defends, smashes and the like take a library painting");
            Assert.That(counts.ContainsKey(CardArtKind.Painting), Is.False, "Core 2 ids have no painting of their own yet (the web composes cutouts, VP-3)");
        }

        [Test]
        public void PlaceholdersAreOnlyCardsWithoutALibraryManeuver()
        {
            var libraryManeuvers = new HashSet<string>(((JArray)Art.Config.Card[ArtKeys.LibraryManeuvers]).Select(t => (string)t));
            var maneuverPrefix = (string)Art.Config.Card[ArtKeys.Tags][ArtKeys.Maneuver];
            foreach (var card in Core2Cards())
            {
                var r = Art.Card(card.Value<string>("id"), card.Value<string>("type"), Tags(card));
                if (r.Kind != CardArtKind.Placeholder) continue;
                var maneuvers = Tags(card).Where(t => t.StartsWith(maneuverPrefix, StringComparison.Ordinal)).Select(t => t.Substring(maneuverPrefix.Length)).ToList();
                Assert.That(maneuvers.Any(libraryManeuvers.Contains) && card.Value<string>("type") == (string)Art.Config.Card[ArtKeys.AttackType], Is.False, card.Value<string>("id"));
            }
        }

        [Test]
        public void LibraryVariantFollowsTheWebHashAndElement()
        {
            // The web's physicalVariant("core.strike:") is 4, ("core.defend:") 1, ("core.smash:") 3 (FNV-1a, src/ui/cardArtComposition.js).
            string Physical(string id, string maneuver, string type) => Art.Card(id, type, new[] { "camp:physical", "maneuver:" + maneuver }).AssetId;
            Assert.That(Physical("core.strike", "attack", "attack"), Is.EqualTo("cardlib.physical-attack-04.art512"));
            Assert.That(Physical("core.defend", "defend", "skill"), Is.EqualTo("cardlib.physical-defend-01.art512"));
            Assert.That(Physical("core.smash", "smash", "attack"), Is.EqualTo("cardlib.physical-smash-03.art512"));
            // A magical card takes its element's variant: frost 1, an unknown or missing element reads as force (4).
            Assert.That(Art.Card("x", "attack", new[] { "camp:spell", "maneuver:casting", "school:frost" }).AssetId, Is.EqualTo("cardlib.magical-attack-01.art512"));
            Assert.That(Art.Card("x", "attack", new[] { "camp:spell", "maneuver:casting" }).AssetId, Is.EqualTo("cardlib.magical-attack-04.art512"));
            // An alias (illusion reads as psychic, 4) and a casting skill (no library painting) falling to its category's placeholder.
            Assert.That(Art.Card("x", "attack", new[] { "camp:spell", "maneuver:casting", "school:illusion" }).AssetId, Is.EqualTo("cardlib.magical-attack-04.art512"));
            var skill = Art.Card("x", "skill", new[] { "camp:spell", "maneuver:casting" });
            Assert.That(skill.Kind, Is.EqualTo(CardArtKind.Placeholder));
            Assert.That(skill.AssetId, Does.StartWith("cardlib.magical-"));
        }

        [Test]
        public void AnUnknownCardStillGetsAStatedPlaceholder()
        {
            var r = Art.Card("no.such.card", null, null);
            Assert.That(r.Kind, Is.EqualTo(CardArtKind.Placeholder));
            Assert.That(Art.Has(r.AssetId), Is.True);
        }

        [Test]
        public void EveryCoreTwoEnemyHasItsFigureArt()
        {
            var template = Ui.Components.EnemyArt;
            var missing = TestContent.ContentJson(ContentFiles.ProfileContentEnemies).Properties()
                .Select(p => StringTable.Fill(template, new StringArgs().Add(UiPlaceholders.Id, p.Name)))
                .Where(id => !Art.Has(id)).ToList();
            Assert.That(missing, Is.Empty);
        }

        private static IEnumerable<string> IconNamesOf(IEnumerable<JObject> rows) => rows.Select(r => (string)r["icon"]).Where(n => !string.IsNullOrEmpty(n)).Distinct();

        [Test]
        public void EveryIconTheCoreTwoContentNamesHasArt()
        {
            var cardIcons = IconNamesOf(Core2Cards()).ToList();
            var statusIcons = IconNamesOf(TestContent.ContentJson(ContentFiles.ProfileBundleStatuses).Properties().Select(p => (JObject)p.Value)).ToList();
            Assert.That(cardIcons.Count, Is.GreaterThan(5));
            Assert.That(statusIcons.Count, Is.GreaterThan(20));
            var missing = cardIcons.Concat(statusIcons).Distinct().Where(n => Art.Icon(n) == null).ToList();
            Assert.That(missing, Is.Empty, "icon names with no art and no alias in ui/art.json iconAlias");
        }

        [Test]
        public void IconAliasesPointAtRealIcons()
        {
            foreach (var kv in Art.Config.IconAlias)
            {
                Assert.That(Art.Has(StringTable.Fill(Art.Config.Lookup[ArtKinds.Icon], new StringArgs().Add(ArtPlaceholders.Name, kv.Value))), Is.True, kv.Key + " -> " + kv.Value);
            }
        }

        [Test]
        public void EveryCoreTwoEventHasAPaintingExceptTheStatedOnes()
        {
            var events = TestContent.ContentJson(ContentFiles.ProfileBundleEvents).Properties().Select(p => p.Name).ToList();
            Assert.That(events.Count, Is.GreaterThan(20));
            var missing = events.Where(e => Art.Event(e) == null).OrderBy(e => e, StringComparer.Ordinal).ToList();
            Assert.That(missing, Is.EqualTo(EventsWithoutPainting.OrderBy(e => e, StringComparer.Ordinal).ToList()));
        }

        [Test]
        public void TheCardFrameLayersAndOrbArtAreImported()
        {
            foreach (var layer in new[] { "base", "art", "panel", "flag", "energy", "mana", "stamina" })
                Assert.That(Art.Frame(layer), Is.Not.Null, layer);
            foreach (var part in new[] { "orb", "frame", "diamond", "sigil", "spent" })
                Assert.That(Art.Find(ArtKinds.Orb, new StringArgs().Add(ArtPlaceholders.Name, part)), Is.Not.Null, part);
            foreach (var part in new[] { "plate", "draw", "spent-cards", "potions", "connector" })
                Assert.That(Art.Find(ArtKinds.Footer, new StringArgs().Add(ArtPlaceholders.Name, part)), Is.Not.Null, part);
        }

        [Test]
        public void ANameThatIsNotImportedFindsNothing()
        {
            Assert.That(Art.Icon(string.Empty), Is.Null);
            Assert.That(Art.Icon(null), Is.Null);
            Assert.That(Art.Event("no.such.event"), Is.Null);
            Assert.That(Art.Find("no-such-kind", new StringArgs()), Is.Null);
        }
    }
}
