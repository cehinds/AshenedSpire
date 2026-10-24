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
    }

    /// <summary>What a card face is drawn from: the card's id, type, tag ids and (nested) effect rows. Ids and tags are the content's own.</summary>
    public sealed class CardFaceQuery
    {
        public string CardId;
        public string Type;
        public string ProfileId;
        public IReadOnlyList<string> Tags = Array.Empty<string>();
        public IReadOnlyList<CardEffectRef> Effects = Array.Empty<CardEffectRef>();

        /// <summary>The query of a card definition (Core 2 cards read their card tags first, as the web's cardTags): id, type, tags and every effect row.</summary>
        public static CardFaceQuery From(JObject def, string profileId = null)
        {
            var tags = (def[Core2CombatKeys.CardTags] ?? def[CombatKeys.Tags]) as JArray ?? new JArray();
            var effects = new List<CardEffectRef>();
            CollectEffects(def[CombatKeys.Effects], effects);
            return new CardFaceQuery
            {
                CardId = (string)def[CombatKeys.Id],
                Type = (string)def[CombatKeys.Type],
                ProfileId = profileId,
                Tags = tags.Select(t => t.Type == JTokenType.String ? (string)t : (string)t[CombatKeys.Id]).Where(t => t != null).ToList(),
                Effects = effects,
            };
        }

        /// <summary>Every row with an op under an effect list (a conditional row's branches too).</summary>
        public static void CollectEffects(JToken token, List<CardEffectRef> into)
        {
            if (token is JObject row)
            {
                if (row[CombatKeys.Op]?.Type == JTokenType.String)
                    into.Add(new CardEffectRef { Op = (string)row[CombatKeys.Op], Target = (string)row[CombatKeys.Target], Status = row[CombatKeys.Status]?.Type == JTokenType.String ? (string)row[CombatKeys.Status] : null });
                foreach (var child in row.Properties()) CollectEffects(child.Value, into);
            }
            else if (token is JArray list)
                foreach (var child in list) CollectEffects(child, into);
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
        public List<string> Rail = new List<string>();

        public bool Composed => Code != null;
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
            plan.Sigil = Sigil(query);
            plan.Rail = Rail(query);
            return plan;
        }

        // ------------------------------------------------------------------ tags

        private string Prefix(string key) => ArtConfig.Strings(_art.Config.Card[ArtKeys.Tags])[key];

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

        private void Compose(CardFaceQuery query, CardFacePlan plan)
        {
            var card = _art.Config.Card;
            var tags = query.Tags;
            var camp = TagValue(tags, Prefix(ArtKeys.Camp));
            var categories = ArtConfig.Strings(card[ArtKeys.CampCategory]);
            if (camp == null || !categories.TryGetValue(camp, out var category)) return;
            var maneuverPrefix = Prefix(ArtKeys.Maneuver);
            var hasManeuverTag = tags.Any(t => t.StartsWith(maneuverPrefix, StringComparison.Ordinal));
            var maneuver = ArtLookup.Maneuver(card, TagValue(tags, maneuverPrefix), query.Type, hasManeuverTag);
            var rail = _config.Json[CardFaceKeys.Rail];
            // Evade is an authored payload (a dodge roll), whatever the card's maneuver tag says.
            if (query.Effects.Any(e => e.Op == rail.Value<string>(CardFaceKeys.EvadeOp))) maneuver = rail.Value<string>(CardFaceKeys.EvadeManeuver);
            var skill = card.Value<string>(ArtKeys.SkillManeuver);
            var utilityTypes = ((JObject)rail[CardFaceKeys.Category]).Properties().Select(p => p.Name).ToList();
            if (maneuver == null && !hasManeuverTag && utilityTypes.Contains(query.Type)) maneuver = skill;
            var library = ((JArray)card[ArtKeys.LibraryManeuvers]).Select(t => (string)t).ToList();
            if (maneuver == null || (maneuver != skill && !library.Contains(maneuver))) return;

            var magical = category == card.Value<string>(ArtKeys.MagicalCategory);
            var element = Element(TagValue(tags, Prefix(ArtKeys.Element))) ?? Element(TagValue(tags, Prefix(ArtKeys.School)))
                ?? (magical ? card.Value<string>(ArtKeys.DefaultElement) : null);
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

        /// <summary>The sigil id of the type band: by the card's maneuver tag, else by its type, else the fallback (combatCardType).</summary>
        private string Sigil(CardFaceQuery query)
        {
            var band = (JObject)_config.Json[CardFaceKeys.Band];
            var maneuver = TagValue(query.Tags, Prefix(ArtKeys.Maneuver));
            var byManeuver = ArtConfig.Strings(band[CardFaceKeys.Maneuver]);
            if (maneuver != null && byManeuver.TryGetValue(maneuver, out var sigil)) return sigil;
            var attacks = ArtConfig.Strings(band[CardFaceKeys.AttackManeuver]);
            if (maneuver != null && query.Type == _art.Config.Card.Value<string>(ArtKeys.AttackType) && attacks.TryGetValue(maneuver, out var attackSigil)) return attackSigil;
            var byType = ArtConfig.Strings(band[CardFaceKeys.Type]);
            if (query.Type != null && byType.TryGetValue(query.Type, out var typed)) return typed;
            return band.Value<string>(CardFaceKeys.Fallback);
        }

        // ------------------------------------------------------------------ tag rail

        /// <summary>The glyph id of a tag key: its alias, else itself when the rail has a mark of that name, else the fallback (shapeFor).</summary>
        public string Glyph(string key)
        {
            var rail = (JObject)_config.Json[CardFaceKeys.Rail];
            var aliases = ArtConfig.Strings(rail[CardFaceKeys.Alias]);
            var resolved = key != null && aliases.TryGetValue(key, out var alias) ? alias : key;
            return resolved != null && TagGlyphs.Contains(resolved) ? resolved : rail.Value<string>(CardFaceKeys.Fallback);
        }

        private IReadOnlyList<string> TagGlyphs => _config.TagGlyphs;

        /// <summary>
        /// The marks down the card's right edge, in the web's order and cap: its category (a Power, a Skill) or its camp when it deals damage or
        /// is a spell, its evade or element/school, a Smash, its symbol traits, then the statuses it applies. Damage-type tags and
        /// counted bonus tags need data the Unity card does not carry, so they are not drawn.
        /// </summary>
        private List<string> Rail(CardFaceQuery query)
        {
            var rail = (JObject)_config.Json[CardFaceKeys.Rail];
            var max = rail.Value<int>(CardFaceKeys.Max);
            var tags = query.Tags;
            var marks = new List<string>();
            void Add(string key)
            {
                if (key == null) return;
                var glyph = Glyph(key);
                if (marks.Count < max && !marks.Contains(glyph)) marks.Add(glyph);
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

            var maneuvers = ArtConfig.Strings(rail[CardFaceKeys.Maneuver]);
            var maneuver = TagValue(tags, Prefix(ArtKeys.Maneuver));
            var evade = maneuver == rail.Value<string>(CardFaceKeys.EvadeManeuver) || query.Effects.Any(e => e.Op == rail.Value<string>(CardFaceKeys.EvadeOp));
            if (evade) Add(rail.Value<string>(CardFaceKeys.EvadeManeuver));
            else
            {
                var element = TagValue(tags, Prefix(ArtKeys.Element));
                var school = TagValue(tags, Prefix(ArtKeys.School));
                Add(Element(element) != null ? element : school);
            }
            if (maneuver != null && maneuvers.TryGetValue(maneuver, out var benefit) && maneuver != rail.Value<string>(CardFaceKeys.EvadeManeuver)) Add(benefit);

            var traitPrefix = _config.Json[CardFaceKeys.Prefixes].Value<string>(CardFaceKeys.Trait);
            var symbolTraits = ScreenRegistry.Strings(rail[CardFaceKeys.Traits]);
            foreach (var trait in tags.Where(t => t.StartsWith(traitPrefix, StringComparison.Ordinal)).Select(t => t.Substring(traitPrefix.Length)).Distinct())
                if (symbolTraits.Contains(trait)) Add(trait);

            var statusOps = ScreenRegistry.Strings(rail[CardFaceKeys.StatusOps]);
            var statuses = query.Effects.Where(e => e.Status != null && statusOps.Contains(e.Op)).Select(e => e.Status).Distinct().Take(rail.Value<int>(CardFaceKeys.StatusMax));
            foreach (var status in statuses) Add(status);
            return marks;
        }
    }
}
