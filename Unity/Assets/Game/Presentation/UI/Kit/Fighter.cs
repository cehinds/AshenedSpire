using System;
using System.Collections.Generic;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
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

        /// <summary>The rating rows top to bottom (ui/components.json fighter.ratings: Core2UiValues.Rating* ids).</summary>
        public IReadOnlyList<string> RatingOrder = Array.Empty<string>();
    }

    /// <summary>
    /// Fighter (docs/design/04 §1, W-07 for a Core 2 fight): the intent chip with the knowledge label and the Perception predict control, the figure with its hit-chance and state
    /// badges, the name and the target preview with the damage receipt, then the ratings in the data's order (Evade, Poise, Ward, Block, HP), the stance, the stack chips
    /// (Precision, Barrier, Counter) and the status chips. Focusing or clicking it and pressing Submit raise Activated; the predict control raises PredictPressed.
    /// </summary>
    [UxmlElement]
    public partial class Fighter : VisualElement
    {
        private readonly LocLabel _intent;
        private readonly LocButton _predict;
        private readonly VisualElement _art;
        private readonly LocLabel _hit;
        private readonly LocLabel _badge;
        private readonly LocLabel _name;
        private readonly LocLabel _preview;
        private readonly LocLabel _receipt;
        private readonly VisualElement _ratings;
        private readonly LocLabel _evade;
        private readonly Meter _poise;
        private readonly Meter _ward;
        private readonly LocLabel _block;
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
            _predict = this.Q<LocButton>(UiNames.FighterPredict);
            _art = this.Q(UiNames.FighterArt);
            _hit = this.Q<LocLabel>(UiNames.FighterHit);
            _badge = this.Q<LocLabel>(UiNames.FighterBadge);
            _name = this.Q<LocLabel>(UiNames.FighterName);
            _preview = this.Q<LocLabel>(UiNames.FighterPreview);
            _receipt = this.Q<LocLabel>(UiNames.FighterReceipt);
            _ratings = this.Q(UiNames.FighterRatings);
            _evade = this.Q<LocLabel>(UiNames.FighterEvade);
            _poise = this.Q<Meter>(UiNames.FighterPoise);
            _ward = this.Q<Meter>(UiNames.FighterWard);
            _block = this.Q<LocLabel>(UiNames.FighterBlock);
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
            if (_intentClass != null) _intent?.RemoveFromClassList(_intentClass);
            _intentClass = string.IsNullOrEmpty(d.IntentKind) ? null : UiClasses.IntentPrefix + d.IntentKind;
            if (_intentClass != null) _intent?.AddToClassList(_intentClass);
            if (_intent != null) _intent.style.visibility = string.IsNullOrEmpty(d.IntentText) ? Visibility.Hidden : Visibility.Visible;
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
            Text(_evade, d.EvadeText);
            Text(_block, d.BlockText);
            UiDom.Show(_poise, d.HasPoise && d.Alive);
            _poise?.Set(d.Poise, d.PoiseMax);
            UiDom.Show(_ward, d.HasWard && d.Alive);
            _ward?.Set(d.Ward, d.WardMax);
            _hp?.Set(d.Hp, d.MaxHp, d.ReferenceMaxHp);
            Text(_stance, d.StanceText);
            Chips(_stacks, d.Stacks);
            Chips(_statuses, d.Statuses);
        }

        /// <summary>Puts the rating rows in the order the data names (each id once; an unknown id is skipped).</summary>
        private void Arrange(IReadOnlyList<string> order)
        {
            var key = string.Join(UiFormats.ListJoin, order);
            if (_ratings == null || key == _order) return;
            _order = key;
            foreach (var id in order)
            {
                VisualElement row = id == Core2UiValues.RatingEvade ? _evade
                    : id == Core2UiValues.RatingPoise ? _poise
                    : id == Core2UiValues.RatingWard ? _ward
                    : id == Core2UiValues.RatingBlock ? _block
                    : id == Core2UiValues.RatingHp ? _hp : null;
                if (row != null) _ratings.Add(row);
            }
        }

        private static void Text(LocLabel label, string text)
        {
            if (label == null) return;
            label.SetResolved(text ?? string.Empty);
            UiDom.Show(label, !string.IsNullOrEmpty(text));
        }

        private static void Chips(VisualElement host, List<string> chips)
        {
            if (host == null) return;
            host.Clear();
            foreach (var text in chips)
            {
                var chip = new LocLabel();
                chip.SetResolved(text);
                chip.AddToClassList(UiClasses.StatusChip);
                chip.AddToClassList(UiClasses.ValueText);
                chip.pickingMode = PickingMode.Ignore;
                host.Add(chip);
            }
        }
    }
}
