using System;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-7a (D-1710..): the web's Controls list (src/ui/screens/controls.js over the ACTIONS registry of src/ui/input.js) and the Menu overlay's quick list (quicknav.js over quickMenuRows, menuComponents.js).
    /// The actions, their labels, their default keys and pad buttons, how a binding reads, what a rebind stores, which key a press is, and the quick menu's rows, bands, locks and switches are the web's. Engine-free.
    /// </summary>
    [TestFixture, Category("Presentation")]
    public class ControlsMenuTests
    {
        private static UiData _ui;
        private string _dir;
        private SaveService _saves;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-controls-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private SettingsSession Settings() => new SettingsSession(Core2HostParityTests.Content, ProfileStore.Load(_saves), core2: true);

        // ------------------------------------------------------------------ the registry

        [Test]
        public void TheActionsAreTheWebsInItsOrderWithItsWords()
        {
            var actions = Ui.Controls.Actions;
            Assert.That(actions.Select(a => a.Id), Is.EqualTo(new[] { "confirm", "cancel", "endTurn", "menu", "deck", "relics", "stats", "flask1", "flask2", "flask3", "inspect" }));
            Assert.That(actions.Select(a => Ui.Strings.Get(a.LabelKey)), Is.EqualTo(new[]
            {
                "Confirm / Play", "Cancel / Back", "End Turn", "Open Menu", "Open Armoury (Deck)", "Open Armoury (Equipment)", "Open Armoury (Stats)",
                "Use Flask 1", "Use Flask 2", "Use Flask 3", "Inspect Selected",
            }));
            Assert.That(actions.Where(a => !a.KeyRebindable).Select(a => a.Id), Is.EqualTo(new[] { "confirm", "cancel" }), "Enter and Esc are fixed so the cursor and the overlay's close always work");
            Assert.That(actions.Select(a => a.DefButton), Is.EqualTo(new[] { 0, 1, 2, 9, 3, 4, 5, 6, 7, 10, 11 }));
        }

        [Test]
        public void TheDefaultBindingsReadAsTheWebsDo()
        {
            var bindings = Settings().Bindings(Ui.Controls);
            Assert.That(Ui.Controls.Actions.Select(a => bindings.KeyLabel(a.Id)), Is.EqualTo(new[] { "Enter", "Esc", "E", "M", "D", "R", "T", "F", "G", "H", "I" }));
            Assert.That(Ui.Controls.Actions.Select(a => bindings.PadName(a.Id)), Is.EqualTo(new[] { "A", "B", "X", "Start", "Y", "LB", "RB", "LT", "RT", "L3", "R3" }));
        }

        [Test]
        public void ThePadButtonsHaveTheWebsGlyphsNamesAndFallbacks()
        {
            var data = Ui.Controls;
            Assert.That(data.PadButtons.Count, Is.EqualTo(17));
            Assert.That(data.PadGlyph(12), Is.EqualTo("▲"));
            Assert.That(data.PadName(14), Is.EqualTo("D-Left"));
            Assert.That(data.PadName(16), Is.EqualTo("Guide"));
            Assert.That(data.PadName(40), Is.EqualTo("Btn 40"), "a button with no name");
            Assert.That(data.PadGlyph(40), Is.EqualTo("B40"));
            Assert.That(data.PadName(null), Is.EqualTo("—"), "nothing bound");
            Assert.That(data.PadButtons.Where(b => b.Index != 16).All(b => !string.IsNullOrEmpty(b.Source)), Is.True, "every button but Guide is an Input System control");
        }

        // ------------------------------------------------------------------ rebinding

        [Test]
        public void ARebindChangesTheLabelAndAFixedKeyRefusesOne()
        {
            var bindings = Settings().Bindings(Ui.Controls);
            Assert.That(bindings.SetKey("endTurn", " "), Is.True);
            Assert.That(bindings.KeyLabel("endTurn"), Is.EqualTo("Space"));
            Assert.That(bindings.SetKey("menu", "Escape"), Is.True, "the web does not refuse a clash or Escape for another action");
            Assert.That(bindings.KeyLabel("menu"), Is.EqualTo("Esc"));
            Assert.That(bindings.SetKey("deck", "ArrowUp"), Is.True);
            Assert.That(bindings.KeyLabel("deck"), Is.EqualTo("ArrowUp"), "a named key reads as its name");
            Assert.That(bindings.SetKey("confirm", "x"), Is.False);
            Assert.That(bindings.KeyLabel("confirm"), Is.EqualTo("Enter"));
            Assert.That(bindings.SetKey("nothing", "x"), Is.False);
            Assert.That(bindings.SetPad("inspect", 15), Is.True);
            Assert.That(bindings.PadName("inspect"), Is.EqualTo("D-Right"));
            Assert.That(bindings.ActionForButton(15), Is.EqualTo("inspect"));
        }

        [Test]
        public void AKeyMatchesWithoutCaseAndTwoActionsMayShareOne()
        {
            var bindings = Settings().Bindings(Ui.Controls);
            Assert.That(bindings.Matches("endTurn", "E"), Is.True);
            Assert.That(bindings.Matches("endTurn", "e"), Is.True);
            Assert.That(bindings.Matches("endTurn", "m"), Is.False);
            Assert.That(bindings.Matches("confirm", "Enter"), Is.False, "a fixed key is not in the key map: Enter always activates the cursor");
            bindings.SetKey("flask1", "e");
            Assert.That(bindings.Matches("flask1", "E") && bindings.Matches("endTurn", "E"), Is.True);
        }

        [Test]
        public void APressedKeyIsTheWebsKeyName()
        {
            var data = Ui.Controls;
            Assert.That(data.KeyName("LeftShift", default(char)), Is.Null, "a lone modifier binds nothing");
            Assert.That(data.KeyName("RightControl", default(char)), Is.Null);
            Assert.That(data.KeyName("E", 'e'), Is.EqualTo("e"), "a printable key is its character");
            Assert.That(data.KeyName("Space", ' '), Is.EqualTo(" "));
            Assert.That(data.KeyName("Return", '\n'), Is.EqualTo("Enter"));
            Assert.That(data.KeyName("KeypadEnter", '\n'), Is.EqualTo("Enter"));
            Assert.That(data.KeyName("UpArrow", default(char)), Is.EqualTo("ArrowUp"));
            Assert.That(data.KeyName("F5", default(char)), Is.EqualTo("F5"));
            Assert.That(data.KeyName("Escape", '\u001b'), Is.EqualTo(data.CancelKey), "Escape is the one key that cancels an armed rebind");
        }

        [Test]
        public void TheBindingsAreKeptInTheProfileAsTheWebKeepsThem()
        {
            var settings = Settings();
            var bindings = settings.Bindings(Ui.Controls);
            bindings.SetKey("endTurn", "x");
            bindings.SetPad("endTurn", 12);
            settings.SaveBindings(Ui.Controls, bindings);

            var again = new SettingsSession(Core2HostParityTests.Content, ProfileStore.Load(_saves), core2: true).Bindings(Ui.Controls);
            Assert.That(again.Key("endTurn"), Is.EqualTo("x"));
            Assert.That(again.KeyLabel("endTurn"), Is.EqualTo("X"));
            Assert.That(again.Pad("endTurn"), Is.EqualTo(12));
            Assert.That(again.KeyLabel("menu"), Is.EqualTo("M"), "the rest are the defaults");

            var stored = (JObject)ProfileStore.Load(_saves).Doc[SettingsKeys.Settings];
            Assert.That(((JObject)stored[Ui.Controls.StoredKeyBindings]).Properties().Count(), Is.EqualTo(9), "the whole key map is stored (every rebindable action), as the web writes it");
            Assert.That(((JObject)stored[Ui.Controls.StoredPadBindings]).Properties().Count(), Is.EqualTo(11), "and the whole pad map");
        }

        [Test]
        public void ChangedIsRaisedForLiveConsumers()
        {
            var settings = Settings();
            string seen = null;
            Action<SettingsSession, string> handler = (s, key) => seen = key;
            SettingsSession.Changed += handler;
            try { settings.SaveBindings(Ui.Controls, settings.Bindings(Ui.Controls)); }
            finally { SettingsSession.Changed -= handler; }
            Assert.That(seen, Is.EqualTo(Ui.Controls.StoredKeyBindings));
        }

        // ------------------------------------------------------------------ the pane

        [Test]
        public void TheControlsPaneHasTheWebsWordsAndOneRowPerAction()
        {
            var view = ControlsView.Build(Ui, Settings().Bindings(Ui.Controls), false);
            Assert.That(view.Eyebrow, Is.EqualTo("Controls"));
            Assert.That(view.Title, Is.EqualTo("Bindings"));
            Assert.That(view.Intro, Is.EqualTo("Move the focus cursor with the <b>arrow keys</b>, <b>D-pad</b>, or <b>left stick</b>; activate with <b>Enter</b> / <b>A</b>. In combat, number keys <b>1–9</b> select cards and targets."));
            Assert.That(view.PadState, Is.EqualTo("No pad detected"));
            Assert.That(view.PadNote, Is.EqualTo(" · Click a Rebind, then press a key or controller button. Arrow keys / D-pad always navigate."));
            Assert.That(ControlsView.Build(Ui, Settings().Bindings(Ui.Controls), true).PadState, Is.EqualTo("● pad connected"));
            Assert.That(new[] { view.KeyButton, view.PadButton, view.Listening }, Is.EqualTo(new[] { "Key", "Pad", "Press…" }));
            Assert.That(view.Rows.Select(r => r.Id), Is.EqualTo(Ui.Controls.Actions.Select(a => a.Id)));
            var confirm = view.Rows[0];
            Assert.That(confirm.KeyRebindable, Is.False);
            Assert.That(confirm.KeyText, Is.EqualTo("Enter"));
            Assert.That(confirm.PadText, Is.EqualTo("A"));
            var endTurn = view.Rows.Single(r => r.Id == "endTurn");
            Assert.That(endTurn.KeyRebindable, Is.True);
            Assert.That(new[] { endTurn.Label, endTurn.KeyText, endTurn.PadText }, Is.EqualTo(new[] { "End Turn", "E", "X" }));
        }

        // ------------------------------------------------------------------ the Menu overlay

        [Test]
        public void TheOverlayHasTheWebsTwoTabsInItsOrder()
        {
            var tabs = Ui.Menus.OverlayTabs;
            Assert.That(tabs.Select(t => t.Id), Is.EqualTo(new[] { "settings", "controls" }));
            Assert.That(tabs.Select(t => Ui.Strings.Get(t.LabelKey)), Is.EqualTo(new[] { "Settings", "Controls" }));
            Assert.That(tabs.Select(t => Ui.Strings.Get(t.TipKey)), Is.EqualTo(new[] { "Display, audio, and accessibility.", "Every key and pad button, and how to rebind them." }));
            Assert.That(Ui.Strings.Get(StringKeys.MenuGoTo), Is.EqualTo("Go to…"));
            Assert.That(Ui.Strings.Get(StringKeys.MenuSave), Is.EqualTo("Save Game"));
            foreach (var tab in tabs) Assert.That(Ui.Menus.Pause.Any(r => r.Tab == tab.Id), Is.True, "the quick menu has the row for tab " + tab.Id);
        }

        [Test]
        public void TheMenuOverlayIsAModalScreenOverTheRunWithTheSettingsFramesFocusRegions()
        {
            var menu = Ui.Screens.Get(ScreenIds.Menu);
            Assert.That(Ui.Screens.IsBuilt(ScreenIds.Menu) && menu.IsModal, Is.True, "the overlay sits over the run screen, as the web's does, not over the title's door art");
            Assert.That(menu.Background, Is.Null, "the run shows behind its veil");
            Assert.That(menu.Uxml, Is.EqualTo(Ui.Screens.Get(ScreenIds.Settings).Uxml), "the Settings frame in its overlay form");
            Assert.That(menu.FocusOrder, Does.Contain(UiNames.OverlayHead).And.Contain(UiNames.OverlayFoot).And.Contain(UiNames.ControlsScroll));
            Assert.That(menu.FocusOrderFor(MenuValues.FocusQuick), Is.EqualTo(new[] { UiNames.OverlayQuick }), "the Go to... list takes the focus while it is open");
            Assert.That(Ui.Screens.Get(ScreenIds.Settings).FocusOrder, Does.Not.Contain(UiNames.OverlayHead), "the title's Settings has no overlay head");
            Assert.That(Ui.Menus.Pause.Where(r => r.Tab != null).Select(r => r.Target).Distinct(), Is.EqualTo(new[] { ScreenIds.Menu }), "Settings and Controls rows open the overlay on their tab");
        }

        [Test]
        public void TheQuickMenuHasTheWebsRowsBandsAndTones()
        {
            var rows = QuickMenuView.Build(Ui, Ui.MenuContext(true, false), Settings());
            Assert.That(rows.Select(r => r.Def.Id), Is.EqualTo(new[] { "settings", "controls", "fullscreen", "music", "inventory", "character", "load", "save", "saveQuit", "quitWithoutSaving" }));
            Assert.That(rows.Select(r => r.Label), Is.EqualTo(new[] { "Settings", "Controls", "Fullscreen", "Music", "Inventory", "Character", "Load", "Save", "Save and Quit", "Quit Without Saving" }));
            Assert.That(rows.Where(r => r.Band).Select(r => r.Def.Id), Is.EqualTo(new[] { "fullscreen", "inventory", "load" }), "a hairline above the first row of the comfort, Armoury and run bands");
            Assert.That(rows.Where(r => r.Danger).Select(r => r.Def.Id), Is.EqualTo(new[] { "load", "quitWithoutSaving" }));
            Assert.That(rows.Where(r => r.Switch).Select(r => r.Def.Id), Is.EqualTo(new[] { "fullscreen", "music" }));
            Assert.That(rows.All(r => r.Enabled && !r.Locked), Is.True);
            Assert.That(rows.Single(r => r.Def.Id == "controls").Tip, Is.EqualTo("Every key and pad button, and how to rebind them."));
            Assert.That(rows.Single(r => r.Def.Id == "load").Tip, Is.EqualTo("Replace unsaved progress with the active slot after confirmation."));
            Assert.That(rows.Single(r => r.Def.Id == "character").Def.ArmouryTab, Is.EqualTo(ArmouryValues.TabStats));
            Assert.That(rows.Single(r => r.Def.Id == "inventory").Def.ArmouryTab, Is.EqualTo(ArmouryValues.TabInventory));
        }

        [Test]
        public void TheArmouryRowsAreLockedInAFightWithTheWebsSentence()
        {
            var rows = QuickMenuView.Build(Ui, Ui.MenuContext(true, true), Settings());
            Assert.That(rows.Select(r => r.Def.Id), Is.EqualTo(new[] { "settings", "controls", "fullscreen", "music", "inventory", "character", "load", "save", "saveQuit", "quitWithoutSaving" }), "shown, not hidden");
            foreach (var id in new[] { "inventory", "character" })
            {
                var row = rows.Single(r => r.Def.Id == id);
                Assert.That(row.Enabled, Is.False);
                Assert.That(row.Locked, Is.True);
                Assert.That(row.Tip, Is.EqualTo("Deck and equipment can only be changed outside combat. They unlock when the fight is resolved."));
            }
            Assert.That(rows.Where(r => !r.Locked).All(r => r.Enabled), Is.True);
        }

        [Test]
        public void TheOpenTabsRowIsMarkedInTheOverlay()
        {
            Assert.That(QuickMenuView.Build(Ui, Ui.MenuContext(true, false), Settings()).Any(r => r.Current), Is.False, "outside the overlay no row is current");
            Assert.That(QuickMenuView.Build(Ui, Ui.MenuContext(true, false), Settings(), MenuValues.TabControls).Where(r => r.Current).Select(r => r.Def.Id), Is.EqualTo(new[] { "controls" }));
            Assert.That(QuickMenuView.Build(Ui, Ui.MenuContext(true, false), Settings(), MenuValues.TabSettings).Where(r => r.Current).Select(r => r.Def.Id), Is.EqualTo(new[] { "settings" }));
        }

        [Test]
        public void TheSwitchesSayTheirStateAsTheWebDoes()
        {
            var settings = Settings();
            Row(settings, "fullscreen", out var fullscreen);
            Assert.That(new[] { fullscreen.State, fullscreen.Condition }, Is.EqualTo(new[] { "Off", "Fullscreen off." }));
            Assert.That(settings.Set(SettingIds.Fullscreen, true), Is.Null);
            Row(settings, "fullscreen", out fullscreen);
            Assert.That(fullscreen.Checked, Is.True);
            Assert.That(new[] { fullscreen.State, fullscreen.Condition }, Is.EqualTo(new[] { "On", "Fullscreen on." }));

            Row(settings, "music", out var music);
            Assert.That(music.Checked, Is.True, "music is on by default");
            Assert.That(music.Condition, Is.EqualTo("Music on · volume " + (int)settings.Number(SettingIds.MusicVolume) + "%."));
            Assert.That(settings.Set(SettingIds.MuteAudio, true), Is.Null);
            Row(settings, "music", out music);
            Assert.That(music.Condition, Is.EqualTo("Music on · all audio muted."));
            Assert.That(settings.Set(SettingIds.MusicEnabled, false), Is.Null);
            Row(settings, "music", out music);
            Assert.That(music.Checked, Is.False);
            Assert.That(music.Condition, Is.EqualTo("Music off · sound effects unchanged."), "off says so first, whatever the mute");
        }

        private static void Row(SettingsSession settings, string id, out QuickMenuRow row) =>
            row = QuickMenuView.Build(Ui, Ui.MenuContext(true, false), settings).Single(r => r.Def.Id == id);

        [Test]
        public void EveryWordTheMenuAndControlsDataNamesIsAString()
        {
            var keys = Ui.Menus.Pause.SelectMany(r => new[] { r.LabelKey, r.TipKey })
                .Concat(Ui.Menus.OverlayTabs.SelectMany(t => new[] { t.LabelKey, t.TipKey }))
                .Concat(Ui.Controls.Actions.Select(a => a.LabelKey))
                .Where(k => k != null).Distinct().ToList();
            Assert.That(keys.Where(k => !Ui.Strings.Has(k)), Is.Empty);
            foreach (var setting in Ui.Menus.Pause.Where(r => r.IsSwitch).Select(r => r.Setting))
                Assert.That(Settings().Defs.Find(setting), Is.Not.Null, "the switch's setting exists: " + setting);
        }
    }
}
