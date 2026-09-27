using System.Linq;
using Ashen.App.Nodes;
using Ashen.App.Run;
using Ashen.Content;
using Ashen.Presentation.UI;
using Ashen.Presentation.UI.Screens;
using Newtonsoft.Json.Linq;

namespace Ashen.Tests.Play
{
    /// <summary>
    /// Capture fixtures for the F3 node screens (W-09, W-11, W-13): a new climb in slot 1 travelled to the fixture's node
    /// (NodeReview), with optional edits (cinders, the shopSell setting, a smith, the history, a dungeon node), shown by the
    /// climb's router (RunFlow.Show), then put in the fixture's state (a shelf and a selection, a pressed or taken
    /// response, a chosen dungeon response).
    /// </summary>
    public static class NodeCapture
    {
        public const string Merchant = "merchant";
        public const string Event = "event";
        public const string Dialogue = "dialogue";
        public const string Dungeon = "legacyDungeon";

        public static bool Handles(string screen) => screen == Merchant || screen == Event || screen == Dialogue || screen == Dungeon;

        public static void Show(UiHost host, JObject fixture, IContentSource source)
        {
            var ui = host.Context;
            var content = RunContent.Load(source);
            var seed = (uint)((long?)fixture["seed"] ?? 20260927L);
            var session = RunSession.New(content, ui.Saves, 1, seed, content.DefaultClass(), ui.Data.Strings.Get(content.Flow.NameKey));
            if (fixture["sellOn"] != null) session.Profile.Doc["settings"] = new JObject { ["shopSell"] = (bool)fixture["sellOn"] };
            switch ((string)fixture["screen"])
            {
                case Merchant:
                    NodeReview.AtMerchant(session);
                    break;
                case Dungeon:
                    NodeReview.AtDungeon(session, (string)fixture["dungeon"]);
                    if (fixture["standAt"] != null) NodeReview.StandAt(session, (string)fixture["standAt"], (bool?)fixture["resolved"] ?? false);
                    break;
                default:
                    NodeReview.AtEvent(session, (string)fixture["eventId"]);
                    break;
            }
            session.EditRunForReview(run =>
            {
                if (fixture["cinders"] != null) run["cinders"] = (double)fixture["cinders"];
                if (fixture["history"] is JArray history)
                    foreach (var row in history.OfType<JObject>())
                        ((JArray)run["history"]).Add(new JObject { ["kind"] = "eventChoice", ["eventId"] = row["eventId"], ["choiceId"] = row["choiceId"], ["actNumber"] = 1.0, ["floor"] = 0.0, ["mapNodeId"] = null });
                if ((bool?)fixture["smith"] == true)
                {
                    run["shopStock"]["smith"] = new JObject { ["offered"] = true, ["services"] = new JArray("upgrade", "extract", "install") };
                    run["smithingStones"] = 1.0;
                }
            });
            session.Save();
            RunFlow.Show(new ScreenContext { Host = host, Navigator = host.Navigator, Ui = ui }, session);
        }

        public static void After(UiHost host, JObject fixture)
        {
            switch (host.Navigator.Top?.View)
            {
                case MerchantScreen merchant:
                    if (fixture["shelf"] != null) merchant.ReviewShelf((string)fixture["shelf"], (string)fixture["select"] == "unavailable");
                    // The refusal is anchored to the laid-out control: press once the redraw above has been laid out.
                    if ((bool?)fixture["primary"] == true) host.Navigator.Top.Root.schedule.Execute(merchant.Primary).StartingIn(150);
                    break;
                case EventScreen ev:
                    if (fixture["take"] != null) ev.Take((string)fixture["take"]);
                    if (fixture["press"] != null) ev.PressForReview((string)fixture["press"]);
                    break;
                case DungeonScreen dungeon:
                    if (fixture["choose"] != null) dungeon.PressForReview("response", (string)fixture["choose"]);
                    break;
            }
        }
    }
}
