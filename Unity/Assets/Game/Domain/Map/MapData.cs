using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;
using MM = Ashen.Generated.MapMessages;

namespace Ashen.Domain.Map
{
    /// <summary>
    /// Everything the map port reads (the shipped registries' view for engine/actmap.js and friends): the encounter,
    /// event, enemy and seat registries in authoring order, the per-tier map configs (with their Unknown-node
    /// weights), the quest gates on events, the boss locations, the run-shape limits, the legacy act bosses, the
    /// Endless cycle length and the engine's own rules (rules/mapEngine.json). Read-only; never re-sorted (D-040).
    /// </summary>
    public sealed class MapData
    {
        public MapData(Registry encounters, Registry events, Registry enemies, Registry seats, JObject mapConfigs,
            JObject eventHistoryRequirements, JObject bossLocations, JObject mapShapeLimits, JObject legacyActBosses,
            JObject endless, MapRules rules)
        {
            Encounters = encounters ?? throw new ArgumentNullException(nameof(encounters));
            Events = events ?? throw new ArgumentNullException(nameof(events));
            Enemies = enemies ?? throw new ArgumentNullException(nameof(enemies));
            Seats = seats ?? throw new ArgumentNullException(nameof(seats));
            MapConfigs = mapConfigs ?? new JObject();
            EventHistoryRequirements = eventHistoryRequirements ?? new JObject();
            BossLocations = bossLocations ?? new JObject();
            MapShapeLimits = mapShapeLimits ?? new JObject();
            LegacyActBosses = legacyActBosses ?? new JObject();
            Endless = endless ?? new JObject();
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
        }

        public Registry Encounters { get; }
        public Registry Events { get; }
        public Registry Enemies { get; }
        public Registry Seats { get; }

        /// <summary>mapConfigs keyed by tier ("1", "2", ...).</summary>
        public JObject MapConfigs { get; }

        /// <summary>Quest steps (E12): event id → requirement, absent means ungated.</summary>
        public JObject EventHistoryRequirements { get; }

        /// <summary>BOSS_LOCATIONS: boss encounter id → destination name.</summary>
        public JObject BossLocations { get; }

        /// <summary>MAP_SHAPE_LIMITS (minColumns, maxWeight).</summary>
        public JObject MapShapeLimits { get; }

        /// <summary>LEGACY_ACT_BOSSES: tier → the boss a pre-§13 singular graph always had.</summary>
        public JObject LegacyActBosses { get; }

        /// <summary>balance.endless (actsPerCycle is the final tier).</summary>
        public JObject Endless { get; }

        public MapRules Rules { get; }

        /// <summary>registries.mapConfig(tier): throws on an unknown tier, like the shipped getter.</summary>
        public JObject MapConfig(int tier)
        {
            var cfg = MapConfigs[tier.ToString(CultureInfo.InvariantCulture)] as JObject;
            if (cfg == null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, MM.UnknownMapConfig, tier));
            return cfg;
        }

        /// <summary>
        /// The table dump layout written by Tools/oracle-map.mjs: encounters, events, enemies and seats as arrays of rows
        /// in authoring order, plus mapConfigs, eventHistoryRequirements, endless, bossLocations, mapShapeLimits and
        /// legacyActBosses.
        /// </summary>
        public static MapData FromTableDump(JObject dump, JObject engineRules)
        {
            if (dump == null) throw new ArgumentNullException(nameof(dump));
            return new MapData(
                new Registry(MK.Encounters, Js.Items(dump[MK.Encounters]), K.Id),
                new Registry(MK.Events, Js.Items(dump[MK.Events]), K.Id),
                new Registry(K.Enemies, Js.Items(dump[K.Enemies]), K.Id),
                new Registry(MK.Seats, Js.Items(dump[MK.Seats]), K.Id),
                dump.Obj(MK.MapConfigs), dump.Obj(MK.EventHistoryRequirements), dump.Obj(MK.BossLocations),
                dump.Obj(MK.MapShapeLimits), dump.Obj(MK.LegacyActBosses), dump.Obj(MK.Endless),
                new MapRules(engineRules));
        }
    }
}
