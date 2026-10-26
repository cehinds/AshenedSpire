using Ashen.Generated;
using UnityEngine.InputSystem;

namespace Ashen.Platform
{
    /// <summary>
    /// The gamepad as the Controls list reads it (web input.js captureNextButton, hasGamepad): whether a pad is connected, and whether one of the web's numbered buttons (ui/controls.json padButtons 'source') is held. The
    /// web's button numbers are the standard mapping: A, B, X, Y, the bumpers, the triggers, Back, Start, the two stick clicks and the D-pad.
    /// </summary>
    public static class PadInput
    {
        public static bool Connected => Gamepad.current != null;

        /// <summary>The Input System control of the button Confirm is bound to (a press-and-hold stays held while it is down); the HotkeyHost keeps it in step with the player's bindings.</summary>
        public static string ConfirmSource = ControlsValues.PadSouth;

        /// <summary>True while the named control (ControlsValues.Pad*) of the current pad is held; false with no pad or an unknown name.</summary>
        public static bool IsDown(string source)
        {
            var pad = Gamepad.current;
            if (pad == null || source == null) return false;
            var control = Control(pad, source);
            return control != null && control.isPressed;
        }

        private static ButtonControl Control(Gamepad pad, string source)
        {
            switch (source)
            {
                case ControlsValues.PadSouth: return pad.buttonSouth;
                case ControlsValues.PadEast: return pad.buttonEast;
                case ControlsValues.PadWest: return pad.buttonWest;
                case ControlsValues.PadNorth: return pad.buttonNorth;
                case ControlsValues.PadLeftShoulder: return pad.leftShoulder;
                case ControlsValues.PadRightShoulder: return pad.rightShoulder;
                case ControlsValues.PadLeftTrigger: return pad.leftTrigger;
                case ControlsValues.PadRightTrigger: return pad.rightTrigger;
                case ControlsValues.PadSelect: return pad.selectButton;
                case ControlsValues.PadStart: return pad.startButton;
                case ControlsValues.PadLeftStick: return pad.leftStickButton;
                case ControlsValues.PadRightStick: return pad.rightStickButton;
                case ControlsValues.PadDpadUp: return pad.dpad.up;
                case ControlsValues.PadDpadDown: return pad.dpad.down;
                case ControlsValues.PadDpadLeft: return pad.dpad.left;
                case ControlsValues.PadDpadRight: return pad.dpad.right;
                default: return null;
            }
        }
    }
}
