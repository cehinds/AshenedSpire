using Ashen.App.Ui;
using Ashen.Generated;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The deck editor's share of the input layer (web screens/deckEditor.js dispatch, 1030-1075; P-7c, D-1750..): the Menu key (and pad Start) is Done while the editor stands. A confirmation or dialog over it owns the keys.
    /// </summary>
    public sealed partial class ArmouryScreen
    {
        public override bool VeilOpen => DialogOpen;

        public override bool HandleHotkey(HotkeyResult result)
        {
            if (result.Kind != HotkeyKind.Action || result.Id != HotkeyActions.Menu || _editor == null) return false;
            DonePressed();
            return true;
        }
    }
}
