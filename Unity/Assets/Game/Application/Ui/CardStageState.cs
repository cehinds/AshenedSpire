using System;
using System.Linq;
using Ashen.Generated;

namespace Ashen.App.Ui
{
    /// <summary>What the stage shows at one moment: the art, where it is drawn, the effect trail over it and the red mask of a hit (alternativeCardStage.js paint).</summary>
    public sealed class StageDraw
    {
        /// <summary>The name of the pose the stage is in (the web's data-pose).</summary>
        public string Pose;

        /// <summary>The art of the body, the hurt clip or the downed figure, and the rectangle it is drawn in.</summary>
        public string Art;
        public StageRect Rect;
        public bool Hurt;
        public bool Down;

        /// <summary>The trail laid over the body, or null.</summary>
        public string EffectArt;
        public StageRect EffectRect;

        /// <summary>How strongly the red mask covers the body (0 for none).</summary>
        public double Flash;
    }

    /// <summary>
    /// The clock and the choices of the player's card stage (src/ui/alternativeCardStage.js): which pose it rests in (the ready figure, a held stance, the guard brace, the downed figure), the
    /// card action it is playing and how far through it is, the hit reaction (the hurt clip with its red strobe and its knock-back) and a pilot's body reaction. The web runs these on animation
    /// frames; here every call carries the time in milliseconds, so the state is a pure function of the calls and tested without the editor. The picture it chooses is read with
    /// <see cref="Draw"/>; the artwork it names is the presentation's to load.
    /// </summary>
    public sealed class CardStageState
    {
        private sealed class Clip
        {
            public string Action;
            public StageSequence Sequence;
            public double Duration;

            /// <summary>contactCue: the clock waits at the contact frame (<see cref="ContactMs"/>) until the timeline says the blow has landed.</summary>
            public bool AwaitContact;
            public double ContactMs;
        }

        private sealed class Reaction
        {
            public StageSequence Sequence;
            public double Elapsed;
            public double Last;
            public double Duration;
        }

        private readonly CardStageData _data;
        private readonly StageClass _class;
        private string _pose = StageNames.Ready;
        private Clip _playing;
        private Reaction _reaction;
        private double _elapsed;
        private double _last;
        private double _holdUntil;
        private bool _flashing;
        private double _flashMs;
        private double _flashElapsed;

        public CardStageState(CardStageData data, string classId)
        {
            _data = data;
            _class = data.Class(classId) ?? throw new ArgumentException(classId);
        }

        public string ClassId => _class.Id;

        public string Rest { get; private set; } = StageNames.Idle;

        public string Stance { get; private set; }

        /// <summary>True while a clip, a hit reaction or a body reaction is running (the web keeps its animation frame going).</summary>
        public bool Busy => _playing != null || _flashing || _reaction != null;

        public string Pose => _pose;

        public string PlayingAction => _playing?.Action;

        /// <summary>restPose: the frame the figure holds with nothing playing: the downed figure, a held stance, the counter stance, the guard brace for a guard, else ready.</summary>
        public string RestPose()
        {
            if (Rest == StageNames.Defeated) return StageNames.Defeated;
            if (Stance != null) return StageNames.StancePrefix + Stance;
            if (Rest == StageNames.RestCounter) return _class.Pilot ? StageNames.PilotCounter : _class.CounterRest;
            if (Rest == StageNames.RestDefend || Rest == StageNames.RestGuard || Rest == StageNames.RestEvade) return StageNames.GuardBrace;
            return StageNames.Ready;
        }

        /// <summary>setStance: hold the figure in a stance (the frame the class has for it), or release it. False when the class has no frame for the stance or none was asked for.</summary>
        public bool SetStance(string next)
        {
            var normalized = next != null && _class.Held.Contains(next) ? next : null;
            if (normalized == Stance && (_playing != null || _pose == RestPose())) return Stance != null;
            Stance = normalized;
            if (_playing == null) _pose = RestPose();
            return Stance != null;
        }

        /// <summary>setRestPose: the rest the figure keeps between actions. A clip that is playing is not cut short by the same rest; the downed figure drops a reaction and a hit in progress.</summary>
        public void SetRest(string next)
        {
            if (next == Rest && _playing != null) return;
            Rest = next;
            if (next == StageNames.Defeated)
            {
                _reaction = null;
                _flashing = false;
            }
            Settle(false);
        }

        /// <summary>settle: stop the clip and take the rest frame; a click-skip passes <paramref name="reaction"/> to end the hit and the body reaction too.</summary>
        public void Settle(bool reaction)
        {
            if (reaction)
            {
                _flashing = false;
                _reaction = null;
                _flashElapsed = 0;
                _holdUntil = 0;
            }
            _playing = null;
            _elapsed = 0;
            _pose = RestPose();
        }

