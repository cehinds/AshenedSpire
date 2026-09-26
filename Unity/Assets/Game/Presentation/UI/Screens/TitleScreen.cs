using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
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
        private string _portrait;
        private string _build;
        private IReadOnlyList<MenuItemViewModel> _items = Array.Empty<MenuItemViewModel>();

        public bool GateActive { get => _gateActive; set => Set(ref _gateActive, value); }
        public bool HasPreview { get => _hasPreview; set => Set(ref _hasPreview, value); }
        public string PreviewIdentity { get => _identity; set => Set(ref _identity, value); }
        public string PreviewProgress { get => _progress; set => Set(ref _progress, value); }
        public string PreviewSlot { get => _slot; set => Set(ref _slot, value); }
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
                var button = new LocButton(entry.LabelKey);
                button.AddToClassList(UiClasses.MenuItem);
                var captured = entry;
                button.clicked += () => Activate(captured, button);
                menu.Add(button);
                _buttons.Add(button);
            }
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
            ViewModel.PreviewProgress = strings.Format(StringKeys.TitlePreviewProgress, new StringArgs().Add(UiPlaceholders.Act, target.Act).Add(UiPlaceholders.Floor, target.Floor).Add(UiPlaceholders.Hp, target.Hp));
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
            Root.Q<LocLabel>(UiNames.PreviewIdentity)?.SetResolved(ViewModel.PreviewIdentity);
            Root.Q<LocLabel>(UiNames.PreviewProgress)?.SetResolved(ViewModel.PreviewProgress);
            Root.Q<LocLabel>(UiNames.PreviewSlot)?.SetResolved(ViewModel.PreviewSlot);
            var portrait = Root.Q(UiNames.PreviewPortrait);
            if (portrait != null && ViewModel.HasPreview) Ui.ApplyBackground(portrait, ViewModel.PreviewPortrait);
        }

        /// <summary>US-1.2: any input lifts the gate; the lifting press must not also press a row; focus goes to the first enabled row.</summary>
        public void LiftGate()
        {
            if (!ViewModel.GateActive) return;
            _gateLifted = true;
            _anyButton?.Dispose();
            _anyButton = null;
            _pulse?.Pause();
            Root.RemoveFromClassList(UiClasses.GatePulse);
            ViewModel.GateActive = false;
            var settle = Ui.Data.Tokens.Duration(TokenKeys.GateSettle);
            Nav.SuppressSubmit(settle / (float)UiMath.MillisPerSecond);
            Nav.FocusTop(null, settle);
        }

        public bool GateActive => ViewModel.GateActive;

        public override bool InputBlocked => ViewModel.GateActive;

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
                        OnConfirm = () => Refuse(button, StringKeys.SlotsRefusalLater),
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

        private void Refuse(VisualElement anchor, string key) =>
            Refusal.Show(anchor, Context.Overlay, Ui.Data.Strings.Get(key), Ui.Data.Tokens.Duration(TokenKeys.Refusal));

        public override void OnReturned() => Refresh();

        public override void Unbind()
        {
            _anyButton?.Dispose();
            _pulse?.Pause();
        }
    }
}
