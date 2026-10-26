using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;

namespace Ashen.App.Ui
{
    /// <summary>What the enemy stage shows at one moment: the frame and the rectangle it is drawn in, and the themes whose glow colours the silhouette.</summary>
    public sealed class EnemyDraw
    {
        /// <summary>The frame shown (the web's data-pose).</summary>
        public string Frame;

        /// <summary>The art id of the frame (exp.&lt;id&gt;.&lt;frame&gt;).</summary>
        public string Art;

        public StageRect Rect;
        public IReadOnlyList<string> Themes;
    }

    /// <summary>
    /// The clock and the choices of an enemy's expansion stage (src/ui/enemyExpansionStage.js): the rest it keeps (idle, a braced guard, afflicted, wounded or defeated, or the stance frame of the action
    /// its intent announces), the clip it is playing and the hold the hit-stop puts on it. The web runs these on timers; here every call carries the time in milliseconds, so the state is a pure function of
    /// the calls and is tested without the editor. The picture it chooses is read with <see cref="Draw"/>.
    /// </summary>
    public sealed class EnemyStageState
    {
        private readonly EnemyStageData _data;
        private readonly EnemyFigure _figure;
        private string _rest = EnemyRests.Idle;
        private IReadOnlyList<string> _themes = Array.Empty<string>();
        private string _stance;
        private string _frame;
        private bool _normalized;
        private bool _playing;
        private bool _held;
        private string _heldFrame;
        private bool _normalizedHeld;
        private EnemyClip _clip;
        private string _single;
        private double _start;
        private double _ms;
        private double _due;

        public EnemyStageState(EnemyStageData data, string enemyId)
        {
            _data = data;
            _figure = data.Figure(enemyId) ?? throw new ArgumentException(enemyId);
            Settle();
        }

        public string EnemyId => _figure.Id;

        /// <summary>The rest the stage keeps now: idle, guard, afflicted, wounded, defeated, or the frame of the stance (presentation.rest).</summary>
        public string Rest => _rest;

        /// <summary>True while a clip or a hold is running (the web keeps its timer going).</summary>
        public bool Busy => _playing;

        /// <summary>The frame the stage shows (data-pose).</summary>
        public string Frame => _frame;

        /// <summary>
        /// setState: the rest (enemyPresentation), the themes of the buffs, and the action category of the intent the figure holds (presentationStance). A stance frame the enemy has replaces the rest unless
        /// the enemy is defeated. A clip in progress is not cut short; a defeated enemy settles at once.
        /// </summary>
        public void SetState(string rest, IReadOnlyList<string> themes, string category)
        {
            var presentation = rest ?? EnemyRests.Idle;
            var stance = _data.StanceFrame(_figure.Id, category);
            var held = presentation != EnemyRests.Defeated && stance != null;
            _rest = held ? stance : presentation;
            _stance = held ? category : null;
            _themes = themes ?? Array.Empty<string>();
            if (_rest == EnemyRests.Defeated || !_playing) Settle();
        }

        /// <summary>The category the figure holds a stance for, or null (data-stance).</summary>
        public string Stance => _stance;

        /// <summary>settle: stop whatever is playing and take the rest frame, normalized to the idle body's height.</summary>
        public void Settle()
        {
            _playing = false;
            _held = false;
            _clip = null;
            _single = null;
            _frame = _data.FrameOf(_data.Resolve(_rest));
            _normalized = true;
        }

        /// <summary>
        /// play: show a pose for <paramref name="ms"/> and settle: the frames of its clip, each for its share of the length (the durations are written against <see cref="EnemyStageData.ClipMs"/>), else the
        /// frame itself. False for a pose the figure has no frame for, and for any pose but defeated once the enemy is down; reduced motion and a zero length settle at once.
        /// </summary>
        public bool Play(string pose, double ms, double now, bool reduced)
        {
            if (_rest == EnemyRests.Defeated && pose != EnemyRests.Defeated) return false;
            var selected = _data.Resolve(pose);
            if (selected == EnemyRests.Defeated)
            {
                _rest = EnemyRests.Defeated;
                _themes = Array.Empty<string>();
                _stance = null;
                Settle();
                return true;
            }
            _data.Clips.TryGetValue(selected, out var clip);
            if (clip == null && !_data.Poses.Contains(selected)) return false;
            if (reduced || ms <= 0)
            {
                Settle();
                return true;
            }
            _playing = true;
            _held = false;
            _clip = clip;
            _single = clip == null ? selected : null;
            _start = now;
            _ms = ms;
            _due = now + ms;
            Advance(now);
            return true;
        }

        /// <summary>hold: the hit-stop. The frame on show stays and the clip's end moves back by <paramref name="ms"/>; false when nothing is playing.</summary>
        public bool Hold(double ms, double now)
        {
            if (!_playing || !(ms > 0)) return false;
            if (!_held)
            {
                Advance(now);
                if (!_playing) return false;
                _held = true;
                _heldFrame = _frame;
                _normalizedHeld = _normalized;
            }
            _due += ms;
            return true;
        }

        /// <summary>Moves the stage's clock to <paramref name="now"/>; true while something is still playing.</summary>
        public bool Tick(double now)
        {
            if (!_playing) return false;
            if (now >= _due) { Settle(); return false; }
            if (!_held) Advance(now);
            return true;
        }

        private void Advance(double now)
        {
            var elapsed = now - _start;
            if (elapsed >= _ms && !_held) { Settle(); return; }
            if (_single != null) { _frame = _data.FrameOf(_single); _normalized = false; return; }
            var boundary = 0d;
            var index = _clip.Poses.Count - 1;
            for (var i = 0; i < _clip.Poses.Count; i++)
            {
                boundary += _clip.Durations[i] / _data.ClipMs * _ms;
                if (elapsed < boundary) { index = i; break; }
            }
            _frame = _clip.Poses[index];
            _normalized = false;
        }

        /// <summary>What the stage draws now; null when the enemy has no frame art at all (a texture lookup that says so is <paramref name="has"/>).</summary>
        public EnemyDraw Draw(Func<string, string> artOf, Func<string, bool> has)
        {
            var frame = _held ? _heldFrame : _frame;
            var normalized = _held ? _normalizedHeld : _normalized;
            var art = artOf(frame);
            if (!has(art)) return null;
            return new EnemyDraw { Frame = frame, Art = art, Rect = _data.Place(_figure, frame, normalized), Themes = _themes };
        }
    }
}
