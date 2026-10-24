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

        /// <summary>Checkpoint, then the title; a failure opens W1r with Retry and leaves the menu open.</summary>
        public static void SaveAndQuit(ScreenContext context, IClimb session)
        {
            try
            {
                session?.Save();
            }
            catch (Exception e)
            {
                Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.SaveFailed, e.Message));
                ConfirmRequest.Open(context.Navigator, new ConfirmRequest { ConfirmId = ConfirmIds.SaveFailed, OnConfirm = () => SaveAndQuit(context, session) });
                return;
            }
            context.Ui.ClearRun();
            context.Navigator.Go(ScreenIds.Title, null, true);
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
                ConfirmRequest.Open(context.Navigator, new ConfirmRequest { ConfirmId = ConfirmIds.SaveFailed, OnConfirm = () => Abandon(context, session) });
                return;
            }
            RunFlow.ShowClimb(context, session);
        }
    }
}
