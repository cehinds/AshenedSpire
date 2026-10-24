using System.Collections.Generic;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>What one option tile says: the card's name, a one-line effect and its cost pills (src/ui/components/cardOptionGrid.js).</summary>
    public sealed class OptionTileData
    {
        public string Name;
        public string Effect;
        public List<string> Costs = new List<string>();
        public bool Selected;
        public bool Disabled;
    }

    /// <summary>
    /// A card choice as a uniform tile (combat.css:2037-2057): bold name, the effect in one line, small cost pills at the foot; a selected tile wears the gold edge. The reaction offer
    /// and the card choices use it. The tile is a button, so the pad, Tab and Enter work as they do on every other control; a refused option stays focusable and says why when pressed.
    /// </summary>
    public sealed class OptionTile : Button
    {
        private readonly LocLabel _name = new LocLabel { pickingMode = PickingMode.Ignore };
        private readonly LocLabel _effect = new LocLabel { pickingMode = PickingMode.Ignore };
        private readonly VisualElement _costs = new VisualElement { pickingMode = PickingMode.Ignore };

        public OptionTile()
        {
            AddToClassList(UiClasses.OptionTile);
            AddToClassList(UiClasses.Touch);
            _name.AddToClassList(UiClasses.OptionTileName);
            _name.AddToClassList(UiClasses.ValueText);
            _effect.AddToClassList(UiClasses.OptionTileEffect);
            _effect.AddToClassList(UiClasses.ValueText);
            _costs.AddToClassList(UiClasses.OptionTileCosts);
            Add(_name);
            Add(_effect);
            Add(_costs);
        }

        public void Bind(OptionTileData data)
        {
            _name.SetResolved(data.Name);
            _effect.SetResolved(data.Effect ?? string.Empty);
            UiDom.Show(_effect, !string.IsNullOrEmpty(data.Effect));
            _costs.Clear();
            foreach (var cost in data.Costs)
            {
                var chip = new LocLabel { pickingMode = PickingMode.Ignore };
                chip.AddToClassList(UiClasses.Chip);
                chip.AddToClassList(UiClasses.OptionTileCost);
                chip.SetResolved(cost);
                _costs.Add(chip);
            }
            EnableInClassList(UiClasses.OptionTileSelected, data.Selected);
            EnableInClassList(UiClasses.OptionTileDisabled, data.Disabled);
        }
    }
}
