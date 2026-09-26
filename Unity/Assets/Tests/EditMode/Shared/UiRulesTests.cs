using System;
using System.IO;
using System.Linq;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>Pure UI rules (US-1.2, US-1.3, US-1.4, US-17.1; suite: Presentation). Engine-free: runs under Unity and dotnet.</summary>
    [TestFixture, Category("Presentation")]
    public class UiRulesTests
    {
        private static UiData _ui;
        private static UiData Ui => _ui ?? (_ui = UiData.Load(TestContent.Source));

        // ------------------------------------------------------------------ menu (US-1.3)

        [Test]
        public void TitleRowsAreInTheOwnerOrder()
        {
            Assert.That(Ui.Menus.Title.Select(e => e.Id), Is.EqualTo(new[] { "continue", "load", "new", "journal", "customRun", "settings", "quit", "coop" }));
            Assert.That(Ui.Menus.Title.Where(e => e.Visible).Select(e => e.Id).Last(), Is.EqualTo("quit"), "co-op row exists but is hidden");
        }

        [Test]
        public void ContinueIsDisabledWithoutAValidSlotAndFocusGoesToLoad()
        {
            var states = MenuRules.Evaluate(Ui.Menus.Title, Ui.MenuContext(false));
            Assert.That(states[0].Visible, Is.True, "Continue is shown");
            Assert.That(states[0].Enabled, Is.False, "Continue is disabled with no valid slot (AW:245)");
            Assert.That(MenuRules.FirstEnabled(states), Is.EqualTo(1));
        }

        [Test]
        public void ContinueIsEnabledAndFirstWithAValidSlot()
        {
            var states = MenuRules.Evaluate(Ui.Menus.Title, Ui.MenuContext(true));
            Assert.That(states[0].Enabled, Is.True);
            Assert.That(MenuRules.FirstEnabled(states), Is.EqualTo(0));
        }

        [Test]
        public void RowsTargetingPlannedScreensAreDisabledUntilBuilt()
        {
            var journal = Ui.Menus.Title.Single(e => e.Id == "journal");
            Assert.That(MenuRules.IsEnabled(journal, Ui.MenuContext(true)), Is.False, "journal is planned");
            Assert.That(MenuRules.IsEnabled(journal, new MenuContext { IsBuilt = id => id == "journal" }), Is.True);
            Assert.That(MenuRules.IsEnabled(new MenuEntryDef { EnabledWhen = "noSuchRule" }, new MenuContext()), Is.False);
            var hidden = MenuRules.Evaluate(Ui.Menus.Title, new MenuContext { HasValidSlot = true, IsBuilt = _ => true }).Single(s => s.Def.Id == "coop");
            Assert.That(hidden.Enabled, Is.False, "an invisible row is never enabled");
        }

        [Test]
        public void DestructiveDoorsComeFromThePolicyData()
        {
            Assert.That(Ui.Policies.IsDestructive(Ui.Menus.Confirm(ConfirmIds.DeleteSlot).Policy), Is.True);
            Assert.That(Ui.Menus.Confirm(ConfirmIds.DeleteSlot).Hold, Is.True);
            Assert.That(Ui.Policies.IsDestructive(Ui.Menus.Confirm(ConfirmIds.Quit).Policy), Is.False);
            Assert.That(Ui.Menus.Confirm(ConfirmIds.AiNotice).Single, Is.True);
        }

        // ------------------------------------------------------------------ Rule 11 CategoryNav

        private static CategoryNavRules Nav => Ui.Layout.CategoryNav;

        [Test]
        public void RailWhenItFitsSelectorWhenItDoesNot()
        {
            var wide = Nav.RailWidth + Nav.MinPaneWidth + 200;
            Assert.That(CategoryNavModel.Choose(wide, 700, 4, Nav), Is.EqualTo(CategoryNavMode.Rail));
            Assert.That(CategoryNavModel.Choose(Nav.MinPaneWidth, 700, 4, Nav), Is.EqualTo(CategoryNavMode.Selector), "no room beside the pane");
            var tooMany = (int)((700 - Nav.Chrome) / Nav.TapFloor) + 1;
            Assert.That(CategoryNavModel.Choose(wide, 700, tooMany, Nav), Is.EqualTo(CategoryNavMode.Selector), "categories below the tap floor");
        }

        [Test]
        public void HysteresisKeepsTheCurrentModeNearTheEdge()
        {
            var edge = Nav.RailWidth + Nav.MinPaneWidth;
            var justUnder = edge - Nav.Hysteresis / 2;
            var justOver = edge + Nav.Hysteresis / 2;
            Assert.That(CategoryNavModel.Choose(justUnder, 700, 4, Nav), Is.EqualTo(CategoryNavMode.Selector));
            Assert.That(CategoryNavModel.Choose(justUnder, 700, 4, Nav, CategoryNavMode.Rail), Is.EqualTo(CategoryNavMode.Rail));
            Assert.That(CategoryNavModel.Choose(justOver, 700, 4, Nav, CategoryNavMode.Selector), Is.EqualTo(CategoryNavMode.Selector));
            Assert.That(CategoryNavModel.Choose(edge + Nav.Hysteresis * 2, 700, 4, Nav, CategoryNavMode.Selector), Is.EqualTo(CategoryNavMode.Rail));
        }

        // ------------------------------------------------------------------ focus order (US-17.1)

        [Test]
        public void FocusOrderWalksRegionsThenItemsAndSkipsUnusable()
        {
            var order = FocusOrder.Resolve(new[] { new[] { false, true, true }, new bool[0], new[] { true } });
            Assert.That(order.Select(s => s.Region + ":" + s.Item), Is.EqualTo(new[] { "0:1", "0:2", "2:0" }));
            Assert.That(FocusOrder.Initial(order), Is.EqualTo(0));
            Assert.That(FocusOrder.Initial(order, new FocusSlot(2, 0)), Is.EqualTo(2), "initialFocus wins when usable");
            Assert.That(FocusOrder.Initial(order, new FocusSlot(0, 0)), Is.EqualTo(0), "an unusable preferred item falls back to the first");
            Assert.That(FocusOrder.Initial(FocusOrder.Resolve(new[] { new[] { false } })), Is.EqualTo(-1));
        }

        [Test]
        public void FocusStepWraps()
        {
            Assert.That(FocusOrder.Step(3, 2, 1), Is.EqualTo(0));
            Assert.That(FocusOrder.Step(3, 0, -1), Is.EqualTo(2));
            Assert.That(FocusOrder.Step(3, -1, 1), Is.EqualTo(0));
            Assert.That(FocusOrder.Step(3, -1, -1), Is.EqualTo(2));
            Assert.That(FocusOrder.Step(0, 0, 1), Is.EqualTo(-1));
        }

        // ------------------------------------------------------------------ layout (D-001, D-016)

        [TestCase(1280, 720, HeightBand.Standard, false, 1.0)]
        [TestCase(1920, 1080, HeightBand.Standard, false, 1.5)]
        [TestCase(844, 390, HeightBand.Compact, false, 390.0 / 720.0)]
        [TestCase(390, 844, HeightBand.Standard, true, 390.0 / 430.0)]
        [TestCase(740, 360, HeightBand.Compact, false, 0.5)]
        [TestCase(700, 300, HeightBand.Gate, false, 300.0 / 720.0)]
        public void ViewportsResolveToBandsModesAndScales(int w, int h, HeightBand band, bool narrow, double scale)
        {
            var s = LayoutClassifier.Classify(w, h, Ui.Layout);
            Assert.That(s.Band, Is.EqualTo(band));
            Assert.That(s.Narrow, Is.EqualTo(narrow));
            Assert.That(s.Panel, Is.SameAs(narrow ? Ui.Layout.Narrow : Ui.Layout.Wide));
            Assert.That(s.Scale, Is.EqualTo(scale).Within(1e-9));
        }

        [Test]
        public void PhysicalMinimumsHoldAfterScaling()
        {
            var compact = LayoutClassifier.Classify(844, 390, Ui.Layout);
            var touch = Ui.Layout.MinPhysicalOf(UiKeys.Touch);
            Assert.That(touch, Is.EqualTo(44));
            Assert.That(compact.ReferenceMinimum(touch) * compact.Scale, Is.EqualTo(touch).Within(1e-9));
            Assert.That(compact.ReferenceMinimum(touch), Is.GreaterThan(touch), "a scaled-down panel needs larger reference sizes");
        }

        [Test]
        public void MatchWidthOrHeightBlendsLogarithmically()
        {
            var panel = new PanelDef { Width = 1280, Height = 720, ScreenMatch = UiValues.MatchWidthOrHeight, Match = 0.5 };
            Assert.That(LayoutClassifier.Scale(2560, 720, panel), Is.EqualTo(Math.Sqrt(2)).Within(1e-9));
            panel.Match = 0;
            Assert.That(LayoutClassifier.Scale(2560, 720, panel), Is.EqualTo(2).Within(1e-9));
        }

        // ------------------------------------------------------------------ strings

        [Test]
        public void TemplatesFillNamedPlaceholders()
        {
            var args = new StringArgs().Add("slot", 2).Add("seed", "ASH-7Q");
            Assert.That(StringTable.Fill("Slot {slot} · Seed {seed}", args), Is.EqualTo("Slot 2 · Seed ASH-7Q"));
            Assert.That(StringTable.Fill("{missing} {slot}", args), Is.EqualTo("{missing} 2"), "unknown placeholders stay visible");
            Assert.That(Ui.Strings.Format(StringKeys.ConfirmLoadTitle, args), Is.EqualTo("Load slot 2?"));
            Assert.That(Ui.Strings.Get("no.such.key"), Is.EqualTo("no.such.key"));
            Assert.That(Ui.Strings.Has("class.reaver.name"), Is.True, "shipped strings are merged in");
        }

        // ------------------------------------------------------------------ slots (US-1.4)

        private string _dir;

        [SetUp]
        public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "ashen-ui-slots-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private static JObject Payload(string name, string savedAt, int floor) => new JObject
        {
            ["summary"] = new JObject
            {
                ["name"] = name, ["classId"] = "reaver", ["portrait"] = "class.reaver.sprite.ember", ["act"] = 2, ["floor"] = floor,
                ["hp"] = 31, ["hpMax"] = 40, ["seed"] = "ASH-7Q", ["savedAt"] = savedAt, ["journey"] = "classic", ["playtimeSeconds"] = 1800,
            },
        };

        [Test]
        public void EmptySlotsSayEmptyAndContinueHasNoTarget()
        {
            var rules = SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves));
            var slots = SlotSummaries.Read(new SaveService(_dir, rules));
            Assert.That(slots.Count, Is.EqualTo(rules.RunSlots));
            Assert.That(slots.Select(s => s.Index), Is.EqualTo(Enumerable.Range(1, rules.RunSlots)));
            Assert.That(slots.All(s => s.Status == SlotStatus.Empty));
            Assert.That(SlotSummaries.HasValidSlot(slots), Is.False);
            Assert.That(SlotSummaries.ContinueTarget(slots), Is.Null);
        }

        [Test]
        public void ReadySlotsCarryTheirSummaryAndContinueTakesTheNewest()
        {
            var rules = SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves));
            var saves = new SaveService(_dir, rules);
            saves.Checkpoint(rules.RunSlotName(1), Payload("Aldric", "2026-09-25T10:00:00Z", 7), "c", "s");
            saves.Checkpoint(rules.RunSlotName(3), Payload("Mira", "2026-09-26T08:30:00Z", 3), "c", "s");
            var slots = SlotSummaries.Read(saves);
            Assert.That(slots.Select(s => s.Status), Is.EqualTo(new[] { SlotStatus.Ready, SlotStatus.Empty, SlotStatus.Ready }));
            Assert.That(slots[0].Name, Is.EqualTo("Aldric"));
            Assert.That(slots[0].Floor, Is.EqualTo(7));
            Assert.That(slots[0].HpMax, Is.EqualTo(40));
            Assert.That(slots[0].Portrait, Is.EqualTo("class.reaver.sprite.ember"));
            Assert.That(SlotSummaries.HasValidSlot(slots), Is.True);
            Assert.That(SlotSummaries.ContinueTarget(slots).Index, Is.EqualTo(3));
        }

        [Test]
        public void NewerAndDamagedSavesAreReportedNotLoaded()
        {
            var rules = SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves));
            var newer = SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves));
            newer.CurrentVersion = rules.CurrentVersion + 1;
            new SaveService(_dir, newer).Checkpoint(rules.RunSlotName(1), Payload("Future", "2026-09-26T00:00:00Z", 1), "c", "s");
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, string.Format(SaveLayout.Primary, rules.RunSlotName(2))), "{ not json");
            var slots = SlotSummaries.Read(new SaveService(_dir, rules));
            Assert.That(slots[0].Status, Is.EqualTo(SlotStatus.Newer));
            Assert.That(slots[1].Status, Is.EqualTo(SlotStatus.Unreadable));
            Assert.That(SlotSummaries.HasValidSlot(slots), Is.False, "Continue stays disabled: nothing loadable");
        }
    }
}
