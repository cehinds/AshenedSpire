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
    /// <summary>A class's starting numbers for the W-04 Class pane.</summary>
    public sealed class ClassPreview
    {
        public IReadOnlyList<KeyValuePair<string, double>> Attributes = Array.Empty<KeyValuePair<string, double>>();
        public double MaxHp;
        public double MaxMana;
        public double MaxStamina;
        public double Actions;
        public double HpFlasks;
        public double ManaFlasks;
    }

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

        public CombatData Combat => Data.Combat;
        public string ContentVersion => Snapshot.ContentVersion;
        public string ContentHash => Snapshot.Hash;
        public string PresetId => Snapshot.PresetId;

        /// <summary>Class ids in authoring order (W-04 lists them in this order).</summary>
        public IReadOnlyList<string> ClassIds => Data.Classes.All.Select(c => c.Str(CombatKeys.Id)).ToList();

        /// <summary>The effective content for a run: the named preset (the default one when null) and optional ordered patches.</summary>
        public static RunContent Load(IContentSource source, string presetId = null, IReadOnlyList<JObject> patches = null) =>
            From(new ConfigLayers(source).Build(new LayerSelection { PresetId = presetId, Patches = patches ?? Array.Empty<JObject>() }));

        public static RunContent From(RunSnapshot snapshot)
        {
            var content = snapshot.Content;
            JObject Doc(string file) => (JObject)content.Get(file);
            var registries = RuntimeRegistries.Build(content, Doc(ContentFiles.StringsEn));
            var seed = (JObject)Doc(ContentFiles.RulesRng)[RunFlowKeys.SeedRules];
            return new RunContent
            {
                Snapshot = snapshot,
                Data = registries.ToRunData(Doc(ContentFiles.RulesMechanics), Doc(ContentFiles.RulesCombatEngine), Doc(ContentFiles.RulesHandRules), Doc(ContentFiles.RulesRunEngine), snapshot.ContentVersion),
                Flow = RunFlowRules.From((JObject)content.Get(ContentFiles.RulesRunFlow)),
                Seeds = new SeedCodec((string)seed[RunFlowKeys.Alphabet],
                    ((JArray)seed[RunFlowKeys.Homoglyphs]).Select(p => new KeyValuePair<string, string>((string)p[0], (string)p[1])).ToList(),
                    (int)seed[RunFlowKeys.MaxLength]),
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

        /// <summary>
        /// What W-04 shows for a class (US-2.1): its attributes in authoring order and the derived pools and flask split
        /// the run would start with, read from createRunState's own document; null when the class cannot begin.
        /// </summary>
        public ClassPreview Preview(string classId)
        {
            if (ClassProblem(classId) != null) return null;
            var run = RunState.Create(Data, 1u, classId);
            var attributes = run.Obj(CombatKeys.Attributes) ?? new JObject();
            var charges = run.Obj(CombatKeys.FlaskCharges) ?? new JObject();
            return new ClassPreview
            {
                Attributes = Data.Attributes.All.Select(a => a.Str(CombatKeys.Id)).Where(id => attributes[id] != null)
                    .Select(id => new KeyValuePair<string, double>(id, attributes.Num(id))).ToList(),
                MaxHp = run.Num(CombatKeys.MaxHp),
                MaxMana = run.Num(CombatKeys.MaxMana),
                MaxStamina = run.Num(CombatKeys.MaxStamina),
                Actions = run.Num(CombatKeys.EnergyMax),
                HpFlasks = charges.Num(CombatKeys.Hp),
                ManaFlasks = charges.Num(CombatKeys.Mana),
            };
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
}
