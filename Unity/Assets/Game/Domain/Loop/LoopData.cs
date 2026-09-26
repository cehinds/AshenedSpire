using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Map;
using Ashen.Domain.Random;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;
using LK = Ashen.Generated.LoopKeys;
using LV = Ashen.Generated.LoopValues;

namespace Ashen.Domain.Loop
{
    /// <summary>
    /// Everything the run loop reads (the shipped registries main.js hands its controller between fights): the
    /// post-combat data (run and combat data, the tag tree, the ascension order), the map data (encounters, seats,
    /// configs, boss destinations), the tagging rows (a location's tags), the legacy dungeons, the unlock rows and the
    /// loop's own rules (rules/loopEngine.json). Read-only.
    /// </summary>
    public sealed class LoopData
    {
        public LoopData(RewardsData rewards, MapData map, JArray tagging, JObject legacyDungeons, JArray unlocks, JObject engine)
        {
            Rewards = rewards ?? throw new ArgumentNullException(nameof(rewards));
            Map = map ?? throw new ArgumentNullException(nameof(map));
            Tagging = tagging ?? new JArray();
            LegacyDungeons = legacyDungeons ?? new JObject();
            Unlocks = unlocks ?? new JArray();
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public RewardsData Rewards { get; }
        public RunData Run => Rewards.Run;
        public CombatData Combat => Rewards.Combat;
        public MapData Map { get; }
        public JObject Balance => Rewards.Balance;

        /// <summary>registries.tagging: every tagging row ({ family, scope, objectId, tagId }) in authoring order.</summary>
        public JArray Tagging { get; }

        /// <summary>content/generated/legacyDungeons.js: { version, status, flee, dungeons, scenes }.</summary>
        public JObject LegacyDungeons { get; }

        /// <summary>registries.unlocks: the unlock rows in authoring order.</summary>
        public JArray Unlocks { get; }

        /// <summary>rules/loopEngine.json.</summary>
        public JObject Engine { get; }

        public JToken Rule(string section, string key) => Engine.Obj(section)?[key];

        public double RuleNum(string section, string key) => Js.D(Rule(section, key));

        public string RuleStr(string section, string key) => Js.Str(Rule(section, key));

        public JObject RuleObj(string section, string key) => Rule(section, key) as JObject ?? new JObject();

        public List<string> RuleList(string section, string key) => Js.Items(Rule(section, key)).Select(Js.Str).ToList();
    }

    /// <summary>
    /// The settings the loop reads, resolved by the caller from the profile (the shipped settings screen's
    /// resolveLevelUpValue, settingOn('shrineMultiUse'), the rewardCollect dial and resolveGraceRefill).
    /// </summary>
    public sealed class LoopSettings
    {
        /// <summary>resolveLevelUpValue(settings): points per character level (null reads balance.levelUp).</summary>
        public JToken PointsPerLevel;

        /// <summary>settingOn(settings, 'shrineMultiUse'): Rest, Smith and the card services re-open the place instead of leaving it.</summary>
        public bool MultiUse;

        /// <summary>The rewardCollect dial: 'auto' (Continue takes every pending row) or 'manual'.</summary>
        public string RewardCollect = LV.CollectAuto;

        /// <summary>resolveGraceRefill(settings).counts, handed to the refillFlasks opcode.</summary>
        public JObject RefillCounts = new JObject();
    }

    /// <summary>
    /// One run's live loop state (the shipped main.js module state: <c>run</c>, <c>rng</c>, the profile behind
    /// <c>saves.loadMeta()</c>, the rest place the screen stands at and the fight just entered). Documents are the
    /// shipped JSON shapes (D-037); every loop call mutates them in place, as the shipped controller does.
    /// </summary>
    public sealed class LoopContext
    {
        public LoopContext(LoopData data, JObject run, Rng rng, JObject profile, LoopSettings settings = null)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Run = run ?? throw new ArgumentNullException(nameof(run));
            Rng = rng ?? throw new ArgumentNullException(nameof(rng));
            Profile = profile ?? new JObject();
            Settings = settings ?? new LoopSettings();
            RestLocationId = data.RuleStr(LK.Locations, MK.Shrine);
        }

        public LoopData Data { get; }
        public JObject Run { get; }
        public Rng Rng { get; }

        /// <summary>The profile document (saves.loadMeta()): its stored JSON, rewritten in place by the loop's writes.</summary>
        public JObject Profile { get; set; }

        public LoopSettings Settings { get; }

        /// <summary>
        /// The place the Rest screen stands at, kept across its own re-opens (main.js <c>restLocationId</c>): a door that
        /// names no place (a legacy-dungeon shrine) stands where the last one stood.
        /// </summary>
        public string RestLocationId { get; set; }

        /// <summary>The fight just entered (enterCombat): its encounter and the createCombat arguments.</summary>
        public FightEntry Fight { get; set; }

        /// <summary>The found armaments of the profile (meta.found), for the pure drop rolls.</summary>
        public List<string> Found() => Js.Items(Profile[LK.Found]).Select(Js.Str).ToList();

        /// <summary>The run's active Custom Climb rules (content/customMods.js activeMods).</summary>
        public bool ModOn(string mod) => CombatEnd.ModOn(Data.Rewards, Run, mod);

        /// <summary>The profile's stored settings (saves.loadMeta().settings), read by the fight arguments (hand rules, swap price).</summary>
        internal JObject ProfileSettings() => Profile.Obj(LK.Settings) ?? new JObject();
    }

    /// <summary>A fight entered by the loop: the encounter row, its pool and the createCombat arguments (JSON-plain).</summary>
    public sealed class FightEntry
    {
        public string EncounterId;
        public string Pool;
        public JObject Encounter;
        public JObject Args;
    }
}
