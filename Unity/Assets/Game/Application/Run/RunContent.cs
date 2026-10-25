using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

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
    /// unless a preset is named), the Core 2 data built from it, the run-flow rules and the seed codec.
    /// Engine-free, so the shared tests and the game build it the same way.
    /// </summary>
    public sealed class RunContent
    {
        private RunContent() { }

        public RunSnapshot Snapshot { get; private set; }
        public RunFlowRules Flow { get; private set; }
        public SeedCodec Seeds { get; private set; }

        private Core2Content _core2;

        /// <summary>The Core 2 ruleset's data (the profile, its registries, the fight, map and loop data), built from this snapshot on first use.</summary>
        public Core2Content Core2 => _core2 ??= Core2Content.From(Snapshot);

        public string ContentVersion => Snapshot.ContentVersion;
        public string ContentHash => Snapshot.Hash;
        public string PresetId => Snapshot.PresetId;

        /// <summary>The effective content for a run: the named preset (the default one when null) and optional ordered patches.</summary>
        public static RunContent Load(IContentSource source, string presetId = null, IReadOnlyList<JObject> patches = null) =>
            From(new ConfigLayers(source).Build(new LayerSelection { PresetId = presetId, Patches = patches ?? Array.Empty<JObject>() }));

        public static RunContent From(RunSnapshot snapshot)
        {
            var content = snapshot.Content;
            var seed = (JObject)((JObject)content.Get(ContentFiles.RulesRng))[RunFlowKeys.SeedRules];
            return new RunContent
            {
                Snapshot = snapshot,
                Flow = RunFlowRules.From((JObject)content.Get(ContentFiles.RulesRunFlow)),
                Seeds = new SeedCodec((string)seed[RunFlowKeys.Alphabet],
                    ((JArray)seed[RunFlowKeys.Homoglyphs]).Select(p => new KeyValuePair<string, string>((string)p[0], (string)p[1])).ToList(),
                    (int)seed[RunFlowKeys.MaxLength]),
            };
        }

        private HashSet<string> _assets;

        /// <summary>Whether assets/registry.json has the asset id (the registry is the one source of art and sound; US-0.1).</summary>
        public bool HasAsset(string assetId)
        {
            if (assetId == null) return false;
            _assets ??= new HashSet<string>(((Snapshot.Content.Get(ContentFiles.AssetsRegistry) as JObject)?.Properties() ?? Enumerable.Empty<JProperty>()).Select(p => p.Name), StringComparer.Ordinal);
            return _assets.Contains(assetId);
        }

        /// <summary>The portrait registry id of a class in a tint (rules/runFlow.json creation.portrait).</summary>
        public string Portrait(string classId, string tint = null) =>
            Ashen.App.Ui.StringTable.Fill(Flow.PortraitTemplate,
                new Ashen.App.Ui.StringArgs().Add(RunFlowValues.PlaceholderClass, classId).Add(RunFlowValues.PlaceholderTint, tint ?? Flow.DefaultTint));
    }
}
