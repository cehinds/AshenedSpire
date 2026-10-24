using System;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2RunKeys;
using PK = Ashen.Generated.Core2ProfileKeys;

namespace Ashen.Domain.Run
{
    /// <summary>
    /// Everything Core 2 run creation reads beyond <see cref="RunData"/> (WP-3, docs/design/11): the resolved game profile
    /// (<c>profile/core.json</c> plus a <c>content</c> object holding its content keys), the version scalars
    /// (<c>rules/versions.json</c>), the creation constants (<c>rules/core2Run.json</c>) and the rule snapshots a run is
    /// stamped with (<c>rangePolicy</c>, <c>reactionRules</c>, <c>combatExpansionRules</c>). The content layer builds it
    /// (Core2Registries.ToCoreRunData); the domain only reads it.
    /// </summary>
    public sealed class CoreRunData
    {
        public CoreRunData(RunData run, JObject profile, JObject versions, JObject rules, JObject rangePolicy, JObject reactionRules, JObject combatExpansionRules, JObject deckRules = null)
        {
            Run = run ?? throw new ArgumentNullException(nameof(run));
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            Versions = versions ?? throw new ArgumentNullException(nameof(versions));
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            RangePolicy = rangePolicy ?? throw new ArgumentNullException(nameof(rangePolicy));
            ReactionRules = reactionRules ?? throw new ArgumentNullException(nameof(reactionRules));
            CombatExpansionRules = combatExpansionRules ?? throw new ArgumentNullException(nameof(combatExpansionRules));
            DeckRules = deckRules;
        }

        public RunData Run { get; }

        /// <summary>The resolved profile: <c>profile/core.json</c> with its content keys under <c>content</c>.</summary>
        public JObject Profile { get; }

        /// <summary>rules/versions.json.</summary>
        public JObject Versions { get; }

        /// <summary>rules/core2Run.json.</summary>
        public JObject Rules { get; }

        public JObject RangePolicy { get; }
        public JObject ReactionRules { get; }
        public JObject CombatExpansionRules { get; }

        /// <summary>rules/core2Deck.json (the deck editor's defaults, among them Play in deck order), or null when a caller builds the data without it.</summary>
        public JObject DeckRules { get; }

        public string ProfileId => Js.Str(Profile[PK.Id]);

        public string ProfileHash => Js.Str(Profile[PK.Hash]);

        /// <summary>The creation constants section <paramref name="key"/> of rules/core2Run.json.</summary>
        public JObject RuleSection(string key) => Rules.Obj(key) ?? new JObject();

        /// <summary>The subject a profile read goes to: the run's own pinned profile when it carries one, else the loaded profile.</summary>
        public JObject SubjectOf(JObject run) => run?.Obj(Ashen.Generated.Core2RunSnapshotKeys.GameProfile) ?? Profile;

        /// <summary>gameModule(subject, id): the module's settings, or null when it is off.</summary>
        public static JObject Module(JObject subject, string id) => subject?.Obj(PK.Modules)?[id] as JObject;

        /// <summary>gameContent(subject, key).</summary>
        public static JToken Content(JObject subject, string key) => subject?.Obj(CK.Content)?[key];

        public static bool IsConfigured(JObject subject) => subject?.Obj(PK.Foundation)?.Str(CK.Mode) == Ashen.Generated.Core2RunValues.Configured;

        public static bool ModuleConfigured(JObject subject, string id) => Module(subject, id)?.Str(CK.Mode) == Ashen.Generated.Core2RunValues.Configured;
    }
}
