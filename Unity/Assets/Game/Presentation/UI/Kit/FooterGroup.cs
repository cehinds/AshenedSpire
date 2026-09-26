using System;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// The combat footer group (docs/design/04 §1): Actions · Draw · End Turn · Discard/Exhaust · Potions, packed
    /// and centred. Shrink order is by class: in the compact band the text labels drop first and the glyphs and
    /// counts stay. End Turn is neutral at rest and green when Actions are spent (04 §0 exception).
    /// </summary>
    [UxmlElement]
    public partial class FooterGroup : VisualElement
    {
        private readonly LocLabel _actions;
        private readonly LocButton _draw;
        private readonly LocButton _endTurn;
        private readonly LocButton _discard;
        private readonly LocButton _potions;

        public FooterGroup()
        {
            AddToClassList(nameof(FooterGroup).ToLowerInvariant());
            UiDom.CloneTemplate(this, UiResources.KitFooterGroup);
            _actions = this.Q<LocLabel>(UiNames.GroupActions);
            _draw = this.Q<LocButton>(UiNames.GroupDraw);
            _endTurn = this.Q<LocButton>(UiNames.GroupEndTurn);
            _discard = this.Q<LocButton>(UiNames.GroupDiscard);
            _potions = this.Q<LocButton>(UiNames.GroupPotions);
            if (_endTurn != null) _endTurn.clicked += () => EndTurn?.Invoke();
        }

        public event Action EndTurn;

        public void Set(int actions, int actionsMax, int draw, int discard, int potions, bool endTurnReady)
        {
            _actions?.SetResolved(Loc.Get(StringKeys.GlyphActions) + Loc.Format(StringKeys.MeterValue, new StringArgs().Add(UiPlaceholders.Value, actions).Add(UiPlaceholders.Max, actionsMax)));
            _draw?.SetResolved(Loc.Format(StringKeys.KitFooterDraw, Count(draw)));
            _discard?.SetResolved(Loc.Format(StringKeys.KitFooterDiscard, Count(discard)));
            _potions?.SetResolved(Loc.Get(StringKeys.GlyphPotions) + Loc.Format(StringKeys.KitFooterPotions, Count(potions)));
            _endTurn?.EnableInClassList(UiClasses.EndTurnReady, endTurnReady);
            _endTurn?.EnableInClassList(UiClasses.ButtonReady, endTurnReady);
        }

        private static StringArgs Count(int n) => new StringArgs().Add(UiPlaceholders.Count, n);
    }
}
