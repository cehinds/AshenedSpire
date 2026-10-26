using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>One coloured glow of the aura: the web's three shadows of a palette entry (a hard copy of the silhouette <see cref="Radius"/> to each side and a blurred one below) in one colour.</summary>
    public sealed class AuraGlow
    {
        /// <summary>The colour's channels, 0..255.</summary>
        public double R;
        public double G;
        public double B;

        /// <summary>The opacity of the colour, 0..1.</summary>
        public double Alpha;

        /// <summary>How far the hard copies sit from the silhouette, in the stage's canvas units, and the blur of the lower one.</summary>
        public double Radius;
        public double Blur;
    }

    /// <summary>What <c>auraFilter</c> chose for a frame: the glows (none for an unlit figure) and the brightness of the figure under them.</summary>
    public sealed class AuraLayers
    {
        public readonly List<AuraGlow> Glows = new List<AuraGlow>();
        public double Brightness = 1;

        public bool None => Glows.Count == 0;
    }

    /// <summary>What a card paid, as the aura reads it (<c>resourceAura</c>): the costs printed on the card and whether it spends hit points by its own effects.</summary>
    public sealed class AuraCost
    {
        public double StaminaCost;
        public double ManaCost;
        public double HpCost;

        /// <summary>A loseHp effect on the player itself that is not a number or is above 0 (immediate self-payment; delayed status damage and enemy damage do not turn the outline red).</summary>
        public bool SelfPayment;
    }

    /// <summary>
    /// ui/combatAura.json (P-4, D-1540..): the silhouette glow of the player's card stage, src/ui/combatAura.js over content/config/ui/presentation/combatAura.json. The web draws it with CSS drop-shadow
    /// filters; this chooses what they would draw, as data a renderer without filters can follow (the Unity figure tints a soft silhouette sprite with each glow). Engine-free.
    /// </summary>
    public sealed class CombatAuraData
    {
        private readonly Dictionary<string, double[]> _colors = new Dictionary<string, double[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, JObject> _powerFrames = new Dictionary<string, JObject>(StringComparer.Ordinal);
        private readonly HashSet<string> _guardedRests;
        private readonly string _defaultPalette;
        private readonly string _guardedPalette;
        private readonly double _spread;
        private readonly JObject _faded;
        private readonly JObject _lit;

        public CombatAuraData(JObject json)
        {
            foreach (var row in (JObject)json[AuraKeys.Colors]) _colors[row.Key] = ((JArray)row.Value).Select(v => v.Value<double>()).ToArray();
            foreach (var row in (JObject)json[AuraKeys.PowerFrames]) _powerFrames[row.Key] = (JObject)row.Value;
            _guardedRests = new HashSet<string>(((JArray)json[AuraKeys.GuardedRestPoses]).Select(v => v.Value<string>()), StringComparer.Ordinal);
            _defaultPalette = json.Value<string>(AuraKeys.DefaultPalette);
            _guardedPalette = json.Value<string>(AuraKeys.GuardedPalette);
            _spread = json.Value<double>(AuraKeys.Spread);
            _faded = (JObject)json[AuraKeys.Faded];
            _lit = (JObject)json[AuraKeys.Lit];
            SpellFrame = json.Value<string>(AuraKeys.SpellFrame);
            GuardRest = json.Value<string>(AuraKeys.GuardRest);
        }

        /// <summary>The power frame the stage names for a spell, whatever pose the clip is in.</summary>
        public string SpellFrame { get; }

        /// <summary>The rest the stage names for a braced figure (a defend or a counter).</summary>
        public string GuardRest { get; }

        /// <summary>
        /// auraFilter: <paramref name="frame"/> is the pose on show (a spell names the power frame), <paramref name="rest"/> the rest the figure keeps, <paramref name="resources"/> what the card playing paid
        /// and <paramref name="active"/> whether a clip is playing. A lit glow is the strength of an action, a faded one the pale outline of a braced figure, a power frame its own.
        /// </summary>
        public AuraLayers For(string frame, string rest, IReadOnlyList<string> resources, bool active)
        {
            var layers = new AuraLayers();
            JObject phase = null;
            if (frame != null) _powerFrames.TryGetValue(frame, out phase);
            var guarded = rest != null && _guardedRests.Contains(rest);
            IReadOnlyList<string> palette;
            if (active && resources != null && resources.Count > 0) palette = resources;
            else if (phase != null) palette = new[] { _defaultPalette };
            else if (guarded) palette = new[] { _guardedPalette };
            else return layers;
            var glow = !active && phase == null ? _faded : _lit;
            var radius = (phase ?? glow).Value<double>(AuraKeys.Radius);
            var alpha = (phase ?? glow).Value<double>(AuraKeys.Alpha);
            var blur = (phase ?? glow).Value<double>(AuraKeys.Blur);
            for (var i = 0; i < palette.Count; i++)
            {
                var rgb = _colors.TryGetValue(palette[i], out var own) ? own : _colors[_defaultPalette];
                layers.Glows.Add(new AuraGlow { R = rgb[0], G = rgb[1], B = rgb[AuraCount.Blue], Alpha = alpha, Radius = radius + i * _spread, Blur = blur });
            }
            if (phase != null) layers.Brightness = phase.Value<double>(AuraKeys.Brightness);
            return layers;
        }

        /// <summary>
        /// The aura of what the stage shows (filterFor in alternativeCardStage.js): none on the downed figure and on a light layout (the lite quality draws no filters), a spell on the power frame, a braced
        /// figure (a defend or a counter held) on the guard rest.
        /// </summary>
        public AuraLayers For(StageDraw draw, string rest, bool lite)
        {
            if (lite || draw == null || draw.Down) return new AuraLayers();
            var frame = draw.AuraAction == StageActions.Spell ? SpellFrame : draw.Pose;
            var braced = rest == StageNames.RestDefend || rest == StageNames.RestCounter ? GuardRest : rest;
            return For(frame, braced, draw.AuraResources, draw.AuraAction != null);
        }

        /// <summary>
        /// resourceAura: the resources the card paid, in the order stamina, mana, hit points. A receipt (what the play really spent) wins over the printed cost; hit points need a printed cost or a self-payment,
        /// and a receipt that spent none cancels them.
        /// </summary>
        public static List<string> Resources(AuraCost cost, double? staminaSpent, double? manaSpent, double? hpSpent)
        {
            var result = new List<string>();
            if ((staminaSpent ?? cost.StaminaCost) > 0) result.Add(AuraResources.Stamina);
            if ((manaSpent ?? cost.ManaCost) > 0) result.Add(AuraResources.Mana);
            if ((cost.HpCost > 0 || cost.SelfPayment) && (hpSpent ?? 1) > 0) result.Add(AuraResources.Hp);
            return result;
        }
    }
}
