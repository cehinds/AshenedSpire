using System;
using System.Linq;
using Ashen.App.Run;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// One option of the spoils offer (VP-7, D-850; web gameProfileOffers.js reward-choice-option): a column of the option's face, its NEW or TAKEN chip and its name as the radio. A card is
    /// the VP-3 <see cref="CardView"/>; a relic, flask, armament or armour is a collectible face (kind, picture, name, one line of what it is), as the web's collectible and equipment cards
    /// are. A press on the face or on the name raises <see cref="Chosen"/>; the name button is the one control the pad and Tab stop on, so the face itself never takes focus. A taken option stays
    /// pressable and says why it cannot be picked (the screen shows the refusal at it).
    /// </summary>
    public sealed class SpoilsOption : VisualElement
    {
        public SpoilsOption(Core2SpoilsOption option, UiContext ui, bool selected)
        {
            Index = option.Index;
            AddToClassList(SpoilsClasses.Option);
            EnableInClassList(SpoilsClasses.OptionTaken, option.Taken);
            EnableInClassList(SpoilsClasses.OptionSelected, selected);
            Face = option.IsCard ? CardFace(option) : Collectible(option, ui);
            Face.AddToClassList(SpoilsClasses.Face);
            Face.RegisterCallback<PointerUpEvent>(_ => Chosen?.Invoke(Index, PickButton));
            Add(Face);
            if (option.Chip != Core2SpoilsChip.None)
            {
                var chips = new VisualElement { pickingMode = PickingMode.Ignore };
                chips.AddToClassList(SpoilsClasses.Chips);
                var chip = new LocLabel { pickingMode = PickingMode.Ignore };
                chip.AddToClassList(UiClasses.Chip);
                chip.EnableInClassList(UiClasses.ChipOn, option.Chip == Core2SpoilsChip.New);
                chip.SetResolved(option.ChipText);
                chips.Add(chip);
                Face.Add(chips);
            }
            PickButton = new LocButton();
            PickButton.AddToClassList(SpoilsClasses.Pick);
            PickButton.SetResolved(option.Name);
            PickButton.clicked += () => Chosen?.Invoke(Index, PickButton);
            Add(PickButton);
        }

        public int Index { get; }

        public VisualElement Face { get; }

        /// <summary>The control that takes focus: the option's name, a button.</summary>
        public LocButton PickButton { get; }

        /// <summary>The option was pressed (index, the control to show a refusal at).</summary>
        public event Action<int, VisualElement> Chosen;

        private static VisualElement CardFace(Core2SpoilsOption option)
        {
            var pick = option.Card;
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

        private static VisualElement Collectible(Core2SpoilsOption option, UiContext ui)
        {
            var face = new VisualElement();
            face.AddToClassList(SpoilsClasses.Collect);
            var kind = new LocLabel { pickingMode = PickingMode.Ignore };
            kind.AddToClassList(SpoilsClasses.CollectKind);
            kind.AddToClassList(UiClasses.Upper);
            kind.SetResolved(option.KindLabel);
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.AddToClassList(SpoilsClasses.CollectArt);
            var texture = (option.ArtId != null ? ui.Texture(option.ArtId) : null) ?? ui.Icon(KindIcon(option.Kind));
            if (texture != null) art.style.backgroundImage = new StyleBackground(texture);
            var name = new LocLabel { pickingMode = PickingMode.Ignore };
            name.AddToClassList(SpoilsClasses.CollectName);
            name.SetResolved(option.Name);
            var text = new LocLabel { pickingMode = PickingMode.Ignore };
            text.AddToClassList(SpoilsClasses.CollectText);
            text.AddToClassList(UiClasses.ValueText);
            text.SetResolved(option.Effect ?? string.Empty);
            UiDom.Show(text, !string.IsNullOrEmpty(option.Effect));
            face.Add(kind);
            face.Add(art);
            face.Add(name);
            face.Add(text);
            return face;
        }

        /// <summary>The icon a reward kind falls back to when its own picture is not in the art catalog.</summary>
        public static string KindIcon(string kind)
        {
            switch (kind)
            {
                case Core2RewardsValues.KindRelics: return SpoilsIcons.Relic;
                case Core2RewardsValues.KindPotions: return SpoilsIcons.Flask;
                case Core2RewardsValues.KindWeapons: return SpoilsIcons.Armament;
                case Core2RewardsValues.KindArmor: return SpoilsIcons.Armour;
                case Core2RewardsValues.KindWeaponArts: return SpoilsIcons.WeaponArt;
                default: return SpoilsIcons.Card;
            }
        }
    }
}
