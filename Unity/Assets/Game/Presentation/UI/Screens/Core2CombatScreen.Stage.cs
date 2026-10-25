using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using Newtonsoft.Json.Linq;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The player's card stage on the Core 2 board (P-3b, D-1490..), as src/ui/screens/combat.js drives src/ui/alternativeCardStage.js: the stance that holds the figure (the card selected, else the
    /// last card played: the stance ledger), the rest it keeps between actions (a guard rests braced into the next turn until the player's turn starts), the action a card plays and how long it takes.
    /// The ledger and the rest are applied as the web applies them, when the beat that played the card is over (onBeatApplied), or all at once when the beats are skipped (onFlush).
    /// </summary>
    public sealed partial class Core2CombatScreen
    {
        private readonly Dictionary<string, CardStagePlan> _stagePlans = new Dictionary<string, CardStagePlan>(StringComparer.Ordinal);
        private readonly List<(int Beat, Core2LogLine Line)> _stagePending = new List<(int Beat, Core2LogLine Line)>();
        private List<FxBeat> _stageBeats = new List<FxBeat>();
        private CardStagePlan _stageHeld;
        private string _playerRest = StageNames.Idle;

        /// <summary>The plans of the cards in the hand, so a card that leaves it still has its plan when its beat plays.</summary>
        private void RememberPlans()
        {
            foreach (var card in View.Hand)
                if (card.Stage != null) _stagePlans[card.InstanceId] = card.Stage;
        }

        private bool PilotClass => Ui.Data.CardStage?.Class(_run.ClassId)?.Pilot ?? false;

        /// <summary>playerStance(def): the stance of the card armed, else the last card played; the pilot holds the stance its card prepares (selectedWeaponStanceFor).</summary>
        private string PlayerStance()
        {
            var armed = _flow.Armed?.Stage;
            var plan = armed ?? _stageHeld;
            var stance = plan?.Stance;
            if (!PilotClass) return stance;
            var player = _flow.Session?.State?.Player;
            if (player != null && player[StageTags.EntityAlive] != null && !player.Value<bool>(StageTags.EntityAlive)) return stance;
            return plan != null ? plan.PilotStance : CardStagePlans.PilotEntityStance(player, stance);
        }

        /// <summary>
        /// syncPlayerActionIntent: the glyph over the player's head, always shown: the armed card's maneuver while it is armed, else the last card played, else the neutral resting mark (the prepare hourglass).
        /// </summary>
        private string PlayerGlyph() => CardStagePlans.IntentGlyph((_flow.Armed?.Stage ?? _stageHeld)?.Maneuver);

        /// <summary>resolveCombatPose's rest: the downed figure, a guard or counter braced, else idle.</summary>
        private string PlayerRest(bool alive)
        {
            if (!alive) return StageNames.Defeated;
            return _playerRest == StageNames.RestGuard || _playerRest == StageNames.RestCounter || _playerRest == StageNames.RestDefend ? _playerRest : StageNames.Idle;
        }

        /// <summary>The action the player's figure plays for a beat (animateActor): the card's own, a counter's return, else the default swing or guard of a beat with no card.</summary>
        private string StageActionOf(FxBeat beat)
        {
            var played = beat.Events.FirstOrDefault(l => l.Type == FxEvents.CardPlayed);
            if (played != null)
                return played.Event != null && _stagePlans.TryGetValue(played.Event.Str(FxKeys.CardInstanceId) ?? string.Empty, out var plan) ? plan.Play : null;
            if (beat.Kind == FxBeatKind.Counter) return StageActions.Counter;
            return beat.Kind == FxBeatKind.Attack ? StageActions.Attack : StageNames.RestGuard;
        }

        /// <summary>The authored length of the player's action in a beat and when its blow lands (stage.actionTiming): only a card's action or a counter's return has its own length.</summary>
        private StageTiming? AuthoredTiming(FxBeat beat)
        {
            if (beat.ActorId != View.Player.Id) return EnemyTiming(beat);
            if (_player == null || !_player.HasStage) return null;
            var card = beat.Events.Any(l => l.Type == FxEvents.CardPlayed);
            if (!card && beat.Kind != FxBeatKind.Counter) return null;
            var action = StageActionOf(beat);
            return action == null ? null : _player.TimingOf(action, _pace.Sprite);
        }

        /// <summary>The events of a step the ledger and the rest follow, with the beat they belong to.</summary>
        private void PlanStage(IReadOnlyList<FxBeat> beats)
        {
            _stageBeats = beats.ToList();
            _stagePending.Clear();
            for (var i = 0; i < beats.Count; i++)
                foreach (var line in beats[i].Events.Where(l => l.Type == FxEvents.CardPlayed || l.Type == FxEvents.PlayerTurnStart || l.Type == FxEvents.EnemyMoveStarted || l.Type == FxEvents.EnemyActorTurnStarted))
                    _stagePending.Add((i, line));
            _enemyLedger = _stagePending.Count > 0;
        }

        /// <summary>applyVisualEvents for the beats up to <paramref name="through"/> (all when negative): the last card played holds the figure, a guard leaves it braced, the player's turn start clears the rest.</summary>
        private void ApplyStage(int through)
        {
            var any = false;
            var enemies = false;
            while (_stagePending.Count > 0 && (through < 0 || _stagePending[0].Beat <= through))
            {
                var line = _stagePending[0].Line;
                _stagePending.RemoveAt(0);
                any = true;
                if (line.Type == FxEvents.PlayerTurnStart)
                {
                    _playerRest = StageNames.Idle;
                    enemies |= ApplyEnemyEvent(line);
                }
                else if (line.Type == FxEvents.EnemyMoveStarted || line.Type == FxEvents.EnemyActorTurnStarted) enemies |= ApplyEnemyEvent(line);
                else if (line.Event != null && _stagePlans.TryGetValue(line.Event.Str(FxKeys.CardInstanceId) ?? string.Empty, out var plan))
                {
                    _stageHeld = plan;
                    if (plan.Rest != null) _playerRest = plan.Rest;
                }
            }
            if (any) RefreshStage();
            if (_stagePending.Count == 0) _enemyLedger = false;
            if (enemies) RefreshEnemies();
        }

        private void ApplyStage(FxBeat beat)
        {
            var at = _stageBeats.IndexOf(beat);
            if (at >= 0) ApplyStage(at);
        }

        private void ApplyStageAll() => ApplyStage(-1);

        /// <summary>A beat is over (its numbers landed): the card it played holds the figure now, and the player's fall is drawn.</summary>
        private void EndBeat(FxBeat beat)
        {
            ApplyStage(beat);
            if (beat.Events.Any(l => l.Type == FxEvents.PlayerDied) && _dying.Remove(View.Player.Id)) RenderFighters();
        }

        /// <summary>Gives the figure the stance and the rest the ledger holds now.</summary>
        private void RefreshStage()
        {
            _player?.SetGlyph(Ui.IntentGlyph(PlayerGlyph()));
            if (_player?.Data?.Stage == null) return;
            _player.Data.Stage = MakeFeed(_player.Data.Alive);
            _player.RefreshStage();
        }
    }
}
