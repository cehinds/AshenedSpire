using System;
using System.Collections.Generic;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>Display-ready values for one combatant (text resolved, numbers from the combat view-model).</summary>
    public sealed class CombatantData
    {
        public string Id;
        public string Name;
        public Texture2D Art;
        public bool Player;
        public bool Alive = true;
        public int Hp;
        public int MaxHp;
        public int ReferenceMaxHp;
        public string BlockText;
        public bool HasBreakMeters;
        public int Poise;
        public int PoiseMax;
        public int Ward;
        public int WardMax;
        public string IntentText;
        public string IntentKind;
        public string StanceText;
        public string BadgeText;
        public bool Staggered;
        public string PreviewText;
        public bool Target;
        public bool Acting;
        public List<string> Statuses = new List<string>();
    }

    /// <summary>
    /// Combatant (docs/design/04 §1, W-07): the intent chip above the figure, the figure with its guard badge and state
    /// badge (staggered, defeated), then the lower stack: name, HP, Poise and Ward, stance and status chips (the last chip
    /// reads +N). Legal targets get the dashed target outline and the ghost preview ("HP 30 → 18"). Focusing or clicking
    /// it and pressing Submit raise Activated.
    /// </summary>
    [UxmlElement]
    public partial class Combatant : VisualElement
    {
        private readonly LocLabel _intent;
        private readonly VisualElement _art;
        private readonly LocLabel _block;
        private readonly LocLabel _badge;
        private readonly LocLabel _name;
        private readonly LocLabel _preview;
        private readonly Meter _hp;
        private readonly VisualElement _break;
        private readonly Meter _poise;
        private readonly Meter _ward;
        private readonly LocLabel _stance;
        private readonly VisualElement _statuses;
        private string _intentClass;

        public Combatant()
        {
            AddToClassList(nameof(Combatant).ToLowerInvariant());
            focusable = true;
            UiDom.CloneTemplate(this, UiResources.KitCombatant);
            _intent = this.Q<LocLabel>(UiNames.CombatantIntent);
            _art = this.Q(UiNames.CombatantArt);
            _block = this.Q<LocLabel>(UiNames.CombatantBlock);
            _badge = this.Q<LocLabel>(UiNames.CombatantBadge);
            _name = this.Q<LocLabel>(UiNames.CombatantName);
            _preview = this.Q<LocLabel>(UiNames.CombatantPreview);
            _hp = this.Q<Meter>(UiNames.CombatantHp);
            _break = this.Q(UiNames.CombatantBreak);
            _poise = this.Q<Meter>(UiNames.CombatantPoise);
            _ward = this.Q<Meter>(UiNames.CombatantWard);
            _stance = this.Q<LocLabel>(UiNames.CombatantStance);
            _statuses = this.Q(UiNames.CombatantStatuses);
            RegisterCallback<PointerUpEvent>(_ => Activated?.Invoke(this));
            RegisterCallback<NavigationSubmitEvent>(e =>
            {
                if (e.target != this) return;
                Activated?.Invoke(this);
                e.StopPropagation();
            });
        }

        public CombatantData Data { get; private set; }

        public event Action<Combatant> Activated;

        public void Bind(CombatantData d)
        {
            Data = d;
            EnableInClassList(UiClasses.CombatantPlayer, d.Player);
            EnableInClassList(UiClasses.CombatantEnemy, !d.Player);
            EnableInClassList(UiClasses.CombatantDead, !d.Alive);
            EnableInClassList(UiClasses.CombatantTarget, d.Target);
            EnableInClassList(UiClasses.CombatantActing, d.Acting);
            EnableInClassList(UiClasses.CombatantStaggered, d.Staggered);
            _intent?.SetResolved(d.IntentText ?? string.Empty);
            if (_intentClass != null) _intent?.RemoveFromClassList(_intentClass);
            _intentClass = string.IsNullOrEmpty(d.IntentKind) ? null : UiClasses.IntentPrefix + d.IntentKind;
            if (_intentClass != null) _intent?.AddToClassList(_intentClass);
            if (_intent != null) _intent.style.visibility = string.IsNullOrEmpty(d.IntentText) ? Visibility.Hidden : Visibility.Visible;
            if (_art != null) _art.style.backgroundImage = d.Art != null ? new StyleBackground(d.Art) : new StyleBackground(StyleKeyword.None);
            Text(_block, d.BlockText);
            Text(_badge, d.BadgeText);
            _name?.SetResolved(d.Name);
            Text(_preview, d.PreviewText);
            _hp?.Set(d.Hp, d.MaxHp, d.ReferenceMaxHp);
            UiDom.Show(_break, d.HasBreakMeters && d.Alive);
            _poise?.Set(d.Poise, d.PoiseMax);
            _ward?.Set(d.Ward, d.WardMax);
            Text(_stance, d.StanceText);
            if (_statuses == null) return;
            _statuses.Clear();
            foreach (var status in d.Statuses)
            {
                var chip = new LocLabel();
                chip.SetResolved(status);
                chip.AddToClassList(UiClasses.StatusChip);
                chip.AddToClassList(UiClasses.ValueText);
                chip.pickingMode = PickingMode.Ignore;
                _statuses.Add(chip);
            }
        }

        private static void Text(LocLabel label, string text)
        {
            if (label == null) return;
            label.SetResolved(text ?? string.Empty);
            UiDom.Show(label, !string.IsNullOrEmpty(text));
        }
    }
}
