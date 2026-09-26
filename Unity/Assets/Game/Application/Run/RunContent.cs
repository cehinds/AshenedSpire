using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using MK = Ashen.Generated.MapKeys;

namespace Ashen.App.Run
{
    /// <summary>
    /// The effective content a run is played with (docs/design/08 §8): the layered snapshot (the default preset
    /// unless a preset is named), the combat and run data built from it, the run-flow rules and the seed codec.
    /// Engine-free, so the shared tests and the game build it the same way.
    /// </summary>
    public sealed class RunContent
    {
        private RunContent() { }

        public RunSnapshot Snapshot { get; private set; }
        public RunData Data { get; private set; }
        public RunFlowRules Flow { get; private set; }
        public SeedCodec Seeds { get; private set; }
        public string ContentVersion { get; private set; }

        public CombatData Combat => Data.Combat;
        public string ContentHash => Snapshot.Hash;
        public string PresetId => Snapshot.PresetId;

        /// <summary>Class ids in authoring order (W-04 lists them in this order).</summary>
        public IReadOnlyList<string> ClassIds => Data.Classes.All.Select(c => c.Str(CombatKeys.Id)).ToList();

        public static RunContent Load(IContentSource source, string presetId = null)
        {
            var snapshot = new ConfigLayers(source).Build(new LayerSelection { PresetId = presetId });
            return From(snapshot, ContentManifest.Load(source).ContentVersion);
        }

        public static RunContent From(RunSnapshot snapshot, string contentVersion)
        {
            var content = snapshot.Content;
            var registries = RuntimeRegistries.Build(content, (JObject)content.Get(ContentFiles.StringsEn));
            var seed = (JObject)content.Get(ContentFiles.RulesRng)[RunFlowKeys.SeedRules];
            return new RunContent
            {
                Snapshot = snapshot,
                Data = ContentRunData.Build(content, registries, contentVersion),
                Flow = RunFlowRules.From((JObject)content.Get(ContentFiles.RulesRunFlow)),
                Seeds = new SeedCodec((string)seed[RunFlowKeys.Alphabet],
                    ((JArray)seed[RunFlowKeys.Homoglyphs]).Select(p => new KeyValuePair<string, string>((string)p[0], (string)p[1])).ToList(),
                    (int)seed[RunFlowKeys.MaxLength]),
                ContentVersion = contentVersion,
            };
        }

        private readonly Dictionary<string, string> _classProblems = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Why a class cannot begin a run with this content (null when it can): createRunState is tried once per class
        /// with the class defaults, and its refusal message is kept (the tuning may make a class invalid; D-065f).
        /// </summary>
        public string ClassProblem(string classId)
        {
            if (classId == null || !Data.Classes.Has(classId)) return string.Format(CultureInfo.InvariantCulture, RunFlowMessages.UnknownClass, classId);
            if (_classProblems.TryGetValue(classId, out var known)) return known;
            string problem = null;
            try { RunState.Create(Data, 1u, classId); }
            catch (InvalidOperationException e) { problem = e.Message; }
            catch (NotSupportedException e) { problem = e.Message; }
            _classProblems[classId] = problem;
            return problem;
        }

        /// <summary>The preselected class: the rule's default when it can begin, else the first class that can (null when none can).</summary>
        public string DefaultClass() =>
            ClassProblem(Flow.DefaultClass) == null ? Flow.DefaultClass : ClassIds.FirstOrDefault(id => ClassProblem(id) == null);

