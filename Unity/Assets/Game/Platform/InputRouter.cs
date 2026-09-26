using System;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ashen.Platform
{
    /// <summary>
    /// One input source for the focus model (US-17.1; docs/design/09 §5.2 InputRouter). Reads the Input System's
    /// UI actions (Navigate, Submit, Cancel: keyboard arrows/WASD/Enter/Escape, pad stick/d-pad/South/East) plus
    /// Tab/Shift+Tab, and raises intents: Move(+1 next / -1 previous), Submit, Cancel. A held direction repeats after
    /// the navRepeatDelay token, then every navRepeatInterval. Pointer input stays with UI Toolkit.
    /// </summary>
    public sealed class InputRouter : IDisposable
    {
        private readonly DefaultInputActions _actions = new DefaultInputActions();
        private readonly float _repeatDelay;
        private readonly float _repeatInterval;
        private int _held;
        private float _nextRepeat;

        public InputRouter(int repeatDelayMs, int repeatIntervalMs)
        {
            _repeatDelay = (float)(repeatDelayMs / UiMath.MillisPerSecond);
            _repeatInterval = (float)(repeatIntervalMs / UiMath.MillisPerSecond);
            _actions.UI.Navigate.Enable();
            _actions.UI.Submit.Enable();
            _actions.UI.Cancel.Enable();
            _actions.UI.Submit.performed += OnSubmit;
            _actions.UI.Cancel.performed += OnCancel;
        }

        public event Action<int> Move;
        public event Action Submit;
        public event Action Cancel;

        private void OnSubmit(InputAction.CallbackContext _) => Submit?.Invoke();
        private void OnCancel(InputAction.CallbackContext _) => Cancel?.Invoke();

        /// <summary>Call once per frame with realtime seconds.</summary>
        public void Tick(float now)
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.tabKey.wasPressedThisFrame) Move?.Invoke(keyboard.shiftKey.isPressed ? -1 : 1);

            var direction = Direction(_actions.UI.Navigate.ReadValue<Vector2>());
            if (direction == 0)
            {
                _held = 0;
                return;
            }
            if (direction != _held)
            {
                _held = direction;
                _nextRepeat = now + _repeatDelay;
                Move?.Invoke(direction);
                return;
            }
            if (now < _nextRepeat) return;
            _nextRepeat = now + _repeatInterval;
            Move?.Invoke(direction);
        }

        /// <summary>Down or right is next (+1), up or left is previous (-1); vertical wins on diagonals.</summary>
        private static int Direction(Vector2 v)
        {
            var threshold = (float)UiMath.Half;
            if (v.y <= -threshold) return 1;
            if (v.y >= threshold) return -1;
            if (v.x >= threshold) return 1;
            if (v.x <= -threshold) return -1;
            return 0;
        }

        public void Dispose()
        {
            _actions.UI.Submit.performed -= OnSubmit;
            _actions.UI.Cancel.performed -= OnCancel;
            _actions.Dispose();
        }
    }
}
