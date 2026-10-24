using System.Linq;
using Ashen.App.Run;

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
                Costs = pick.Costs.Select(c => new CardCostData { Kind = c.Kind, Text = c.Text }).ToList(),
            });
            return card;
        }
    }
}
