using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// The combat feedback layer (VP-5, D-750): a transparent overlay above the board on which the web's floating numbers, on-character words, banners, impact marks and effect
    /// frames play (src/ui/fx.js, styles/combat.css). It draws what <see cref="Core2Feedback"/> planned: it knows a fighter only as an anchor box, never the rules. Placement is the
    /// web's (a float is centred on its anchor, a quarter of the way down, clamped to the layer; a word keeps clear of the fighter's overhead, three lanes of 34 px); the motion is the
    /// web's keyframes (<see cref="FxMotion"/>); how long each lives is the pace's (<see cref="CombatPace"/>).
    /// </summary>
    public sealed class FxLayer : VisualElement
    {
        private sealed class Word
        {
            public string Key;
            public int Arrival;
            public double Rank;
            public double Start = double.PositiveInfinity;
            public double End;
            public int Lane;
            public VisualElement Element;
            public IVisualElementScheduledItem Starter;
            public IVisualElementScheduledItem Ender;
            public Func<VisualElement> Build;
        }

        private readonly List<FxRun> _runs = new List<FxRun>();
        private readonly List<IVisualElementScheduledItem> _timers = new List<IVisualElementScheduledItem>();
        private readonly Dictionary<string, List<Word>> _words = new Dictionary<string, List<Word>>(StringComparer.Ordinal);
        private readonly List<VisualElement> _scenery = new List<VisualElement>();
        private FxRun _shake;
        private double _shakeUntil;
        private double _shakeNow;
        private int _arrival;

        public FxLayer()
        {
            name = UiNames.FxLayer;
            AddToClassList(UiClasses.FxLayer);
            pickingMode = PickingMode.Ignore;
        }

        public UiContext Ui { get; set; }
        public FxMotion Motion { get; set; }
        public CombatPace Pace { get; set; }

        /// <summary>The fighter a cue names (null for one that is gone).</summary>
        public Func<string, Fighter> AnchorFor { get; set; } = _ => null;

        private FeedbackDefs Defs => Motion.Defs;

        /// <summary>The elements a hit shakes: the scenery (the web shakes the backdrop and the effect layer, never the actors or the controls).</summary>
        public void SetScenery(IEnumerable<VisualElement> scenery)
        {
            _scenery.Clear();
            _scenery.AddRange(scenery.Where(e => e != null));
        }

        // ------------------------------------------------------------------ placement

        /// <summary>A box in this layer's own space (the web's anchorLocalBox).</summary>
        private Rect LocalBox(VisualElement anchor)
        {
            var world = anchor.worldBound;
            var origin = this.WorldToLocal(world.position);
            return new Rect(origin, world.size);
        }

        private void Track(FxRun run) => _runs.Add(run);

        private void After(double ms, Action action)
        {
            var timer = schedule.Execute(action).StartingIn((long)Math.Max(0, ms));
            _timers.Add(timer);
        }

        /// <summary>Removes <paramref name="el"/> once it has lived <paramref name="ms"/>.</summary>
        private void Expire(VisualElement el, double ms) => After(ms, () => el.RemoveFromHierarchy());

        // ------------------------------------------------------------------ floating numbers (fx.js floatNum)

        public void Float(Fighter anchor, FxCue cue)
        {
            if (anchor == null) return;
            var f = Defs.Float;
            var box = LocalBox(anchor.ArtBox);
            var host = new VisualElement { pickingMode = PickingMode.Ignore };
            host.AddToClassList(UiClasses.FxFloat);
            var label = new Label(cue.Text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList(UiClasses.FxFloatText);
            label.AddToClassList(UiClasses.HeaderText);
            label.AddToClassList(FloatClass(cue.Float));
            host.Add(label);
            var jitter = cue.Jitter ? UnityEngine.Random.Range((float)-f.JitterPx, (float)f.JitterPx) : 0f;
            var centre = box.center.x + (float)cue.X + jitter;
            var top = box.y + box.height * (float)f.TopShare + (float)cue.Y;
            host.style.left = centre;
            host.style.top = top;
            Add(host);
            // Keep it inside the layer once its real size is known (the web's clampBox, pad 6): the centre moves, never the number's own width.
            host.RegisterCallback<GeometryChangedEvent>(_ => Clamp(host, centre, top, f.ClampPad));
            var track = Pace != null && Pace.Reduced ? Defs.Track(FxTrack.FloatUp) : Defs.Track(FxTrack.NumPop);
            Track(Motion.Play(label, track, track.Ms, p => FxMotion.Apply(label, p), pop: cue.Pop));
            Expire(host, f.LifeMs);
        }

        private void Clamp(VisualElement host, float centre, float top, double pad)
        {
            var width = host.resolvedStyle.width;
            var height = host.resolvedStyle.height;
            if (float.IsNaN(width) || float.IsNaN(height) || layout.width <= 0) return;
            var half = width * (float)UiMath.Half;
            var lo = (float)pad + half;
            var hi = Math.Max(lo, layout.width - (float)pad - half);
            host.style.left = Math.Min(Math.Max(centre, lo), hi);
            var topLo = (float)pad;
            var topHi = Math.Max(topLo, layout.height - (float)pad - height);
            host.style.top = Math.Min(Math.Max(top, topLo), topHi);
        }

        private static string FloatClass(FxFloat kind)
        {
            switch (kind)
            {
                case FxFloat.DamageSmall: return UiClasses.FxNumDamageSmall;
                case FxFloat.DamageHeavy: return UiClasses.FxNumDamageHeavy;
                case FxFloat.DamageCrit: return UiClasses.FxNumDamageCrit;
                case FxFloat.DamagePct20: return UiClasses.FxNumDamagePct20;
                case FxFloat.DamagePct35: return UiClasses.FxNumDamagePct35;
                case FxFloat.Barrier: return UiClasses.FxNumBarrier;
                case FxFloat.Ward: return UiClasses.FxNumWard;
                case FxFloat.Poise: return UiClasses.FxNumPoise;
                case FxFloat.Immune: return UiClasses.FxNumImmune;
                case FxFloat.Guard: return UiClasses.FxNumGuard;
                case FxFloat.GuardSmall: return UiClasses.FxNumGuardSmall;
                case FxFloat.Heal: return UiClasses.FxNumHeal;
                case FxFloat.Burst: return UiClasses.FxNumBurst;
                default: return UiClasses.FxNumDamage;
            }
        }

        // ------------------------------------------------------------------ on-character words (fx.js callout, planWordQueue, layoutCallouts)

        /// <summary>
        /// Shows a word over a fighter: the same word already showing or waiting on it is not repeated, at most three show at once in three lanes starting one step apart in
        /// priority order, and a fourth waits for the earliest to finish. <paramref name="text"/> is the word, <paramref name="tone"/> its tone class suffix (a feedback tone) and
        /// <paramref name="colour"/> a status word's own colour.
        /// </summary>
        public void Callout(string fighterId, Fighter anchor, FxCue cue, string text, Color? colour, bool status)
        {
            if (anchor == null || string.IsNullOrEmpty(text)) return;
            var defs = Defs.Callout;
            var now = FxClock.Now();
            if (!_words.TryGetValue(fighterId, out var queue)) _words[fighterId] = queue = new List<Word>();
            queue.RemoveAll(w => w.End > 0 && w.End <= now);
            if (queue.Any(w => w.Key == cue.Key)) return;
            var action = cue.Callout == FxCallout.None && !status;
            var rank = CalloutPlan.Rank(defs, cue.Callout == FxCallout.None ? null : FxCalloutNames.ToWire(cue.Callout), status);
            var life = Pace != null ? Pace.CalloutMs : defs.LifeMs;
            var word = new Word { Key = cue.Key, Arrival = _arrival++, Rank = rank };
            word.Build = () =>
            {
                var host = new VisualElement { pickingMode = PickingMode.Ignore };
                host.AddToClassList(UiClasses.FxCallout);
                if (action) host.AddToClassList(UiClasses.FxCalloutAction);
                var label = new Label(text) { pickingMode = PickingMode.Ignore };
                label.AddToClassList(UiClasses.FxCalloutText);
                label.AddToClassList(UiClasses.HeaderText);
                label.AddToClassList(UiClasses.TextShadowed);
                if (colour.HasValue) label.style.color = colour.Value;
                else if (!string.IsNullOrEmpty(cue.Tone)) label.AddToClassList(string.Format(CultureInfo.InvariantCulture, FxFormats.ToneClass, cue.Tone));
                host.Add(label);
                host.userData = label;
                return host;
            };
            queue.Add(word);
            Layout(fighterId, anchor, queue, now, life);
        }

        private void Layout(string fighterId, Fighter anchor, List<Word> queue, double now, double life)
        {
            var defs = Defs.Callout;
            var active = queue.Where(w => w.Start <= now).ToList();
            var pending = queue.Where(w => w.Start > now).ToList();
            var lanes = new double[defs.Lanes];
            for (var l = 0; l < lanes.Length; l++) lanes[l] = Math.Max(0, active.Where(w => w.Lane == l).Select(w => w.End - now).DefaultIfEmpty(0).Max());
            double? after = active.Count > 0 ? active.Max(w => w.Start) - now : (double?)null;
            var plan = CalloutPlan.Plan(pending.Select(w => new CalloutWord { Arrival = w.Arrival, Rank = w.Rank }).ToList(), lanes, defs.StepMs, life, after);
            foreach (var slot in plan)
            {
                var word = pending.First(p => p.Arrival == slot.Arrival);
                word.Start = now + slot.Start;
                word.Lane = slot.Lane;
                word.End = word.Start + life;
                word.Starter?.Pause();
                word.Ender?.Pause();
                var captured = word;
                captured.Starter = schedule.Execute(() => Start(fighterId, anchor, queue, captured, life)).StartingIn((long)slot.Start);
                captured.Ender = schedule.Execute(() => captured.Element?.RemoveFromHierarchy()).StartingIn((long)(slot.Start + life));
                _timers.Add(captured.Starter);
                _timers.Add(captured.Ender);
            }
        }

        private void Start(string fighterId, Fighter anchor, List<Word> queue, Word word, double life)
        {
            if (word.Element != null || anchor.panel == null) return;
            word.Element = word.Build();
            Add(word.Element);
            word.Element.RegisterCallback<GeometryChangedEvent>(_ => Place(anchor, queue));
            Place(anchor, queue);
            var label = (VisualElement)word.Element.userData;
            var track = Pace != null && Pace.Reduced ? Defs.Track(FxTrack.CalloutFade) : Defs.Track(FxTrack.CalloutRise);
            Track(Motion.Play(label, track, life, p => FxMotion.Apply(label, p)));
        }

        /// <summary>fx.js layoutCallouts: the words sit above the figure's head, or below the overhead HUD that covers it, stacked a slot (34 px) apart by lane.</summary>
        private void Place(Fighter anchor, List<Word> queue)
        {
            var defs = Defs.Callout;
            var box = LocalBox(anchor.ArtBox);
            var floor = OverheadFloor(anchor, box);
            var shown = queue.Where(w => w.Element != null && w.Element.parent == this).ToList();
            if (shown.Count == 0) return;
            var firstHeight = shown[0].Element.resolvedStyle.height;
            var firstH = float.IsNaN(firstHeight) || firstHeight <= 0 ? (float)defs.DefaultHeight : firstHeight;
            var baseLine = floor == null ? box.y + (float)defs.TopInset : Math.Max(box.y + (float)defs.TopInset, floor.Value + (float)defs.FloorGap + firstH + (float)defs.RisePx);
            var centre = box.center.x;
            foreach (var word in shown)
            {
                var slot = floor == null ? defs.Lanes - 1 - word.Lane : word.Lane;
                var bottom = baseLine + (floor == null ? -1 : 1) * slot * (float)defs.SlotPx;
                var width = word.Element.resolvedStyle.width;
                var height = word.Element.resolvedStyle.height;
                var left = centre;
                if (!float.IsNaN(width) && layout.width > 0)
                {
                    var half = width * (float)UiMath.Half;
                    var lo = (float)defs.ClampPad + half;
                    left = Math.Min(Math.Max(centre, lo), Math.Max(lo, layout.width - (float)defs.ClampPad - half));
                }
                word.Element.style.left = left;
                word.Element.style.top = float.IsNaN(height) ? bottom : Math.Max(bottom, (float)defs.ClampPad + height);
            }
        }

        /// <summary>The lowest bottom edge of the fighter's overhead block when it sits over the head of the figure, else null.</summary>
        private float? OverheadFloor(Fighter anchor, Rect box)
        {
            var overhead = anchor.OverheadBox;
            if (overhead == null) return null;
            var o = LocalBox(overhead);
            if (!(o.width > 0 && o.height > 0)) return null;
            var overlapsX = o.x < box.xMax && o.xMax > box.x;
            var overHead = o.yMax >= box.y - (float)Defs.Callout.Reach && o.y < box.y + box.height * (float)UiMath.Half;
            return overlapsX && overHead ? o.yMax : (float?)null;
        }

        // ------------------------------------------------------------------ banners (fx.js banner)

        public void Banner(string text, bool turn, bool blood, double ms)
        {
            var host = new VisualElement { pickingMode = PickingMode.Ignore };
            host.AddToClassList(UiClasses.FxBanner);
            host.EnableInClassList(UiClasses.FxBannerTurn, turn);
            host.EnableInClassList(UiClasses.FxBannerBlood, blood);
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList(UiClasses.FxBannerText);
            label.AddToClassList(UiClasses.HeaderText);
            host.Add(label);
            Add(host);
            var life = ms > 0 ? ms : Defs.Track(FxTrack.BannerPop).Ms;
            var track = Defs.Track(FxTrack.BannerPop);
            Track(Motion.Play(label, track, life, p => FxMotion.Apply(label, p)));
            Expire(host, life);
        }

        // ------------------------------------------------------------------ one-shot marks (fx.js spawnFx)

        private VisualElement Mark(Fighter anchor, string cls, string text = null)
        {
            var box = LocalBox(anchor.ArtBox);
            var el = new Label(text ?? string.Empty) { pickingMode = PickingMode.Ignore };
            el.AddToClassList(cls);
            el.style.left = box.center.x;
            el.style.top = box.y + box.height * (float)Defs.Float.SpawnShare;
            Add(el);
            return el;
        }

        private void Spawn(Fighter anchor, string cls, FxTrack trackId, double ms, string text = null, string extraClass = null, double rotateJitter = 0)
        {
            if (anchor == null) return;
            var el = Mark(anchor, cls, text);
            if (extraClass != null) el.AddToClassList(extraClass);
            var track = Defs.Track(trackId);
            var offset = track.RotateJitter > 0 ? UnityEngine.Random.Range((float)-track.RotateJitter, (float)track.RotateJitter) : 0f;
            Track(Motion.Play(el, track, track.Ms, p => FxMotion.Apply(el, p), rotate: offset));
            Expire(el, ms);
        }

        public void Slash(Fighter anchor) => Spawn(anchor, UiClasses.FxSlash, FxTrack.Slash, Defs.Feel.SlashMs);

        public void Spark(Fighter anchor, string text) => Spawn(anchor, UiClasses.FxSpark, FxTrack.Spark, Defs.Feel.SparkMs, text);

        public void Glyph(Fighter anchor, string text) => Spawn(anchor, UiClasses.FxGlyph, FxTrack.Glyph, Defs.Feel.GlyphMs, text);

        public void Shatter(Fighter anchor, string tone) =>
            Spawn(anchor, UiClasses.FxShatter, FxTrack.Shatter, Defs.Feel.BurstMs, null, string.Format(CultureInfo.InvariantCulture, FxFormats.RingToneClass, tone));

        public void Streak(Fighter anchor) => Spawn(anchor, UiClasses.FxStreak, FxTrack.Streak, Defs.Feel.StreakMs);

        // ------------------------------------------------------------------ scenery shake (fx.js shake)

        /// <summary>
        /// Shakes the scenery by <paramref name="px"/> for the shake's 200 ms; a smaller hit does not cut a bigger shake short. The caller leaves it out when the player turned screen
        /// shake off or asked for reduced motion.
        /// </summary>
        public void Shake(double px)
        {
            if (!(px > 0) || _scenery.Count == 0) return;
            var now = FxClock.Now();
            if (now < _shakeUntil && px < _shakeNow) return;
            var track = Defs.Track(FxTrack.Shake);
            _shakeUntil = now + Defs.Feel.ShakeMs;
            _shakeNow = px;
            _shake?.Stop();
            _shake = Motion.Play(this, track, Defs.Feel.ShakeMs, p =>
            {
                foreach (var e in _scenery) FxMotion.Apply(e, p);
            }, () =>
            {
                foreach (var e in _scenery) FxMotion.Reset(e);
            }, amp: px);
        }

        // ------------------------------------------------------------------ effect frames (combatEffectSprites.js playCombatEffect)

        /// <summary>Plays the frames of an effect kind over a fighter: six frames across the effect's time, scaling and fading as the web's track does; nothing when the art is not imported.</summary>
        public void Effect(Fighter anchor, string kind, double size)
        {
            if (anchor == null || Ui == null) return;
            var frames = Frames(kind);
            if (frames.Count == 0) return;
            var play = Defs.Effects.Playback;
            var treatment = Defs.Effects.TreatmentOf(kind, out var opacity);
            var edge = (float)(size * treatment.SizeScale);
            var box = LocalBox(anchor.ArtBox);
            var el = new VisualElement { pickingMode = PickingMode.Ignore };
            el.AddToClassList(UiClasses.FxEffect);
            el.style.width = edge;
            el.style.height = edge;
            el.style.left = box.center.x - edge * (float)UiMath.Half;
            el.style.top = box.center.y - edge * (float)UiMath.Half;
            el.style.backgroundImage = new StyleBackground(frames[0]);
            Add(el);
            var ms = Math.Max(frames.Count * play.MinFrameMs, play.EffectMs);
            for (var i = 1; i < frames.Count; i++)
            {
                var frame = frames[i];
                After(ms * i / frames.Count, () => el.style.backgroundImage = new StyleBackground(frame));
            }
            var track = new TrackDef
            {
                Ms = ms,
                Ease = FxFormats.EffectEase,
                Stops = new List<FxStop>
                {
                    new FxStop { T = 0, Scale = treatment.StartScale, Opacity = play.EntryOpacityFraction * opacity },
                    new FxStop { T = play.TravelMidOffset, Scale = 1, Opacity = opacity },
                    new FxStop { T = 1, Scale = treatment.EndScale, Opacity = 0 },
                },
            };
            Track(Motion.Play(el, track, ms, p => FxMotion.Apply(el, p)));
            Expire(el, ms);
        }

        private List<Texture2D> Frames(string kind)
        {
            var frames = new List<Texture2D>();
            for (var i = 1; i <= Defs.Effects.Playback.Frames; i++)
            {
                var args = new StringArgs().Add(ArtPlaceholders.Kind, kind).Add(ArtPlaceholders.Frame, i);
                var id = Ui.Data.Art.Find(ArtKinds.Vfx, args) ?? Ui.Data.Art.Find(ArtKinds.PoseFx, args);
                var texture = Ui.Texture(id);
                if (texture != null) frames.Add(texture);
            }
            return frames;
        }

        // ------------------------------------------------------------------ a played card's flight (combat.js flyCard)

        /// <summary>
        /// A ghost of the played card leaves the hand for the target (or the player): a USS transition (translate, scale, turn and fade over the flight's 220 ms) toward the destination's
        /// centre. <paramref name="from"/> is the card's world box, <paramref name="to"/> the destination's.
        /// </summary>
        public void CardFlight(CardView ghost, Rect from, Rect to)
        {
            var start = this.WorldToLocal(from.position);
            var target = this.WorldToLocal(to.position);
            var host = new VisualElement { pickingMode = PickingMode.Ignore };
            host.AddToClassList(UiClasses.FxFlight);
            host.style.left = start.x;
            host.style.top = start.y;
            host.style.width = from.width;
            host.style.height = from.height;
            ghost.style.width = from.width;
            ghost.style.height = from.height;
            ghost.pickingMode = PickingMode.Ignore;
            host.Add(ghost);
            Add(host);
            var dx = target.x + to.width * (float)UiMath.Half - (start.x + from.width * (float)UiMath.Half);
            var dy = target.y + to.height * (float)UiMath.Half - (start.y + from.height * (float)UiMath.Half);
            // The class change after one frame is what the transition animates: translate to the target, then the rest from the sheet.
            host.schedule.Execute(() =>
            {
                host.style.translate = new Translate(dx, dy);
                host.AddToClassList(UiClasses.FxFlightGo);
            }).StartingIn(0);
            Expire(host, Defs.Feel.CardFlyMs);
        }

        // ------------------------------------------------------------------ the end

        /// <summary>Ends every word, number, mark and motion at once (the web's click-skip: clearCallouts, clearCombatEffects).</summary>
        public void CancelAll()
        {
            foreach (var run in _runs) run.Stop();
            _runs.Clear();
            foreach (var timer in _timers) timer?.Pause();
            _timers.Clear();
            _words.Clear();
            _shake = null;
            _shakeUntil = 0;
            foreach (var e in _scenery) FxMotion.Reset(e);
            Clear();
        }
    }
}
