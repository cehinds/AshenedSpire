using System.Collections.Generic;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// The poker-style equipment card (web ui/components/equipmentCard.js renderEquipmentCard at the inspect level, which is every level the Armoury draws it at): a frame laid out at the design size of
    /// ui/equipmentCard.json, scaled into the element's width. From the top: the name over the picture, the type band with its requirement, the facts, the tag badges, the effects block with its
    /// heading, the flavour line and the footer (the rarity, and how many the run holds). The rows are solved by <see cref="EquipmentCardLayout"/>; a card with no tags gives that row to the effects.
    /// </summary>
    public sealed class PokerCard : VisualElement
    {
        private readonly VisualElement _frame;
        private readonly EquipmentCardLayout _layout;
        private float _width;

        public PokerCard(UiContext ui, EquipmentCardModel model, int owned = -1)
        {
            _layout = ui.Data.EquipmentCard;
            var strings = ui.Data.Strings;
            AddToClassList(PokerClasses.Card);
            AddToClassList(model.Accent == ReceiptCardKeys.AccentStaff ? PokerClasses.Staff : model.Accent == ReceiptCardKeys.AccentBlade ? PokerClasses.Blade : PokerClasses.Gold);
            pickingMode = PickingMode.Ignore;
            _frame = new VisualElement { pickingMode = PickingMode.Ignore };
            _frame.AddToClassList(PokerClasses.Frame);
            _frame.style.width = (float)_layout.FrameWidth;
            _frame.style.height = (float)_layout.FrameHeight;
            var pad = (float)_layout.Padding;
            _frame.style.paddingLeft = pad;
            _frame.style.paddingRight = pad;
            _frame.style.paddingTop = pad;
            _frame.style.paddingBottom = pad;
            Add(_frame);
            var rows = _layout.Solve(model.Tags.Count == 0 ? _layout.Optional : System.Array.Empty<string>()).ToDictionary(r => r.Key, r => (float)r.Value);
            var gap = (float)_layout.Gap;

            VisualElement Region(string id, VisualElement element)
            {
                element.pickingMode = PickingMode.Ignore;
                element.style.height = rows[id];
                element.style.marginBottom = id == _layout.Regions.Last().Id ? 0f : gap;
                _frame.Add(element);
                return element;
            }

            Label Text(VisualElement parent, string text, string cls, bool upper = false, bool lore = false)
            {
                var label = new LocLabel { pickingMode = PickingMode.Ignore };
                label.AddToClassList(cls);
                if (upper) label.AddToClassList(UiClasses.Upper);
                if (lore) label.AddToClassList(PokerClasses.Lore);
                label.SetResolved(text ?? string.Empty);
                parent.Add(label);
                return label;
            }

            var name = Text(_frame, model.Name, PokerClasses.Name, false, true);
            name.style.position = Position.Absolute;
            var art = new VisualElement();
            art.AddToClassList(PokerClasses.Art);
            var texture = model.ArtId == null ? null : ui.Texture(model.ArtId);
            if (texture != null) art.style.backgroundImage = new StyleBackground(texture);
            Region(CardLayoutKeys.ArtRegion, art);

            var type = new VisualElement();
            type.AddToClassList(PokerClasses.Type);
            Text(type, model.Type, PokerClasses.TypeText, true);
            Text(type, model.Requirement, PokerClasses.Requirement);
            Region(CardLayoutKeys.TypeRegion, type);

            var facts = new VisualElement();
            facts.AddToClassList(PokerClasses.Facts);
            foreach (var fact in model.Facts)
            {
                var holder = new VisualElement { pickingMode = PickingMode.Ignore };
                holder.AddToClassList(PokerClasses.Fact);
                Text(holder, fact.Id == ReceiptCardKeys.DrFact && fact.Number > 0 ? RunValues.Plus + fact.Value : fact.Value, PokerClasses.FactValue, false, true);
                Text(holder, fact.Label, PokerClasses.FactLabel, true);
                facts.Add(holder);
            }
            Region(CardLayoutKeys.FactsRegion, facts);

            var tags = new VisualElement();
            tags.AddToClassList(PokerClasses.Tags);
            foreach (var tag in model.Tags) Text(tags, tag.Label, PokerClasses.Tag, true);
            Region(CardLayoutKeys.TagsRegion, tags);

            var effects = new VisualElement();
            effects.AddToClassList(PokerClasses.Effects);
            Text(effects, model.Heading, PokerClasses.Heading, true);
            var lineHeight = (float)_layout.BonusLineHeight;
            if (model.Effects.Count == 0) Text(effects, model.Empty, PokerClasses.Bonus);
            foreach (var bonus in model.Effects)
            {
                var line = Text(effects, bonus.Label, PokerClasses.Bonus);
                line.style.maxHeight = lineHeight * _layout.BonusMaxLines;
                line.style.overflow = Overflow.Hidden;
            }
            Region(CardLayoutKeys.EffectsRegion, effects);

            var flavor = new LocLabel();
            flavor.AddToClassList(PokerClasses.Flavor);
            flavor.AddToClassList(PokerClasses.Lore);
            flavor.SetResolved(model.Flavor);
            Region(CardLayoutKeys.FlavorRegion, flavor);

            var footer = new VisualElement();
            footer.AddToClassList(PokerClasses.Footer);
            Text(footer, model.Rarity, PokerClasses.FooterRarity, true);
            if (owned >= 0) Text(footer, strings.Format(ArmouryWebKeys.CardOwned, new StringArgs().Add(UiPlaceholders.Count, owned)), PokerClasses.FooterOwned);
            Region(CardLayoutKeys.FooterRegion, footer);

            RegisterCallback<GeometryChangedEvent>(_ => Fit());
        }

        /// <summary>Scales the frame into the element's width and gives the element the frame's proportions.</summary>
        private void Fit()
        {
            var w = resolvedStyle.width;
            if (float.IsNaN(w) || w <= 0f || Mathf.Approximately(w, _width)) return;
            _width = w;
            var scale = w / (float)_layout.FrameWidth;
            _frame.style.scale = new Scale(new Vector3(scale, scale, 1f));
            style.height = w * (float)(_layout.FrameHeight / _layout.FrameWidth);
        }
    }
}
