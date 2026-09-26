using System.Collections.Generic;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>US-0.7 config layering (suite: Content).</summary>
    [TestFixture, Category("Content")]
    public class ConfigLayersTests
    {
        private sealed class MemorySource : IContentSource
        {
            private readonly Dictionary<string, string> _files = new Dictionary<string, string>();
            public MemorySource Add(string path, string json) { _files[path] = json; return this; }
            public bool Exists(string p) => _files.ContainsKey(p);
            public string ReadText(string p) => _files[p];
        }

        private static ConfigLayers Layers() => new ConfigLayers(TestContent.Source);

        private static decimal Num(RunSnapshot s, string file, string path) => s.Content.Select(file, path).Value<decimal>();

        [Test]
        public void DefaultPresetIsTheOwnersReferenceConfig()
        {
            var snap = Layers().Build(new LayerSelection());
            Assert.That(snap.PresetId, Is.EqualTo("reference"));
            Assert.That(Num(snap, ContentFiles.BalanceBalance, "startingCinders"), Is.EqualTo(100m));
            Assert.That(Num(snap, ContentFiles.BalanceBalance, "combatRatings.ratings.ar.dexterity"), Is.EqualTo(0.25m));
            Assert.That(Num(snap, ContentFiles.BalanceBalance, "combatRatings.breaks.recoveryPerTurn"), Is.EqualTo(1m));
            Assert.That(Num(snap, ContentFiles.CatalogClasses, "reaver.startingFlaskAllocation.hp"), Is.EqualTo(2m));
            Assert.That(snap.Content.Select(ContentFiles.RulesHandRules, "openingWeighted.base"), Is.Not.Null);
            Assert.That((bool)snap.PlayerSettings["shrineMultiUse"], Is.True);
        }

        [Test]
        public void ShippedPresetKeepsTheWebDefaults()
        {
            var snap = Layers().Build(new LayerSelection { PresetId = "shipped" });
            Assert.That(Num(snap, ContentFiles.BalanceBalance, "startingCinders"), Is.EqualTo(0m));
            Assert.That(Num(snap, ContentFiles.BalanceBalance, "combatRatings.ratings.ar.dexterity"), Is.EqualTo(0m));
            Assert.That(Num(snap, ContentFiles.CatalogClasses, "reaver.startingFlaskAllocation.hp"), Is.EqualTo(3m));
            Assert.That(snap.Content.Select(ContentFiles.RulesHandRules, "openingWeighted"), Is.Null);
        }

        [Test]
        public void EffectiveConfigurationOfBothPresetsIsSchemaValid()
        {
            foreach (var id in Layers().PresetIds)
            {
                var report = new ValidationReport();
                Layers().Build(new LayerSelection { PresetId = id }, report);
                Assert.That(report.IsValid, Is.True, id + ":\n" + report.Summary(20));
            }
        }

        [Test]
        public void LaterPatchesWinAndNullDeletes()
        {
            var ascension = JObject.Parse("{\"files\":{\"balance/balance.json\":{\"startingCinders\":7,\"flaskSlots\":null}}}");
            var modifier = JObject.Parse("{\"files\":{\"balance/balance.json\":{\"startingCinders\":9}}}");
            var snap = Layers().Build(new LayerSelection { Patches = new[] { ascension, modifier } });
            Assert.That(Num(snap, ContentFiles.BalanceBalance, "startingCinders"), Is.EqualTo(9m));
            Assert.That(snap.Content.Select(ContentFiles.BalanceBalance, "flaskSlots"), Is.Null);
        }

        [Test]
        public void PatchingAnUnknownFileIsRefused()
        {
            var bad = JObject.Parse("{\"files\":{\"balance/nope.json\":{\"x\":1}}}");
            Assert.Throws<System.ArgumentException>(() => Layers().Build(new LayerSelection { Patches = new[] { bad } }));
        }

        [Test]
        public void InvalidPatchIsCaughtBySchemaValidation()
        {
            var bad = JObject.Parse("{\"files\":{\"catalog/cards.json\":{\"strike\":{\"rarity\":5}}}}");
            var report = new ValidationReport();
            Layers().Build(new LayerSelection { Patches = new[] { bad } }, report);
            Assert.That(report.ByRule(ValidationRules.Type), Is.Not.Empty);
        }

        [Test]
        public void SnapshotHashIsStableAndDistinguishesPresets()
        {
            var a = Layers().Build(new LayerSelection());
            var b = Layers().Build(new LayerSelection());
            var shipped = Layers().Build(new LayerSelection { PresetId = "shipped" });
            Assert.That(a.Hash, Is.EqualTo(b.Hash));
            Assert.That(a.Hash, Is.Not.EqualTo(shipped.Hash));
            Assert.That(a.Hash.Length, Is.EqualTo(64));
        }

        [Test]
        public void CanonicalHashIgnoresPropertyOrder()
        {
            var x = new ContentSet(new Dictionary<string, JToken> { { "a.json", JObject.Parse("{\"b\":1,\"a\":{\"d\":2,\"c\":0.25}}") } });
            var y = new ContentSet(new Dictionary<string, JToken> { { "a.json", JObject.Parse("{\"a\":{\"c\":0.25,\"d\":2},\"b\":1}") } });
            Assert.That(x.Hash, Is.EqualTo(y.Hash));
        }

        [Test]
        public void SnapshotIsImmutable()
        {
            var snap = Layers().Build(new LayerSelection());
            var balance = (JObject)snap.Content.Get(ContentFiles.BalanceBalance);
            balance["startingCinders"] = 999;
            Assert.That(Num(snap, ContentFiles.BalanceBalance, "startingCinders"), Is.EqualTo(100m));
        }

        [Test]
        public void ModdingOverridesApplyOnlyWhenEnabled()
        {
            var mods = new MemorySource().Add(ContentFiles.BalanceBalance, "{\"startingCinders\":5}");
            var off = Layers().Build(new LayerSelection { Overrides = mods, ModdingEnabled = false });
            var on = Layers().Build(new LayerSelection { Overrides = mods, ModdingEnabled = true });
            Assert.That(Num(off, ContentFiles.BalanceBalance, "startingCinders"), Is.EqualTo(100m));
            Assert.That(Num(on, ContentFiles.BalanceBalance, "startingCinders"), Is.EqualTo(5m));
            Assert.That(on.OverridesApplied, Is.EqualTo(new[] { ContentFiles.BalanceBalance }));
        }

        [Test]
        public void OversizedOverrideIsRefused()
        {
            var big = "{\"note\":\"" + new string('x', ConfigLimits.MaxOverrideBytes) + "\"}";
            var mods = new MemorySource().Add(ContentFiles.BalanceBalance, big);
            Assert.Throws<System.ArgumentException>(() => Layers().Build(new LayerSelection { Overrides = mods, ModdingEnabled = true }));
        }

        [Test]
        public void PlayerSettingsLayerOverThePresetDefaults()
        {
            var snap = Layers().Build(new LayerSelection { PlayerSettings = JObject.Parse("{\"holdConfirm\":\"off\"}") });
            Assert.That((string)snap.PlayerSettings["holdConfirm"], Is.EqualTo("off"));
            Assert.That((string)snap.PlayerSettings["rewardCollect"], Is.EqualTo("manual"));
        }
    }
}
