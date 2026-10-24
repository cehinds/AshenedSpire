using System;
using System.Globalization;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>The raster art of the combat footer (art lookups orb.* and footer.*; null leaves a layer empty).</summary>
    public sealed class FooterArt
    {
        public OrbArt Orb = new OrbArt();
        public Texture2D Plate;
        public Texture2D Draw;
        public Texture2D Spent;
        public Texture2D Potions;
    }

    /// <summary>
    /// The combat footer group (docs/design/04 §1; VP-4 look, D-700): the stamina Orb with its mana ring, the Draw pile, End Turn on its plate, the Discard/Exhaust pile and the Potions
    /// tray, laid out as the web's footer art row (the plate stays centred). In the compact band and on the narrow board the controls shrink by class. End Turn is the
    /// plate at rest and reads green when Actions are spent (04 §0 exception); while the enemy acts it becomes the fast-forward control.
    /// </summary>
    [UxmlElement]
    public partial class FooterGroup : VisualElement
    {
        private readonly Orb _orb;
        private readonly LocButton _draw;
        private readonly LocButton _endTurn;
        private readonly LocButton _discard;
        private readonly LocButton _potions;
        private readonly LocLabel _discardCaption;
        private readonly VisualElement _drawArt;
        private readonly VisualElement _discardArt;
        private readonly VisualElement _potionsArt;
        private int _actions;
        private int _mana;
        private int _manaMax;

        public FooterGroup()
        {
            AddToClassList(nameof(FooterGroup).ToLowerInvariant());
            UiDom.CloneTemplate(this, UiResources.KitFooterGroup);
            _orb = this.Q<Orb>(UiNames.GroupOrb);
            _draw = this.Q<LocButton>(UiNames.GroupDraw);
            _endTurn = this.Q<LocButton>(UiNames.GroupEndTurn);
            _discard = this.Q<LocButton>(UiNames.GroupDiscard);
            _potions = this.Q<LocButton>(UiNames.GroupPotions);
            _discardCaption = this.Q<LocLabel>(UiNames.GroupDiscardCaption);
            _drawArt = this.Q(UiNames.GroupDrawArt);
            _discardArt = this.Q(UiNames.GroupDiscardArt);
            _potionsArt = this.Q(UiNames.GroupPotionsArt);
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
        public LocButton DrawButton => _draw;
        public LocButton DiscardButton => _discard;
        public Orb Orb => _orb;

        /// <summary>Narrow layout: the pile and potion buttons use their short labels so End Turn keeps its words (04 W-07 narrow).</summary>
        public bool Compact { get; set; }

        /// <summary>Sets the raster art: the orb layers, the End Turn plate, the pile cradles and the potion tray (the screen looks them up by name).</summary>
        public void SetArt(FooterArt art)
        {
            if (art == null) return;
            _orb?.SetArt(art.Orb);
            if (_endTurn != null) _endTurn.style.backgroundImage = Image(art.Plate);
            if (_drawArt != null) _drawArt.style.backgroundImage = Image(art.Draw);
            if (_discardArt != null) _discardArt.style.backgroundImage = Image(art.Spent);
            if (_potionsArt != null) _potionsArt.style.backgroundImage = Image(art.Potions);
        }

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

        /// <summary>The discard pile on its button and the exhaust pile in the caption under it ("Discard 4" / "Exhaust 1").</summary>
        public void SetPiles(int discard, int exhaust)
        {
            _discard?.SetResolved(Loc.Format(Compact ? StringKeys.KitFooterDiscardShort : StringKeys.KitFooterDiscard, Count(discard)));
            _discardCaption?.SetResolved(Loc.Format(StringKeys.Core2BoardExhaust, Count(exhaust)));
        }

        /// <summary>The Mana ring of the orb: <paramref name="mana"/> available out of <paramref name="manaMax"/> diamonds.</summary>
        public void SetMana(int mana, int manaMax)
        {
            _mana = mana;
            _manaMax = manaMax;
            _orb?.Set(_actions, _mana, _manaMax);
        }

        public void Set(int actions, int actionsMax, int draw, int discard, int potions, bool endTurnReady)
        {
            _actions = actions;
            _orb?.Set(_actions, _mana, _manaMax);
            _draw?.SetResolved(draw.ToString(CultureInfo.InvariantCulture));
            _discard?.SetResolved(Loc.Format(Compact ? StringKeys.KitFooterDiscardShort : StringKeys.KitFooterDiscard, Count(discard)));
            _potions?.SetResolved(Loc.Get(StringKeys.Core2BoardPotions));
            _endTurn?.EnableInClassList(UiClasses.EndTurnReady, endTurnReady);
            _endTurn?.EnableInClassList(UiClasses.ButtonReady, endTurnReady);
        }

        private static StyleBackground Image(Texture2D texture) =>
            texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);

        private static StringArgs Count(int n) => new StringArgs().Add(UiPlaceholders.Count, n);
    }
}
