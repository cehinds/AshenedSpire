using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.App.Settings;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>Title options (review captures force the gate on or off).</summary>
    public sealed class TitleArgs
    {
        public bool? Gate;
    }

    public sealed class MenuItemViewModel
    {
        public MenuEntryDef Def;
        public string Label;
        public bool Enabled;
    }

    public sealed class TitleViewModel : ViewModel
    {
        private bool _gateActive;
        private bool _hasPreview;
        private string _identity;
        private string _progress;
        private string _slot;
        private string _class;
        private string _portrait;
        private string _build;
        private IReadOnlyList<MenuItemViewModel> _items = Array.Empty<MenuItemViewModel>();

        public bool GateActive { get => _gateActive; set => Set(ref _gateActive, value); }
        public bool HasPreview { get => _hasPreview; set => Set(ref _hasPreview, value); }
        public string PreviewIdentity { get => _identity; set => Set(ref _identity, value); }
        public string PreviewProgress { get => _progress; set => Set(ref _progress, value); }
        public string PreviewSlot { get => _slot; set => Set(ref _slot, value); }
        public string PreviewClass { get => _class; set => Set(ref _class, value); }
        public string PreviewPortrait { get => _portrait; set => Set(ref _portrait, value); }
        public string Build { get => _build; set => Set(ref _build, value); }
        public IReadOnlyList<MenuItemViewModel> Items { get => _items; set { _items = value; Notify(); } }
    }

    /// <summary>
    /// W-02 title (US-1.2 gate, US-1.3 menu, US-0.10 footer). The press-any-input gate shows first; any key,
    /// button, click or tap lifts it and focus lands on the first enabled row. Rows come from ui/menus.json in the
    /// owner order; Continue is disabled without a valid slot. With a save, the slot preview sits beside the menu
    /// (W3b); without, the menu stands alone (W3a). The footer shows the build and the AI disclosure, which stays
    /// visible and opens the full disclosure.
    /// </summary>
    public sealed class TitleScreen : ScreenView
    {
        private static bool _gateLifted;
        private bool _revealing;

        private readonly List<LocButton> _buttons = new List<LocButton>();
        private IDisposable _anyButton;
        private IVisualElementScheduledItem _pulse;
        private IReadOnlyList<SlotSummary> _slots = Array.Empty<SlotSummary>();

        public TitleViewModel ViewModel { get; } = new TitleViewModel();

        /// <summary>The menu buttons in data order (tests and captures inspect them).</summary>
        public IReadOnlyList<LocButton> Buttons => _buttons;

        /// <summary>Forget that the gate was lifted this session (tests).</summary>
        public static void ResetSession() => _gateLifted = false;

        protected override void OnBind(object args)
        {
            var menu = Root.Q(UiNames.Menu);
            foreach (var entry in Ui.Data.Menus.Title.Where(e => e.Visible))
            {
                var button = MenuButton(entry);
                var captured = entry;
                button.clicked += () => Activate(captured, button);
                menu.Add(button);
                _buttons.Add(button);
            }
            SetArt(UiNames.TitleArtDoor, FrontArt.BackgroundDoor);
            SetArt(UiNames.TitleArtDoorPhone, FrontArt.BackgroundDoorPhone);
            SetArt(UiNames.TitleArtGate, FrontArt.BackgroundGate);
            SetArt(UiNames.TitleArtLit, FrontArt.BackgroundLit);
            var disclosure = Root.Q<LocButton>(UiNames.AiDisclosure);
            if (disclosure != null) disclosure.clicked += () => ConfirmRequest.Open(Nav, new ConfirmRequest { ConfirmId = ConfirmIds.AiNotice });

            var gate = Root.Q(UiNames.Gate);
            gate?.RegisterCallback<PointerDownEvent>(_ => LiftGate());
            ViewModel.Build = Ui.Data.Strings.Format(StringKeys.TitleBuild, new StringArgs().Add(UiPlaceholders.Version, Ui.BuildVersion));
            ViewModel.Observe(Render);
            Refresh();

            var gateOn = (args as TitleArgs)?.Gate ?? (Context.Def.Gate && !_gateLifted);
            ViewModel.GateActive = gateOn;
            if (gateOn && !(args is TitleArgs))
            {
                _anyButton = InputSystem.onAnyButtonPress.CallOnce(_ => LiftGate());
                var pulseMs = Ui.Data.Tokens.Duration(TokenKeys.GatePulse);
                _pulse = Root.schedule.Execute(() => Root.ToggleInClassList(UiClasses.GatePulse)).Every(pulseMs);
            }
        }

        private void SetArt(string name, string art) => BoardHud.SetImage(Root.Q(name), BoardHud.Art(Ui, ArtKinds.Background, art));

        /// <summary>
        /// One plate of the menu (web title.js menuHtml): the label in Cinzel caps; Continue is the taller card (the journey icon, the class, the facts, the arrow);
        /// a row with an icon wears it beside its label; New Game is the ready green (player-polish.css:221).
        /// </summary>
        private LocButton MenuButton(MenuEntryDef entry)
        {
            var isContinue = entry.Action == MenuActions.Continue;
            var plain = !isContinue && entry.Icon == null;
            var button = plain ? new LocButton(entry.LabelKey) : new LocButton();
            button.AddToClassList(UiClasses.MenuItem);
            button.AddToClassList(UiClasses.ButtonPlate);
            button.AddToClassList(UiClasses.Upper);
            if (entry.Action == MenuActions.Open && entry.Mode == UiValues.SlotModeNew)
            {
                button.AddToClassList(UiClasses.ButtonReady);
                button.AddToClassList(FrontClasses.MenuItemReady);
            }
            if (plain) return button;
            if (isContinue)
            {
                button.AddToClassList(FrontClasses.MenuItemContinue);
                button.Add(Image(FrontClasses.MenuIcon, entry.Icon));
                var texts = new VisualElement { pickingMode = PickingMode.Ignore };
                texts.AddToClassList(FrontClasses.ContinueTexts);
                texts.Add(Label(new LocLabel(entry.LabelKey), FrontClasses.ContinueHead, null));
                texts.Add(Label(new LocLabel(), FrontClasses.ContinueName, UiNames.PreviewIdentity));
                texts.Add(Label(new LocLabel(), FrontClasses.ContinueFacts, UiNames.PreviewProgress));
                button.Add(texts);
                button.Add(Image(FrontClasses.ContinueArrow, FrontArt.IconNext));
                return button;
            }
            button.Add(Image(FrontClasses.MenuIcon, entry.Icon));
            button.Add(Label(new LocLabel(entry.LabelKey), FrontClasses.MenuLabel, null));
            return button;
        }

        private VisualElement Image(string cls, string icon)
        {
            var image = new VisualElement { pickingMode = PickingMode.Ignore };
            image.AddToClassList(cls);
            BoardHud.SetImage(image, Ui.Icon(icon));
            return image;
        }

        private static LocLabel Label(LocLabel label, string cls, string name)
        {
            label.pickingMode = PickingMode.Ignore;
            label.AddToClassList(cls);
            label.AddToClassList(UiClasses.Upper);
            if (name != null) label.name = name;
            return label;
        }

        /// <summary>Re-read the slots and re-evaluate the menu (after returning from W-03, a save may be gone).</summary>
        public void Refresh()
        {
            _slots = SlotSummaries.Read(Ui.Saves);
            var states = MenuRules.Evaluate(Ui.Data.Menus.Title, Ui.Data.MenuContext(SlotSummaries.HasValidSlot(_slots)));
            ViewModel.Items = states.Where(s => s.Visible).Select(s => new MenuItemViewModel { Def = s.Def, Label = Ui.Data.Strings.Get(s.Def.LabelKey), Enabled = s.Enabled }).ToList();
            var target = SlotSummaries.ContinueTarget(_slots);
            ViewModel.HasPreview = target != null;
            if (target == null) return;
            var strings = Ui.Data.Strings;
            ViewModel.PreviewIdentity = strings.Format(StringKeys.TitlePreviewIdentity, new StringArgs().Add(UiPlaceholders.Name, target.Name).Add(UiPlaceholders.Class, ClassName(target.ClassId)));
            ViewModel.PreviewProgress = strings.Format(StringKeys.TitlePreviewProgress, new StringArgs().Add(UiPlaceholders.Act, target.Act).Add(UiPlaceholders.Floor, target.Floor).Add(UiPlaceholders.Hp, target.Hp).Add(UiPlaceholders.HpMax, target.HpMax));
            ViewModel.PreviewClass = strings.Format(StringKeys.TitlePreviewClass, new StringArgs().Add(UiPlaceholders.Class, ClassName(target.ClassId)));
            ViewModel.PreviewSlot = strings.Format(StringKeys.TitlePreviewSlot, new StringArgs().Add(UiPlaceholders.Slot, target.Index).Add(UiPlaceholders.Seed, target.Seed));
            ViewModel.PreviewPortrait = target.Portrait;
        }

        private string ClassName(string classId) =>
            classId == null ? string.Empty : Ui.Data.Strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.ClassNameKey, classId));

        private void Render()
        {
            Root.EnableInClassList(UiClasses.GateActive, ViewModel.GateActive);
            Root.EnableInClassList(UiClasses.HasSave, ViewModel.HasPreview);
            Root.EnableInClassList(UiClasses.NoSave, !ViewModel.HasPreview);
            for (var i = 0; i < _buttons.Count && i < ViewModel.Items.Count; i++) _buttons[i].SetEnabled(ViewModel.Items[i].Enabled);
            Root.Q<LocLabel>(UiNames.Build)?.SetResolved(ViewModel.Build);
            Root.Q<LocLabel>(UiNames.PreviewIdentity)?.SetResolved(ViewModel.PreviewClass);
            Root.Q<LocLabel>(UiNames.PreviewProgress)?.SetResolved(ViewModel.PreviewProgress);
        }

        /// <summary>
        /// US-1.2: any input lifts the gate; the lifting press must not also press a row; focus goes to the first enabled row. The lift is the web's entrance (startupGate.js finish): the city lights up
        /// (titleLightUp), is held for the player's Lit city pause, then fades away over the hall (titleFade); Reduced motion skips it for a short cut (titleReducedFade).
        /// </summary>
        public void LiftGate()
        {
            if (!ViewModel.GateActive || _revealing) return;
            _revealing = true;
            _gateLifted = true;
            _anyButton?.Dispose();
            _anyButton = null;
            _pulse?.Pause();
            Root.RemoveFromClassList(UiClasses.GatePulse);
            var settings = Ui.Climb?.PlayerSettings ?? new Ashen.App.Settings.SettingsSession(Ui.RunContent, Ashen.App.Run.ProfileStore.Load(Ui.Saves), core2: true);
            var tokens = Ui.Data.Tokens;
            var reduced = settings.On(SettingIds.ReducedMotion);
            var light = reduced ? 0 : tokens.Duration(TokenKeys.TitleLightUp);
            var hold = reduced ? 0 : TooltipPolicy.Millis(settings.Text(SettingIds.TitleCityHold), 0);
            var fade = tokens.Duration(reduced ? TokenKeys.TitleReducedFade : TokenKeys.TitleFade);
            Root.AddToClassList(UiClasses.GateLighting);
            Root.schedule.Execute(() => Leave(fade)).StartingIn(light + hold);
        }

        private void Leave(int fadeMs)
        {
            Root.RemoveFromClassList(UiClasses.GateLighting);
            Root.AddToClassList(UiClasses.GateLeaving);
            ViewModel.GateActive = false;
            Root.schedule.Execute(() => Root.AddToClassList(UiClasses.GateLeavingOut)).StartingIn(1);
            Root.schedule.Execute(() =>
            {
                Root.RemoveFromClassList(UiClasses.GateLeaving);
                Root.RemoveFromClassList(UiClasses.GateLeavingOut);
                _revealing = false;
                var settle = Ui.Data.Tokens.Duration(TokenKeys.GateSettle);
                Nav.SuppressSubmit(settle / (float)UiMath.MillisPerSecond);
                Nav.FocusTop(null, settle);
            }).StartingIn(fadeMs);
        }

        public bool GateActive => ViewModel.GateActive;

        public override bool InputBlocked => ViewModel.GateActive || _revealing;

        private void Activate(MenuEntryDef entry, LocButton button)
        {
            if (ViewModel.GateActive) return;
            switch (entry.Action)
            {
                case MenuActions.Continue:
                    var target = SlotSummaries.ContinueTarget(_slots);
                    if (target == null) return;
                    ConfirmRequest.Open(Nav, new ConfirmRequest
                    {
                        ConfirmId = ConfirmIds.LoadSlot,
                        Args = new StringArgs().Add(UiPlaceholders.Slot, target.Index),
                        Target = ViewModel.PreviewIdentity,
                        OnConfirm = () => RunFlow.Resume(Context, target.Index, button),
                    });
                    break;
                case MenuActions.Open:
                    if (entry.Target == ScreenIds.Slots) Nav.Go(ScreenIds.Slots, new SlotsArgs { Mode = entry.Mode });
                    else Nav.Go(entry.Target);
                    break;
                case MenuActions.Confirm:
                    ConfirmRequest.Open(Nav, new ConfirmRequest { ConfirmId = entry.Confirm, OnConfirm = entry.Confirm == ConfirmIds.Quit ? Ui.Quit : null });
                    break;
            }
        }

        public override void OnReturned() => Refresh();

        public override void Unbind()
        {
            _anyButton?.Dispose();
            _pulse?.Pause();
        }
    }
}
