using System;
using System.Collections.Generic;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>One rating or status chip under the HP bar: the icon with its number (the web's chips), else the text alone when the art is missing.</summary>
    public sealed class ChipData
    {
        public Texture2D Icon;
        public string Text;
        public bool Gold;
    }

    /// <summary>Display-ready values for one fighter of a Core 2 fight (text resolved by Core2CombatText, numbers from the combat view).</summary>
    public sealed class FighterData
    {
        public string Id;
        public string Name;
        public Texture2D Art;
        public bool Player;
        public bool Alive = true;
        public int Hp;
        public int MaxHp;
        public int ReferenceMaxHp;
        public bool HasPoise;
        public int Poise;
        public int PoiseMax;
        public bool HasWard;
        public int Ward;
        public int WardMax;
        public string EvadeText;
        public string BlockText;
        public string IntentText;
        public string IntentKind;
        public Texture2D IntentIcon;
        public bool CanPredict;
        public string HitText;
        public string PreviewText;
        public string ReceiptText;
        public string BadgeText;
        public string StanceText;
        public bool Staggered;
        public bool Target;
        public bool Acting;
        public List<string> Stacks = new List<string>();
        public List<string> Statuses = new List<string>();

        /// <summary>The rating chips (Evade, Block), the stack chips (Precision, Barrier, Counter) and the status chips, in that order (VP-4: icons from the art lookup).</summary>
        public List<ChipData> RatingChips = new List<ChipData>();
        public List<ChipData> StackChips = new List<ChipData>();
        public List<ChipData> StatusChips = new List<ChipData>();

        /// <summary>The rating rows top to bottom (ui/components.json fighter.ratings: Core2UiValues.Rating* ids).</summary>
        public IReadOnlyList<string> RatingOrder = Array.Empty<string>();
    }

    /// <summary>
    /// Fighter (docs/design/04 §1, W-07 for a Core 2 fight; VP-4 look, D-700): the overhead (intent range and icon with the Perception predict control, the long HP bar,
    /// the break meters, then the rating, stack and status chips) over the figure with its hit-chance and state badges and its ground shadow; below the shadow the
    /// name, the target preview and the damage receipt. The rating rows follow the data's order (ui/components.json fighter.ratings: Evade, Poise, Ward, Block, HP):
    /// Evade and Block are chips, Poise and Ward thin meters under the HP bar, HP is the bar itself. Focusing or clicking it and pressing Submit raise Activated;
    /// the predict control raises PredictPressed.
    /// </summary>
    [UxmlElement]
    public partial class Fighter : VisualElement
    {
        private readonly LocLabel _intent;
        private readonly VisualElement _intentIcon;
        private readonly LocButton _predict;
        private readonly VisualElement _art;
        private readonly LocLabel _hit;
        private readonly LocLabel _badge;
        private readonly LocLabel _name;
        private readonly LocLabel _preview;
        private readonly LocLabel _receipt;
        private readonly VisualElement _ratings;
        private readonly VisualElement _break;
        private readonly Meter _poise;
        private readonly Meter _ward;
        private readonly Meter _hp;
        private readonly LocLabel _stance;
        private readonly VisualElement _stacks;
        private readonly VisualElement _statuses;
        private string _intentClass;
        private string _order;

        public Fighter()
        {
            AddToClassList(nameof(Combatant).ToLowerInvariant());
            AddToClassList(nameof(Fighter).ToLowerInvariant());
            focusable = true;
            UiDom.CloneTemplate(this, UiResources.KitFighter);
            _intent = this.Q<LocLabel>(UiNames.FighterIntent);
            _intentIcon = this.Q(UiNames.FighterIntentIcon);
            _predict = this.Q<LocButton>(UiNames.FighterPredict);
            _art = this.Q(UiNames.FighterArt);
            _hit = this.Q<LocLabel>(UiNames.FighterHit);
            _badge = this.Q<LocLabel>(UiNames.FighterBadge);
            _name = this.Q<LocLabel>(UiNames.FighterName);
            _preview = this.Q<LocLabel>(UiNames.FighterPreview);
            _receipt = this.Q<LocLabel>(UiNames.FighterReceipt);
            _ratings = this.Q(UiNames.FighterRatings);
            _break = this.Q(UiNames.FighterBreak);
            _poise = this.Q<Meter>(UiNames.FighterPoise);
            _ward = this.Q<Meter>(UiNames.FighterWard);
            _hp = this.Q<Meter>(UiNames.FighterHp);
            _stance = this.Q<LocLabel>(UiNames.FighterStance);
            _stacks = this.Q(UiNames.FighterStacks);
            _statuses = this.Q(UiNames.FighterStatuses);
            if (_predict != null) _predict.clicked += () => PredictPressed?.Invoke(this);
            RegisterCallback<PointerUpEvent>(_ => Activated?.Invoke(this));
            RegisterCallback<NavigationSubmitEvent>(e =>
            {
                if (e.target != this) return;
                Activated?.Invoke(this);
                e.StopPropagation();
            });
        }

        public FighterData Data { get; private set; }

        public LocButton PredictButton => _predict;

        public event Action<Fighter> Activated;

        public event Action<Fighter> PredictPressed;

        public void Bind(FighterData d)
        {
            Data = d;
            EnableInClassList(UiClasses.CombatantPlayer, d.Player);
            EnableInClassList(UiClasses.CombatantEnemy, !d.Player);
            EnableInClassList(UiClasses.CombatantDead, !d.Alive);
            EnableInClassList(UiClasses.CombatantTarget, d.Target);
            EnableInClassList(UiClasses.CombatantActing, d.Acting);
            EnableInClassList(UiClasses.CombatantStaggered, d.Staggered);
            _intent?.SetResolved(d.IntentText ?? string.Empty);
            if (_intentClass != null)
            {
                _intent?.RemoveFromClassList(_intentClass);
                _intentIcon?.RemoveFromClassList(_intentClass);
            }
            _intentClass = string.IsNullOrEmpty(d.IntentKind) ? null : UiClasses.IntentPrefix + d.IntentKind;
            if (_intentClass != null)
            {
                _intent?.AddToClassList(_intentClass);
                _intentIcon?.AddToClassList(_intentClass);
            }
            var hasIntent = !string.IsNullOrEmpty(d.IntentText);
            if (_intent != null) _intent.style.visibility = hasIntent ? Visibility.Visible : Visibility.Hidden;
            if (_intentIcon != null)
            {
                _intentIcon.style.backgroundImage = d.IntentIcon != null ? new StyleBackground(d.IntentIcon) : new StyleBackground(StyleKeyword.None);
                UiDom.Show(_intentIcon, hasIntent && d.IntentIcon != null);
            }
            if (_predict != null)
            {
                UiDom.Show(_predict, d.CanPredict);
                _predict.focusable = d.CanPredict;
            }
            if (_art != null) _art.style.backgroundImage = d.Art != null ? new StyleBackground(d.Art) : new StyleBackground(StyleKeyword.None);
            Text(_hit, d.HitText);
            Text(_badge, d.BadgeText);
            _name?.SetResolved(d.Name);
            Text(_preview, d.PreviewText);
            Text(_receipt, d.ReceiptText);
            Arrange(d.RatingOrder);
            UiDom.Show(_poise, d.HasPoise && d.Alive);
            _poise?.Set(d.Poise, d.PoiseMax);
            UiDom.Show(_ward, d.HasWard && d.Alive);
            _ward?.Set(d.Ward, d.WardMax);
            _hp?.Set(d.Hp, d.MaxHp, d.ReferenceMaxHp);
            Text(_stance, d.StanceText);
            Chips(_ratings, d.RatingChips);
            Chips(_stacks, d.StackChips);
            Chips(_statuses, d.StatusChips);
        }

        /// <summary>Puts the break meters (Poise, Ward) in the order the data names (each id once; the other ratings are chips and the HP bar has its own place).</summary>
        private void Arrange(IReadOnlyList<string> order)
        {
            var key = string.Join(UiFormats.ListJoin, order);
            if (_break == null || key == _order) return;
            _order = key;
            foreach (var id in order)
            {
                var meter = id == Core2UiValues.RatingPoise ? _poise : id == Core2UiValues.RatingWard ? _ward : null;
                if (meter != null) _break.Add(meter);
            }
        }

        private static void Text(LocLabel label, string text)
        {
            if (label == null) return;
            label.SetResolved(text ?? string.Empty);
            UiDom.Show(label, !string.IsNullOrEmpty(text));
        }

        /// <summary>The chips of one row: an icon with its number, or the text alone when the art is missing.</summary>
        private static void Chips(VisualElement host, List<ChipData> chips)
        {
            if (host == null) return;
            host.Clear();
            foreach (var data in chips)
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
                host.Add(chip);
            }
        }
    }
}
