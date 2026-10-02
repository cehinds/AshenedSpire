using Ashen.App.Run;
using Ashen.Domain.Events;
using Ashen.Generated;

namespace Ashen.App.Nodes
{
    /// <summary>
    /// The node screens under the climb's router (D-140): RunFlow.Show maps a run's location to its screen through
    /// rules/runFlow.json 'screens' (merchant → merchant, event → event, dungeon → legacyDungeon); an event that is a quest
    /// chain's step has a speaker and shows on screen:dialogue (W4c) instead.
    /// </summary>
    public static class NodeScreens
    {
        /// <summary>The run stands at an event that is a quest chain's step (it has a speaker).</summary>
        public static bool IsQuestStep(RunSession session) =>
            session?.EventId != null && Quests.QuestChainForEvent(session.Content.Loop.Events, session.EventId) != null;

        /// <summary>The screen for a run's location as the router named it, with a quest chain's step on dialogue.</summary>
        public static string Refine(string screen, RunSession session) =>
            screen == ScreenIds.Event && IsQuestStep(session) ? ScreenIds.Dialogue : screen;

        /// <summary>The location each node screen stands for.</summary>
        public static string LocationOf(string screen) =>
            screen == ScreenIds.Merchant ? RunFlowValues.LocationMerchant
            : screen == ScreenIds.Event || screen == ScreenIds.Dialogue ? RunFlowValues.LocationEvent
            : screen == ScreenIds.LegacyDungeon ? RunFlowValues.LocationDungeon
            : null;
    }
}
