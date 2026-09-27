using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Shop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Nodes
{
    /// <summary>ui/nodes.json: the node screens' presentation data (shelf order, art by registry id or template; D-140n).</summary>
    public sealed class NodeRules
    {
        public IReadOnlyList<string> MerchantShelves = Array.Empty<string>();
        public string MerchantBackground;
        public string MerchantFigure;
        public string EventBackground;
        public string EventFallbackBackground;
        public string EventEnemyArt;
        public JObject EventArt = new JObject();
        public string SpeakerArt;
        public string DungeonBackground;
        public string DungeonFloor;
        public string DungeonMap;
        public string DungeonOccupantArt;

        public static NodeRules From(JObject json)
        {
            json = json ?? new JObject();
            var merchant = json[NodeKeys.Merchant] as JObject ?? new JObject();
            var ev = json[NodeKeys.Event] as JObject ?? new JObject();
            var dialogue = json[NodeKeys.Dialogue] as JObject ?? new JObject();
            var dungeon = json[NodeKeys.LegacyDungeon] as JObject ?? new JObject();
            return new NodeRules
            {
                MerchantShelves = (merchant[NodeKeys.Shelves] as JArray ?? new JArray()).Select(t => (string)t).ToList(),
                MerchantBackground = (string)merchant[NodeKeys.Background],
                MerchantFigure = (string)merchant[NodeKeys.Figure],
                EventBackground = (string)ev[NodeKeys.Background],
                EventFallbackBackground = (string)ev[NodeKeys.FallbackBackground],
                EventEnemyArt = (string)ev[NodeKeys.EnemyArt],
                EventArt = ev[NodeKeys.Art] as JObject ?? new JObject(),
                SpeakerArt = (string)dialogue[NodeKeys.SpeakerArt],
                DungeonBackground = (string)dungeon[NodeKeys.Background],
                DungeonFloor = (string)dungeon[NodeKeys.Floor],
                DungeonMap = (string)dungeon[NodeKeys.Map],
                DungeonOccupantArt = (string)dungeon[NodeKeys.OccupantArt],
            };
        }

        /// <summary>A registry id from a template and one placeholder, or null when either is missing.</summary>
        public static string Fill(string template, string placeholder, string value) =>
            string.IsNullOrEmpty(template) || string.IsNullOrEmpty(value) ? null : StringTable.Fill(template, new StringArgs().Add(placeholder, value));
    }

    /// <summary>Text helpers shared by the node views (engine-free).</summary>
    public static class NodeText
    {
        public static string Number(double d) => Ashen.App.Combat.CombatText.Number(d);

        /// <summary>
        /// A refusal key as text: a key of the string tables (strings/shop.en.json, strings/events.en.json, the node table),
        /// else its shipped ".short" form (the ui.shop.avail.* ids), else the generic refusal. {0}.. slots take the arguments.
        /// </summary>
        public static string Refusal(StringTable strings, string key, IReadOnlyList<string> args = null, string fallback = null)
        {
            if (key == null) return null;
            var shortKey = string.Format(CultureInfo.InvariantCulture, NodeFormats.ShortKey, key);
            var chosen = strings.Has(key) ? key : strings.Has(shortKey) ? shortKey : fallback ?? NodeStringKeys.NodesMerchantRefused;
            var fill = new StringArgs();
            if (args != null)
                for (var i = 0; i < args.Count; i++) fill.Add(i.ToString(CultureInfo.InvariantCulture), args[i]);
            return strings.Format(chosen, fill);
        }

        public static string Refusal(StringTable strings, Refusal refusal, string fallback = null) =>
            refusal == null ? null : Refusal(strings, refusal.Key, refusal.Args, fallback);

        /// <summary>Items joined with the table's list separator.</summary>
        public static string Join(StringTable strings, string separatorKey, IEnumerable<string> items) =>
            string.Join(strings.Get(separatorKey), items.Where(s => !string.IsNullOrEmpty(s)));
    }
}