        /// <summary>
        /// The F1 encounter (rules/runFlow.json firstFight): the encounters of the seat in the pool, in authoring
        /// order, at the ordinal. No encounter id is named in code.
        /// </summary>
        public string FirstFightEncounter()
        {
            var f = Flow;
            var matches = Data.Encounters.All.Where(e => e.Str(MK.Seat) == f.FirstFightSeat && e.Str(MK.Pool) == f.FirstFightPool).ToList();
            if (f.FirstFightOrdinal < 0 || f.FirstFightOrdinal >= matches.Count)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, RunFlowMessages.NoFirstFight, f.FirstFightSeat, f.FirstFightPool, f.FirstFightOrdinal));
            return matches[f.FirstFightOrdinal].Str(CombatKeys.Id);
        }

        /// <summary>The region of an encounter's seat (the combat background is chosen by region), or null.</summary>
        public string RegionOf(string encounterId)
        {
            if (encounterId == null || !Data.Encounters.Has(encounterId)) return null;
            var seat = Data.Encounters.Get(encounterId).Str(MK.Seat);
            return seat != null && Data.Seats.Has(seat) ? Data.Seats.Get(seat).Str(RunFlowKeys.RegionId) : null;
        }

        /// <summary>The portrait registry id of a class in a tint (rules/runFlow.json creation.portrait).</summary>
        public string Portrait(string classId, string tint = null) =>
            Ashen.App.Ui.StringTable.Fill(Flow.PortraitTemplate,
                new Ashen.App.Ui.StringArgs().Add(RunFlowValues.PlaceholderClass, classId).Add(RunFlowValues.PlaceholderTint, tint ?? Flow.DefaultTint));
    }

    /// <summary>
    /// TEMPORARY (D-062f): RunData built from a content set, until the integrator's RuntimeRegistries.ToRunData lands on
    /// dev. The combat data, seats and encounters come from the runtime registries; creation modes and tag families are
    /// read in authoring order (rules/rowOrder.json) with their display text attached (rules/stringKeys.json); the
    /// attribute rules, character creation and derived-stat rules are the content documents. Character creation's
    /// keepsakes are not tag-stamped here (createRunState does not read keepsake tags).
    /// </summary>
    internal static class ContentRunData
    {
        public static RunData Build(ContentSet content, RuntimeRegistries registries, string contentVersion)
        {
            JObject Doc(string file) => content.Get(file) as JObject;
            var combat = registries.ToCombatData(Doc(ContentFiles.RulesMechanics), Doc(ContentFiles.RulesCombatEngine));
            Registry Table(string name) => new Registry(name, (registries.Table(name) ?? new JObject()).Properties().Select(p => p.Value), CombatKeys.Id);
            var strings = Doc(ContentFiles.StringsEn) ?? new JObject();
            var rowOrder = Doc(ContentFiles.RulesRowOrder) ?? new JObject();
            var stringKeys = Doc(ContentFiles.RulesStringKeys) ?? new JObject();
            return new RunData(combat,
                new Registry(RunKeys.CreationModes, Keyed(content, ContentFiles.CatalogCreationModes, rowOrder, stringKeys, strings), CombatKeys.Id),
                Table(MK.Seats),
                Table(RegistryKeys.Encounters),
                Doc(ContentFiles.RulesAttributeRules),
                Doc(ContentFiles.CatalogCharacterCreation),
                Doc(ContentFiles.RulesDerivedStatRules),
                new JArray(Keyed(content, ContentFiles.TagsTagFamilies, rowOrder, stringKeys, strings)),
                contentVersion,
                Doc(ContentFiles.RulesHandRules),
                Doc(ContentFiles.RulesRunEngine));
        }

        /// <summary>An id-keyed content file as rows in authoring order, with its string fields attached.</summary>
        private static List<JToken> Keyed(ContentSet content, string file, JObject rowOrder, JObject stringKeys, JObject strings)
        {
            var doc = content.Get(file) as JObject ?? new JObject();
            var present = new HashSet<string>(doc.Properties().Select(p => p.Name), StringComparer.Ordinal);
            var keys = new List<string>();
            foreach (var key in (rowOrder[file] as JArray ?? new JArray()).Select(t => (string)t))
                if (key != null && present.Remove(key)) keys.Add(key);
            keys.AddRange(present.OrderBy(k => k, StringComparer.Ordinal));
            var spec = stringKeys[file] as JObject;
            var prefix = (string)spec?[RuleKeys.Prefix];
            var fields = spec?[RegistryKeys.StringKeyFields] as JObject;
            var rows = new List<JToken>();
            foreach (var key in keys)
            {
                var row = doc[key].DeepClone();
                if (row is JObject obj && fields != null && prefix != null)
                    foreach (var field in fields.Properties())
                    {
                        var text = strings[string.Join(ContentLayout.SchemaNameSeparator, prefix, key, (string)field.Value)];
                        if (text != null && text.Type == JTokenType.String) obj[field.Name] = text.DeepClone();
                    }
                rows.Add(row);
            }
            return rows;
        }
    }
}
