using System;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// The target card (the web's combatant hover card, docs/qa/web-screens/combat-target-hover-* and combat-intent-tooltip-*): the combatant's name, HP bar, rating/stack/status chips and
    /// the intent with its icon, drawn from the same <see cref="FighterData"/> the figure uses. The round info button raises <see cref="InfoPressed"/> (the full reading).
    /// </summary>
    [UxmlElement]
    public partial class TargetCard : VisualElement
    {
        private readonly LocLabel _name;
        private readonly Meter _hp;
        private readonly VisualElement _chips;
        private readonly VisualElement _statuses;
        private readonly VisualElement _intentRow;
        private readonly VisualElement _intentIcon;
        private readonly LocLabel _intent;
        private readonly LocButton _info;
        private Tooltip _intentTip;

        public TargetCard()
        {
            AddToClassList(nameof(TargetCard).ToLowerInvariant());
            UiDom.CloneTemplate(this, UiResources.KitTargetCard);
            _name = this.Q<LocLabel>(UiNames.TargetName);
            _hp = this.Q<Meter>(UiNames.TargetHp);
            _chips = this.Q(UiNames.TargetChips);
            _statuses = this.Q(UiNames.TargetStatuses);
            _intentRow = this.Q(UiNames.TargetIntentRow);
            _intentIcon = this.Q(UiNames.TargetIntentIcon);
            _intent = this.Q<LocLabel>(UiNames.TargetIntent);
            _info = this.Q<LocButton>(UiNames.TargetInfo);
            if (_info != null) _info.clicked += () => InfoPressed?.Invoke();
        }

        public LocButton InfoButton => _info;

        public event Action InfoPressed;

        public void Bind(FighterData d)
        {
            _name?.SetResolved(d.Name);
            _hp?.Set(d.Hp, d.MaxHp, d.MaxHp);
            if (_chips != null)
            {
                _chips.Clear();
                foreach (var chip in d.RatingChips) AddChip(_chips, chip);
                foreach (var chip in d.StackChips) AddChip(_chips, chip);
                UiDom.Show(_chips, _chips.childCount > 0);
            }
            if (_statuses != null)
            {
                _statuses.Clear();
                foreach (var chip in d.StatusChips) AddChip(_statuses, chip);
                UiDom.Show(_statuses, _statuses.childCount > 0);
            }
            var hasIntent = !string.IsNullOrEmpty(d.IntentText);
            UiDom.Show(_intentRow, hasIntent);
            _intent?.SetResolved(d.IntentText ?? string.Empty);
            _intent?.EnableInClassList(UiClasses.IntentReduced, d.IntentReduced);
            ChipTips.Retell(_intentRow, ref _intentTip, d.IntentTipTitle, d.IntentTipBody);
            if (_intentIcon != null)
            {
                _intentIcon.style.backgroundImage = d.IntentIcon != null ? new StyleBackground(d.IntentIcon) : new StyleBackground(StyleKeyword.None);
                UiDom.Show(_intentIcon, d.IntentIcon != null);
            }
        }

        private static void AddChip(VisualElement host, ChipData data)
        {
            var chip = new VisualElement { pickingMode = PickingMode.Ignore };
            chip.AddToClassList(UiClasses.FighterChip);
            chip.EnableInClassList(UiClasses.FighterChipGold, data.Gold);
            if (data.Icon != null)
            {
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.AddToClassList(UiClasses.FighterChipIcon);
                icon.style.backgroundImage = new StyleBackground(data.Icon);
                chip.Add(icon);
            }
            var text = new LocLabel { pickingMode = PickingMode.Ignore };
            text.SetResolved(data.Text);
            text.AddToClassList(UiClasses.FighterChipText);
            text.AddToClassList(UiClasses.ValueText);
            chip.Add(text);
            ChipTips.Attach(chip, data.TipTitle, data.TipBody);
            host.Add(chip);
        }
    }
}
