using System;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>A request to open a W2 door (ui/menus.json 'confirms').</summary>
    public sealed class ConfirmRequest
    {
        public string ConfirmId;
        public StringArgs Args = new StringArgs();

        /// <summary>The target identity line (slot summary, item), already resolved.</summary>
        public string Target;

        /// <summary>Use the door's occupiedBodyKey (the slot already holds a climb).</summary>
        public bool Occupied;

        public Action OnConfirm;
        public Action OnBack;

        /// <summary>Opens the door as a modal; the callbacks run after it closes, once focus is back on the opener.</summary>
        public static ScreenInstance Open(Navigator nav, ConfirmRequest request) => nav.OpenModal(ScreenIds.Confirm, request);
    }

    /// <summary>
    /// W-23 confirmation doors as a modal screen (focus starts on Back; Escape closes). Tone comes from the policy
    /// data (destructive = red primary, never green); hold doors commit with hold-to-confirm (holdConfirm token).
    /// </summary>
    public sealed class ConfirmScreen : ScreenView
    {
        private ConfirmRequest _request;

        public ConfirmViewModel ViewModel { get; } = new ConfirmViewModel();

        protected override void OnBind(object args)
        {
            _request = args as ConfirmRequest ?? new ConfirmRequest { ConfirmId = ConfirmIds.Quit };
            var def = Ui.Data.Menus.Confirm(_request.ConfirmId);
            var strings = Ui.Data.Strings;
            ViewModel.Title = strings.Format(def.TitleKey, _request.Args);
            ViewModel.Target = _request.Target;
            ViewModel.Body = strings.Format(_request.Occupied && def.OccupiedBodyKey != null ? def.OccupiedBodyKey : def.BodyKey, _request.Args);
            ViewModel.Back = strings.Get(def.BackKey);
            ViewModel.Primary = strings.Get(def.PrimaryKey);
            ViewModel.Destructive = Ui.Data.Policies.IsDestructive(def.Policy);
            ViewModel.Hold = def.Hold;
            ViewModel.Single = def.Single;
            ViewModel.HoldMs = Ui.Data.Tokens.Duration(TokenKeys.HoldConfirm);
            var confirm = Root.Q<Confirm>(UiNames.Confirm);
            confirm.Bind(ViewModel);
            confirm.Back += () => Finish(_request.OnBack);
            confirm.Confirmed += () => Finish(_request.OnConfirm);
        }

        public override bool HandleBack()
        {
            Finish(_request.OnBack);
            return true;
        }

        private void Finish(Action then)
        {
            Nav.Close(Context.Instance);
            then?.Invoke();
        }
    }
}
