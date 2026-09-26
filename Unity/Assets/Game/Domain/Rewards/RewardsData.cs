using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using WK = Ashen.Generated.RewardsKeys;

namespace Ashen.Domain.Rewards
{
    /// <summary>
    /// Everything the post-combat pipeline reads (the shipped registries main.js onCombatEnd hands to skills.js,
    /// classTree.js, levelup.js, smithing.js and engine/encounters.js): the run data (combat data plus the creation
    /// tables), the item-type/card-domain tree (<c>registries.nodes</c>, which the skill tracks and draft schools are
    /// derived from), the Custom Climb ascension order (balance/customRun.json ASCENSION_ORDER, read by activeMods)
    /// and the pipeline's own rules (rules/rewardsEngine.json). Read-only.
    /// </summary>
    public sealed class RewardsData
    {
        public RewardsData(RunData run, JArray nodes, JArray ascensionOrder, JObject engine)
        {
            Run = run ?? throw new ArgumentNullException(nameof(run));
            Nodes = nodes ?? new JArray();
            AscensionOrder = ascensionOrder ?? new JArray();
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public RunData Run { get; }
        public CombatData Combat => Run.Combat;
        public JObject Balance => Run.Balance;

        /// <summary>registries.nodes: the tag tree rows in authoring order.</summary>
        public JArray Nodes { get; }

        /// <summary>customMods.js ASCENSION_ORDER.</summary>
        public JArray AscensionOrder { get; }

        /// <summary>rules/rewardsEngine.json.</summary>
        public JObject Engine { get; }

        public JToken Rule(string section, string key) => Engine.Obj(section)?[key];

        public double RuleNum(string section, string key) => Js.D(Rule(section, key));

        public string RuleStr(string section, string key) => Js.Str(Rule(section, key));

        public JObject RuleObj(string section, string key) => Rule(section, key) as JObject ?? new JObject();

        public List<string> RuleList(string section, string key) => Js.Items(Rule(section, key)).Select(Js.Str).ToList();

        /// <summary>
        /// The rewards registry dump written by Tools/oracle-rewards.mjs: the run dump's tables plus <c>nodes</c>.
        /// </summary>
        public static RewardsData FromRegistryDump(JObject dump, JObject mechanics, JObject combatEngine, JObject handRules, JObject runEngine,
            JArray ascensionOrder, JObject rewardsEngine) =>
            new RewardsData(RunData.FromRegistryDump(dump, mechanics, combatEngine, handRules, runEngine), dump[WK.Nodes] as JArray, ascensionOrder, rewardsEngine);
    }
}
