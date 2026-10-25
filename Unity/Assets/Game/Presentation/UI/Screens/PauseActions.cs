using System;
using System.Globalization;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// What a pause menu row does (W-20, D-134), shared by the pause screen and the combat quick menu (VP-6): Save and quit and Abandon open their confirmation doors, an Open row goes to its screen.
    /// </summary>
    internal static class PauseActions
    {
        public static void Activate(ScreenContext context, IClimb session, MenuEntryDef def, VisualElement anchor = null)
        {
            var ui = context.Ui;
            var nav = context.Navigator;
            switch (def.Action)
            {
                case MenuActions.Save:
                    SaveHere(context, session, anchor);
                    break;
                case MenuActions.SaveQuit:
                    SaveAndQuit(context, session);
                    break;
                case MenuActions.Confirm when def.Confirm == ConfirmIds.QuitWithoutSaving:
                    ConfirmRequest.Open(nav, new ConfirmRequest
                    {
                        ConfirmId = ConfirmIds.QuitWithoutSaving,
                        Target = session == null ? null : RunFlow.SlotFacts(ui, SlotSummaries.ReadOne(ui.Saves, session.SlotIndex)),
                        OnConfirm = () => QuitWithoutSaving(context),
                    });
                    break;
                case MenuActions.Confirm when def.Confirm == ConfirmIds.SaveQuit:
                    ConfirmRequest.Open(nav, new ConfirmRequest
                    {
                        ConfirmId = ConfirmIds.SaveQuit,
                        Target = session == null ? null : RunFlow.SlotFacts(ui, SlotSummaries.ReadOne(ui.Saves, session.SlotIndex)),
                        OnConfirm = () => SaveAndQuit(context, session),
                    });
                    break;
                case MenuActions.Confirm when def.Confirm == ConfirmIds.Abandon:
                    ConfirmRequest.Open(nav, new ConfirmRequest
                    {
                        ConfirmId = ConfirmIds.Abandon,
                        Target = session == null ? null : RunFlow.SlotFacts(ui, SlotSummaries.ReadOne(ui.Saves, session.SlotIndex)),
                        OnConfirm = () => Abandon(context, session),
                    });
                    break;
                case MenuActions.Open when def.Target == ScreenIds.Armoury:
                    // The Armoury opens over the pause menu on a Core 2 run; any other run says it arrives later at the row that asked.
                    if (!ArmouryDoor.Open(context, session, ArmouryValues.TabEquipment)) RunFlow.Refuse(context, anchor, StringKeys.MapRefusalArmoury);
                    break;
                case MenuActions.Open:
                    nav.Go(def.Target);
                    break;
            }
        }

        /// <summary>Save (web overlay.js saveButton, main.js saveNow): write the run to its slot and stay where it is; the row says Saved · Slot n for a moment. A failure opens the save door with Retry.</summary>
        public static void SaveHere(ScreenContext context, IClimb session, VisualElement anchor)
        {
            if (!TrySave(context, session, () => SaveHere(context, session, anchor))) return;
            if (!(anchor is LocButton button) || session == null) return;
            var strings = context.Ui.Data.Strings;
            var label = button.text;
            button.SetResolved(strings.Format(StringKeys.PauseSaved, new StringArgs().Add(UiPlaceholders.Slot, session.SlotIndex)));
            button.schedule.Execute(() => button.SetResolved(label)).StartingIn(context.Ui.Data.Tokens.Duration(TokenKeys.SavedLabel));
        }

        /// <summary>Checkpoint, then the title with its gate down again (web onQuit: persist(); showCollapsedTitle()); a failure opens the save door with Retry and leaves the menu open.</summary>
        public static void SaveAndQuit(ScreenContext context, IClimb session)
        {
            if (!TrySave(context, session, () => SaveAndQuit(context, session))) return;
            LeaveRun(context);
        }

        /// <summary>Quit without saving (web quitWithoutSaving): the run is let go as it stands; its slot keeps the last save.</summary>
        public static void QuitWithoutSaving(ScreenContext context) => LeaveRun(context);

        private static void LeaveRun(ScreenContext context)
        {
            context.Ui.ClearRun();
            TitleScreen.ResetSession();
            context.Navigator.Go(ScreenIds.Title, null, true);
        }

        private static bool TrySave(ScreenContext context, IClimb session, Action retry)
        {
            try
            {
                session?.Save();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.SaveFailed, e.Message));
                ConfirmRequest.Open(context.Navigator, new ConfirmRequest { ConfirmId = ConfirmIds.SaveFailed, Args = new StringArgs().Add(UiPlaceholders.Reason, e.Message), OnConfirm = retry });
                return false;
            }
        }

        /// <summary>Abandon run (D-134): the run ends as abandoned and W-15 shows its summary; a save failure opens W1r with Retry.</summary>
        public static void Abandon(ScreenContext context, IClimb session)
        {
            if (session == null) return;
            try
            {
                session.Abandon();
            }
            catch (Exception e)
            {
                Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.SaveFailed, e.Message));
                ConfirmRequest.Open(context.Navigator, new ConfirmRequest { ConfirmId = ConfirmIds.SaveFailed, Args = new StringArgs().Add(UiPlaceholders.Reason, e.Message), OnConfirm = () => Abandon(context, session) });
                return;
            }
            RunFlow.ShowClimb(context, session);
        }
    }
}
