using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;
using KitCard = Ashen.Presentation.UI.Kit.CardView;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The combat feedback of the Core 2 screen (VP-5, D-750): every step the session returns is planned into the web's beats (<see cref="Core2Feedback"/>), timed by the player's pacing
    /// (<see cref="CombatPace"/>, <see cref="FxTimeline"/>) and drawn by the feedback layer and the fighters: the played card's flight, the action word, the swing and its pose, the hit
    /// flash and recoil, the numbers and words, the turn banners, the effect frames, the boss splash and the victory beat. An enemy turn locks the screen until its beats are done (Skip ends
    /// them); a card the player plays is shown the same way without locking anything. The screen redraws from the flow's final view at once; the cues play over it.
    /// </summary>
    public sealed partial class Core2CombatScreen
    {
        private sealed class Flight
        {
            public string Card;
            public string Target;
            public Rect From;
            public CardViewData Data;
        }

        private FxLayer _fx;
        private FxMotion _motion;
        private CombatPace _pace;
        private readonly List<IVisualElementScheduledItem> _fxItems = new List<IVisualElementScheduledItem>();
        private readonly HashSet<string> _dying = new HashSet<string>(StringComparer.Ordinal);
        private IVisualElementScheduledItem _introTimer;
        private bool _introOpen;
        private bool _ending;
        private Flight _flight;

        private FeedbackDefs Defs => Ui.Data.Feedback;

        private bool ReduceFlashes => _flow.Pacing.ReduceFlashes;

        private bool ReducedMotion => _run.PlayerSettings.On(SettingIds.ReducedMotion);

        private void SetupFx()
        {
            _motion = new FxMotion(Defs, Ui.TokenColor(TokenKeys.FxHitTint));
            _fx = new FxLayer { Ui = Ui, Motion = _motion, AnchorFor = AnchorOf };
            _pace = CombatPace.From(_flow.Pacing, Defs, ReducedMotion);
            _fx.Pace = _pace;
            Root.Add(_fx);
            _fx.SetScenery(new[] { Root.Q(UiNames.CombatBackground), Root.Q(UiNames.StageGrid) });
            // A click ends what is showing, as the web's does (fx.js playTimeline skip); an enemy turn has its Skip control.
            Root.RegisterCallback<PointerDownEvent>(_ => ClickSkip(), TrickleDown.TrickleDown);
        }

        private Fighter AnchorOf(string id)
        {
            if (id == null) return null;
            if (_player?.Data != null && _player.Data.Id == id) return _player;
            return _enemies.TryGetValue(id, out var enemy) ? enemy : null;
        }

        private double MaxHpOf(string id)
        {
            if (id == null) return 0;
            if (id == View.Player.Id) return View.Player.MaxHp;
            return View.Enemies.FirstOrDefault(e => e.Id == id)?.MaxHp ?? 0;
        }

        private FxContext PlanContext() => new FxContext
        {
            Defs = Defs, Strings = Strings, Pace = _pace, PlayerId = View.Player.Id, PlayerManeuver = View.Player.StanceManeuver, MaxHp = MaxHpOf,
        };

        // ------------------------------------------------------------------ the timeline

        /// <summary>Plans the step's events into the web's beats and starts them; an enemy turn locks the screen until they are done, any other play just shows its cues over the redrawn board.</summary>
        private void StartTimeline(Core2StepResult step)
        {
            CancelFx();
            _pace = CombatPace.From(_flow.Pacing, Defs, ReducedMotion);
            _fx.Pace = _pace;
            var beats = Core2Feedback.Plan(step.Timeline, PlanContext());
            var schedule = FxTimeline.Build(beats, _pace, Defs.Pace.InstantStepMs);
            var enemyTurn = !_pace.Instant && step.Actors.Count > 0;
            TakeFlight(step);
            if (schedule.Items.Count == 0 && !enemyTurn)
            {
                _hiddenLines = 0;
                Render();
                AfterStep();
                return;
            }
            foreach (var died in step.Timeline.Where(l => l.Type == FxEvents.EnemyDied && l.TargetId != null)) _dying.Add(died.TargetId);
            _busy = enemyTurn;
            _hiddenLines = enemyTurn ? step.Timeline.Count(l => !string.IsNullOrEmpty(l.Text)) : 0;
            _acting = null;
            Render();
            if (enemyTurn) FocusSoon(_footer.EndTurnButton);
            foreach (var item in schedule.Items)
            {
                var captured = item;
                _fxItems.Add(Root.schedule.Execute(() => RunItem(captured)).StartingIn((long)item.AtMs));
            }
            _fxItems.Add(Root.schedule.Execute(FinishPlay).StartingIn((long)schedule.TotalMs));
            if (!_flow.Ended) AfterNonLockedStep();
        }

        /// <summary>A play that locks nothing returns the focus at once, as before (the cues play over the redrawn board).</summary>
        private void AfterNonLockedStep()
        {
            if (_busy || _flow.Prompt != null) return;
            FocusSoon(_flow.Targeting ? FirstTarget() : FirstPlayableCard());
        }

        private void FinishPlay()
        {
            try
            {
                if (_flow.Ended)
                {
                    // The last blow has landed: the web waits a beat before the spoils (COMBAT_TIMING.fightEndDelayMs).
                    _fxItems.Add(Root.schedule.Execute(_busy ? (Action)FinishTimeline : EnterEnded).StartingIn((long)_pace.FightEndMs));
                    return;
                }
                if (_busy) { FinishTimeline(); return; }
                _dying.Clear();
                _acting = null;
                RenderFighters();
            }
            catch (Exception e)
            {
                Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.Core2StepFailed, e));
                Unlock();
            }
        }

        /// <summary>Ends every cue, word, pose and motion at once and puts the figures back (the web's click-skip, and the start of the next play).</summary>
        private void CancelFx()
        {
            foreach (var item in _fxItems) item?.Pause();
            _fxItems.Clear();
            _fx?.CancelAll();
            _player?.Calm();
            foreach (var enemy in _enemies.Values) enemy.Calm();
            _dying.Clear();
        }

        private void ClickSkip()
        {
            if (_busy || _fxItems.Count == 0) return;
            CancelFx();
            RenderFighters();
            // The cancelled cues may have held the fight's end (FinishPlay's beat before the spoils): skipping them ends the fight now.
            if (_flow.Ended) EnterEnded();
        }

        /// <summary>Skip: end the timeline now.</summary>
        public void Skip()
        {
            if (!_busy) return;
            FinishTimeline();
        }

        private void FinishTimeline()
        {
            Unlock();
            try { AfterStep(); }
            catch (Exception e) { Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.Core2StepFailed, e)); }
        }

        /// <summary>After an intent and its walk: the end of the fight, or the player's turn again.</summary>
        private void AfterStep()
        {
            if (_flow.Ended) { EnterEnded(); return; }
            if (_flow.Prompt != null) return;
            FocusSoon(_flow.Targeting ? FirstTarget() : FirstPlayableCard());
        }

        private void RunItem(FxItem item)
        {
            try
            {
                switch (item.Kind)
                {
                    case FxItemKind.Cue: PlayCue(item.Cue); break;
                    case FxItemKind.Swing: Swing(item); break;
                    case FxItemKind.Reveal:
                        if (_busy && item.Lines > 0)
                        {
                            _hiddenLines = Math.Max(0, _hiddenLines - item.Lines);
                            RenderLog();
                        }
                        break;
                    case FxItemKind.Acting:
                        _acting = _busy && item.ActorId != null && _enemies.ContainsKey(item.ActorId) ? item.ActorId : null;
                        foreach (var pair in _enemies) pair.Value.EnableInClassList(UiClasses.CombatantActing, pair.Key == _acting);
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.Core2StepFailed, e));
            }
        }

        // ------------------------------------------------------------------ the cues

        private void PlayCue(FxCue cue)
        {
            var target = AnchorOf(cue.TargetId);
            switch (cue.Type)
            {
                case FxCueType.Float: _fx.Float(target, cue); break;
                case FxCueType.Callout: CalloutCue(cue, target); break;
                case FxCueType.HitFlash: HitFlash(cue, target); break;
                case FxCueType.GuardPose:
                    if (target != null && !target.Data.Player && !ReducedMotion) Pose(target, FxPoses.GuardHit, cue.Amount);
                    break;
                case FxCueType.Wobble:
                    if (target != null && !ReduceFlashes && !ReducedMotion) target.Play(_motion, Defs.Track(FxTrack.Wobble), Defs.Track(FxTrack.Wobble).Ms, false);
                    break;
                case FxCueType.Shake:
                    if (_flow.Pacing.ScreenShake && !ReducedMotion) _fx.Shake(cue.Amount);
                    break;
                case FxCueType.Slash: _fx.Slash(target); break;
                case FxCueType.Spark: _fx.Spark(target, cue.Text); break;
                case FxCueType.Glyph: _fx.Glyph(target, cue.Text); break;
                case FxCueType.Shatter: _fx.Shatter(target, cue.Tone); break;
                case FxCueType.Streak: _fx.Streak(target); break;
                case FxCueType.Effect:
                    if (!ReduceFlashes && !ReducedMotion) _fx.Effect(target, cue.Kind, Defs.Effects.Playback.EventSize);
                    break;
                case FxCueType.Banner:
                    _fx.Banner(cue.Text, cue.Tone == null, cue.Tone == FxValues.Blood, cue.Tone == null ? _pace.BannerMs : 0);
                    break;
                case FxCueType.Defeated:
                    _dying.Remove(cue.TargetId);
                    RenderFighters();
                    break;
            }
        }

        private void CalloutCue(FxCue cue, Fighter target)
        {
            if (target == null) return;
            var status = cue.Status != null;
            string text;
            Color? colour = null;
            if (status)
            {
                text = Strings.Get(string.Format(CultureInfo.InvariantCulture, FxFormats.StatusKey, cue.Status));
                if (ColorUtility.TryParseHtmlString(cue.Tone, out var parsed)) colour = parsed;
            }
            else if (cue.Callout != FxCallout.None)
            {
                var kind = Defs.Kind(cue.Callout);
                text = Strings.Get(string.Format(CultureInfo.InvariantCulture, FxFormats.CalloutKey, kind.Word));
                cue.Tone = kind.Tone;
            }
            else text = cue.Text;
            _fx.Callout(cue.TargetId, target, cue, text, colour, status);
        }

        private void HitFlash(FxCue cue, Fighter target)
        {
            if (target == null) return;
            var player = target.Data.Player;
            var id = player ? (cue.Heavy ? FxTrack.HitPlayerHeavy : FxTrack.HitPlayer) : (cue.Heavy ? FxTrack.HitEnemyHeavy : FxTrack.HitEnemy);
            if (!ReduceFlashes) target.Play(_motion, Defs.Track(id), cue.Amount, true);
            if (!ReducedMotion) Pose(target, FxPoses.Hurt, cue.Amount);
        }

        private void Swing(FxItem item)
        {
            var actor = AnchorOf(item.ActorId);
            if (actor == null) return;
            var player = actor.Data.Player;
            if (!ReduceFlashes && !ReducedMotion)
            {
                var track = Defs.Track(player ? FxTrack.LungePlayer : FxTrack.LungeEnemy);
                actor.Play(_motion, track, track.Ms, false);
            }
            if (!ReducedMotion) Pose(actor, item.Attack ? FxPoses.Attack : FxPoses.Guard, item.Ms + (item.Beat?.HoldMs ?? 0));
        }

        // ------------------------------------------------------------------ pose art (enemyPoseStage.js)

        private string DefOf(Fighter f) => f?.Data == null || f.Data.Player ? null : View.Enemies.FirstOrDefault(e => e.Id == f.Data.Id)?.DefId;

        private (Texture2D Frame, double Scale) PoseFrame(string def, string pose)
        {
            if (def == null) return (null, 1);
            var frames = pose == FxPoses.Idle || pose == FxPoses.Attack;
            var id = Ui.Data.Art.Find(frames ? ArtKinds.EnemyPose : ArtKinds.EnemyState, new StringArgs().Add(ArtPlaceholders.Id, def).Add(ArtPlaceholders.Name, pose));
            var scale = frames ? 1 : Defs.Poses.StateScale.TryGetValue(def, out var s) ? s : 1;
            if (pose == FxPoses.Projectile) scale *= Defs.Poses.ProjectileScale;
            return (Ui.Texture(id), scale);
        }

        private void Pose(Fighter f, string pose, double ms)
        {
            var frame = PoseFrame(DefOf(f), pose);
            if (frame.Frame != null) f.PlayPose(frame.Frame, frame.Scale, ms);
        }

        /// <summary>enemyStates.js enemyPresentation: the frame an enemy rests in: defeated, guard while it holds Block, afflicted under a hostile status, wounded at 35% HP, else its idle pose.</summary>
        private void ApplyRestArt(FighterData d, Core2FighterView f)
        {
            if (f.DefId == null) return;
            if (!d.Alive)
            {
                var id = Ui.Data.Art.Find(ArtKinds.Defeated, new StringArgs().Add(ArtPlaceholders.Id, f.DefId));
                d.RestArt = Ui.Texture(id);
                d.RestScale = Defs.Poses.DefeatedScale.TryGetValue(f.DefId, out var scale) ? scale : 1;
                return;
            }
            string rest = null;
            if (f.Block > 0) rest = FxPoses.Guard;
            else if (f.Statuses.Any(s => s.MeterValue == null && s.Stacks > 0 && Defs.Poses.Negative.Contains(s.Id))) rest = FxPoses.Afflicted;
            else if (f.Hp > 0 && f.MaxHp > 0 && f.Hp / f.MaxHp <= Defs.Poses.WoundedShare) rest = FxPoses.Wounded;
            var frame = rest != null ? PoseFrame(f.DefId, rest) : (null, 1);
            // The idle pose replaces the base art only with the attack pose beside it, so the swing does not jump in size.
            if (frame.Frame == null && PoseFrame(f.DefId, FxPoses.Attack).Frame != null) frame = PoseFrame(f.DefId, FxPoses.Idle);
            d.RestArt = frame.Frame;
            d.RestScale = frame.Scale;
        }

        // ------------------------------------------------------------------ the played card's flight (combat.js flyCard)

        /// <summary>Remembers the card about to be played: where it sits and what it shows (the redraw replaces it), and where it is going.</summary>
        private void CaptureFlight(string instanceId, string targetId)
        {
            _flight = null;
            if (instanceId == null || ReducedMotion || !_run.PlayerSettings.On(SettingIds.ShowPlayedCard)) return;
            var i = View.Hand.FindIndex(c => c.InstanceId == instanceId);
            if (i < 0 || i >= _hand.Cards.Count) return;
            var card = _hand.Cards[i];
            if (card.Data == null) return;
            _flight = new Flight { Card = instanceId, Target = targetId, From = card.worldBound, Data = card.Data };
        }

        /// <summary>Flies the captured card when the step really played it (an accepted card play), then forgets it.</summary>
        private void TakeFlight(Core2StepResult step)
        {
            var flight = _flight;
            _flight = null;
            if (flight == null || flight.Data == null) return;
            var played = step.Timeline.Any(l => l.Type == FxEvents.CardPlayed && l.Event?.Str(FxKeys.CardInstanceId) == flight.Card);
            if (!played) return;
            var destination = (AnchorOf(flight.Target) ?? AnchorOf(View.Player.Id) ?? _player)?.ArtBox;
            if (destination == null) return;
            var ghost = new KitCard();
            ghost.Bind(flight.Data);
            _fx.CardFlight(ghost, flight.From, destination.worldBound);
        }

        // ------------------------------------------------------------------ the boss splash (components/intro.js)

        private void ShowBossIntro()
        {
            if (_run.EncounterPool != RewardsValues.Boss || View.Enemies.Count == 0) return;
            var intro = Root.Q(UiNames.BossIntro);
            if (intro == null) return;
            Root.Q<LocLabel>(UiNames.BossTag)?.SetResolved(Strings.Format(StringKeys.Core2FxBossTag, new StringArgs().Add(UiPlaceholders.Act, _run.Act)));
            Root.Q<LocLabel>(UiNames.BossName)?.SetResolved(View.Enemies[0].Name);
            _introOpen = true;
            UiDom.Show(intro, true);
            intro.focusable = true;
            intro.RegisterCallback<PointerDownEvent>(_ => CloseIntro());
            intro.RegisterCallback<KeyDownEvent>(_ => CloseIntro());
            intro.RegisterCallback<NavigationSubmitEvent>(_ => CloseIntro());
            intro.RegisterCallback<NavigationCancelEvent>(_ => CloseIntro());
            intro.Focus();
            Root.schedule.Execute(() => intro.AddToClassList(UiClasses.FxIn)).StartingIn(0);
            _introTimer = Root.schedule.Execute(CloseIntro).StartingIn((long)Defs.Intro.BossHoldMs);
        }

        private void CloseIntro()
        {
            if (!_introOpen) return;
            _introOpen = false;
            _introTimer?.Pause();
            var intro = Root.Q(UiNames.BossIntro);
            intro.RemoveFromClassList(UiClasses.FxIn);
            intro.AddToClassList(UiClasses.FxOut);
            Root.schedule.Execute(() => UiDom.Show(intro, false)).StartingIn((long)Defs.Intro.RemoveMs);
            if (!_busy && !_flow.Ended) FocusSoon(FirstPlayableCard());
        }

        // ------------------------------------------------------------------ the end of the fight (victoryBeat.js)

        /// <summary>A won fight stands its title over the field for the beat's 600 ms, then the spoils; a lost one goes straight to its panel.</summary>
        private void EnterEnded()
        {
            if (_ending) return;
            _ending = true;
            var victory = View.Result == CombatValues.Victory;
            if (!victory || ReducedMotion || !(Defs.Intro.VictoryBeatMs > 0))
            {
                ShowEnded();
                return;
            }
            var pool = _run.EncounterPool;
            var title = pool == RewardsValues.Boss
                ? Strings.Format(StringKeys.Core2FxVictoryBoss, new StringArgs().Add(UiPlaceholders.Name, (View.Enemies.FirstOrDefault()?.Name ?? string.Empty).ToUpperInvariant()))
                : Strings.Get(pool == RewardsValues.Elite ? StringKeys.Core2FxVictoryElite : StringKeys.Core2FxVictory);
            var beat = Root.Q(UiNames.VictoryBeat);
            Root.Q<LocLabel>(UiNames.VictoryTitle)?.SetResolved(title);
            UiDom.Show(beat, true);
            Root.schedule.Execute(() => beat.AddToClassList(UiClasses.FxIn)).StartingIn(0);
            Root.schedule.Execute(() =>
            {
                UiDom.Show(beat, false);
                beat.RemoveFromClassList(UiClasses.FxIn);
                ShowEnded();
            }).StartingIn((long)Defs.Intro.VictoryBeatMs);
        }
    }
}
