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
            if (_potions != null) _potions.clicked += () => Potions?.Invoke();
            if (_draw != null) _draw.clicked += () => Piles?.Invoke(_draw);
            if (_discard != null) _discard.clicked += () => Piles?.Invoke(_discard);
        }

        public event Action EndTurn;

        /// <summary>The Potions circle was pressed (it opens the flask panel; 04 W-07 WGC11).</summary>
        public event Action Potions;

        /// <summary>A pile button was pressed (the pile viewer; the button is the refusal anchor while it is planned).</summary>
        public event Action<VisualElement> Piles;

        public LocButton EndTurnButton => _endTurn;
        public LocButton PotionsButton => _potions;

        /// <summary>
        /// The enemy-turn state (04 W-07): the group goes busy, End Turn becomes the fast-forward control (its label key
        /// is given), and the other controls are disabled until the player's turn.
        /// </summary>
        public void SetBusy(bool busy, string endTurnKey)
        {
            EnableInClassList(UiClasses.GroupBusy, busy);
            if (_endTurn != null) _endTurn.stringKey = endTurnKey;
            _draw?.SetEnabled(!busy);
            _discard?.SetEnabled(!busy);
            _potions?.SetEnabled(!busy);
        }

        /// <summary>The discard and exhaust piles on one button ("Discard 4 · Exhaust 1").</summary>
        public void SetPiles(int discard, int exhaust) =>
            _discard?.SetResolved(Loc.Format(Compact ? StringKeys.KitFooterPilesShort : StringKeys.KitFooterPiles, Count(discard).Add(UiPlaceholders.Exhaust, exhaust)));

        /// <summary>Narrow layout: the pile and potion buttons use their short labels so End Turn keeps its words (04 W-07 narrow).</summary>
        public bool Compact { get; set; }

        public void Set(int actions, int actionsMax, int draw, int discard, int potions, bool endTurnReady)
        {
            _actions?.SetResolved(Loc.Get(StringKeys.GlyphActions) + Loc.Format(StringKeys.MeterValue, new StringArgs().Add(UiPlaceholders.Value, actions).Add(UiPlaceholders.Max, actionsMax)));
            _draw?.SetResolved(Loc.Format(Compact ? StringKeys.KitFooterDrawShort : StringKeys.KitFooterDraw, Count(draw)));
            _discard?.SetResolved(Loc.Format(Compact ? StringKeys.KitFooterDiscardShort : StringKeys.KitFooterDiscard, Count(discard)));
            _potions?.SetResolved(Loc.Get(StringKeys.GlyphPotions) + Loc.Format(Compact ? StringKeys.KitFooterPotionsShort : StringKeys.KitFooterPotions, Count(potions)));
            _endTurn?.EnableInClassList(UiClasses.EndTurnReady, endTurnReady);
            _endTurn?.EnableInClassList(UiClasses.ButtonReady, endTurnReady);
        }

        private static StringArgs Count(int n) => new StringArgs().Add(UiPlaceholders.Count, n);
    }
}