        /// <summary>The clip a card action plays (the sequence the action's alias names), or null when the class has none.</summary>
        public StageSequence SequenceFor(string action) => _class.Sequence(_data.Alias(action));

        /// <summary>actionTiming: how long the action plays at this pace and when its blow lands.</summary>
        public StageTiming Timing(string action, SpriteSpeed speed) => _data.Timing(SequenceFor(action), speed);

        /// <summary>play: start a card action for <paramref name="ms"/> (the clip is not played when it is not positive); hit and hurt start the hit reaction, defeated lays the figure down. False when the stage cannot play it.</summary>
        public bool Play(string action, double ms, double now, bool reduced)
        {
            if (Rest == StageNames.Defeated && action != StageNames.Defeated) return false;
            if (action == StageNames.Hit || action == StageNames.Hurt) return Hit(ms, now, reduced);
            if (action == StageNames.Defeated)
            {
                Rest = StageNames.Defeated;
                _reaction = null;
                _flashing = false;
                Settle(false);
                return true;
            }
            var key = _data.Alias(action);
            var sequence = _class.Sequence(key);
            if (sequence == null) return false;
            if (_class.Pilot && key == StageNames.CounterPrepare && Stance == StageActions.Evade)
                sequence = new StageSequence { Name = sequence.Name, Poses = new[] { StageNames.PilotEvade }, Durations = sequence.Durations, Travel = sequence.Travel, Impact = sequence.Impact, Hold = sequence.Hold };
            _reaction = null;
            if (reduced || ms <= 0)
            {
                Settle(false);
                return true;
            }
            _elapsed = 0;
            _holdUntil = 0;
            _playing = new Clip { Action = key, Sequence = sequence, Duration = ms };
            _last = now;
            return true;
        }

        /// <summary>hit: the hurt reaction: the hurt clip over the figure with the red strobe and the knock-back, for at least the clip's floor.</summary>
        public bool Hit(double ms, double now, bool reduced)
        {
            if (reduced) return false;
            var idle = !Busy;
            _flashing = true;
            _flashMs = Math.Max(_data.HurtMinMs, ms > 0 ? ms : _data.HitMs);
            _flashElapsed = 0;
            if (idle) _last = now;
            return true;
        }

        /// <summary>The body reactions this class can play (the pilot's), as the web lists them.</summary>
        public bool CanReact(string kind) =>
            _class.Pilot && _data.Reactions.TryGetValue(kind ?? string.Empty, out var sequence) && sequence.Poses.All(p => _class.Frame(p) != null);

        /// <summary>reactPose: a body reaction to an enemy blow (evade, guard-break, fully-defended, defend) of the pilot's, for <paramref name="ms"/>.</summary>
        public bool React(string kind, double ms, double now)
        {
            if (Rest == StageNames.Defeated || !CanReact(kind)) return false;
            var idle = !Busy;
            _reaction = new Reaction { Sequence = _data.Reactions[kind], Elapsed = 0, Last = now, Duration = ms > 0 ? ms : _data.ReactMs };
            if (idle) _last = now;
            return true;
        }

        /// <summary>endReaction: a click-skip ends a hit and a body reaction, not the clip.</summary>
        public bool EndReaction()
        {
            if (!_flashing && _reaction == null) return false;
            _flashing = false;
            _reaction = null;
            _flashElapsed = 0;
            _holdUntil = 0;
            if (_playing == null) _pose = RestPose();
            return true;
        }

        /// <summary>hold: the hit-stop. The clock of what is playing waits <paramref name="ms"/>; a clip with a contact frame is first brought to it, so the blow is on screen while the figure holds.</summary>
        public bool Hold(double ms, double now)
        {
            if (!Busy || !(ms > 0)) return false;
            if (_playing != null && (_playing.Sequence.Pilot || _playing.Sequence.ContactCue) && _reaction == null)
            {
                var contact = _playing.Sequence.ContactShare * _playing.Duration;
                if (_elapsed < contact) _elapsed = contact;
            }
            _holdUntil = Math.Max(now, _holdUntil) + ms;
            return true;
        }

        /// <summary>
        /// contactCue: a clip with a contact frame (a pilot's attack and smash) waits at it until <see cref="Contact"/>, so a late timer never lets the follow-through play before the blow lands. False for a clip with
        /// no such cue, for nothing playing and under reduced motion.
        /// </summary>
        public bool CueContact(bool reduced)
        {
            if (_playing == null || !_playing.Sequence.ContactCue || reduced) return false;
            _playing.ContactMs = _playing.Sequence.ContactShare * _playing.Duration;
            _playing.AwaitContact = true;
            return true;
        }

