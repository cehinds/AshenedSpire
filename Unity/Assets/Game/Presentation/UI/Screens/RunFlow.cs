using System;
using System.Globalization;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// Entering and routing a run (AF-10 Resume; the climb's screen seam). <see cref="Resume"/> loads a slot through
    /// RunSession and shows the place it was saved at; <see cref="Show"/> is the one router every climb screen uses after a
    /// step: the session's location (combat, rewards, map, rest, merchant, event, dungeon, runEnd) → the screen
    /// rules/runFlow.json 'screens' names. A screen that is planned (or not registered) falls back to the act map, whose
    /// planned door carries the node's one legal action until the node screens land (D-126). A slot that cannot be resumed is
    /// refused at the control that asked, the kit's unreadable-slot path (US-1.4).
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
            Show(context, result.Session, result.Status == RunLoadStatus.ResumedWithWarning);
        }

        /// <summary>The screen that shows where the run stands (the stack is replaced: a run screen is always the root).</summary>
        public static void Show(ScreenContext context, RunSession session, bool resumeWarning = false)
        {
            var ui = context.Ui;
            ui.Session = session;
            var location = session.Location;
            var id = session.Content.Flow.ScreenFor(location);
            if (id == null || !ui.Data.Screens.IsBuilt(id)) id = ScreenIds.ActMap;
            object args;
            switch (id)
            {
                case ScreenIds.Combat:
                    args = new CombatArgs { Session = session, ResumeWarning = resumeWarning };
                    break;
                case ScreenIds.Rewards:
                    args = new RewardsArgs { Session = session };
                    break;
                default:
                    args = new RunScreenArgs { Session = session, ResumeWarning = resumeWarning };
                    break;
            }
            context.Navigator.Go(id, args, true);
        }

        /// <summary>A fixed seed for new climbs (the PlayMode smoke pins its runs); null draws a fresh one.</summary>
        public static uint? SeedOverride;

        /// <summary>A Custom Climb block for new climbs (the PlayMode smoke's short map shape); null is Classic.</summary>
        public static JObject CustomOverride;

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
