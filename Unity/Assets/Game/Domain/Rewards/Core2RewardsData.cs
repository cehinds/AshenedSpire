using System;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using RW = Ashen.Generated.Core2RewardsKeys;

namespace Ashen.Domain.Rewards
{
    /// <summary>
    /// Everything Core 2 rewards and the combat end read (WP-15, docs/design/11): the deck rules (whose <see cref="CoreRunData"/> holds the resolved profile
    /// and its content), the post-combat data (<see cref="RewardsData"/>: the balance rows, the tag tree the skill tracks are derived from), the fight's data
    /// (the stance reading of a reward card) and <c>rules/core2Rewards.json</c> (the literals the web keeps in code). The content layer builds it; the
    /// domain only reads it.
    /// </summary>
    public sealed class Core2RewardsData
    {
        public Core2RewardsData(CoreDeckData deck, RewardsData rewards, CoreCombatData combat, JObject rules)
        {
            Deck = deck ?? throw new ArgumentNullException(nameof(deck));
            Rewards = rewards ?? throw new ArgumentNullException(nameof(rewards));
            Combat = combat ?? throw new ArgumentNullException(nameof(combat));
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
        }

        public CoreDeckData Deck { get; }

        /// <summary>The post-combat data of the Core 2 registries (balance, tag tree, rewards rules).</summary>
        public RewardsData Rewards { get; }

        public CoreCombatData Combat { get; }

        /// <summary>rules/core2Rewards.json.</summary>
        public JObject Rules { get; }

        public CoreRunData Run => Deck.Run;

        public RunData RunData => Deck.Run.Run;

        /// <summary>gameModule(run, id): the module's settings under the profile the run pinned, or null when it is off.</summary>
        public JObject Module(JObject run, string id) => CoreRunData.Module(Run.SubjectOf(run), id);

        /// <summary>gameContent(run, key) of the run's pinned profile.</summary>
        public JToken Content(JObject run, string key) => CoreRunData.Content(Run.SubjectOf(run), key);

        public JObject Section(string key) => Rules.Obj(key) ?? new JObject();
    }
}
