using System;
using System.Collections.Generic;
using System.Globalization;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>One cost pip: its kind (ui/components.json card.costKinds) and display text (a number or X).</summary>
    public sealed class CardCostData
    {
        public string Kind;
        public string Text;
    }

    /// <summary>Display-ready card values (strings resolved, numbers already previewed by the Domain).</summary>
    public sealed class CardViewData
    {
        public string Name;
        public string TypeLine;
        public string Text;
        public string Rarity;
        public string Keywords;
        public string RefusalText;
        public string LentText;

        /// <summary>A painting to use as it is; the face otherwise takes the art its <see cref="Face"/> plans.</summary>
        public Texture2D Art;

        public bool Affordable = true;
        public bool Upgraded;
        public List<CardCostData> Costs = new List<CardCostData>();

        /// <summary>What the card's face is drawn from (id, type, tags, effects): its art, type band and tag rail. Null draws the frame and the text only.</summary>
        public CardFaceQuery Face;

        /// <summary>The rank badge text ("Rank 2"), or empty for a card without one.</summary>
        public string Rank;
    }

    /// <summary>
    /// CardView (VP-3, D-650): the web's illustrated card (src/ui/components/illustratedCard.js) as a kit element. The face is laid out in the
    /// web template's 360x540 design space (Card.uss, tokens group face) and scaled to the element's box: the frame layers (cardframe.*
    /// textures), the card's art (the composed cutouts of ui/cardface.json when the web composes this card, else its painting), the cost
    /// banner, the name and rules text fitted to their boxes, the type band, the tag rail and the rank gem. Affordability keeps the refusal
    /// text as its non-colour cue; lent-by and upgraded are marked. The element is as wide as its USS says (the hand, a reward pick, the shop).
    /// </summary>
    [UxmlElement]
    public partial class CardView : VisualElement
    {
        private static readonly Dictionary<string, Sprite> Glyphs = new Dictionary<string, Sprite>(StringComparer.Ordinal);

        private readonly VisualElement _face;
        private readonly VisualElement _costs;
        private readonly LocLabel _name;
        private readonly VisualElement _art;
        private readonly VisualElement _stage;
        private readonly LocLabel _type;
        private readonly LocLabel _text;
        private readonly LocLabel _refusal;
        private readonly LocLabel _lent;
        private readonly VisualElement _sigil;
        private readonly VisualElement _rail;
        private readonly LocLabel _rankText;
        private readonly VisualElement _info;
        private string _rarityClass;
        private float _scale;
        private string _fitted;

        public CardView()
        {
            AddToClassList(nameof(CardView).ToLowerInvariant());
            focusable = true;
            UiDom.CloneTemplate(this, UiResources.KitCard);
            _face = this.Q(UiNames.CardFace);
            _costs = this.Q(UiNames.CardCosts);
            _name = this.Q<LocLabel>(UiNames.CardName);
            _art = this.Q(UiNames.CardArt);
            _stage = this.Q(UiNames.CardStage);
            _type = this.Q<LocLabel>(UiNames.CardType);
            _text = this.Q<LocLabel>(UiNames.CardText);
            _refusal = this.Q<LocLabel>(UiNames.CardRefusal);
            _lent = this.Q<LocLabel>(UiNames.CardLent);
            _sigil = this.Q(UiNames.CardSigil);
            _rail = this.Q(UiNames.CardRail);
            _rankText = this.Q<LocLabel>(UiNames.CardRankText);
            _info = this.Q(UiNames.CardInfo);
            _info?.RegisterCallback<PointerUpEvent>(e =>
            {
                Inspect?.Invoke(this);
                e.StopPropagation();
            });
            RegisterCallback<GeometryChangedEvent>(_ => Rescale());
            ApplyFrame();
        }

        /// <summary>The UI services the face reads its art and glyphs from (set when the UI context is created).</summary>
        public static UiContext Source { get; set; }

        public CardViewData Data { get; private set; }

        /// <summary>Raised when the inspect button (shown with <see cref="ShowInspect"/>) is pressed.</summary>
        public event Action<CardView> Inspect;

        /// <summary>Shows the round inspect button above the card.</summary>
        public bool ShowInspect
        {
            get => ClassListContains(UiClasses.CardInspectable);
            set => EnableInClassList(UiClasses.CardInspectable, value);
        }

        private static StyleBackground Background(Texture2D texture) => texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);

        private static StyleBackground Background(Sprite sprite) => sprite != null ? new StyleBackground(sprite) : new StyleBackground(StyleKeyword.None);

        /// <summary>A glyph sprite of Resources/UI/CardFace (path format of UiFormats), cached.</summary>
        private static Sprite Glyph(string format, string id)
        {
            var path = string.Format(CultureInfo.InvariantCulture, format, id);
            if (!Glyphs.TryGetValue(path, out var sprite)) Glyphs[path] = sprite = Resources.Load<Sprite>(path);
            return sprite;
        }

        /// <summary>Gives each frame layer (an element of class card-layer, named for its cardframe texture) its texture, and the inspect button its icon.</summary>
        private void ApplyFrame()
        {
            var ui = Source;
            if (ui?.Data?.Art == null) return;
            foreach (var layer in this.Query<VisualElement>(className: UiClasses.CardLayer).ToList()) FrameTexture(layer, layer.name);
            if (_info != null) _info.style.backgroundImage = Background(ui.Icon(ui.Data.CardFace.Config.InspectIcon));
        }

        private static void FrameTexture(VisualElement layer, string frame)
        {
            var ui = Source;
            if (ui?.Data?.Art == null) return;
            layer.style.backgroundImage = Background(ui.Texture(ui.Data.Art.Frame(frame)));
        }

        /// <summary>Scales the face to the card's box (the face is 360 design px wide) and refits the text the new size may change.</summary>
        private void Rescale()
        {
            var config = Source?.Data?.CardFace?.Config;
            var width = resolvedStyle.width;
            if (_face == null || config == null || float.IsNaN(width) || width <= 0f) return;
            var scale = width / (float)config.DesignWidth;
            if (!Mathf.Approximately(scale, _scale))
            {
                _scale = scale;
                _face.style.scale = new Scale(new Vector3(scale, scale, 1f));
            }
            EnableInClassList(UiClasses.CardSized, true);
            FitText();
        }

        public void Bind(CardViewData data)
        {
            Data = data;
            var ui = Source;
            var plan = data.Face != null ? ui?.Data?.CardFace?.Plan(data.Face) : null;
            _name?.SetResolved(data.Name);
            _text?.SetResolved(data.Text);
            _refusal?.SetResolved(data.RefusalText);
            UiDom.Show(_refusal, !data.Affordable && !string.IsNullOrEmpty(data.RefusalText));
            _lent?.SetResolved(data.LentText);
            UiDom.Show(_lent, !string.IsNullOrEmpty(data.LentText));
            BindArt(data, plan);
            BindBand(data, plan);
            BindRail(plan);
            BindCosts(data);
            _rankText?.SetResolved(data.Rank);
            EnableInClassList(UiClasses.CardRanked, !string.IsNullOrEmpty(data.Rank));
            if (_rarityClass != null) RemoveFromClassList(_rarityClass);
            _rarityClass = string.IsNullOrEmpty(data.Rarity) ? null : UiClasses.CardRarityPrefix + data.Rarity;
            if (_rarityClass != null) AddToClassList(_rarityClass);
            EnableInClassList(UiClasses.CardUnaffordable, !data.Affordable);
            EnableInClassList(UiClasses.CardUpgraded, data.Upgraded);
            _fitted = null;
            FitText();
        }

        // ------------------------------------------------------------------ art

        /// <summary>The painting (the composition's background when the web composes the card) and the cutout layers standing on the stage.</summary>
        private void BindArt(CardViewData data, CardFacePlan plan)
        {
            var ui = Source;
            _stage?.Clear();
            var painting = data.Art;
            if (painting == null && plan != null && ui != null) painting = ui.Texture(plan.Composed ? plan.BackgroundId : plan.Painting.AssetId);
            if (_art != null) _art.style.backgroundImage = Background(painting);
            if (plan == null || !plan.Composed || _stage == null || ui == null) return;
            foreach (var layer in plan.Layers)
            {
                var cut = new VisualElement { pickingMode = PickingMode.Ignore };
                cut.AddToClassList(UiClasses.CardCut);
                cut.style.backgroundImage = Background(ui.Texture(layer.AssetId));
                cut.style.left = new Length(UiDom.Percent(layer.X - layer.W * UiMath.Half), LengthUnit.Percent);
                cut.style.top = new Length(UiDom.Percent(layer.Y - layer.H * UiMath.Half), LengthUnit.Percent);
                cut.style.width = new Length(UiDom.Percent(layer.W), LengthUnit.Percent);
                cut.style.height = new Length(UiDom.Percent(layer.H), LengthUnit.Percent);
                cut.style.opacity = (float)layer.Opacity;
                if (layer.Rotation != 0d) cut.style.rotate = new Rotate(new Angle((float)layer.Rotation, AngleUnit.Degree));
                if (layer.ScaleX != 1d || layer.ScaleY != 1d) cut.style.scale = new Scale(new Vector3((float)layer.ScaleX, (float)layer.ScaleY, 1f));
                _stage.Add(cut);
            }
        }

        // ------------------------------------------------------------------ type band and tag rail

        /// <summary>The action's mark and word; a card with no face data keeps its type line.</summary>
        private void BindBand(CardViewData data, CardFacePlan plan)
        {
            if (plan == null)
            {
                _type?.SetResolved(data.TypeLine);
                if (_sigil != null) _sigil.style.backgroundImage = new StyleBackground(StyleKeyword.None);
                return;
            }
            _type?.SetResolved(Loc.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.CardSigilKey, plan.Sigil)));
            if (_sigil != null) _sigil.style.backgroundImage = Background(Glyph(UiFormats.CardSigilGlyph, plan.Sigil));
        }

        private void BindRail(CardFacePlan plan)
        {
            if (_rail == null) return;
            _rail.Clear();
            if (plan == null) return;
            foreach (var id in plan.Rail)
            {
                var mark = new VisualElement { pickingMode = PickingMode.Ignore };
                mark.AddToClassList(UiClasses.CardRailMark);
                mark.style.backgroundImage = Background(Glyph(UiFormats.CardTagGlyph, id));
                _rail.Add(mark);
            }
        }

        // ------------------------------------------------------------------ costs

        /// <summary>One banner icon with its number per cost: the first of each kind of icon in its own place, a repeat in the overflow place; a second cost lengthens the banner.</summary>
        private void BindCosts(CardViewData data)
        {
            var config = Source?.Data?.CardFace?.Config;
            _costs?.Clear();
            _face?.EnableInClassList(UiClasses.CardFlagTall, data.Costs.Count > 1);
            if (_costs == null || config == null) return;
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cost in data.Costs)
            {
                var frame = config.CostFrame(cost.Kind);
                if (!used.Add(frame)) frame = config.OverflowFrame;
                var pip = new VisualElement { pickingMode = PickingMode.Ignore };
                pip.AddToClassList(UiClasses.CardCost);
                pip.AddToClassList(UiClasses.CostPrefix + cost.Kind);
                pip.AddToClassList(UiClasses.CardSlotPrefix + frame);
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.AddToClassList(UiClasses.CardCostIcon);
                var crop = new VisualElement { name = frame, pickingMode = PickingMode.Ignore };
                crop.AddToClassList(UiClasses.CardLayer);
                crop.AddToClassList(UiClasses.CardCropPrefix + frame);
                FrameTexture(crop, frame);
                icon.Add(crop);
                pip.Add(icon);
                var value = new LocLabel { pickingMode = PickingMode.Ignore };
                value.AddToClassList(UiClasses.CardCostValue);
                value.AddToClassList(UiClasses.FontLore);
                value.SetResolved(cost.Text);
                pip.Add(value);
                _costs.Add(pip);
            }
        }

        // ------------------------------------------------------------------ text fit

        /// <summary>Shrinks the name and the rules from the template's largest size until they fit their boxes (the web's autoFit), once per text and scale.</summary>
        private void FitText()
        {
            var config = Source?.Data?.CardFace?.Config;
            if (config == null || _name == null || _text == null) return;
            var key = _name.text + UiFormats.Newline + _text.text + UiFormats.Newline + _scale.ToString(CultureInfo.InvariantCulture);
            if (key == _fitted) return;
            var fitted = Fit(_name, config.Fit(CardFaceKeys.Title)) & Fit(_text, config.Fit(CardFaceKeys.Rules));
            if (fitted) _fitted = key;
        }

        /// <summary>False when the label has no layout yet (the geometry event fits it later).</summary>
        private static bool Fit(LocLabel label, CardTextFit fit)
        {
            var width = label.resolvedStyle.width;
            var height = label.resolvedStyle.height;
            if (float.IsNaN(width) || float.IsNaN(height) || width <= 0f) return false;
            if (string.IsNullOrEmpty(label.text)) return true;
            var single = fit.Lines <= 1;
            var size = (float)fit.Max;
            for (; ; size -= 1f)
            {
                label.style.fontSize = size;
                var measured = label.MeasureTextSize(label.text, single ? 0f : width, single ? VisualElement.MeasureMode.Undefined : VisualElement.MeasureMode.Exactly, 0f, VisualElement.MeasureMode.Undefined);
                if (size <= (float)fit.Min || (single ? measured.x <= width : measured.y <= height)) break;
            }
            label.style.fontSize = Mathf.Max(size, (float)fit.Min);
            return true;
        }
    }
}
