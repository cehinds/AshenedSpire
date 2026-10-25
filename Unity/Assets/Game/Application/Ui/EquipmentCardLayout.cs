using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>One region of the poker card (web balance.ui.equipmentCard.regions): its floor, the priority that breaks ties and its share of the spare height.</summary>
    public sealed class EquipmentCardRegion
    {
        public string Id;
        public double MinPx;
        public double Priority;
        public double Grow;
    }

    /// <summary>
    /// The poker-style equipment card's geometry (ui/equipmentCard.json, web model/equipmentCard.js equipmentCardTokens): the frame's design size and the height each region gets. The floors are laid down first
    /// and the spare is shared out by <c>grow</c>; when the floors do not fit, the region of the lowest priority gives up its slack first and never more than half its floor. A region with nothing to say
    /// takes no floor and no growth (the tags row of a relic). Engine-free.
    /// </summary>
    public sealed class EquipmentCardLayout
    {
        public double FrameWidth;
        public double FrameHeight;
        public double Padding;
        public double Gap;
        public int BonusMaxLines;
        public IReadOnlyList<EquipmentCardRegion> Regions = Array.Empty<EquipmentCardRegion>();

        /// <summary>The ids of the regions that may be empty (the tags row).</summary>
        public IReadOnlyList<string> Optional = Array.Empty<string>();

        /// <summary>The registry id templates of the card's picture: an armament's icon ({art}), an armour set's painted outfit ({key}) and the outfit shared by every class ({class}, {id}).</summary>
        public string ArtIcon;

        public string ArtOutfit;
        public string ArtShared;

        public static EquipmentCardLayout From(JObject json)
        {
            var frame = (JObject)json[CardLayoutKeys.Frame];
            return new EquipmentCardLayout
            {
                FrameWidth = frame.Value<double>(CardLayoutKeys.Width),
                FrameHeight = frame.Value<double>(CardLayoutKeys.Height),
                Padding = frame.Value<double>(CardLayoutKeys.Padding),
                Gap = frame.Value<double>(CardLayoutKeys.Gap),
                BonusMaxLines = json.Value<int>(CardLayoutKeys.BonusMaxLines),
                Regions = ((JArray)json[CardLayoutKeys.Regions]).OfType<JObject>().Select(r => new EquipmentCardRegion
                {
                    Id = r.Value<string>(CardLayoutKeys.Id),
                    MinPx = r.Value<double>(CardLayoutKeys.MinPx),
                    Priority = r.Value<double>(CardLayoutKeys.Priority),
                    Grow = r.Value<double>(CardLayoutKeys.Grow),
                }).ToList(),
                Optional = ((JArray)json[CardLayoutKeys.Optional]).Select(t => (string)t).ToList(),
                ArtIcon = json[CardLayoutKeys.Art].Value<string>(CardLayoutKeys.Icon),
                ArtOutfit = json[CardLayoutKeys.Art].Value<string>(CardLayoutKeys.Outfit),
                ArtShared = json[CardLayoutKeys.Art].Value<string>(CardLayoutKeys.Shared),
            };
        }

        /// <summary>The height of each region, in the regions' order. <paramref name="collapse"/> names the regions the card has nothing to put in.</summary>
        public IReadOnlyList<KeyValuePair<string, double>> Solve(ICollection<string> collapse)
        {
            var regions = Regions.Select(r => collapse != null && collapse.Contains(r.Id) ? new EquipmentCardRegion { Id = r.Id, MinPx = 0, Priority = r.Priority, Grow = 0 } : r).ToList();
            var gaps = Gap * Math.Max(0, regions.Count - 1);
            var budget = FrameHeight - Padding * CombatMath.Two - gaps;
            var floors = regions.Sum(r => r.MinPx);
            var height = regions.ToDictionary(r => r.Id, r => r.MinPx);
            if (floors <= budget)
            {
                var growth = regions.Sum(r => r.Grow);
                var spare = budget - floors;
                foreach (var r in regions) height[r.Id] = r.MinPx + (growth > 0 ? spare * r.Grow / growth : 0);
            }
            else
            {
                var debt = floors - budget;
                foreach (var r in regions.OrderBy(r => r.Priority))
                {
                    if (debt <= 0) break;
                    var give = Math.Min(debt, r.MinPx / CombatMath.Two);
                    height[r.Id] -= give;
                    debt -= give;
                }
            }
            return regions.Select(r => new KeyValuePair<string, double>(r.Id, height[r.Id])).ToList();
        }
    }
}
