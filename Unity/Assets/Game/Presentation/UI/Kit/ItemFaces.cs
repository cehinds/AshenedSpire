using System.Collections.Generic;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>The kit's faces of the things a run is offered (VP-7 spoils, VP-9 shop): a card is the VP-3 <see cref="CardView"/> bound from the offer's pick data.</summary>
    public static class ItemFaces
    {
        /// <summary>A card face that takes no focus (the control next to it does).</summary>
        public static CardView Card(RewardPickView pick)
        {
            var card = new CardView { focusable = false };
            card.Bind(new CardViewData
            {
                Name = pick.Name,
                Text = pick.Text,
                TypeLine = pick.TypeLine,
                Rarity = pick.Rarity,
                Face = pick.Face,
                Upgraded = pick.Upgraded,
                RulesHtml = pick.RulesHtml,
                RulesOverflow = pick.RulesOverflow,
                Costs = pick.Costs.Select(c => new CardCostData { Kind = c.Kind, Text = c.Text }).ToList(),
            });
            return card;
        }

        /// <summary>A deck card's face data: the cost pips in the components' order (Stamina always, Mana when it costs), its type line, rank and traits; a deck card is never dimmed (the Armoury's Cards pane, the creation review).</summary>
        public static CardViewData DeckCard(UiData ui, Core2CardView card)
        {
            var strings = ui.Strings;
            var costs = new List<CardCostData>();
            foreach (var kind in ui.Components.CostKinds)
            {
                if (kind == Core2UiValues.CostStamina) costs.Add(new CardCostData { Kind = kind, Text = card.CostIsX ? strings.Get(StringKeys.CombatCostX) : CombatText.Number(card.Stamina), Shift = card.CostIsX ? 0 : card.CostShift });
                else if (kind == Core2UiValues.CostMana && card.Mana > 0) costs.Add(new CardCostData { Kind = kind, Text = CombatText.Number(card.Mana) });
            }
            return new CardViewData
            {
                Name = card.Name,
                TypeLine = Core2CombatText.TypeLine(strings, card),
                Face = card.Face,
                Rank = Core2CombatText.Rank(strings, card),
                Text = card.Text,
                Shifts = card.TextShifts,
                RulesHtml = card.Words?.Face,
                RulesOverflow = card.Words?.Lines != null && card.Words.HandLines != null && card.Words.Lines.Count > card.Words.HandLines.Count,
                Keywords = Core2CombatText.Traits(strings, card),
                Upgraded = card.Upgraded,
                Costs = costs,
            };
        }
    }
}
