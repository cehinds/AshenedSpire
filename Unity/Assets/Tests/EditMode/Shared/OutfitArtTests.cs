using System.IO;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-5 (D-1594): the player's painted figure outside the combat board. Tools/outfit-art.mjs runs the web's paintedOutfit, equipmentAnimationForLoadout and animationArt for every class, armour and
    /// pair of hands; OutfitArtData answers from what it wrote. The frames of the starting hands, the empty hands and an armour with no animation set are pinned, and every frame it names is an art id the
    /// registry imports. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class OutfitArtTests
    {
        private static OutfitArtData _data;
        private static JObject _registry;

        private static OutfitArtData Data => _data ?? (_data = new OutfitArtData((JObject)JsonContent.Parse(File.ReadAllText(Path.Combine(TestContent.Root, ContentFiles.UiOutfitArt)))));

        private static JObject Registry => _registry ?? (_registry = (JObject)JsonContent.Parse(File.ReadAllText(Path.Combine(TestContent.Root, ContentFiles.AssetsRegistry))));

        [Test]
        public void TheStartingReaverHoldsTheGreatswordSoItsPortraitIsTheGreatswordSets()
        {
            Assert.That(Data.Frame("reaver", "default", "greatsword", null, OutfitViews.Portrait), Is.EqualTo("anim.greatsword-outfits.reaver.portrait"));
            Assert.That(Data.Frame("reaver", "default", "greatsword", null, OutfitViews.Stand), Is.EqualTo("anim.greatsword-outfits.reaver.stance-ready"));
        }

        [Test]
        public void EmptyHandsTakeTheUnarmedSetAndAnUnboundPairTheOutfit()
        {
            Assert.That(Data.Frame("reaver", "default", null, null, OutfitViews.Portrait), Is.EqualTo("anim.unarmed.reaver.portrait"));
            Assert.That(Data.Frame("rogue", "default", "no-such-piece", null, OutfitViews.Portrait), Is.EqualTo("outfit.rogue.portrait"), "hands that select no set leave the outfit's own frame");
            Assert.That(Data.Frame("rogue", null, "no-such-piece", null, OutfitViews.Stand), Is.EqualTo("outfit.rogue.menu"), "no armour is the default armour");
        }

        [Test]
        public void AClassWithNoOutfitInThatArmourHasNoFrame()
        {
            Assert.That(Data.Frame("reaver", "no-such-armour", "greatsword", null, OutfitViews.Portrait), Is.Null);
            Assert.That(Data.Frame("no-such-class", "default", null, null, OutfitViews.Portrait), Is.Null);
        }

        [Test]
        public void EveryFrameItNamesIsAnImportedArtId()
        {
            var ids = Data.Ids.ToList();
            Assert.That(ids.Count, Is.GreaterThan(100));
            foreach (var id in ids) Assert.That(Registry[id]?[ArtRegistryKeys.UnityPath]?.Type, Is.EqualTo(JTokenType.String), id);
        }
    }
}
