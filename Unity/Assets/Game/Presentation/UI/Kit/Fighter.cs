using System;
using System.Collections.Generic;
using Ashen.App.Ui;
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

        /// <summary>What pointing at the chip explains (the web's tray icon tooltip: the status's name with its stacks, and its tooltip); null for a chip with nothing to say.</summary>
        public string TipTitle;
        public string TipBody;
    }

    /// <summary>
    /// The tooltip layer and timing the board's small marks (a fighter's status chips, the relic pips) share, set once by the screen that draws them (the web's iconTray: hover or focus
    /// explains after the shared delay). A chip with words attaches a tooltip and takes pointer events; one without stays inert.
    /// </summary>
    public static class ChipTips
    {
        public static VisualElement Overlay;
        public static string Size;
        public static int DelayMs;

        /// <summary>Makes a chip or pip explain itself when hovered or focused.</summary>
        public static void Attach(VisualElement mark, string title, string body)
        {
            if (Overlay == null || mark == null || string.IsNullOrEmpty(body) && string.IsNullOrEmpty(title)) return;
            mark.pickingMode = PickingMode.Position;
            Tooltip.AttachResolved(mark, Overlay, Size, title, body, DelayMs);
        }

        /// <summary>
        /// Makes a mark that lives across redraws (a meter, the intent) explain itself, and changes what it says as the fight moves: the tooltip is attached once and its words replaced.
        /// No words detaches nothing but lets the pointer pass.
        /// </summary>
        public static void Retell(VisualElement mark, ref Tooltip tip, string title, string body)
        {
            if (Overlay == null || mark == null) return;
            var speaks = !(string.IsNullOrEmpty(body) && string.IsNullOrEmpty(title));
            if (tip == null)
            {
                if (!speaks) return;
                mark.pickingMode = PickingMode.Position;
                tip = Tooltip.AttachResolved(mark, Overlay, Size, title, body, DelayMs);
                return;
            }
            if (speaks) tip.SetText(title, body);
            tip.style.display = speaks ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }

    /// <summary>Display-ready values for one fighter of a Core 2 fight (text resolved by Core2CombatText, numbers from the combat view).</summary>
    public sealed class FighterData
    {
        public string Id;
        public string Name;
        public Texture2D Art;

        /// <summary>The figure's rest frame when it is not the base art (an enemy's state pose: guard, afflicted, wounded, defeated; VP-5), and the share of the figure box it fills (the web's per-enemy state scale).</summary>
        public Texture2D RestArt;
        public double RestScale = 1;
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

        /// <summary>What pointing at the intent, the Poise meter or the Ward meter says (the web's combat target HUD and health-badge tooltips); null for nothing to say.</summary>
        public bool IntentReduced;

        /// <summary>
        /// The player figure as a pose stage (P-1, the web's "animated" figure style): the geometry of its frames, the class and tint they are of, and the texture of a pose by name (null when the
        /// frame is not imported). Null for an enemy and for a class whose frames are not shipped; the figure keeps its base art then.
        /// </summary>
        public PoseData Poses;
        public string PoseClass;
        public string PoseTint;
        public Func<string, Texture2D> PoseTexture;
        public string IntentTipTitle;
        public string IntentTipBody;
        public string PoiseTipTitle;
        public string PoiseTipBody;
        public string WardTipTitle;
        public string WardTipBody;
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
        private readonly VisualElement _figure;
        private readonly VisualElement _pose;
        private string _intentClass;
        private Tooltip _intentTip;
        private Tooltip _poiseTip;
        private Tooltip _wardTip;
        private string _order;
        private FxRun _motion;
        private IVisualElementScheduledItem _settle;
        private Texture2D _baseArt;
        private Texture2D _restArt;
        private double _restScale = 1;
        private bool _posing;
        private bool _placed;
        private string _stagePose = PoseKeys.Idle;
        private int _swings;
        private double _poseScale = 1;

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
            // The figure and its pose frame sit under the hit chance and badge: the recoil, the lunge and the stagger wobble move this one element, as the web moves .sprite's first child.
            _figure = new VisualElement { pickingMode = PickingMode.Ignore };
            _figure.AddToClassList(UiClasses.FighterFigure);
            _pose = new VisualElement { pickingMode = PickingMode.Ignore };
            _pose.AddToClassList(UiClasses.FighterPose);
            _pose.AddToClassList(UiClasses.Hidden);
            _figure.Add(_pose);
            _art?.Insert(0, _figure);
            _art?.RegisterCallback<GeometryChangedEvent>(_ => FitPose());
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
            _intent?.EnableInClassList(UiClasses.IntentReduced, d.IntentReduced);
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
            _baseArt = d.Art;
            _restArt = d.RestArt;
            _restScale = d.RestScale;
            if (_posing) _figure.style.backgroundImage = new StyleBackground(StyleKeyword.None);
            else ShowRest();
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
            ChipTips.Retell(_poise, ref _poiseTip, d.PoiseTipTitle, d.PoiseTipBody);
            ChipTips.Retell(_ward, ref _wardTip, d.WardTipTitle, d.WardTipBody);
            ChipTips.Retell(_intent, ref _intentTip, d.IntentTipTitle, d.IntentTipBody);
            _hp?.Set(d.Hp, d.MaxHp, d.ReferenceMaxHp);
            Text(_stance, d.StanceText);
            Chips(_ratings, d.RatingChips);
            Chips(_stacks, d.StackChips);
            Chips(_statuses, d.StatusChips);
        }

        // ------------------------------------------------------------------ feedback (VP-5, D-750): pose swaps, recoil, lunge, wobble

        /// <summary>Moves the HP bar alone (a number has landed; the rest of the fighter is already drawn).</summary>
        public void SetHp(int hp, int maxHp)
        {
            if (Data != null) Data.Hp = hp;
            _hp?.Set(hp, maxHp, Data?.ReferenceMaxHp ?? maxHp);
        }

        /// <summary>The figure's box: the anchor the floating numbers, the words and the effect frames are placed from (the web's .sprite).</summary>
        public VisualElement ArtBox => _art ?? (VisualElement)this;

        /// <summary>The overhead block (intent, HP bar, chips) the words keep clear of.</summary>
        public VisualElement OverheadBox => this.Q(UiNames.FighterOverhead);

        private void ShowRest()
        {
            if (_restArt != null) ShowFrame(_restArt, _restScale);
            else if (!ShowStage(PoseKeys.Idle)) HideFrame();
        }

        /// <summary>
        /// Draws one pose of the player's pose stage at its own place on the shared canvas (PoseAnimator.js place): the idle frame's floor line on the bottom of the figure box and its pelvis
        /// on the box centre, every other frame at its crop. False when this figure has no stage or the frame is not imported.
        /// </summary>
        private bool ShowStage(string pose)
        {
            var d = Data;
            if (d?.Poses == null || d.PoseTexture == null || _art == null) return false;
            var idle = d.Poses.Frame(d.PoseClass, PoseKeys.Idle, d.PoseTint);
            var frame = d.Poses.Frame(d.PoseClass, pose, d.PoseTint);
            var texture = d.PoseTexture(pose);
            var h = _art.resolvedStyle.height;
            var w = _art.resolvedStyle.width;
            if (idle == null || frame == null || texture == null) return false;
            var aspect = float.IsNaN(h) || float.IsNaN(w) || h <= 0 ? 1.0 : w / h;
            var at = d.Poses.Place(idle, frame, aspect);
            _placed = true;
            _stagePose = pose;
            _pose.style.backgroundImage = new StyleBackground(texture);
            _pose.style.left = Length.Percent((float)(at.Left * Ashen.Generated.P1Math.Percent));
            _pose.style.top = Length.Percent((float)(at.Top * Ashen.Generated.P1Math.Percent));
            _pose.style.width = Length.Percent((float)(at.Width * Ashen.Generated.P1Math.Percent));
            _pose.style.height = Length.Percent((float)(at.Height * Ashen.Generated.P1Math.Percent));
            _pose.style.bottom = StyleKeyword.Auto;
            _pose.RemoveFromClassList(UiClasses.Hidden);
            _figure.style.backgroundImage = new StyleBackground(StyleKeyword.None);
            return true;
        }

        /// <summary>
        /// Holds a pose of the player's stage for <paramref name="ms"/>, then settles to idle (PoseAnimator.js play). 'attack' throws the next swing of the rotation the figure keeps (attack1 to attack4),
        /// the others (guard, hit) are frames of their own. False when the figure has no such frame; it stays as it is then.
        /// </summary>
        public bool PlayStage(string pose, double ms)
        {
            var d = Data;
            if (d?.Poses == null) return false;
            if (pose == PoseKeys.Attack)
            {
                var swings = d.Poses.Throwable(d.PoseClass, d.PoseTint);
                if (swings.Count == 0) return false;
                pose = swings[_swings % swings.Count];
            }
            var previous = _settle;
            if (!ShowStage(pose)) return false;
            _swings += pose.StartsWith(PoseKeys.Attack, StringComparison.Ordinal) ? 1 : 0;
            previous?.Pause();
            _posing = true;
            _settle = schedule.Execute(Settle).StartingIn((long)Math.Max(d.Poses.MinimumPlayMs, ms));
            return true;
        }

        private void ShowFrame(Texture2D frame, double scale)
        {
            _placed = false;
            _pose.style.top = StyleKeyword.Null;
            _pose.style.bottom = StyleKeyword.Null;
            _pose.style.backgroundImage = new StyleBackground(frame);
            _poseScale = scale;
            FitPose();
            _pose.RemoveFromClassList(UiClasses.Hidden);
            _figure.style.backgroundImage = new StyleBackground(StyleKeyword.None);
        }

        /// <summary>A pose frame is a square canvas as tall as its share of the figure box (the web sets its height to the per-enemy scale and lets the width follow), centred on the box.</summary>
        private void FitPose()
        {
            if (_art == null) return;
            if (_placed) { ShowStage(_stagePose); return; }
            var h = _art.resolvedStyle.height;
            var w = _art.resolvedStyle.width;
            if (float.IsNaN(h) || float.IsNaN(w) || h <= 0) return;
            var edge = h * (float)_poseScale;
            _pose.style.width = edge;
            _pose.style.height = edge;
            _pose.style.left = (w - edge) * (float)UiMath.Half;
        }

        /// <summary>Makes a frame the figure's rest frame for good (a defeated enemy stays down).</summary>
        public void Pin(Texture2D frame, double scale)
        {
            Rest();
            _settle?.Pause();
            _posing = false;
            _restArt = frame;
            _restScale = scale;
            ShowRest();
        }

        private void HideFrame()
        {
            _pose.AddToClassList(UiClasses.Hidden);
            _figure.style.backgroundImage = _baseArt != null ? new StyleBackground(_baseArt) : new StyleBackground(StyleKeyword.None);
        }

        /// <summary>
        /// Shows another frame of the figure for <paramref name="ms"/>, then returns to its rest frame (enemyPoseStage.js play): the attack pose while it swings, the hurt frame while it recoils.
        /// A figure with no such art (the player's poses are not imported) stays as it is.
        /// </summary>
        public bool PlayPose(Texture2D frame, double scale, double ms)
        {
            if (frame == null) return false;
            _settle?.Pause();
            _posing = true;
            ShowFrame(frame, scale);
            _settle = schedule.Execute(Settle).StartingIn((long)ms);
            return true;
        }

        /// <summary>Extends the pose being held (the hit-stop).</summary>
        public void HoldPose(double ms)
        {
            if (!_posing || _settle == null) return;
            _settle.Pause();
            _settle = schedule.Execute(Settle).StartingIn((long)ms);
        }

        /// <summary>Back to the rest frame (the base art, or the state pose the fighter's Block, statuses and HP call for).</summary>
        public void Settle()
        {
            _settle?.Pause();
            _settle = null;
            _posing = false;
            ShowRest();
        }

        /// <summary>The red tint of a hit, on whichever frame shows (the web's stepped filter on the figure).</summary>
        private void Tint(Color? color)
        {
            var value = color.HasValue ? new StyleColor(color.Value) : new StyleColor(StyleKeyword.Null);
            _pose.style.unityBackgroundImageTintColor = value;
            _figure.style.unityBackgroundImageTintColor = value;
        }

        /// <summary>Plays a motion track on the figure: recoil and lunge slide it, the wobble turns it; <paramref name="tinted"/> also reddens it while the track says so.</summary>
        public void Play(FxMotion motion, TrackDef track, double ms, bool tinted, double holdMs = 0, Action done = null)
        {
            _motion?.Stop();
            _motion = motion.Play(this, track, ms, pose =>
            {
                FxMotion.Apply(_figure, pose);
                if (tinted) Tint(pose.Tinted ? motion.HitTint : (Color?)null);
            }, () =>
            {
                Rest();
                done?.Invoke();
            }, holdMs: holdMs);
        }

        /// <summary>Ends the motion in progress and puts the figure back (a skip, or a new fight).</summary>
        public void Rest()
        {
            _motion?.Stop();
            _motion = null;
            FxMotion.Reset(_figure);
            Tint(null);
        }

        /// <summary>Drops every held pose and motion at once (the web's click-skip settles the figures).</summary>
        public void Calm()
        {
            Rest();
            Settle();
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
                ChipTips.Attach(chip, data.TipTitle, data.TipBody);
                host.Add(chip);
            }
        }
    }
}
