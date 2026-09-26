using System;
using System.Collections.Generic;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// HandFan (docs/design/04 §1): the hand as overlapping cards on a shallow fan. Angles come from
    /// ui/components.json handFan (degrees per card, capped by the widest spread); overlap and lift are tokens.
    /// Focused or hovered cards lift (USS). Past scrollAfter cards the fan flattens and the row scrolls.
    /// </summary>
    [UxmlElement]
    public partial class HandFan : VisualElement
    {
        private readonly ScrollView _cards;
        private readonly List<CardView> _views = new List<CardView>();

        public HandFan()
        {
            AddToClassList(nameof(HandFan).ToLowerInvariant());
            UiDom.CloneTemplate(this, UiResources.KitHandFan);
            _cards = this.Q<ScrollView>(UiNames.HandCards);
            // The focus model walks the cards; the scroll bars never take focus (the router, not the scroller, moves).
            if (_cards != null)
            {
                _cards.horizontalScroller.slider.focusable = false;
                _cards.verticalScroller.slider.focusable = false;
            }
        }

        public IReadOnlyList<CardView> Cards => _views;

        public void SetCards(IReadOnlyList<CardViewData> cards, double degreesPerCard, double maxSpread, int scrollAfter)
        {
            if (_cards == null) return;
            _cards.Clear();
            _views.Clear();
            var n = cards.Count;
            var flat = n > scrollAfter;
            var step = n > 1 ? Math.Min(degreesPerCard, maxSpread / (n - 1)) : 0d;
            for (var i = 0; i < n; i++)
            {
                var view = new CardView();
                view.AddToClassList(UiClasses.HandCard);
                view.Bind(cards[i]);
                var angle = flat ? 0d : (i - (n - 1) * UiMath.Half) * step;
                view.style.rotate = new Rotate(new Angle((float)angle, AngleUnit.Degree));
                view.RegisterCallback<FocusInEvent>(_ => _cards.ScrollTo(view));
                _cards.Add(view);
                _views.Add(view);
            }
            _cards.horizontalScrollerVisibility = flat ? ScrollerVisibility.Auto : ScrollerVisibility.Hidden;
        }
    }
}
