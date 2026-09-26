using System;
using System.Globalization;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// Entering a run from the title or W-03 (AF-10 Resume): load the slot through RunSession, open W-07 on the same turn
    /// and hand (or W-08 when the slot holds a pending reward), and warn when the log was skipped or diverged (D-017). A slot that cannot be resumed is refused at the
    /// control that asked, the kit's unreadable-slot path (US-1.4).
    /// </summary>
    public static class RunFlow
    {
        public static void Resume(ScreenContext context, int slotIndex, VisualElement anchor)
        {
            var ui = context.Ui;
            RunLoadResult result;
            try { result = RunSession.Load(ui.RunContent, ui.Saves, slotIndex); }
            catch (Exception e)
            {
                Debug.LogError(Format(UiMessages.LoadFailed, slotIndex, RunLoadStatus.Damaged, e.Message));
                Refuse(context, anchor, StringKeys.CombatLoadRefused);
                return;
            }
            foreach (var warning in result.Warnings) Debug.LogWarning(warning);
            if (!result.Resumed)
            {
                Debug.LogWarning(Format(UiMessages.LoadFailed, slotIndex, result.Status, string.Join(UiFormats.Newline, result.Warnings)));
                Refuse(context, anchor, result.Status == RunLoadStatus.Empty ? StringKeys.SlotsRefusalEmpty : StringKeys.SlotsRefusalUnreadable);
                return;
            }
            var session = result.Session;
            if (session.HasPendingReward)
            {
                ui.Session = session;
                context.Navigator.Go(ScreenIds.Rewards, new RewardsArgs { Session = session }, true);
                return;
            }
            // A node screen (merchant, event, dialogue, legacy dungeon) resumes on the same screen in the same state (D-141n).
            if (NodeRouter.Resume(context.Navigator, ui, session)) return;
            if (session.Location == RunFlowValues.LocationMap)
            {
                // After the rewards the climb goes to the act map, a later build (D-058): the slot is kept and refused here.
                Refuse(context, anchor, StringKeys.SlotsRefusalLater);
                return;
            }
            if (!session.IsInCombat) session.StartEncounter();
            ui.Session = session;
            context.Navigator.Go(ScreenIds.Combat, new CombatArgs { Session = session, ResumeWarning = result.Status == RunLoadStatus.ResumedWithWarning }, true);
        }

        /// <summary>A fixed seed for new climbs (the PlayMode smoke test pins the first fight); null draws a fresh one.</summary>
        public static uint? SeedOverride;

        /// <summary>A new seed for a new climb (the seed is shown on the pause screen and in the slot).</summary>
        public static uint NewSeed()
        {
            if (SeedOverride.HasValue) return SeedOverride.Value;
            var rng = new System.Random();
            return unchecked((uint)rng.Next() ^ ((uint)rng.Next() << 1));
        }

        /// <summary>"name · class · Act · Floor · HP" for a slot, as the W-03 row shows it (the W2 target line).</summary>
        public static string SlotFacts(UiContext ui, SlotSummary s)
        {
            var strings = ui.Data.Strings;
            var label = strings.Format(StringKeys.SlotsLabel, new StringArgs().Add(UiPlaceholders.Slot, s.Index));
            if (!s.IsReady) return label;
            var facts = strings.Format(StringKeys.SlotsFacts, new StringArgs()
                .Add(UiPlaceholders.Name, s.Name).Add(UiPlaceholders.Class, ClassName(ui, s.ClassId)).Add(UiPlaceholders.Act, s.Act).Add(UiPlaceholders.Floor, s.Floor)
                .Add(UiPlaceholders.Hp, s.Hp).Add(UiPlaceholders.HpMax, s.HpMax));
            return strings.Format(StringKeys.SlotsTarget, new StringArgs().Add(UiPlaceholders.Label, label).Add(UiPlaceholders.Facts, facts));
        }

        public static string ClassName(UiContext ui, string classId) =>
            classId == null ? string.Empty : ui.Data.Strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.ClassNameKey, classId));

        public static void Refuse(ScreenContext context, VisualElement anchor, string key)
        {
            if (anchor == null) return;
            Refusal.Show(anchor, context.Overlay, context.Ui.Data.Strings.Get(key), context.Ui.Data.Tokens.Duration(TokenKeys.Refusal));
        }

        private static string Format(string template, params object[] args) => string.Format(CultureInfo.InvariantCulture, template, args);
    }
}
