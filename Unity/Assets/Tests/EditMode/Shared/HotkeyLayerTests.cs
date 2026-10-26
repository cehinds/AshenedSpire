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
    /// P-7c (D-1750..): the web's input layer, live. src/ui/input.js onKeydown and pollPads decide, and the per-screen key handlers (screens/combat.js:2834-2985, screens/map.js:455-490) answer: which action a key or pad
    /// button is on which screen, with the player's rebinding, a veil owning input, a text field owning typing, Ctrl/Alt/Meta swallowing the fight's keys, the tab ring taking the bumpers and [ ], and the d-pad moving the focus
    /// unless an action is bound to it. Plus the card keys, the quick menu setting and the hint bar. Engine-free.
    /// </summary>
    [TestFixture, Category("Presentation")]
    public class HotkeyLayerTests
    {
        private static UiData _ui;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        private static HotkeyState Combat(bool veil = false, bool typing = false, bool ring = false) => new HotkeyState { Screen = ScreenIds.Core2Combat, Veil = veil, Typing = typing, TabRing = ring };

        private static HotkeyState Map(bool veil = false) => new HotkeyState { Screen = ScreenIds.ActMap, Veil = veil };

        private static KeyPress Key(string key, bool ctrl = false, bool repeat = false) => new KeyPress { Key = key, Ctrl = ctrl, Repeat = repeat };

        private static HotkeyDispatcher Dispatcher(JObject keys = null, JObject pads = null) => new HotkeyDispatcher(Ui.Controls, new ControlBindings(Ui.Controls, keys, pads));

        private static string ActionOf(HotkeyResult r) => r.Kind == HotkeyKind.Action ? r.Id : null;

        // ------------------------------------------------------------------ the keyboard

        [Test]
        public void TheFightAnswersTheDefaultKeysWithTheWebsActions()
        {
            var d = Dispatcher();
            var want = new[]
            {
                ("e", HotkeyActions.EndTurn), ("m", HotkeyActions.Menu), ("d", HotkeyActions.Deck), ("r", HotkeyActions.Relics), ("t", HotkeyActions.Stats),
                ("f", HotkeyActions.Flask1), ("g", HotkeyActions.Flask2), ("h", HotkeyActions.Flask3), ("i", HotkeyActions.Inspect), ("E", HotkeyActions.EndTurn),
            };
            foreach (var (key, id) in want) Assert.That(ActionOf(d.Key(Key(key), Combat())), Is.EqualTo(id), key);
            Assert.That(d.Key(Key("x"), Combat()).Consumed, Is.False, "a key nothing is bound to is left alone");
            Assert.That(d.Key(Key("Enter"), Combat()).Consumed, Is.False, "Enter is the focus cursor's, not an action of the fight");
        }

        [Test]
        public void ARebindMovesTheKeyAndTheOldKeyGoesQuiet()
        {
            var d = Dispatcher(new JObject { [HotkeyActions.EndTurn] = "x" });
            Assert.That(ActionOf(d.Key(Key("x"), Combat())), Is.EqualTo(HotkeyActions.EndTurn));
            Assert.That(d.Key(Key("e"), Combat()).Consumed, Is.False, "web matchAction reads the live binding only");
            d.Bindings = new ControlBindings(Ui.Controls, new JObject { [HotkeyActions.EndTurn] = "e" });
            Assert.That(ActionOf(d.Key(Key("e"), Combat())), Is.EqualTo(HotkeyActions.EndTurn), "a live rebind needs no restart");
        }

        [Test]
        public void AVeilATextFieldAGateAndAHeldKeyAllSwallowAScreensKeys()
        {
            var d = Dispatcher();
            Assert.That(d.Key(Key("e"), Combat(veil: true)).Consumed, Is.False, "any veil owns input while it stands (web veilIsOpen)");
            Assert.That(d.Key(Key("e"), Combat(typing: true)).Consumed, Is.False, "typing is not a hotkey");
            Assert.That(d.Key(Key("e"), new HotkeyState { Screen = ScreenIds.Core2Combat, Blocked = true }).Consumed, Is.False, "an armed Rebind or the title gate takes every key");
            Assert.That(d.Key(Key("e", repeat: true), Combat()).Consumed, Is.False, "the key's auto-repeat does not act again");
            Assert.That(d.Key(Key("e"), new HotkeyState { Screen = ScreenIds.Title }).Consumed, Is.False, "a screen with no scope has no hotkeys (the web mounts none on it)");
            Assert.That(d.Key(Key("e"), new HotkeyState { Screen = ScreenIds.Core2Shop }).Consumed, Is.False);
        }

        [Test]
        public void TheFightIgnoresCtrlAltMetaButTheMapDoesNot()
        {
            var d = Dispatcher();
            Assert.That(d.Key(Key("e", ctrl: true), Combat()).Consumed, Is.False, "combat.js: if (ev.metaKey || ev.ctrlKey || ev.altKey) return");
            Assert.That(ActionOf(d.Key(Key("d", ctrl: true), Map())), Is.EqualTo(HotkeyActions.Deck), "map.js has no modifier test");
        }

        [Test]
        public void TheMapAnswersTheMenuAndTheThreeArmouryDoorsAndItsZoomKeysOnly()
        {
            var d = Dispatcher();
            Assert.That(new[] { "m", "d", "r", "t" }.Select(k => ActionOf(d.Key(Key(k), Map()))), Is.EqualTo(new[] { HotkeyActions.Menu, HotkeyActions.Deck, HotkeyActions.Relics, HotkeyActions.Stats }));
            foreach (var key in new[] { "e", "f", "g", "h", "i", "1", "q" }) Assert.That(d.Key(Key(key), Map()).Consumed, Is.False, key + " is the fight's");
            foreach (var key in new[] { "+", "=" }) Assert.That(d.Key(Key(key), Map()).Id, Is.EqualTo(HotkeyValues.CommandZoomIn));
            foreach (var key in new[] { "-", "_" }) Assert.That(d.Key(Key(key), Map()).Id, Is.EqualTo(HotkeyValues.CommandZoomOut));
            Assert.That(d.Key(Key("0"), Map()).Id, Is.EqualTo(HotkeyValues.CommandZoomReset));
            Assert.That(d.Key(Key("0"), Map()).Kind, Is.EqualTo(HotkeyKind.Command));
            Assert.That(d.Key(Key("+"), Map(veil: true)).Consumed, Is.False, "the planned door is a veil: map.js returns on veilIsOpen");
        }

        [Test]
        public void TheArmouryDoorsAnswerOnlyWhenExactlyOneOfThemIsOnTheKeyAndTheMenuComesFirst()
        {
            var two = Dispatcher(new JObject { [HotkeyActions.Deck] = "z", [HotkeyActions.Relics] = "z" });
            Assert.That(two.Key(Key("z"), Map()).Consumed, Is.False, "web actionDestinationForEvent: two matches is no destination");
            Assert.That(two.Key(Key("d"), Map()).Consumed, Is.False);
            var menuFirst = Dispatcher(new JObject { [HotkeyActions.Menu] = "z", [HotkeyActions.Deck] = "z" });
            Assert.That(ActionOf(menuFirst.Key(Key("z"), Map())), Is.EqualTo(HotkeyActions.Menu), "map.js asks the menu before the destination");
            var one = Dispatcher(new JObject { [HotkeyActions.Stats] = "z" });
            Assert.That(ActionOf(one.Key(Key("z"), Map())), Is.EqualTo(HotkeyActions.Stats));
        }

        [Test]
        public void TheArmouryAnswersTheDeckEditorsThreeKeysOnly()
        {
            var d = Dispatcher();
            var armoury = new HotkeyState { Screen = ScreenIds.Armoury };
            Assert.That(ActionOf(d.Key(Key("m"), armoury)), Is.EqualTo(HotkeyActions.Menu), "Done");
            Assert.That(ActionOf(d.Key(Key("d"), armoury)), Is.EqualTo(HotkeyActions.Deck), "cycle the filters (P-7d)");
            Assert.That(ActionOf(d.Key(Key("e"), armoury)), Is.EqualTo(HotkeyActions.EndTurn), "pick a row (P-7d)");
            foreach (var key in new[] { "r", "t", "f", "i", "1" }) Assert.That(d.Key(Key(key), armoury).Consumed, Is.False, key);
        }

        // ------------------------------------------------------------------ the card keys

        [Test]
        public void TheCardKeysAreTheFightsAndTheMapHasNone()
        {
            var d = Dispatcher();
            for (var i = 1; i <= 9; i++)
            {
                var r = d.Key(Key(i.ToString()), Combat());
                Assert.That(r.Kind, Is.EqualTo(HotkeyKind.Card), i.ToString());
                Assert.That(r.Index, Is.EqualTo(i - 1));
                Assert.That(r.Target, Is.True, "a digit can name an enemy while aiming");
            }
            var q = d.Key(Key("Q"), Combat());
            Assert.That(q.Kind, Is.EqualTo(HotkeyKind.Card));
            Assert.That(q.Index, Is.EqualTo(9));
            Assert.That(q.Target, Is.False, "Q never aims");
            Assert.That(d.Key(Key("1"), Map()).Consumed, Is.False);
            Assert.That(d.Key(Key("1"), Combat(veil: true)).Consumed, Is.False);
        }

        [Test]
        public void ACardKeyNamesTheNthHandSlotOrWhileAimingTheNthLivingEnemy()
        {
            Assert.That(CardHotkeys.Resolve(2, true, false, 5, 2).Kind, Is.EqualTo(CardKeyKind.Select));
            Assert.That(CardHotkeys.Resolve(5, true, false, 5, 2).Kind, Is.EqualTo(CardKeyKind.None), "slot 6 of a hand of 5: swallowed");
            var aim = CardHotkeys.Resolve(1, true, true, 5, 2);
            Assert.That(aim.Kind, Is.EqualTo(CardKeyKind.Target));
            Assert.That(aim.Index, Is.EqualTo(1));
            Assert.That(CardHotkeys.Resolve(2, true, true, 5, 2).Kind, Is.EqualTo(CardKeyKind.None), "no third enemy");
            Assert.That(CardHotkeys.Resolve(9, false, true, 10, 1).Kind, Is.EqualTo(CardKeyKind.Select), "Q stays slot 10 while aiming");
        }

        // ------------------------------------------------------------------ the tab ring

        [Test]
        public void TheBracketsAndTheBumpersStepTheOpenTabSetAndOnlyWhileOneIsOpen()
        {
            var d = Dispatcher();
            Assert.That(d.Key(Key("["), Combat(ring: true)).Kind, Is.EqualTo(HotkeyKind.TabPrev));
            Assert.That(d.Key(Key("]"), Combat(ring: true)).Kind, Is.EqualTo(HotkeyKind.TabNext));
            Assert.That(d.Key(Key("]"), Combat()).Consumed, Is.False, "no ring, no brackets");
            Assert.That(d.Key(Key("]"), Combat(ring: true, typing: true)).Consumed, Is.False);
            var menu = new HotkeyState { Screen = ScreenIds.Menu, TabRing = true };
            Assert.That(d.Key(Key("]"), menu).Kind, Is.EqualTo(HotkeyKind.TabNext), "a screen with no scope still has its ring");
            Assert.That(d.Pad(4, menu).Kind, Is.EqualTo(HotkeyKind.TabPrev), "LB");
            Assert.That(d.Pad(5, menu).Kind, Is.EqualTo(HotkeyKind.TabNext), "RB");
        }

        [Test]
        public void TheBumpersKeepTheirGlobalBindingsWhenNoTabSetHoldsThem()
        {
            var d = Dispatcher();
            Assert.That(ActionOf(d.Pad(4, Map())), Is.EqualTo(HotkeyActions.Relics), "LB is Equipment");
            Assert.That(ActionOf(d.Pad(5, Map())), Is.EqualTo(HotkeyActions.Stats), "RB is Stats");
            Assert.That(d.Pad(4, new HotkeyState { Screen = ScreenIds.ActMap, TabRing = true }).Kind, Is.EqualTo(HotkeyKind.TabPrev), "contextual precedence: the open set wins");
        }

        [Test]
        public void TheRingWrapsAtBothEnds()
        {
            Assert.That(TabRing.Step(3, 2, 1), Is.EqualTo(0));
            Assert.That(TabRing.Step(3, 0, -1), Is.EqualTo(2));
            Assert.That(TabRing.Step(3, 1, 1), Is.EqualTo(2));
            Assert.That(TabRing.Step(3, -1, 1), Is.EqualTo(1), "no tab chosen yet: from the first");
            Assert.That(TabRing.Step(0, 0, 1), Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ the pad

        [Test]
        public void APadButtonIsTheSyntheticKeyOfTheActionItIsBoundTo()
        {
            var d = Dispatcher();
            Assert.That(ActionOf(d.Pad(9, Combat())), Is.EqualTo(HotkeyActions.Menu), "Start");
            Assert.That(ActionOf(d.Pad(2, Combat())), Is.EqualTo(HotkeyActions.EndTurn), "X");
            Assert.That(ActionOf(d.Pad(3, Combat())), Is.EqualTo(HotkeyActions.Deck), "Y");
            Assert.That(new[] { 6, 7, 10 }.Select(b => ActionOf(d.Pad(b, Combat()))), Is.EqualTo(new[] { HotkeyActions.Flask1, HotkeyActions.Flask2, HotkeyActions.Flask3 }));
            Assert.That(ActionOf(d.Pad(11, Combat())), Is.EqualTo(HotkeyActions.Inspect));
            Assert.That(d.Pad(2, Map()).Consumed, Is.False, "X is End Turn: the map has none");
            Assert.That(d.Pad(9, Combat(veil: true)).Consumed, Is.False, "a veil owns the pad too");
            Assert.That(d.Pad(8, Combat()).Consumed, Is.False, "Back is bound to nothing");
        }

        [Test]
        public void ConfirmAndCancelAreTheCursorsAndEscapeOnEveryScreen()
        {
            var d = Dispatcher();
            foreach (var screen in new[] { ScreenIds.Title, ScreenIds.Settings, ScreenIds.Core2Shop, ScreenIds.Core2Combat })
            {
                var state = new HotkeyState { Screen = screen };
                Assert.That(d.Pad(0, state).Kind, Is.EqualTo(HotkeyKind.Confirm), screen);
                Assert.That(d.Pad(1, state).Kind, Is.EqualTo(HotkeyKind.Cancel), screen);
            }
            Assert.That(d.Pad(0, new HotkeyState { Screen = ScreenIds.Core2Combat, Blocked = true }).Consumed, Is.False);
            var rebound = Dispatcher(pads: new JObject { [HotkeyActions.Confirm] = 3, [HotkeyActions.Cancel] = 2 });
            Assert.That(rebound.Pad(3, Map()).Kind, Is.EqualTo(HotkeyKind.Confirm), "only the pad button of Confirm and Cancel is rebindable");
            Assert.That(rebound.Pad(2, Map()).Kind, Is.EqualTo(HotkeyKind.Cancel));
        }

        [Test]
        public void TheDpadMovesTheFocusUnlessAnActionIsBoundToIt()
        {
            var d = Dispatcher();
            var state = new HotkeyState { Screen = ScreenIds.Settings };
            Assert.That(d.Pad(12, state).Kind, Is.EqualTo(HotkeyKind.Move));
            Assert.That(d.Pad(12, state).Delta, Is.EqualTo(-1));
            Assert.That(d.Pad(13, state).Delta, Is.EqualTo(1));
            Assert.That(d.Pad(14, state).Delta, Is.EqualTo(-1));
            Assert.That(d.Pad(15, state).Delta, Is.EqualTo(1));
            var bound = Dispatcher(pads: new JObject { [HotkeyActions.Deck] = 12 });
            Assert.That(ActionOf(bound.Pad(12, Map())), Is.EqualTo(HotkeyActions.Deck), "web pollPads asks actionForButton before the d-pad");
        }

        [Test]
        public void ARebindRoutesThePadToo()
        {
            var d = Dispatcher(pads: new JObject { [HotkeyActions.EndTurn] = 8 });
            Assert.That(ActionOf(d.Pad(8, Combat())), Is.EqualTo(HotkeyActions.EndTurn));
            Assert.That(d.Pad(2, Combat()).Consumed, Is.False, "X is bound to nothing now");
        }

        [Test]
        public void AHintChipPressIsTheSameDoorAsThePad()
        {
            var d = Dispatcher();
            Assert.That(ActionOf(d.Perform(HotkeyActions.Deck, Map())), Is.EqualTo(HotkeyActions.Deck));
            Assert.That(d.Perform(HotkeyActions.Deck, Map(veil: true)).Consumed, Is.False, "a chip is refused while a veil stands, as the key is");
            Assert.That(d.Perform(HotkeyActions.EndTurn, Map()).Consumed, Is.False);
            var rebound = Dispatcher(new JObject { [HotkeyActions.Deck] = "z" });
            Assert.That(ActionOf(rebound.Perform(HotkeyActions.Deck, Map())), Is.EqualTo(HotkeyActions.Deck), "the chip dispatches the CURRENT binding");
        }

        // ------------------------------------------------------------------ the hint bar

        [Test]
        public void TheMapsHintBarIsTheWebsFourChipsDerivedFromTheLiveBindings()
        {
            var bindings = new ControlBindings(Ui.Controls);
            var keys = HintBarView.Build(Ui, bindings, HotkeyValues.HintMap, false);
            Assert.That(keys.Select(c => c.Id), Is.EqualTo(new[] { HotkeyActions.Deck, HotkeyActions.Relics, HotkeyActions.Stats, HotkeyActions.Menu }));
            Assert.That(keys.Select(c => c.Key), Is.EqualTo(new[] { "D", "R", "T", "M" }));
            Assert.That(keys.Select(c => c.Short), Is.EqualTo(new[] { "Deck", "Equipment", "Stats", "Menu" }));
            Assert.That(keys.Select(c => c.Title), Is.EqualTo(new[] { "Deck (D)", "Equipment (R)", "Stats (T)", "Menu (M)" }));
            var pad = HintBarView.Build(Ui, bindings, HotkeyValues.HintMap, true);
            Assert.That(pad.Select(c => c.Key), Is.EqualTo(new[] { "Y", "LB", "RB", "Start" }), "a connected pad reads its glyphs");
            Assert.That(pad.Select(c => c.Title), Is.EqualTo(new[] { "Deck (Y)", "Equipment (LB)", "Stats (RB)", "Menu (Start)" }));
        }

        [Test]
        public void ARebindMovesTheChipAndTheOtherContextsAreTheWebs()
        {
            var bindings = new ControlBindings(Ui.Controls, new JObject { [HotkeyActions.Deck] = "z" }, new JObject { [HotkeyActions.Menu] = 100 });
            Assert.That(HintBarView.Build(Ui, bindings, HotkeyValues.HintMap, false)[0].Key, Is.EqualTo("Z"));
            Assert.That(HintBarView.Build(Ui, bindings, HotkeyValues.HintMap, true).Last().Key, Is.EqualTo("B100"), "a button with no glyph");
            Assert.That(HintBarView.Build(Ui, bindings, HotkeyValues.HintCombat, false).Select(c => c.Short), Is.EqualTo(new[] { "End Turn", "Deck", "Equipment", "Stats", "Menu" }));
            var aiming = HintBarView.Build(Ui, new ControlBindings(Ui.Controls), HotkeyValues.HintTargeting, false);
            Assert.That(aiming.Select(c => c.Short + ":" + c.Key), Is.EqualTo(new[] { "Confirm:Enter", "Cancel:Esc" }), "fixed keys read their hint");
            Assert.That(HintBarView.Build(Ui, bindings, "nowhere", false), Is.Empty);
        }

        // ------------------------------------------------------------------ the quick menu setting

        [Test]
        public void TheQuickMenuModesAreTheWebs()
        {
            Assert.That(QuickNav.Resolve(null), Is.EqualTo(HotkeyValues.QuickMirror), "fresh or invalid values use mirror");
            Assert.That(QuickNav.Resolve("sideways"), Is.EqualTo(HotkeyValues.QuickMirror));
            Assert.That(QuickNav.Resolve(HotkeyValues.QuickOff), Is.EqualTo(HotkeyValues.QuickOff));
            Assert.That(QuickNav.Direct(HotkeyValues.QuickOff), Is.True, "off keeps the direct-to-Settings route");
            Assert.That(QuickNav.Direct(HotkeyValues.QuickMirror), Is.False);
            Assert.That(QuickNav.Launcher(HotkeyValues.QuickMirror), Is.True);
            Assert.That(QuickNav.Launcher(HotkeyValues.QuickSwitcher), Is.False);
            Assert.That(QuickNav.Launcher(HotkeyValues.QuickOff), Is.False);
            Assert.That(QuickNav.Folds(HotkeyValues.QuickSwitcher, true), Is.True, "switcher folds the strip on a narrow screen");
            Assert.That(QuickNav.Folds(HotkeyValues.QuickSwitcher, false), Is.False);
            Assert.That(QuickNav.Folds(HotkeyValues.QuickMirror, true), Is.False);
        }

        [Test]
        public void TheQuickMenuIsASettingOfTheInterfaceWithTheWebsWordsAndDefault()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ashen-hotkeys-" + Guid.NewGuid().ToString("N"));
            try
            {
                var saves = new SaveService(dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
                var settings = new SettingsSession(Core2HostParityTests.Content, ProfileStore.Load(saves), core2: true);
                var def = settings.Defs.Find(SettingIds.QuickNav);
                Assert.That(def, Is.Not.Null);
                Assert.That(def.Options, Is.EqualTo(new[] { "off", "mirror", "switcher" }));
                Assert.That(settings.Text(SettingIds.QuickNav), Is.EqualTo(HotkeyValues.QuickMirror));
                var shown = settings.ControlOf(Ui, def);
                Assert.That(shown.Label, Is.EqualTo("Quick menu"));
                Assert.That(shown.AsSelect, Is.False, "three options and no dropdown flag: segmented");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        // ------------------------------------------------------------------ the data

        [Test]
        public void EveryNameTheHotkeyDataUsesIsARealActionOrString()
        {
            var data = Ui.Controls;
            var ids = data.Actions.Select(a => a.Id).ToList();
            foreach (var scope in data.Hotkeys.Scopes.Values) Assert.That(scope.Actions, Is.SubsetOf(ids), scope.Screen);
            foreach (var bar in data.Hotkeys.HintBars) Assert.That(bar.Value, Is.SubsetOf(ids), bar.Key);
            Assert.That(data.Hotkeys.HintLabels.Keys, Is.EquivalentTo(ids), "every action has its short words");
            foreach (var key in data.Hotkeys.HintLabels.Values) Assert.That(Ui.Strings.Get(key), Is.Not.Empty, key);
            Assert.That(Ui.Strings.Get(data.Hotkeys.MenuTipTitle), Is.EqualTo("Menu"));
            Assert.That(Ui.Strings.Get(data.Hotkeys.MenuTipList), Is.EqualTo("Everywhere you can go from here."));
            Assert.That(Ui.Strings.Get(data.Hotkeys.MenuTipDirect), Is.EqualTo("Armoury, settings, controls and saving."));
            foreach (var scope in data.Hotkeys.Scopes.Keys) Assert.That(Ui.Screens.Get(scope), Is.Not.Null, scope + " is a screen of ui/screens.json");
            Assert.That(data.Actions.Where(a => a.Destination != null).Select(a => a.Id), Is.EqualTo(new[] { HotkeyActions.Deck, HotkeyActions.Relics, HotkeyActions.Stats }));
            Assert.That(data.Hotkeys.RingPrevButton, Is.EqualTo(data.Find(HotkeyActions.Relics).DefButton), "the defaults SPEND LB and RB on Equipment and Stats, so the ring takes precedence");
        }

        [Test]
        public void ThePunctuationAndDigitsAKeyPressNamesAreTheWebsKeys()
        {
            var data = Ui.Controls;
            Assert.That(data.KeyName("LeftBracket", '\0'), Is.EqualTo("["));
            Assert.That(data.KeyName("RightBracket", '\0'), Is.EqualTo("]"));
            Assert.That(data.KeyName("Alpha0", '\0'), Is.EqualTo("0"));
            Assert.That(data.KeyName("Minus", '\0'), Is.EqualTo("-"));
            Assert.That(data.KeyName("Equals", '\0'), Is.EqualTo("="));
            Assert.That(data.KeyName("D", '\0'), Is.EqualTo("D"));
        }
    }
}
