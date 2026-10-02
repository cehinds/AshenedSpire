using System;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>What a W2 door shows (docs/design/04 §0, W-23): question title, target identity, exact consequence.</summary>
    public sealed class ConfirmViewModel : ViewModel
    {
        private string _title;
        private string _target;
        private string _body;
        private string _back;
        private string _primary;
        private bool _destructive;
        private bool _hold;
        private bool _single;
        private int _holdMs;
        private bool _holdEnabled = true;

        public string Title { get => _title; set => Set(ref _title, value); }
        public string Target { get => _target; set => Set(ref _target, value); }
        public string Body { get => _body; set => Set(ref _body, value); }
        public string Back { get => _back; set => Set(ref _back, value); }
        public string Primary { get => _primary; set => Set(ref _primary, value); }
        public bool Destructive { get => _destructive; set => Set(ref _destructive, value); }
        public bool Hold { get => _hold; set => Set(ref _hold, value); }
        public bool Single { get => _single; set => Set(ref _single, value); }
        public int HoldMs { get => _holdMs; set => Set(ref _holdMs, value); }

        /// <summary>False when the player turned holds off (holdConfirm 'off', US-17.1): the hold door commits on a press.</summary>
        public bool HoldEnabled { get => _holdEnabled; set => Set(ref _holdEnabled, value); }
    }

    /// <summary>
    /// W2 confirmation (docs/design/04 §0). Focus always starts on Back (screens.json initialFocus). A destructive
    /// door has a red, never-green primary; a hold door commits through the HoldButton. Single-action doors (the
    /// AI notice) show one full-width action.
    /// </summary>
    [UxmlElement]
    public partial class Confirm : VisualElement
    {
        private ConfirmViewModel _vm;

        public Confirm()
        {
            AddToClassList(nameof(Confirm).ToLowerInvariant());
            UiDom.CloneTemplate(this, UiResources.KitConfirm);
            TitleLabel = this.Q<LocLabel>(UiNames.ConfirmTitle);
            TargetLabel = this.Q<LocLabel>(UiNames.ConfirmTarget);
            BodyLabel = this.Q<LocLabel>(UiNames.ConfirmBody);
            BackButton = this.Q<LocButton>(UiNames.ConfirmBack);
            PrimaryButton = this.Q<LocButton>(UiNames.ConfirmPrimary);
            HoldButton = this.Q<HoldButton>(UiNames.ConfirmHold);
            if (BackButton != null) BackButton.clicked += () => Back?.Invoke();
            if (PrimaryButton != null) PrimaryButton.clicked += () => Confirmed?.Invoke();
            if (HoldButton != null) HoldButton.Committed += () => Confirmed?.Invoke();
        }

        public LocLabel TitleLabel { get; }
        public LocLabel TargetLabel { get; }
        public LocLabel BodyLabel { get; }
        public LocButton BackButton { get; }
        public LocButton PrimaryButton { get; }
        public HoldButton HoldButton { get; }

        public event Action Back;
        public event Action Confirmed;

        public void Bind(ConfirmViewModel vm)
        {
            _vm = vm;
            vm.Observe(Render);
        }

        private void Render()
        {
            TitleLabel?.SetResolved(_vm.Title);
            TargetLabel?.SetResolved(_vm.Target);
            UiDom.Show(TargetLabel, !string.IsNullOrEmpty(_vm.Target));
            BodyLabel?.SetResolved(_vm.Body);
            BackButton?.SetResolved(_vm.Back);
            PrimaryButton?.SetResolved(_vm.Primary);
            if (HoldButton != null)
            {
                HoldButton.labelKey = null;
                HoldButton.Q<LocLabel>(UiNames.HoldLabel)?.SetResolved(_vm.Primary);
                HoldButton.HoldMs = _vm.HoldMs;
                HoldButton.HoldEnabled = _vm.HoldEnabled;
            }
            UiDom.Show(PrimaryButton, !_vm.Hold);
            UiDom.Show(HoldButton, _vm.Hold);
            EnableInClassList(UiClasses.ToneDestructive, _vm.Destructive);
            PrimaryButton?.EnableInClassList(UiClasses.ButtonReady, !_vm.Destructive);
            EnableInClassList(UiClasses.ConfirmSingle, _vm.Single);
            UiDom.Show(BackButton, !_vm.Single);
        }
    }
}
