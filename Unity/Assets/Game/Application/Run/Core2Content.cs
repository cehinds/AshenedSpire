using System;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Loop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Run
{
    /// <summary>
    /// The data a Core 2 run is played on (docs/design/11 WP-C1a, D-302): the resolved profile and the registries built from it (<see cref="Core2Registries"/>), what a fight
    /// reads (<see cref="CoreCombatData"/>) and what the climb reads (<see cref="Core2LoopData"/>: the map, the routes, the loop rules). It is built from the same layered
    /// snapshot as <see cref="RunContent"/>, once, on first use; the 405 content beside it is untouched.
    /// </summary>
    public sealed class Core2Content
    {
        private Core2Content(Core2Registries registries, CoreCombatData combat, Core2LoopData loop, Ashen.Domain.Rewards.Core2RewardsData rewards, string contentHash)
        {
            Registries = registries;
            Combat = combat;
            Loop = loop;
            Rewards = rewards;
            ContentHash = contentHash;
        }

        public Core2Registries Registries { get; }
        public CoreCombatData Combat { get; }
        public Core2LoopData Loop { get; }

        /// <summary>What rewards and the combat end read (WP-15).</summary>
        public Ashen.Domain.Rewards.Core2RewardsData Rewards { get; }

        public string ContentHash { get; }

        public string ProfileId => Combat.Run.ProfileId;
        public string ProfileHash => Combat.Run.ProfileHash;

        /// <summary>Class ids of the profile in registry order (the classes a Core 2 run offers).</summary>
        public System.Collections.Generic.IReadOnlyList<string> ClassIds => Combat.Run.Run.Classes.Ids.ToList();

        public static Core2Content From(RunSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var content = snapshot.Content;
            var registries = Core2Registries.Load(content);
            var run = registries.ToCoreRunData(content);
            var combat = registries.ToCoreCombatData(content, run);
            var map = registries.ToMapData((JObject)content.Get(ContentFiles.RulesMapEngine));
            var rewards = registries.ToCoreRewardsData(content, registries.ToCoreDeckData(content, run), combat);
            return new Core2Content(registries, combat, new Core2LoopData(combat, map, registries.RoutesConfig, (JObject)content.Get(ContentFiles.RulesLoopEngine)), rewards, snapshot.Hash);
        }
    }
}