        /// <summary>contact: the timeline reached the blow; the clock is released, and brought to the contact frame if it was behind. False when nothing waits (or the figure is down).</summary>
        public bool Contact(double now)
        {
            if (_playing == null || !_playing.AwaitContact || Rest == StageNames.Defeated) return false;
            _playing.AwaitContact = false;
            _elapsed = Math.Max(_elapsed, _playing.ContactMs);
            _last = now;
            return true;
        }

        /// <summary>remainingMs: what is left of the clip and of the hit-stop held on it (0 when it is not the cued clip any more).</summary>
        public double RemainingMs(double now) => _playing == null ? 0 : Math.Max(0, _playing.Duration - _elapsed) + Math.Max(0, _holdUntil - now);

        /// <summary>tick: advance the clocks to <paramref name="now"/> (the held time does not count). True while something is still running.</summary>
        public bool Tick(double now, bool reduced)
        {
            if (_playing != null)
            {
                _elapsed = Math.Min(_playing.Duration, _elapsed + Math.Max(0, now - Math.Max(_last, _holdUntil)));
                if (_playing.AwaitContact && !reduced) _elapsed = Math.Min(_elapsed, _playing.ContactMs);
                if (_elapsed >= _playing.Duration || reduced)
                {
                    _playing = null;
                    _pose = RestPose();
                }
            }
            if (_flashing)
            {
                _flashElapsed += Math.Max(0, now - Math.Max(_last, _holdUntil));
                if (_flashElapsed >= _flashMs) _flashing = false;
            }
            if (_reaction != null)
            {
                _reaction.Elapsed += Math.Max(0, now - Math.Max(_reaction.Last, _holdUntil));
                _reaction.Last = Math.Max(_reaction.Last, now);
                if (_reaction.Elapsed >= _reaction.Duration)
                {
                    _reaction = null;
                    if (_playing == null) _pose = RestPose();
                }
            }
            _last = Math.Max(_last, now);
            return Busy;
        }

        /// <summary>
        /// paint: what the stage shows now. <paramref name="has"/> says whether a frame's art is loaded: a pose without art shows the ready figure, as the web does while an image loads; with no
        /// ready art nothing is drawn (null). <paramref name="reduceFlashes"/> drops the red mask and the trail (the knock-back stays).
        /// </summary>
        public StageDraw Draw(Func<string, bool> has, bool reduced, bool reduceFlashes)
        {
            var effect = default(string);
            var x = 0d;
            if (_playing != null)
            {
                var sample = _data.Sample(_playing.Sequence, _elapsed, _playing.Duration, reduced);
                _pose = _playing.Sequence.Poses[sample.Index];
                if (_pose == StageNames.Origin) _pose = RestPose();
                if (_pose == StageNames.Ready && Stance != null) _pose = StageNames.StancePrefix + Stance;
                effect = _playing.Sequence.Effects != null && sample.Index < _playing.Sequence.Effects.Count ? _playing.Sequence.Effects[sample.Index] : null;
                x = _playing.Action == StageActions.Attack || _playing.Action == StageActions.Smash || _playing.Action == StageActions.Sweep ? sample.X : 0;
            }
            if (Rest != StageNames.Defeated && _reaction != null)
                _pose = _reaction.Sequence.Poses[_data.Sample(_reaction.Sequence, _reaction.Elapsed, _reaction.Duration, reduced).Index];
            if (_pose == StageNames.Defeated && has(_class.Defeated))
                return new StageDraw { Pose = _pose, Art = _class.Defeated, Rect = _data.PlaceSquare(0), Down = true };
            var ready = _class.Frame(StageNames.Ready);
            var wanted = _class.Frame(_pose);
            var frame = wanted != null && has(wanted.Art) ? wanted : ready;
            if (frame == null || !has(frame.Art)) return null;
            var reacting = _flashing && !reduced;
            var clipping = _reaction == null && reacting && has(_class.Hurt.Art);
            var bodyX = x + (reacting ? _data.RecoilX(_flashElapsed / _flashMs) : 0);
            var rest = _class.Frame(RestPose());
            var heldRegistration = _playing == null || (rest != null && frame.Art == rest.Art);
            var draw = new StageDraw
            {
                Pose = _pose,
                Art = clipping ? _class.Hurt.Art : frame.Art,
                Hurt = clipping,
                Rect = clipping ? _data.PlaceHurt(_class, bodyX) : _data.Place(_class, frame, bodyX, heldRegistration && _reaction == null),
            };
            if (effect != null && !reduced && !reduceFlashes && _class.Effects.TryGetValue(effect, out var trail) && has(trail))
            {
                draw.EffectArt = trail;
                draw.EffectRect = _data.PlaceSquare(x);
            }
            draw.Flash = !_flashing || reduceFlashes ? 0 : _data.HitFlashOpacity(_flashElapsed, _flashMs, reduced);
            return draw;
        }
    }
}
