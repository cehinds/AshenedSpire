using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// W-01 profile recovery (US-1.1), STUB STATE: the W2 frame with [Start fresh] and [Restore copy ⟲] is shown
    /// when the profile has files but no valid copy. Both actions continue to the title for now; archiving the
    /// damaged file and restoring the mirror land with the profile service (F4).
    /// </summary>
    public sealed class ProfileRecoveryScreen : ScreenView
    {
        protected override void OnBind(object args)
        {
            var def = Ui.Data.Menus.Confirm(ConfirmIds.Recovery);
            var strings = Ui.Data.Strings;
            var vm = new ConfirmViewModel
            {
                Title = strings.Get(def.TitleKey),
                Target = strings.Format(StringKeys.RecoveryMirror, new StringArgs().Add(UiPlaceholders.When, string.Empty)),
                Body = strings.Get(def.BodyKey),
                Back = strings.Get(def.BackKey),
                Primary = strings.Get(def.PrimaryKey),
                Hold = def.Hold,
                HoldMs = Ui.Data.Tokens.Duration(TokenKeys.HoldConfirm),
            };
            var confirm = Root.Q<Confirm>(UiNames.Confirm);
            confirm.Bind(vm);
            confirm.Back += () => Nav.Fire(ScreenTriggers.Done);
            confirm.Confirmed += () => Nav.Fire(ScreenTriggers.Done);
        }
    }
}
