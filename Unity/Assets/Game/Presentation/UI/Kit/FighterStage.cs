using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// The player's combat figure as the web draws it (P-3b, D-1490..): the card stage of the class, over its data and the textures of its frames. The screen sets the stance that holds the figure
    /// and the rest it keeps between actions each time it binds the fighter; the fighter plays the actions it is told to and draws the stage every tick while something moves.
    /// </summary>
    public sealed class StageFeed
    {
        public CardStageData Data;
        public string ClassId;

        /// <summary>The texture of an art id of the stage, or null when it is not imported.</summary>
        public Func<string, Texture2D> Texture;

        /// <summary>How often the stage is redrawn while it moves (the web's animation frame).</summary>
        public double TickMs;

        public bool Reduced;
        public bool ReduceFlashes;

        /// <summary>The red of a hit over the figure.</summary>
        public Color HitTint;

        /// <summary>The stance that holds the figure (a card selected, or the last card played), or null; and the rest it keeps between actions.</summary>
        public string Stance;

        public string Rest;

        /// <summary>
        /// The silhouette glow (P-4): the choices of the web's auraFilter and the soft sprite that stands for its drop-shadows, by the art id of the frame it surrounds (null when it is not imported).
        /// The web draws no glow when the Rendering quality is lite (<see cref="Lite"/>, the performanceMode setting; the light art tier is the separate Art quality).
        /// </summary>
        public CombatAuraData Aura;
        public Func<string, Texture2D> Glow;
        public bool Lite;
    }

    public partial class Fighter
    {
        private CardStageState _stageState;
        private VisualElement _stageBox;
        private VisualElement _stageBody;
        private VisualElement[] _stageGlows;
        private VisualElement _stageEffect;
        private VisualElement _stageMask;
        private IVisualElementScheduledItem _stageTick;
        private IVisualElementScheduledItem _stageStagger;

        private static double Now => Time.realtimeSinceStartupAsDouble * UiMath.MillisPerSecond;

        private void BuildStage()
        {
            _stageBox = new VisualElement { pickingMode = PickingMode.Ignore };
            _stageBox.AddToClassList(UiClasses.FighterStage);
            _stageGlows = Enumerable.Range(0, AuraCount.Layers).Select(_ => StageLayer(UiClasses.FighterStageGlow)).ToArray();
            foreach (var glow in _stageGlows) _stageBox.Add(glow);
            _stageBody = StageLayer(UiClasses.FighterStageBody);
            _stageEffect = StageLayer(UiClasses.FighterStageEffect);
            _stageMask = StageLayer(UiClasses.FighterStageMask);
            _stageBox.Add(_stageBody);
            _stageBox.Add(_stageEffect);
            _stageBox.Add(_stageMask);
            UiDom.Show(_stageBox, false);
            _figure.Add(_stageBox);
        }

        private static VisualElement StageLayer(string style)
        {
            var layer = new VisualElement { pickingMode = PickingMode.Ignore };
            layer.AddToClassList(style);
            UiDom.Show(layer, false);
            return layer;
        }

        /// <summary>True when the figure is a card stage that has its art; otherwise it keeps its base art.</summary>
        public bool HasStage => _stageState != null && Data?.Stage != null;

        /// <summary>Takes the stance and the rest the screen holds the figure in, and creates the stage state for the class (it persists across redraws: a clip in progress is not cut short).</summary>
        private void BindStage(FighterData d)
        {
            var feed = d.Stage;
            if (feed == null)
            {
                StopStage();
                return;
            }
            if (_stageState == null || _stageState.ClassId != feed.ClassId) _stageState = new CardStageState(feed.Data, feed.ClassId);
            _stageState.SetStance(feed.Stance);
            _stageState.SetRest(feed.Rest);
            if (_stageState.Busy) EnsureStageTick();
        }

        /// <summary>The screen has changed the stance or the rest the figure is held in: take them and draw (a clip in progress keeps its own clock and draws itself).</summary>
        public void RefreshStage()
        {
            if (Data?.Stage == null) return;
            BindStage(Data);
            if (!(_stageState?.Busy ?? false)) PaintStage();
        }

        private void StopStage()
        {
            _stageTick?.Pause();
            _stageState = null;
            if (_stageBox != null) UiDom.Show(_stageBox, false);
        }

        /// <summary>The length of an action of this figure's stage at the pace, and when its blow lands; null when the figure has no such clip.</summary>
        public StageTiming? TimingOf(string action, SpriteSpeed speed)
        {
            if (!HasStage) return null;
            var sequence = _stageState.SequenceFor(action);
            return sequence == null ? (StageTiming?)null : _stageState.Timing(action, speed);
        }

        /// <summary>Plays a card action of the stage for <paramref name="ms"/> (play). False when the figure has no stage or no such action.</summary>
        public bool PlayStageAction(string action, double ms, IReadOnlyList<string> resources = null)
        {
            if (!HasStage || !_stageState.Play(action, ms, Now, Data.Stage.Reduced, resources)) return false;
            _stageState.CueContact(Data.Stage.Reduced);
            PaintStage();
            EnsureStageTick();
            return true;
        }

        /// <summary>The hurt reaction of the stage: the hurt clip with its red strobe and the knock-back.</summary>
        public bool StageHit(double ms)
        {
            if (!HasStage || !_stageState.Hit(ms, Now, Data.Stage.Reduced)) return false;
            PaintStage();
            EnsureStageTick();
            return true;
        }

        /// <summary>A body reaction to an enemy blow (the pilot's evade, guard break, defend).</summary>
        public bool StageReact(string kind, double ms)
        {
            if (!HasStage || !_stageState.React(kind, ms, Now)) return false;
            PaintStage();
            EnsureStageTick();
            return true;
        }

        /// <summary>The figure can play the body reaction of a kind (the pilot's).</summary>
        public bool CanStageReact(string kind) => HasStage && _stageState.CanReact(kind);

        /// <summary>The timeline reached the blow: the stage may play past its contact frame (activeActorAnimation.contact()).</summary>
        public bool StageContact()
        {
            if (!HasStage || !_stageState.Contact(Now)) return false;
            PaintStage();
            EnsureStageTick();
            return true;
        }

        /// <summary>The hit-stop: the clock of what the stage is playing waits.</summary>
        public bool StageHold(double ms) => HasStage && _stageState.Hold(ms, Now);

        /// <summary>
        /// A stagger tints the stage's root for one step and moves nothing (styles/combat.css rear-stagger-flash: the red from the start to 45% of the wobble, then none).
        /// </summary>
        public void StageStagger(double ms, double share)
        {
            if (!HasStage) return;
            _stageStagger?.Pause();
            SetStageTint(Data.Stage.HitTint);
            _stageStagger = schedule.Execute(() => SetStageTint(Color.white)).StartingIn((long)(ms * share));
        }

        private void SetStageTint(Color tint)
        {
            _stageBody.style.unityBackgroundImageTintColor = tint;
            _stageEffect.style.unityBackgroundImageTintColor = tint;
        }

        private void EnsureStageTick()
        {
            if (_stageTick == null) _stageTick = schedule.Execute(StageTickOnce).Every((long)Math.Max(1d, Data.Stage.TickMs));
            else _stageTick.Resume();
        }

        private void StageTickOnce()
        {
            if (!HasStage) { _stageTick?.Pause(); return; }
            var busy = _stageState.Tick(Now, Data.Stage.Reduced);
            PaintStage();
            if (!busy) _stageTick.Pause();
        }

        private void StageSettle(bool reaction)
        {
            if (!HasStage) return;
            _stageStagger?.Pause();
            SetStageTint(Color.white);
            _stageState.Settle(reaction);
            _stageTick?.Pause();
            PaintStage();
        }

        /// <summary>
        /// Draws what the stage shows now: the body (or the hurt clip, or the downed figure) at its rectangle, the trail over it and the red mask of a hit. The stage's units are measured from the
        /// figure's centre and its floor line, and one unit of its ready figure is the box's height over the figure's height in units. False (and the base art shows) when no frame art is imported.
        /// </summary>
        private bool PaintStage()
        {
            if (!HasStage || _art == null) return false;
            var feed = Data.Stage;
            var draw = _stageState.Draw(art => feed.Texture(art) != null, feed.Reduced, feed.ReduceFlashes);
            var h = _art.resolvedStyle.height;
            var w = _art.resolvedStyle.width;
            if (draw == null || float.IsNaN(h) || float.IsNaN(w) || h <= 0)
            {
                UiDom.Show(_stageBox, false);
                return false;
            }
            var scale = (float)(h / feed.Data.Class(feed.ClassId).Unit);
            PaintGlow(feed, draw, w, h, scale);
            Place(_stageBody, feed.Texture(draw.Art), draw.Rect, w, h, scale, 1);
            if (draw.EffectArt != null) Place(_stageEffect, feed.Texture(draw.EffectArt), draw.EffectRect, w, h, scale, 1);
            else UiDom.Show(_stageEffect, false);
            if (draw.Flash > 0)
            {
                Place(_stageMask, feed.Texture(draw.Art), draw.Rect, w, h, scale, (float)draw.Flash);
                _stageMask.style.unityBackgroundImageTintColor = feed.HitTint;
            }
            else UiDom.Show(_stageMask, false);
            UiDom.Show(_stageBox, true);
            _pose.AddToClassList(UiClasses.Hidden);
            _figure.style.backgroundImage = new StyleBackground(StyleKeyword.None);
            return true;
        }

        /// <summary>
        /// The aura behind the body: one tinted copy of the frame's soft silhouette sprite for each colour the web strokes the figure with, grown by that colour's radius and as opaque as its strength.
        /// An approximation of the drop-shadows (UI Toolkit draws no filters): the sprite is the frame's alpha shrunk, blurred and gained at import (Tools/scan-assets.mjs release.derive), the same
        /// halo for every strength, so a faded outline and a power glow differ in opacity and reach but not in blur. The brightness a power frame adds to the body is not drawn.
        /// </summary>
        private void PaintGlow(StageFeed feed, StageDraw draw, float boxWidth, float boxHeight, float scale)
        {
            var layers = feed.Aura != null && feed.Glow != null ? feed.Aura.For(draw, _stageState.Rest, feed.Lite) : null;
            var glow = layers != null && !layers.None ? feed.Glow(draw.Art) : null;
            for (var i = 0; i < _stageGlows.Length; i++)
            {
                var layer = _stageGlows[i];
                if (glow == null || i >= layers.Glows.Count)
                {
                    UiDom.Show(layer, false);
                    continue;
                }
                var g = layers.Glows[i];
                var grown = new StageRect { Left = draw.Rect.Left - g.Radius, Top = draw.Rect.Top - g.Radius, Width = draw.Rect.Width + g.Radius / UiMath.Half, Height = draw.Rect.Height + g.Radius / UiMath.Half };
                Place(layer, glow, grown, boxWidth, boxHeight, scale, (float)g.Alpha);
                layer.style.unityBackgroundImageTintColor = new Color((float)(g.R / UiMath.ByteMax), (float)(g.G / UiMath.ByteMax), (float)(g.B / UiMath.ByteMax));
            }
        }

        private static void Place(VisualElement layer, Texture2D texture, StageRect rect, float boxWidth, float boxHeight, float scale, float opacity)
        {
            layer.style.backgroundImage = texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);
            layer.style.left = boxWidth * (float)P1Math.Half + (float)rect.Left * scale;
            layer.style.top = boxHeight + (float)rect.Top * scale;
            layer.style.width = (float)rect.Width * scale;
            layer.style.height = (float)rect.Height * scale;
            layer.style.opacity = opacity;
            UiDom.Show(layer, texture != null);
        }
    }
}
