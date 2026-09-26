using System;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// Hold-to-confirm (docs/design/04 §0): pressing and holding for holdMs commits; releasing early cancels.
    /// Pointer, keyboard (Enter/Space) and pad (South) all hold. The progress bar has a text equivalent. With hold
    /// off (settings holdConfirm = off) a plain press commits.
    /// </summary>
    [UxmlElement]
    public partial class HoldButton : Button
    {
        private readonly LocLabel _label;
        private readonly VisualElement _fill;
        private readonly LocLabel _state;
        private IVisualElementScheduledItem _tick;
        private float _startedAt = -1f;
        private bool _pointerHeld;

        public HoldButton()
        {
            AddToClassList(nameof(HoldButton).ToLowerInvariant());
            AddToClassList(UiClasses.Button);
            AddToClassList(UiClasses.Touch);
            UiDom.CloneTemplate(this, UiResources.KitHold);
            _label = this.Q<LocLabel>(UiNames.HoldLabel);
            _fill = this.Q(UiNames.HoldFill);
            _state = this.Q<LocLabel>(UiNames.HoldState);
            clicked += OnClicked;
            RegisterCallback<PointerDownEvent>(e => { _pointerHeld = true; Begin(); }, TrickleDown.TrickleDown);
            RegisterCallback<PointerUpEvent>(e => { _pointerHeld = false; Cancel(); });
            RegisterCallback<PointerLeaveEvent>(e => { _pointerHeld = false; Cancel(); });
            RegisterCallback<NavigationSubmitEvent>(e => Begin());
            RegisterCallback<BlurEvent>(e => Cancel());
            RegisterCallback<DetachFromPanelEvent>(e => Cancel());
            Render(0d);
        }

        /// <summary>How long a hold must last (ms); from the holdConfirm token.</summary>
        public int HoldMs { get; set; }

        /// <summary>False when the player turned hold-to-confirm off: a press commits.</summary>
        public bool HoldEnabled { get; set; } = true;

        public double Progress { get; private set; }

        public event Action Committed;

        [UxmlAttribute]
        public string labelKey
        {
            get => _label?.stringKey;
            set { if (_label != null) _label.stringKey = value; }
        }

        private void OnClicked()
        {
            if (!HoldEnabled) Committed?.Invoke();
        }

        private void Begin()
        {
            if (!HoldEnabled || !enabledInHierarchy || _startedAt >= 0f) return;
            _startedAt = Time.realtimeSinceStartup;
            AddToClassList(UiClasses.Holding);
            _tick = schedule.Execute(Tick).Every(0);
        }

        private void Tick()
        {
            if (_startedAt < 0f) return;
            if (!_pointerHeld && !SubmitHeld()) { Cancel(); return; }
            var elapsedMs = (Time.realtimeSinceStartup - _startedAt) * UiMath.MillisPerSecond;
            var progress = HoldMs > 0 ? Math.Min(1d, elapsedMs / HoldMs) : 1d;
            Render(progress);
            if (progress >= 1d)
            {
                Cancel();
                Committed?.Invoke();
            }
        }

        private static bool SubmitHeld()
        {
            var k = Keyboard.current;
            var pad = Gamepad.current;
            return (k != null && (k.enterKey.isPressed || k.numpadEnterKey.isPressed || k.spaceKey.isPressed))
                || (pad != null && pad.buttonSouth.isPressed);
        }

        public void Cancel()
        {
            _startedAt = -1f;
            _tick?.Pause();
            _tick = null;
            RemoveFromClassList(UiClasses.Holding);
            Render(0d);
        }

        /// <summary>Shows a fixed progress (review captures).</summary>
        public void ShowProgress(double progress) => Render(progress);

        private void Render(double progress)
        {
            Progress = progress;
            if (_fill != null) _fill.style.width = Length.Percent(UiDom.Percent(progress));
            if (_state == null) return;
            if (progress <= 0d) _state.stringKey = StringKeys.HoldHint;
            else _state.SetResolved(Loc.Format(StringKeys.HoldProgress, new StringArgs().Add(UiPlaceholders.Pct, UiDom.Pct(progress))));
        }
    }
}
