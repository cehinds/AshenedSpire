using System;
using System.Globalization;
using Ashen.App.Nodes;
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
        /// <summary>The ruleset the PlayMode smokes pin for new climbs ('legacy' keeps them on the 405 run); null follows rules/runFlow.json newRun.ruleset.</summary>
        public static string RulesetOverride;

        /// <summary>Resume a slot, whichever ruleset saved it (a Core 2 save is format 3, anything else is a 405 run; <see cref="RunHost.Load"/> reads it by its format).</summary>
        public static void Resume(ScreenContext context, int slotIndex, VisualElement anchor)
        {
            var ui = context.Ui;
            HostedLoad result;
            try { result = RunHost.Load(ui.RunContent, ui.Saves, slotIndex); }
            catch (Exception e)
            {
                Debug.LogError(Format(UiMessages.LoadFailed, slotIndex, nameof(RunLoadStatus.Damaged), e.Message));
                Refuse(context, anchor, StringKeys.CombatLoadRefused);
                return;
            }
            foreach (var warning in result.Warnings) Debug.LogWarning(warning);
            if (!result.Resumed)
            {
                Debug.LogWarning(Format(UiMessages.LoadFailed, slotIndex, result.Status, string.Join(UiFormats.Newline, result.Warnings)));
                Refuse(context, anchor, result.Status == nameof(RunLoadStatus.Empty) ? StringKeys.SlotsRefusalEmpty : StringKeys.SlotsRefusalUnreadable);
                return;
            }
            var warned = result.Status == nameof(RunLoadStatus.ResumedWithWarning) || (result.Run.IsCore2 && result.Warnings.Count > 0);
            if (result.Run.IsCore2) Show(context, result.Run.Core2, warned);
            else Show(context, result.Run.Legacy, warned);
        }

        /// <summary>The ruleset a New Game starts now: the override a test pins, else the data's (rules/runFlow.json newRun.ruleset).</summary>
        public static string NewRuleset(UiContext ui) => RulesetOverride ?? RunHost.Ruleset(ui.RunContent);

        /// <summary>The screen that shows where the run stands, whichever ruleset plays it.</summary>
        public static void ShowClimb(ScreenContext context, IClimb climb, bool resumeWarning = false)
        {
            if (climb is Core2RunSession core2) Show(context, core2, resumeWarning);
            else Show(context, (RunSession)climb, resumeWarning);
        }

        /// <summary>Where a Core 2 run stands (rules/runFlow.json core2Screens: the fight, the map, the run end); a screen that is planned or unregistered falls back to the act map.</summary>
        public static void Show(ScreenContext context, Core2RunSession session, bool resumeWarning = false)
        {
            var ui = context.Ui;
            ui.Core2Session = session;
            ui.Session = null;
            var id = session.Content.Flow.Core2ScreenFor(session.Location);
            if (id == null || !ui.Data.Screens.IsBuilt(id)) id = ScreenIds.ActMap;
            context.Navigator.Go(id, new Core2RunArgs { Session = session, ResumeWarning = resumeWarning }, true);
        }

        /// <summary>The screen that shows where the run stands (the stack is replaced: a run screen is always the root).</summary>
        public static void Show(ScreenContext context, RunSession session, bool resumeWarning = false)
        {
            var ui = context.Ui;
            ui.Session = session;
            ui.Core2Session = null;
            var location = session.Location;
            // A quest chain's event step shows on dialogue (W4c, D-140).
            var id = NodeScreens.Refine(session.Content.Flow.ScreenFor(location), session);
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

        /// <summary>False keeps a New Game from playing the opening (W-05) before the map (the PlayMode smokes pin it); null follows the data and the player's setting.</summary>
        public static bool? OpeningOverride;

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

        /// <summary>The slot's one line of facts (web saveSlotSelector.js slotFacts): "Act 1 · Floor 0 · 71/71 HP".</summary>
        public static string ActFacts(UiContext ui, SlotSummary s) =>
            ui.Data.Strings.Format(StringKeys.SlotsFacts, new StringArgs().Add(UiPlaceholders.Act, s.Act).Add(UiPlaceholders.Floor, s.Floor).Add(UiPlaceholders.Hp, s.Hp).Add(UiPlaceholders.HpMax, s.HpMax));

        /// <summary>The run a W2 door acts on, named the way its slot names it (web ConfirmationReviewModel.js runIdentity): "Reaver · Slot 1 · Act 1 · Floor 0 · 71/71 HP".</summary>
        public static string SlotFacts(UiContext ui, SlotSummary s)
        {
            var strings = ui.Data.Strings;
            if (!s.IsReady) return strings.Format(StringKeys.SlotsLabel, new StringArgs().Add(UiPlaceholders.Slot, s.Index));
            return strings.Format(StringKeys.SlotsTarget, new StringArgs().Add(UiPlaceholders.Class, ClassName(ui, s.ClassId)).Add(UiPlaceholders.Slot, s.Index).Add(UiPlaceholders.Facts, ActFacts(ui, s)));
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
