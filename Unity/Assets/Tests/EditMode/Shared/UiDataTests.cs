using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Ashen.App.Ui;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// The UI data, the generated UI keys and the UXML/USS stay in step (docs/design/08 §10, §12; US-17.1 focus
    /// orders are required; US-0.10 the title binds the disclosure). Suite: Presentation. Engine-free.
    /// </summary>
    [TestFixture, Category("Presentation")]
    public class UiDataTests
    {
        private static UiData _ui;
        private static UiData Ui => _ui ?? (_ui = UiData.Load(TestContent.Source));

        private static string UiRoot => Path.GetFullPath(Path.Combine(TestContent.Root, "..", "..", "Game", "Presentation", "UI"));
        private static string ResourcesRoot => Path.Combine(UiRoot, "Resources");

        private static IEnumerable<string> Files(string pattern) => Directory.GetFiles(UiRoot, pattern, SearchOption.AllDirectories);

        private static HashSet<string> Constants(Type t) =>
            new HashSet<string>(t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()), StringComparer.Ordinal);

        private static readonly Regex NameAttr = new Regex("\\bname=\"([^\"]+)\"");
        private static readonly Regex KeyAttr = new Regex("\\bstring-key=\"([^\"]*)\"");

        private static HashSet<string> UxmlNames() =>
            new HashSet<string>(Files("*.uxml").SelectMany(f => NameAttr.Matches(File.ReadAllText(f)).Cast<Match>().Select(m => m.Groups[1].Value)), StringComparer.Ordinal);

        [Test]
        public void ScreenIdsMirrorTheRegistry()
        {
            Assert.That(Constants(typeof(ScreenIds)), Is.EquivalentTo(Ui.Screens.All.Select(s => s.Id)));
            Assert.That(Ui.Screens.Get(Ui.Screens.Initial), Is.Not.Null);
        }

        [Test]
        public void EveryBuiltScreenHasItsUxmlAndARequiredFocusOrder()
        {
            foreach (var s in Ui.Screens.All.Where(s => s.IsBuilt))
            {
                Assert.That(File.Exists(Path.Combine(ResourcesRoot, s.Uxml + ".uxml")), Is.True, s.Id + " UXML " + s.Uxml);
                if (s.AcceptsInput) Assert.That(s.FocusOrder, Is.Not.Empty, s.Id + " needs a focusOrder (US-17.1)");
            }
            foreach (var s in Ui.Screens.All.Where(s => s.Planned)) Assert.That(s.Uxml, Is.Null.Or.Empty, s.Id + " is planned");
        }

        [Test]
        public void FocusRegionsAreGeneratedNamesPresentInTheUxml()
        {
            var names = Constants(typeof(UiNames));
            var inUxml = UxmlNames();
            foreach (var s in Ui.Screens.All)
                foreach (var region in s.FocusOrder.Concat(s.InitialFocus == null ? new string[0] : new[] { s.InitialFocus }))
                {
                    Assert.That(names, Does.Contain(region), s.Id + " focus region " + region + " is not in UiNames");
                    Assert.That(inUxml, Does.Contain(region), s.Id + " focus region " + region + " is in no UXML");
                }
        }

        [Test]
        public void TransitionsBackActionsAndMenuTargetsResolve()
        {
            foreach (var s in Ui.Screens.All)
            {
                foreach (var t in s.Transitions)
                {
                    Assert.That(Ui.Screens.Get(t.To), Is.Not.Null, s.Id + " → " + t.To);
                    Assert.That(t.Mode, Is.EqualTo(UiValues.ModePush).Or.EqualTo(UiValues.ModeReplace));
                }
                Assert.That(s.Back, Is.Null.Or.EqualTo(UiValues.BackPop).Or.EqualTo(UiValues.BackClose), s.Id);
            }
            foreach (var e in Ui.Menus.Title)
            {
                Assert.That(Constants(typeof(MenuActions)), Does.Contain(e.Action), e.Id);
                Assert.That(Constants(typeof(MenuRuleIds)), Does.Contain(e.EnabledWhen), e.Id);
                if (e.Target != null) Assert.That(Ui.Screens.Get(e.Target), Is.Not.Null, e.Id + " → " + e.Target);
                if (e.Confirm != null) Assert.That(Ui.Menus.Confirm(e.Confirm), Is.Not.Null, e.Id + " confirm " + e.Confirm);
                Assert.That(Ui.Strings.Has(e.LabelKey), Is.True, e.LabelKey);
            }
            Assert.That(Constants(typeof(ConfirmIds)), Is.EquivalentTo(Ui.Menus.Confirms.Select(c => c.Id)));
            foreach (var c in Ui.Menus.Confirms)
            {
                foreach (var key in new[] { c.TitleKey, c.BodyKey, c.PrimaryKey, c.BackKey, c.OccupiedBodyKey }.Where(k => k != null))
                    Assert.That(Ui.Strings.Has(key), Is.True, c.Id + " " + key);
                if (c.Policy != null) Assert.That(Ui.Policies.Exists(c.Policy), Is.True, c.Id + " policy " + c.Policy);
            }
        }

        [Test]
        public void BackgroundsAndArtPrefixesExistInTheRegistry()
        {
            var registry = TestContent.ContentJson(ContentFiles.AssetsRegistry);
            foreach (var s in Ui.Screens.All.Where(s => s.Background != null)) Assert.That(registry[s.Background], Is.Not.Null, s.Id + " " + s.Background);
            foreach (var prefix in Ui.Components.ArtInclude)
                Assert.That(registry.Properties().Any(p => p.Name.StartsWith(prefix, StringComparison.Ordinal)), Is.True, prefix);
        }

        [Test]
        public void TokenKeysExistAndCodeDurationsArePositive()
        {
            var tokens = TestContent.ContentJson(ContentFiles.UiTokens);
            foreach (var key in Constants(typeof(TokenKeys)))
                Assert.That(tokens[UiKeys.Duration]?[key] ?? tokens[UiKeys.Color]?[key], Is.Not.Null, "token " + key);
            Assert.That(Ui.Tokens.Duration(TokenKeys.HoldConfirm), Is.EqualTo(1100), "04 §0 holdMs");
            foreach (var group in ((JObject)tokens[UiKeys.Groups]).Properties()) Assert.That(tokens[group.Name], Is.Not.Null, "group " + group.Name);
        }

        [Test]
        public void UxmlNamesAreGeneratedAndTextIsKeyed()
        {
            var names = Constants(typeof(UiNames));
            foreach (var f in Files("*.uxml"))
            {
                var text = File.ReadAllText(f);
                foreach (Match m in NameAttr.Matches(text)) Assert.That(names, Does.Contain(m.Groups[1].Value), Path.GetFileName(f) + " name " + m.Groups[1].Value);
                foreach (Match m in KeyAttr.Matches(text))
                    if (m.Groups[1].Value.Length > 0) Assert.That(Ui.Strings.Has(m.Groups[1].Value), Is.True, Path.GetFileName(f) + " string-key " + m.Groups[1].Value);
                Assert.That(Regex.IsMatch(text, "\\btext=\"[^\"]+\""), Is.False, Path.GetFileName(f) + " has literal text (use string-key)");
            }
            foreach (var key in Constants(typeof(UiResources)).Where(k => k.Contains("/Kit/")))
                Assert.That(File.Exists(Path.Combine(ResourcesRoot, key + ".uxml")), Is.True, key);
        }

        [Test]
        public void StyleSheetsUseTokenVariablesOnly()
        {
            var raw = new Regex("(#[0-9a-fA-F]{3,8}\\b|\\brgba?\\(|(?<![-\\w])-?\\d*\\.?\\d+(px|%|ms|s|deg)\\b)");
            foreach (var f in Files("*.uss").Where(f => Path.GetFileName(f) != "Tokens.uss"))
            {
                var lines = Regex.Replace(File.ReadAllText(f), "/\\*[\\s\\S]*?\\*/", string.Empty).Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    var value = Regex.Replace(lines[i], "var\\(--[a-z0-9-]+\\)", string.Empty);
                    var m = raw.Match(value);
                    Assert.That(m.Success && !Regex.IsMatch(m.Value, "^-?0(px|%|ms|s|deg)?$"), Is.False, Path.GetFileName(f) + ":" + (i + 1) + " raw value " + m.Value);
                }
            }
        }

        [Test]
        public void TitleFooterBindsTheAiDisclosure()
        {
            var shortKey = (string)Ui.About["aiDisclosure"]["shortKey"];
            var title = File.ReadAllText(Path.Combine(ResourcesRoot, Ui.Screens.Get(ScreenIds.Title).Uxml + ".uxml"));
            Assert.That(Regex.IsMatch(title, "name=\"" + UiNames.AiDisclosure + "\"[^>]*string-key=\"" + Regex.Escape(shortKey) + "\"|string-key=\"" + Regex.Escape(shortKey) + "\"[^>]*name=\"" + UiNames.AiDisclosure + "\""), Is.True,
                "the title footer shows {title.aiDisclosure} (US-0.10)");
            Assert.That(Ui.Menus.Confirm(ConfirmIds.AiNotice).BodyKey, Is.EqualTo((string)Ui.About["aiDisclosure"]["fullKey"]), "the disclosure link opens the full text");
        }
    }
}
