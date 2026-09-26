using System.Collections.Generic;
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
        public Texture2D Art;
        public bool Affordable = true;
        public bool Upgraded;
        public List<CardCostData> Costs = new List<CardCostData>();
    }

    /// <summary>
    /// CardView (docs/design/04 §1, ratio 5:8 from tokens): costs (A/S/M or X), name, art, type, templated text,
    /// keywords, affordability with the refusal text as the non-colour cue, lent-by and upgraded marks.
    /// </summary>
    [UxmlElement]
    public partial class CardView : VisualElement
    {
        private readonly VisualElement _costs;
        private readonly LocLabel _name;
        private readonly VisualElement _art;
        private readonly LocLabel _type;
        private readonly LocLabel _text;
        private readonly LocLabel _keywords;
        private readonly LocLabel _refusal;
        private readonly LocLabel _lent;
        private string _rarityClass;

        public CardView()
        {
            AddToClassList(nameof(CardView).ToLowerInvariant());
            focusable = true;
            UiDom.CloneTemplate(this, UiResources.KitCard);
            _costs = this.Q(UiNames.CardCosts);
            _name = this.Q<LocLabel>(UiNames.CardName);
            _art = this.Q(UiNames.CardArt);
            _type = this.Q<LocLabel>(UiNames.CardType);
            _text = this.Q<LocLabel>(UiNames.CardText);
            _keywords = this.Q<LocLabel>(UiNames.CardKeywords);
            _refusal = this.Q<LocLabel>(UiNames.CardRefusal);
            _lent = this.Q<LocLabel>(UiNames.CardLent);
        }

        public CardViewData Data { get; private set; }

        public void Bind(CardViewData data)
        {
            Data = data;
            _name?.SetResolved(data.Name);
            _type?.SetResolved(data.TypeLine);
            _text?.SetResolved(data.Text);
            _keywords?.SetResolved(data.Keywords);
            UiDom.Show(_keywords, !string.IsNullOrEmpty(data.Keywords));
            _refusal?.SetResolved(data.RefusalText);
            UiDom.Show(_refusal, !data.Affordable && !string.IsNullOrEmpty(data.RefusalText));
            _lent?.SetResolved(data.LentText);
            UiDom.Show(_lent, !string.IsNullOrEmpty(data.LentText));
            if (_art != null) _art.style.backgroundImage = data.Art != null ? new StyleBackground(data.Art) : new StyleBackground(StyleKeyword.None);
            if (_rarityClass != null) RemoveFromClassList(_rarityClass);
            _rarityClass = string.IsNullOrEmpty(data.Rarity) ? null : UiClasses.CardRarityPrefix + data.Rarity;
            if (_rarityClass != null) AddToClassList(_rarityClass);
            EnableInClassList(UiClasses.CardUnaffordable, !data.Affordable);
            EnableInClassList(UiClasses.CardUpgraded, data.Upgraded);
            if (_costs == null) return;
            _costs.Clear();
            foreach (var cost in data.Costs)
            {
                var pip = new LocLabel();
                pip.SetResolved(cost.Text);
                pip.AddToClassList(UiClasses.CardCost);
                pip.AddToClassList(UiClasses.CostPrefix + cost.Kind);
                pip.AddToClassList(UiClasses.ValueText);
                _costs.Add(pip);
            }
        }
    }
}
