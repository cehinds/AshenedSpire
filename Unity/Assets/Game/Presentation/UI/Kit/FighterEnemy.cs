using System;
using System.Collections.Generic;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// An enemy's combat figure as the web draws it (P-4, D-1540..): the expansion stage of its definition, over its data and the textures of its frames. The screen sets the rest the enemy is
    /// in (its Block, statuses and HP), the buff themes that colour it and the action category its intent announces each time it binds the fighter; the fighter plays the poses it is told to
    /// and draws the stage every tick while something moves.
    /// </summary>
    public sealed class EnemyFeed
    {
        public EnemyStageData Data;
        public string EnemyId;

        /// <summary>The texture of an art id of the stage, or null when it is not imported.</summary>
        public Func<string, Texture2D> Texture;

        /// <summary>The art id of a frame of this enemy at the art tier the layout asks for (the light tier on a narrow layout).</summary>
        public Func<string, string> ArtOf;

        /// <summary>How often the stage is redrawn while it moves.</summary>
        public double TickMs;

        /// <summary>The visible columns of a frame of this enemy (a floor shadow is as wide as the frame on show), or null when it was not measured.</summary>
        public Func<string, FigureInkColumns?> Ink;

        public bool Reduced;

        /// <summary>enemyPresentation's rest (idle, guard, afflicted, wounded, defeated) and the themes of the buffs.</summary>
        public string Rest;

        public IReadOnlyList<string> Themes;

        /// <summary>The action category the intent announces (smash, attack, counter, sweep, defend, preparing), or null.</summary>
        public string Category;
    }

    public partial class Fighter
    {
        private EnemyStageState _enemyState;
        private IVisualElementScheduledItem _enemyTick;

        /// <summary>True when the figure is an enemy stage that has its art; otherwise it keeps its base art.</summary>
        public bool HasEnemyStage => _enemyState != null && Data?.Enemy != null;

        /// <summary>Takes the rest, the themes and the stance the screen holds the figure in, and creates the stage state for the enemy (it persists across redraws: a clip in progress is not cut short).</summary>
        private void BindEnemy(FighterData d)
        {
            var feed = d.Enemy;
            if (feed == null)
            {
                StopEnemy();
                return;
            }
            if (_enemyState == null || _enemyState.EnemyId != feed.EnemyId) _enemyState = new EnemyStageState(feed.Data, feed.EnemyId);
            _enemyState.SetState(feed.Rest, feed.Themes, feed.Category);
            if (_enemyState.Busy) EnsureEnemyTick();
        }

        private void StopEnemy()
        {
            _enemyTick?.Pause();
            _enemyState = null;
        }

        /// <summary>The screen has changed the category the figure holds: take it and draw (a clip in progress keeps its own clock and draws itself).</summary>
        public void RefreshEnemy()
        {
            if (Data?.Enemy == null) return;
            BindEnemy(Data);
            if (!(_enemyState?.Busy ?? false)) PaintEnemy();
        }

        /// <summary>Plays a pose of the enemy's stage for <paramref name="ms"/> (play). False when the figure has no stage, or no such pose.</summary>
        public bool PlayEnemy(string pose, double ms)
        {
            if (!HasEnemyStage || !_enemyState.Play(pose, ms, Now, Data.Enemy.Reduced)) return false;
            PaintEnemy();
            EnsureEnemyTick();
            return true;
        }

        /// <summary>The width of the frame on show's visible ink in the stage's own units (the web's floor shadow is as wide as the opaque pixels of the frame on show), or null when it is not known.</summary>
        public double? EnemyInkUnits()
        {
            if (!HasEnemyStage) return null;
            var feed = Data.Enemy;
            var draw = _enemyState.Draw(feed.ArtOf, art => true);
            var ink = draw == null ? null : feed.Ink?.Invoke(draw.Frame);
            return ink.HasValue ? ink.Value.Share * draw.Rect.Width : (double?)null;
        }

        /// <summary>The hit-stop: the clip on show stays and its end moves back.</summary>
        public bool HoldEnemy(double ms) => HasEnemyStage && _enemyState.Hold(ms, Now);

        /// <summary>The enemy's stage drops what it plays and takes its rest frame (a click-skip).</summary>
        private void EnemySettle()
        {
            if (!HasEnemyStage) return;
            _enemyState.Settle();
            _enemyTick?.Pause();
            PaintEnemy();
        }

        private void EnsureEnemyTick()
        {
            if (_enemyTick == null) _enemyTick = schedule.Execute(EnemyTickOnce).Every((long)Math.Max(1d, Data.Enemy.TickMs));
            else _enemyTick.Resume();
        }

        private void EnemyTickOnce()
        {
            if (!HasEnemyStage) { _enemyTick?.Pause(); return; }
            var busy = _enemyState.Tick(Now);
            PaintEnemy();
            if (!busy) _enemyTick.Pause();
        }

        /// <summary>
        /// Draws what the enemy's stage shows now: the frame at its rectangle. The stage's units are measured from the idle figure's centre and the stage's top, and the stage is as tall as the
        /// figure box. False (and the base art shows) when the frame art is not imported.
        /// </summary>
        private bool PaintEnemy()
        {
            if (!HasEnemyStage || _art == null) return false;
            var feed = Data.Enemy;
            var draw = _enemyState.Draw(feed.ArtOf, art => feed.Texture(art) != null);
            var h = _art.resolvedStyle.height;
            var w = _art.resolvedStyle.width;
            if (draw == null || float.IsNaN(h) || float.IsNaN(w) || h <= 0)
            {
                UiDom.Show(_stageBox, false);
                return false;
            }
            var scale = (float)(h / feed.Data.Height);
            PlaceFrame(_stageBody, feed.Texture(draw.Art), draw.Rect, w, scale);
            UiDom.Show(_stageEffect, false);
            UiDom.Show(_stageMask, false);
            UiDom.Show(_stageBox, true);
            _pose.AddToClassList(UiClasses.Hidden);
            _figure.style.backgroundImage = new StyleBackground(StyleKeyword.None);
            return true;
        }

        /// <summary>A frame of the enemy stage at its rectangle: left of the figure box's centre, below the stage's top.</summary>
        private static void PlaceFrame(VisualElement layer, Texture2D texture, StageRect rect, float boxWidth, float scale)
        {
            layer.style.backgroundImage = texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);
            layer.style.left = boxWidth * (float)P1Math.Half + (float)rect.Left * scale;
            layer.style.top = (float)rect.Top * scale;
            layer.style.width = (float)rect.Width * scale;
            layer.style.height = (float)rect.Height * scale;
            layer.style.opacity = 1f;
            UiDom.Show(layer, texture != null);
        }
    }
}
