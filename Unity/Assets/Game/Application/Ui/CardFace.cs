using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>One effect row of a card (its op, who it targets and the status it applies): what the cutout selection and the tag rail read of a card's effects.</summary>
    public sealed class CardEffectRef
    {
        public string Op;
        public string Target;
        public string Status;

        /// <summary>The effect's place in the card's own effect list, or -1 for a row nested inside another (a condition's branch): only a card's own rows carry a count on the rail.</summary>
        public int Index = -1;

        /// <summary>The stacks a status row authors when they are a constant (a number, or constants added), else null.</summary>
        public double? Stacks;

        /// <summary>The stacks the live preview prices this row at (set by the combat view), which wins over <see cref="Stacks"/>.</summary>
        public double? Live;

        /// <summary>True when the row's stacks are the stat-driven Evade grant (a metric formula): shown on the rail without a number.</summary>
        public bool StacksFromMetric;

        /// <summary>True when the row carries only an op, target, status and stacks: a count may stand for it.</summary>
        public bool Plain;

        /// <summary>True when a chance below 100 or a condition gates the row: its stacks are not a promise.</summary>
        public bool Gated;
    }

    /// <summary>A number in a card's text that the live preview moved off its printed value: where it sits in the text, and +1 (up) or -1 (down).</summary>
    public sealed class TextShift
    {
        public int Start;
        public int Length;
        public int Direction;
    }

    /// <summary>What a card face is drawn from: the card's id, type, tag ids and (nested) effect rows. Ids and tags are the content's own.</summary>
    public sealed class CardFaceQuery
    {
        public string CardId;
        public string Type;
        public string ProfileId;
        public IReadOnlyList<string> Tags = Array.Empty<string>();
        public IReadOnlyList<CardEffectRef> Effects = Array.Empty<CardEffectRef>();

        /// <summary>The basic card a Spell mirrors ("attack", "technique"): the type band draws the Spell mark and then that card's.</summary>
        public string Mirrors;

        /// <summary>The Precision a stance entry grants and the Evade it grants (combat: what the card's stance entry would give now), or null.</summary>
        public double? EntryPrecision;
        public double? EntryEvade;

        /// <summary>The numbered traits' values (flux 1, impact 2) the card authors.</summary>
        public IReadOnlyDictionary<string, double> TraitValues = new Dictionary<string, double>();

        /// <summary>The query of a card definition (Core 2 cards read their card tags first, as the web's cardTags): id, type, tags and every effect row.</summary>
        public static CardFaceQuery From(JObject def, string profileId = null)
        {
            var tags = (def[Core2CombatKeys.CardTags] ?? def[CombatKeys.Tags]) as JArray ?? new JArray();
            var effects = new List<CardEffectRef>();
            CollectEffects(def[CombatKeys.Effects], effects);
            var traits = new Dictionary<string, double>();
            if (def[Core2DamageKeys.TraitValues] is JObject values)
                foreach (var p in values.Properties())
                    if (p.Value.Type == JTokenType.Integer || p.Value.Type == JTokenType.Float) traits[p.Name] = p.Value.Value<double>();
            return new CardFaceQuery
            {
                CardId = (string)def[CombatKeys.Id],
                Type = (string)def[CombatKeys.Type],
                ProfileId = profileId,
                Tags = tags.Select(t => t.Type == JTokenType.String ? (string)t : (string)t[CombatKeys.Id]).Where(t => t != null).ToList(),
                Effects = effects,
                Mirrors = (string)(def[Core2CombatKeys.CombatProfile]?[Core2CombatKeys.Mirrors] ?? def[Core2CombatKeys.Mirrors]),
                TraitValues = traits,
            };
        }

        /// <summary>Every row with an op under an effect list (a conditional row's branches too); the card's own rows (the list handed in) carry their index.</summary>
        public static void CollectEffects(JToken token, List<CardEffectRef> into) => Collect(token, into, true);

        private static void Collect(JToken token, List<CardEffectRef> into, bool own)
        {
            if (token is JObject row)
            {
                if (row[CombatKeys.Op]?.Type == JTokenType.String) into.Add(Row(row, -1));
                foreach (var child in row.Properties()) Collect(child.Value, into, false);
            }
            else if (token is JArray list)
            {
                var index = 0;
                foreach (var child in list)
                {
                    if (own && child is JObject own1 && own1[CombatKeys.Op]?.Type == JTokenType.String)
                    {
                        into.Add(Row(own1, index));
                        foreach (var nested in own1.Properties()) Collect(nested.Value, into, false);
                    }
                    else Collect(child, into, false);
                    index++;
                }
            }
        }

        /// <summary>One effect row as the rail and the motifs read it (cardSideTags walk, cardBonusTags simple rows).</summary>
        private static CardEffectRef Row(JObject row, int index)
        {
            var stacks = row[CombatKeys.Stacks];
            var plain = row.Properties().All(p => p.Name == CombatKeys.Op || p.Name == CombatKeys.Target || p.Name == CombatKeys.Status || p.Name == CombatKeys.Stacks);
            var chance = row[Core2DamageKeys.Chance];
            var gated = row[CombatKeys.If] != null || (chance != null && !(chance.Type != JTokenType.Null && chance.Value<double>() >= UiMath.Percent));
            return new CardEffectRef
            {
                Op = (string)row[CombatKeys.Op],
                Target = (string)row[CombatKeys.Target],
                Status = row[CombatKeys.Status]?.Type == JTokenType.String ? (string)row[CombatKeys.Status] : null,
                Index = index,
                Stacks = ConstantOf(stacks),
                StacksFromMetric = stacks is JObject formula && (string)formula[Core2CombatKeys.F] == Core2StatusKeys.Metric,
                Plain = plain,
                Gated = gated,
            };
        }

        /// <summary>A number, or a sum of numbers (the rank composition wraps constant stacks in an add formula), else null.</summary>
        private static double? ConstantOf(JToken value)
        {
            if (value == null) return null;
            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float) return value.Value<double>();
            if (value is JObject formula && (string)formula[Core2CombatKeys.F] == FormulaOps.Add && formula[Core2CombatKeys.Args] is JArray args)
            {
                var sum = 0d;
                foreach (var arg in args)
                {
                    var part = ConstantOf(arg);
                    if (part == null) return null;
                    sum += part.Value;
                }
                return sum;
            }
            return null;
        }
    }

    /// <summary>One cutout layer of the card's art stage: a registry id and its centre, size (fractions of the 512x640 stage), opacity, rotation and scale.</summary>
    public sealed class CardCutLayer
    {
        public string Id;
        public string AssetId;
        public double X;
        public double Y;
        public double W;
        public double H;
        public double Opacity = 1;
        public double Rotation;
        public double ScaleX = 1;
        public double ScaleY = 1;
    }

    /// <summary>
    /// What the face of one card shows besides its text: the flat painting (always resolved: the fallback), the composed cutouts
    /// when the web composes this card (a background and the layers over it), the type band's sigil and the tag rail's marks.
    /// </summary>
    public sealed class CardFacePlan
    {
        public CardArtResult Painting;
        public string Code;
        public string BackgroundId;
        public List<CardCutLayer> Layers = new List<CardCutLayer>();
        public string Sigil;

        /// <summary>The second mark of a Spell that mirrors a basic card (sigil id), or null; the band's word is then <see cref="Word"/>.</summary>
        public string MirrorSigil;

        /// <summary>The sigil id whose word the type band prints (the mirrored card's, else the mark's own).</summary>
        public string Word;
        public List<string> Rail = new List<string>();

        /// <summary>The number on a rail mark (its glyph id): a status the card applies once, a stance entry's Precision, a numbered trait.</summary>
        public Dictionary<string, int> RailCounts = new Dictionary<string, int>();

        /// <summary>The line art and colour wash over the painting (element first, then statuses), as the web's card art layers draw them.</summary>
        public List<CardMotif> Motifs = new List<CardMotif>();

        public bool Composed => Code != null;
    }

    /// <summary>One motif over a card's art: its line art (motif id), tint, the element's wash colour, and the share of the art's height it is shifted by.</summary>
    public sealed class CardMotif
    {
        public string Id;
        public string Motif;
        public string Tint;
        public string Glow;
        public bool IsElement;
        public double Opacity;
        public double Offset;
    }

    /// <summary>ui/cardface.json (VP-3, D-650): the card face's cutout compositions, type band and tag rail rules, plus the sizes its text fits to.</summary>
    public sealed class CardFaceConfig
    {
        public JObject Json = new JObject();

        public static CardFaceConfig From(JObject json) => new CardFaceConfig { Json = json };

        public double DesignWidth => Json[CardFaceKeys.Design].Value<double>(CardFaceKeys.Width);
        public double DesignHeight => Json[CardFaceKeys.Design].Value<double>(CardFaceKeys.Height);

        /// <summary>The icon name (ui/art.json icon) of the inspect button.</summary>
        public string InspectIcon => Json.Value<string>(CardFaceKeys.InspectIcon);

        /// <summary>The banner icon (cardframe.{name}) a cost kind takes; a kind the face does not know takes the overflow icon.</summary>
        public string CostFrame(string kind) =>
            ArtConfig.Strings(Json[CardFaceKeys.Cost][CardFaceKeys.Frame]).TryGetValue(kind ?? string.Empty, out var frame) ? frame : OverflowFrame;

        /// <summary>The icon of a second cost of the same kind of icon (the web's energy symbol).</summary>
        public string OverflowFrame => Json[CardFaceKeys.Cost].Value<string>(CardFaceKeys.Overflow);

        /// <summary>The cost kind that wears the secondary (diamond) icon: Mana.</summary>
        public string SecondaryKind => Json[CardFaceKeys.Cost].Value<string>(CardFaceKeys.Secondary);

        /// <summary>The fit of a text layer: its largest and smallest font (design px) and its line budget.</summary>
        public CardTextFit Fit(string key)
        {
            var row = (JObject)Json[CardFaceKeys.Fit][key];
            return new CardTextFit { Max = row.Value<double>(CardFaceKeys.Max), Min = row.Value<double>(CardFaceKeys.Min), Lines = row.Value<int>(CardFaceKeys.Lines) };
        }

        /// <summary>The glyph sprite ids (sigil-{id}, tag-{id}) the face may ask for.</summary>
        public IReadOnlyList<string> SigilGlyphs => ScreenRegistry.Strings(Json[CardFaceKeys.Glyphs][CardFaceKeys.Sigil]);

        public IReadOnlyList<string> TagGlyphs => ScreenRegistry.Strings(Json[CardFaceKeys.Glyphs][CardFaceKeys.Tag]);

        /// <summary>The motif ids (motif-{id}-e and -s sprites).</summary>
        public IReadOnlyList<string> MotifGlyphs => ScreenRegistry.Strings(Json[CardFaceKeys.Glyphs][CardFaceKeys.Motif]);

        public JObject Motifs => (JObject)Json[CardFaceKeys.Motifs];

        /// <summary>The inspector's rule hints data (stance sigils, defence ranges in order, school aliases).</summary>
        public JObject Hints => (JObject)Json[CardFaceKeys.Hints];

        public IReadOnlyList<string> Ranges => ScreenRegistry.Strings(Hints[CardFaceKeys.Ranges]);

        public IReadOnlyList<string> StanceSigils => ScreenRegistry.Strings(Hints[CardFaceKeys.StanceSigils]);

        public IReadOnlyList<string> SchoolGlyphs => ScreenRegistry.Strings(Json[CardFaceKeys.Glyphs][CardFaceKeys.School]);

        /// <summary>The school mark a school takes (Earth has none of its own: it is an alteration school).</summary>
        public string SchoolMark(string school) => ArtConfig.Strings(Hints[CardFaceKeys.SchoolAlias]).TryGetValue(school, out var alias) ? alias : school;
    }

    public struct CardTextFit
    {
        public double Max;
        public double Min;
        public int Lines;
    }

    /// <summary>
    /// The web's card face as data (src/ui/cardArtComposition.js simpleCardArt + src/ui/layeredCardArt.js approvedCardArt for the cutouts,
    /// combatSigilView.js cardSigilsHtml for the type band, cardTagSymbols.js cardSideTags for the rail). Engine-free: Presentation turns
    /// the ids it returns into textures.
    /// </summary>
    public sealed class CardFaceLookup
    {
        private readonly CardFaceConfig _config;
        private readonly ArtLookup _art;

        public CardFaceLookup(CardFaceConfig config, ArtLookup art)
        {
            _config = config;
            _art = art;
        }

        public CardFaceConfig Config => _config;

        public CardFacePlan Plan(CardFaceQuery query)
        {
            var plan = new CardFacePlan { Painting = _art.Card(query.CardId, query.Type, query.Tags, query.ProfileId) };
            // A card with a painting of its own (or its equipment profile's) keeps it; the web composes the cutouts only where the library painting would be used.
            if (plan.Painting.Kind != CardArtKind.Painting && plan.Painting.Kind != CardArtKind.Profile) Compose(query, plan);
            Band(query, plan);
            Rail(query, plan);
            Motifs(query, plan);
            return plan;
        }

        // ------------------------------------------------------------------ tags

        private string Prefix(string key) => ArtConfig.Strings(_art.Config.Card[ArtKeys.Tags])[key];

        /// <summary>The tag prefix of a family ("camp:", "maneuver:", "school:") by its ArtKeys name.</summary>
        public string TagPrefix(string key) => Prefix(key);

        /// <summary>The one value of a tag family (camp:physical gives physical), or null when the card has none or several.</summary>
        private string TagValue(IReadOnlyList<string> tags, string prefix)
        {
            var values = tags.Where(t => t.StartsWith(prefix, StringComparison.Ordinal)).Select(t => t.Substring(prefix.Length)).Distinct().ToList();
            return values.Count == 1 ? values[0] : null;
        }

        /// <summary>The art element a tag value names (an element id or one of its aliases), or null.</summary>
        private string Element(string value)
        {
            if (value == null) return null;
            var card = _art.Config.Card;
            var known = ArtConfig.Ints(card[ArtKeys.ElementVariant]);
            var aliases = ArtConfig.Strings(card[ArtKeys.ElementAlias]);
            var key = value.Substring(value.LastIndexOf(UiFormats.TagSeparator[0]) + 1);
            if (known.ContainsKey(key)) return key;
            return aliases.TryGetValue(key, out var alias) ? alias : null;
        }

        // ------------------------------------------------------------------ cutouts (approvedCardArt)

        /// <summary>
        /// What simpleCardArt reads of a card: its camp (physical or spell), its maneuver (library maneuver or the skill placeholder), and its element
        /// (the element tag, else the school, else the one element its damage tags name, else Force for a spell). False for a card the web gives no art to.
        /// </summary>
        private bool Identity(CardFaceQuery query, out string maneuver, out bool magical, out string element)
        {
            maneuver = null;
            magical = false;
            element = null;
            var card = _art.Config.Card;
            var tags = query.Tags;
            var camp = TagValue(tags, Prefix(ArtKeys.Camp));
            var categories = ArtConfig.Strings(card[ArtKeys.CampCategory]);
            if (camp == null || !categories.TryGetValue(camp, out var category)) return false;
            var maneuverPrefix = Prefix(ArtKeys.Maneuver);
            var hasManeuverTag = tags.Any(t => t.StartsWith(maneuverPrefix, StringComparison.Ordinal));
            maneuver = ArtLookup.Maneuver(card, TagValue(tags, maneuverPrefix), query.Type, hasManeuverTag);
            var rail = _config.Json[CardFaceKeys.Rail];
            // Evade is an authored payload (a dodge roll), whatever the card's maneuver tag says.
            if (query.Effects.Any(e => e.Op == rail.Value<string>(CardFaceKeys.EvadeOp))) maneuver = rail.Value<string>(CardFaceKeys.EvadeManeuver);
            var skill = card.Value<string>(ArtKeys.SkillManeuver);
            var utilityTypes = ((JObject)rail[CardFaceKeys.Category]).Properties().Select(p => p.Name).ToList();
            if (maneuver == null && !hasManeuverTag && utilityTypes.Contains(query.Type)) maneuver = skill;
            var library = ((JArray)card[ArtKeys.LibraryManeuvers]).Select(t => (string)t).ToList();
            if (maneuver == null || (maneuver != skill && !library.Contains(maneuver))) return false;
            magical = category == card.Value<string>(ArtKeys.MagicalCategory);
            element = Element(TagValue(tags, Prefix(ArtKeys.Element))) ?? Element(TagValue(tags, Prefix(ArtKeys.School))) ?? DamageElement(query)
                ?? (magical ? card.Value<string>(ArtKeys.DefaultElement) : null);
            return true;
        }

        /// <summary>The element of the card's damage when its damage tags name exactly one type (even one elemental type beside a physical one is mixed damage), else null.</summary>
        private string DamageElement(CardFaceQuery query)
        {
            var types = query.Tags.Where(t => t.StartsWith(DamagePrefix, StringComparison.Ordinal)).Select(t => t.Substring(DamagePrefix.Length)).Distinct().ToList();
            return types.Count == 1 ? Element(types[0]) : null;
        }

        private string DamagePrefix => _config.Json[CardFaceKeys.Prefixes].Value<string>(CardFaceKeys.Damage);

        private void Compose(CardFaceQuery query, CardFacePlan plan)
        {
            if (!Identity(query, out var maneuver, out var magical, out var element)) return;
            var card = _art.Config.Card;
            var skill = card.Value<string>(ArtKeys.SkillManeuver);
            var selection = (JObject)_config.Json[CardFaceKeys.Cutouts][CardFaceKeys.Selection];
            var choice = Choose(selection, query, maneuver, skill, magical, element);
            if (choice.Code == null) return;
            var compositions = (JObject)_config.Json[CardFaceKeys.Cutouts][CardFaceKeys.Compositions];
            if (!(compositions[choice.Code] is JArray rows)) return;

            var background = _config.Json[CardFaceKeys.Cutouts].Value<string>(CardFaceKeys.Background);
            var layers = new List<CardCutLayer>();
            string backgroundId = null;
            foreach (var row in rows.OfType<JObject>())
            {
                var id = row.Value<string>(CardFaceKeys.Id);
                if (choice.Hidden.Contains(id)) continue;
                var assetId = _art.Find(ArtKinds.Cutout, new StringArgs().Add(ArtPlaceholders.Name, row.Value<string>(CardFaceKeys.Asset)));
                // A missing cutout must not leave a floating glow or an empty background: the whole composition gives way to the painting.
                if (assetId == null) return;
                if (id == background) { backgroundId = assetId; continue; }
                layers.Add(new CardCutLayer
                {
                    Id = id,
                    AssetId = assetId,
                    X = row.Value<double>(CardFaceKeys.X),
                    Y = row.Value<double>(CardFaceKeys.Y),
                    W = row.Value<double>(CardFaceKeys.W),
                    H = row.Value<double>(CardFaceKeys.H),
                    Opacity = row.Value<double>(CardFaceKeys.Opacity),
                    Rotation = row.Value<double?>(CardFaceKeys.Rotation) ?? 0,
                    ScaleX = row.Value<double?>(CardFaceKeys.ScaleX) ?? 1,
                    ScaleY = row.Value<double?>(CardFaceKeys.ScaleY) ?? 1,
                });
            }
            if (backgroundId == null) return;
            plan.Code = choice.Code;
            plan.BackgroundId = backgroundId;
            plan.Layers = layers;
        }

        private struct Choice
        {
            public string Code;
            public HashSet<string> Hidden;
        }

        private Choice Choose(JObject selection, CardFaceQuery query, string maneuver, string skill, bool magical, string element)
        {
            var none = new Choice { Hidden = new HashSet<string>(StringComparer.Ordinal) };
            if (maneuver == skill)
            {
                foreach (var row in ((JArray)selection[CardFaceKeys.Utility]).OfType<JObject>())
                {
                    var target = row.Value<string>(CardFaceKeys.Target);
                    var ops = ScreenRegistry.Strings(row[CardFaceKeys.Operations]);
                    var tagList = ScreenRegistry.Strings(row[CardFaceKeys.Tags]);
                    var has = query.Effects.Any(e => ops.Contains(e.Op) && (target == null || e.Target == target));
                    if (has || tagList.Any(t => query.Tags.Contains(t))) return new Choice { Code = row.Value<string>(CardFaceKeys.Code), Hidden = none.Hidden };
                }
                return magical ? new Choice { Code = selection[CardFaceKeys.SpellUtility].Value<string>(CardFaceKeys.Code), Hidden = none.Hidden } : none;
            }
            var melee = ArtConfig.Ints(selection[CardFaceKeys.Melee]);
            if (magical && melee.TryGetValue(maneuver, out var strike))
            {
                var attack = _art.Config.Card.Value<string>(ArtKeys.AttackType);
                var ranged = maneuver == attack && ScreenRegistry.Strings(selection[CardFaceKeys.ProjectileTags]).Any(t => query.Tags.Contains(t));
                var projectiles = ArtConfig.Ints(selection[CardFaceKeys.Projectiles]);
                int digit;
                if (ranged) digit = element != null && projectiles.TryGetValue(element, out var shot) ? shot : projectiles[_art.Config.Card.Value<string>(ArtKeys.DefaultElement)];
                else
                {
                    var palettes = ArtConfig.Ints(selection[CardFaceKeys.MeleePalettes]);
                    var mint = ScreenRegistry.Strings(selection[CardFaceKeys.MintElements]).Contains(element);
                    digit = strike + palettes[mint ? CardFaceKeys.Mint : CardFaceKeys.Blue];
                }
                var templates = (JObject)selection[CardFaceKeys.MagicalCode];
                var template = templates.Value<string>(ranged ? CardFaceKeys.Projectile : CardFaceKeys.Melee);
                return new Choice { Code = StringTable.Fill(template, new StringArgs().Add(ArtPlaceholders.Digit, digit)), Hidden = none.Hidden };
            }
            if (!magical && ((JObject)selection[CardFaceKeys.Physical])[maneuver] is JObject physical)
                return new Choice { Code = physical.Value<string>(CardFaceKeys.Code), Hidden = new HashSet<string>(ScreenRegistry.Strings(physical[CardFaceKeys.HideLayers]), StringComparer.Ordinal) };
            return none;
        }

        // ------------------------------------------------------------------ type band

        /// <summary>The sigil id of the type band: a Power, a Spell, then the card's maneuver tag, else its type, else the fallback (combatCardType).</summary>
        private string Sigil(CardFaceQuery query)
        {
            var band = (JObject)_config.Json[CardFaceKeys.Band];
            var spell = (JObject)band[CardFaceKeys.Spell];
            var power = (JObject)band[CardFaceKeys.Power];
            var tags = query.Tags;
            var byType = ArtConfig.Strings(band[CardFaceKeys.Type]);
            var isPower = ScreenRegistry.Strings(power[CardFaceKeys.Types]).Contains(query.Type) || ScreenRegistry.Strings(power[CardFaceKeys.Tags]).Any(tags.Contains);
            if (isPower && query.Type != null && byType.TryGetValue(query.Type, out var powered)) return powered;
            if (IsSpell(query, spell)) return spell.Value<string>(CardFaceKeys.Id);
            var maneuver = TagValue(tags, Prefix(ArtKeys.Maneuver));
            var byManeuver = ArtConfig.Strings(band[CardFaceKeys.Maneuver]);
            if (maneuver != null && byManeuver.TryGetValue(maneuver, out var sigil)) return sigil;
            if (query.Type != null && byType.TryGetValue(query.Type, out var typed)) return typed;
            return band.Value<string>(CardFaceKeys.Fallback);
        }

        /// <summary>cardPreparationCategory's Spell: named a spell by tag or type, or an attack or skill in the spell camp.</summary>
        private bool IsSpell(CardFaceQuery query, JObject spell)
        {
            if (ScreenRegistry.Strings(spell[CardFaceKeys.Tags]).Any(query.Tags.Contains) || ScreenRegistry.Strings(spell[CardFaceKeys.Types]).Contains(query.Type)) return true;
            return TagValue(query.Tags, Prefix(ArtKeys.Camp)) == spell.Value<string>(CardFaceKeys.Id)
                && (ScreenRegistry.Strings(spell[CardFaceKeys.CampTypes]).Contains(query.Type) || ScreenRegistry.Strings(spell[CardFaceKeys.CampTags]).Any(query.Tags.Contains));
        }

        /// <summary>The band's marks and word: the primary sigil, and for a Spell that mirrors a basic card that card's mark (Technique wears the Skill mark under its own word).</summary>
        private void Band(CardFaceQuery query, CardFacePlan plan)
        {
            var band = (JObject)_config.Json[CardFaceKeys.Band];
            plan.Sigil = Sigil(query);
            plan.Word = plan.Sigil;
            if (query.Mirrors == null || plan.Sigil != band[CardFaceKeys.Spell].Value<string>(CardFaceKeys.Id)) return;
            var mirror = (JObject)band[CardFaceKeys.Mirror];
            var technique = (JObject)mirror[CardFaceKeys.Technique];
            if (query.Mirrors == technique.Value<string>(CardFaceKeys.Id))
            {
                plan.MirrorSigil = technique.Value<string>(CardFaceKeys.Sigil);
                plan.Word = technique.Value<string>(CardFaceKeys.Word);
            }
            else if (_config.SigilGlyphs.Contains(query.Mirrors) && !ScreenRegistry.Strings(mirror[CardFaceKeys.Exclude]).Contains(query.Mirrors))
                plan.MirrorSigil = plan.Word = query.Mirrors;
        }

        // ------------------------------------------------------------------ tag rail

        /// <summary>The glyph id of a tag key: its damage alias (shapeFor's own cases), its alias, else itself when the rail has a mark of that name, else the fallback.</summary>
        public string Glyph(string key)
        {
            var rail = (JObject)_config.Json[CardFaceKeys.Rail];
            var damage = ArtConfig.Strings(rail[CardFaceKeys.DamageAlias]);
            var aliases = ArtConfig.Strings(rail[CardFaceKeys.Alias]);
            var resolved = key != null && damage.TryGetValue(key, out var own) ? own : key != null && aliases.TryGetValue(key, out var alias) ? alias : key;
            return resolved != null && TagGlyphs.Contains(resolved) ? resolved : rail.Value<string>(CardFaceKeys.Fallback);
        }

        private IReadOnlyList<string> TagGlyphs => _config.TagGlyphs;

        /// <summary>
        /// The marks down the card's right edge, in the web's order and cap (cardSideTags): its category (a Power, a Skill) or its camp when it deals
        /// damage or is a spell, its damage types (else its evade, else its element or school), the counted benefits (a self Precision or Evade grant
        /// and a stance entry's) with a Smash after them, its symbol traits, then the statuses it applies (a status applied once, plainly, carries its
        /// number). The identity marks give way so the benefits always show. <see cref="CardFacePlan.RailCounts"/> holds the numbers.
        /// </summary>
        private void Rail(CardFaceQuery query, CardFacePlan plan)
        {
            var rail = (JObject)_config.Json[CardFaceKeys.Rail];
            var max = rail.Value<int>(CardFaceKeys.Max);
            var tags = query.Tags;
            var marks = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            void Add(string key, int? count = null)
            {
                if (key == null) return;
                var glyph = Glyph(key);
                if (marks.Count >= max || marks.Contains(glyph)) return;
                marks.Add(glyph);
                if (count.HasValue) counts[glyph] = count.Value;
            }

            var camp = TagValue(tags, Prefix(ArtKeys.Camp));
            var physicalCamp = rail.Value<string>(CardFaceKeys.CampWithDamage);
            var categories = ArtConfig.Strings(rail[CardFaceKeys.Category]);
            var campMarks = ArtConfig.Strings(rail[CardFaceKeys.Camp]);
            var damaging = query.Effects.Any(e => e.Op == rail.Value<string>(CardFaceKeys.DamageOp));
            var excluded = ArtConfig.Strings(rail[CardFaceKeys.CategoryExcludeCamp]);
            if (query.Type != null && categories.TryGetValue(query.Type, out var category) && !(excluded.TryGetValue(query.Type, out var barred) && camp == barred))
                Add(category);
            else if (camp != null && campMarks.TryGetValue(camp, out var campMark) && (camp != physicalCamp || damaging))
                Add(campMark);

            var maneuver = TagValue(tags, Prefix(ArtKeys.Maneuver));
            var evadeManeuver = rail.Value<string>(CardFaceKeys.EvadeManeuver);
            var evade = maneuver == evadeManeuver || query.Effects.Any(e => e.Op == rail.Value<string>(CardFaceKeys.EvadeOp));
            var damageTypes = damaging ? tags.Where(t => t.StartsWith(DamagePrefix, StringComparison.Ordinal)).Select(t => t.Substring(DamagePrefix.Length)).Distinct().ToList() : new List<string>();
            if (damageTypes.Count > 0) { foreach (var type in damageTypes) Add(type); }
            else if (evade) Add(evadeManeuver);
            else
            {
                var element = TagValue(tags, Prefix(ArtKeys.Element));
                var school = TagValue(tags, Prefix(ArtKeys.School));
                Add(Element(element) != null ? element : school);
            }

            // The counted benefits and a Smash follow the identity marks, which give way to them (cardSideTags splices the identity to 5 minus the benefits).
            var benefits = Benefits(query, rail);
            var smash = damaging && maneuver != null && maneuver == rail.Value<string>(CardFaceKeys.SmashManeuver) ? ArtConfig.Strings(rail[CardFaceKeys.Maneuver])[maneuver] : null;
            var room = Math.Max(0, max - benefits.Count - (smash != null ? 1 : 0));
            var evadeGlyph = Glyph(evadeManeuver);
            if (benefits.ContainsKey(((JObject)rail[CardFaceKeys.Benefit]).Value<string>(CardFaceKeys.Evade))) marks.Remove(evadeGlyph);
            while (marks.Count > room) { counts.Remove(marks[marks.Count - 1]); marks.RemoveAt(marks.Count - 1); }
            foreach (var benefit in benefits) Add(benefit.Key, benefit.Value);
            Add(smash);

            var traitPrefix = _config.Json[CardFaceKeys.Prefixes].Value<string>(CardFaceKeys.Trait);
            var symbolTraits = ScreenRegistry.Strings(rail[CardFaceKeys.Traits]);
            foreach (var trait in tags.Where(t => t.StartsWith(traitPrefix, StringComparison.Ordinal)).Select(t => t.Substring(traitPrefix.Length)).Distinct())
                if (symbolTraits.Contains(trait))
                    Add(trait, query.TraitValues.TryGetValue(trait, out var value) ? WholeCount(value > 0d ? value : (double?)null) : null);

            var statusOps = ScreenRegistry.Strings(rail[CardFaceKeys.StatusOps]);
            var known = (JObject)_config.Motifs[CardFaceKeys.Statuses];
            var rows = query.Effects.Where(e => e.Status != null && statusOps.Contains(e.Op) && known.ContainsKey(e.Status)).ToList();
            var applyOp = ((JObject)rail[CardFaceKeys.Benefit]).Value<string>(CardFaceKeys.Op);
            foreach (var status in rows.Select(e => e.Status).Distinct().Where(id => !benefits.ContainsKey(id)).Take(rail.Value<int>(CardFaceKeys.StatusMax)))
            {
                var applications = rows.Where(e => e.Status == status).ToList();
                var only = applications.Count == 1 ? applications[0] : null;
                // A count stands for the one direct, ungated application whose stacks are a number (the preview's, else the authored).
                var stacks = only != null && only.Index >= 0 && only.Op == applyOp && !only.Gated ? only.Live ?? only.Stacks : null;
                Add(status, WholeCount(stacks));
            }
            plan.Rail = marks;
            plan.RailCounts = counts;
        }

        private static int? WholeCount(double? value) => value.HasValue && value.Value >= 0d && Math.Abs(value.Value % 1d) < double.Epsilon ? (int)value.Value : (int?)null;

        /// <summary>
        /// The counted benefits (cardBonusTags): a plain self Precision or Evade grant with a whole number of stacks, and a stance entry's Precision and
        /// Evade. Keyed by status id in the web's order (the effects, then the entries); the count is null when it is not a promise (several grants,
        /// a stat-driven Evade the body already says).
        /// </summary>
        private Dictionary<string, int?> Benefits(CardFaceQuery query, JObject rail)
        {
            var benefit = (JObject)rail[CardFaceKeys.Benefit];
            var op = benefit.Value<string>(CardFaceKeys.Op);
            var target = benefit.Value<string>(CardFaceKeys.Target);
            var statuses = ScreenRegistry.Strings(benefit[CardFaceKeys.Statuses]);
            var precision = benefit.Value<string>(CardFaceKeys.Precision);
            var evade = benefit.Value<string>(CardFaceKeys.Evade);
            var grants = new List<KeyValuePair<string, int?>>();
            foreach (var effect in query.Effects.Where(e => e.Index >= 0 && e.Op == op && e.Target == target && e.Status != null && statuses.Contains(e.Status)))
            {
                var whole = WholeCount(effect.Live ?? effect.Stacks);
                if (effect.Plain && (whole.HasValue || effect.StacksFromMetric)) grants.Add(new KeyValuePair<string, int?>(effect.Status, whole));
            }
            var selfEvade = query.Effects.Any(e => e.Op == op && e.Target == target && e.Status == evade);
            if (query.EntryPrecision > 0d) grants.Add(new KeyValuePair<string, int?>(precision, WholeCount(query.EntryPrecision)));
            if (query.EntryEvade > 0d && !selfEvade) grants.Add(new KeyValuePair<string, int?>(evade, WholeCount(query.EntryEvade)));
            // evadeGrantOnlyCard: a card that is only its stat-driven Evade grant says the amount in its body, so its tag has no number.
            var evadeOnly = query.Effects.Count == 1 && query.Effects[0].StacksFromMetric && query.Effects[0].Status == evade;
            var result = new Dictionary<string, int?>(StringComparer.Ordinal);
            foreach (var group in grants.GroupBy(g => g.Key))
                result[group.Key] = group.Count() == 1 && !(evadeOnly && group.Key == evade) ? group.First().Value : null;
            return result;
        }

        // ------------------------------------------------------------------ motifs (cardArtLayersHtml)

        /// <summary>
        /// The line art and washes over a library or composed painting: the card's element (its palette and motif), then up to two (three without an
        /// element) of the statuses it applies in alphabetical order, each in its registry tint or its motif's colour. Only a card the web gives
        /// "simple" art to carries them: a camp, a library maneuver, and (for a skill) a composed cutout stage.
        /// </summary>
        private void Motifs(CardFaceQuery query, CardFacePlan plan)
        {
            if (plan.Painting.Kind == CardArtKind.Painting || plan.Painting.Kind == CardArtKind.Profile) return;
            if (!Identity(query, out var maneuver, out _, out var element)) return;
            if (maneuver == _art.Config.Card.Value<string>(ArtKeys.SkillManeuver) && !plan.Composed) return;
            var motifs = _config.Motifs;
            var themes = (JObject)motifs[CardFaceKeys.Elements];
            var status = (JObject)motifs[CardFaceKeys.Status];
            var theme = element != null ? themes[element] as JObject : null;
            if (theme != null)
            {
                var spec = (JObject)motifs[CardFaceKeys.Element];
                plan.Motifs.Add(new CardMotif
                {
                    Id = element, IsElement = true, Motif = theme.Value<string>(CardFaceKeys.Motif), Tint = theme.Value<string>(CardFaceKeys.Color),
                    Glow = theme.Value<string>(CardFaceKeys.Glow), Opacity = spec.Value<double>(CardFaceKeys.Opacity),
                });
            }
            var registry = (JObject)motifs[CardFaceKeys.Statuses];
            var statusOps = ScreenRegistry.Strings(((JObject)_config.Json[CardFaceKeys.Rail])[CardFaceKeys.StatusOps]);
            var ids = query.Tags.Where(t => t.StartsWith(StatusPrefix, StringComparison.Ordinal)).Select(t => t.Substring(StatusPrefix.Length)).ToList();
            ids.AddRange(query.Effects.Where(e => e.Status != null && statusOps.Contains(e.Op)).Select(e => e.Status));
            var motifOf = ArtConfig.Strings(motifs[CardFaceKeys.StatusMotif]);
            var colorOf = ArtConfig.Strings(motifs[CardFaceKeys.StatusColor]);
            var take = status.Value<int>(CardFaceKeys.Max) - (theme != null ? 1 : 0);
            foreach (var id in ids.Distinct().OrderBy(i => i, StringComparer.Ordinal).Where(registry.ContainsKey).Take(take))
            {
                var motif = motifOf.TryGetValue(id, out var m) ? m : motifs.Value<string>(CardFaceKeys.StatusDefault);
                var tint = registry.Value<string>(id);
                plan.Motifs.Add(new CardMotif
                {
                    Id = id, Motif = motif, Tint = string.IsNullOrEmpty(tint) ? colorOf[motif] : tint, Opacity = status.Value<double>(CardFaceKeys.Opacity),
                    Offset = plan.Motifs.Count >= status.Value<int>(CardFaceKeys.OffsetFrom) ? -status.Value<double>(CardFaceKeys.Offset) / UiMath.Percent : 0d,
                });
            }
        }

        private string StatusPrefix => _config.Json[CardFaceKeys.Prefixes].Value<string>(CardFaceKeys.Status);
    }
}
