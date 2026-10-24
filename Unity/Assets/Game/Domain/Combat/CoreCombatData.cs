using System;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// Everything a Core 2 fight reads beyond <see cref="CombatData"/> (WP-4, docs/design/11): the run's data (registries, the resolved
    /// profile, the rule snapshots), the FixCore rules the fight snapshots (<c>rules/fixCoreSnapshot.json</c>), the action-cost table
    /// (<c>rules/actionCosts.json</c>), the stance defaults (<c>rules/stanceRules.json</c>) and the web literals the combat start reads
    /// (<c>rules/core2Combat.json</c>). Read-only; the content layer builds it (<c>Core2Registries.ToCoreCombatData</c>).
    /// </summary>
    public sealed class CoreCombatData
    {
        public CoreCombatData(CoreRunData run, JObject fixCore, JObject actionCosts, JObject stanceRules, JObject rules, JObject enemyKnowledgeRules, JObject handDefaults, JObject proposalStatusRules = null, JObject combatRules = null, JObject mechanics = null, JObject mechanicOps = null)
        {
            Run = run ?? throw new ArgumentNullException(nameof(run));
            FixCore = fixCore ?? throw new ArgumentNullException(nameof(fixCore));
            ActionCosts = actionCosts ?? throw new ArgumentNullException(nameof(actionCosts));
            StanceRules = stanceRules ?? throw new ArgumentNullException(nameof(stanceRules));
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            EnemyKnowledgeRules = enemyKnowledgeRules ?? throw new ArgumentNullException(nameof(enemyKnowledgeRules));
            HandDefaults = handDefaults ?? throw new ArgumentNullException(nameof(handDefaults));
            ProposalStatusRules = proposalStatusRules ?? new JObject();
            CombatRules = combatRules ?? new JObject();
            Mechanics = mechanics ?? new JObject();
            MechanicOps = mechanicOps ?? new JObject();
        }

        public CoreRunData Run { get; }

        /// <summary>The damage-type, school and magic-split rules of <c>rules/core2CombatRules.json</c> (the web's content/combatRules.js): the share of a hit that is magic.</summary>
        public JObject CombatRules { get; }

        /// <summary>The mechanic folders of <c>rules/core2Mechanics.json</c> by slug (manifest, model schema, interface, service, cases).</summary>
        public JObject Mechanics { get; }

        /// <summary>The op catalog of <c>rules/runtime.json</c> (<c>engine/runtime/ops.json</c>) the mechanic cases run against.</summary>
        public JObject MechanicOps { get; }

        /// <summary>The compiled-binding cache of the mechanics kernel (a pure function of its inputs, so a repeat call returns the one list).</summary>
        public readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MechanicBinding>> BindingCache = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<MechanicBinding>>(StringComparer.Ordinal);

        /// <summary>The mechanics a fight registers (<c>rules/core2Combat.json</c> mechanics.registered), cached.</summary>
        public JObject RegisteredMechanics { get; set; }

        /// <summary>The registries the combat engine reads (cards, relics, statuses, enemies, classes, balance, equipment).</summary>
        public CombatData Combat => Run.Run.Combat;

        /// <summary>The resolved game profile: <c>profile/core.json</c> with its content keys under <c>content</c>.</summary>
        public JObject Profile => Run.Profile;

        /// <summary>rules/fixCoreSnapshot.json: the FixCore rules a fight snapshots (snapshotFixCoreRules at the pinned sha).</summary>
        public JObject FixCore { get; }

        /// <summary>rules/actionCosts.json: the action-cost table and the enemy SP row.</summary>
        public JObject ActionCosts { get; }

        /// <summary>rules/stanceRules.json: the web's stance defaults (content/stanceRules.js).</summary>
        public JObject StanceRules { get; }

        /// <summary>rules/core2Combat.json.</summary>
        public JObject Rules { get; }

        /// <summary>rules/enemyKnowledgeRules.json: the read and perception rules the fight snapshots (snapshotEnemyKnowledgeRules).</summary>
        public JObject EnemyKnowledgeRules { get; }

        /// <summary>rules/core2HandRules.json: the hand rules a configured fight starts from before the profile's hand rows are laid over them (web engine/handRules.js defaults).</summary>
        public JObject HandDefaults { get; }

        /// <summary>The proposal status rules (<c>profile/bundle/proposalStatusRules.json</c>, the web registry's <c>proposalStatusRules</c>): the card taxes, restrictions and stance capacity statuses give, by status id.</summary>
        public JObject ProposalStatusRules { get; }

        /// <summary>combatExpansionVersion of rules/versions.json: the expanded combat a Core 2 fight runs.</summary>
        public double ExpandedVersion => Js.D(Run.Versions[Ashen.Generated.Core2CombatKeys.CombatExpansionVersion]);

        /// <summary>reactionRulesVersion of rules/versions.json.</summary>
        public double ReactionVersion => Js.D(Run.Versions[Ashen.Generated.Core2CombatKeys.ReactionRulesVersion]);

        /// <summary>breakMeterVersion of rules/versions.json.</summary>
        public double BreakMeterVersion => Js.D(Run.Versions[Ashen.Generated.Core2CombatKeys.BreakMeterVersion]);

        /// <summary>The FixCore version the fight snapshots (rules/fixCoreSnapshot.json).</summary>
        public double FixCoreVersion => Js.D(FixCore[Ashen.Generated.Core2CombatKeys.Version]);

        public JObject Section(string key) => Rules.Obj(key) ?? new JObject();

        /// <summary>gameModule(profile, id): the module's settings, or null when it is off.</summary>
        public JObject Module(string id) => CoreRunData.Module(Profile, id);

        /// <summary>configuredModule(profile, id): the module's settings when its mode is configured.</summary>
        public JObject Configured(string id) => CoreRunData.ModuleConfigured(Profile, id) ? Module(id) : null;

        /// <summary>gameContent(profile, key).</summary>
        public JToken Content(string key) => CoreRunData.Content(Profile, key);

        /// <summary>A section of the action-cost rules (<c>rules/actionCosts.json</c>), by its web export name.</summary>
        public JToken ActionCost(string key) => ActionCosts[key];
    }
}
