using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using CK = Ashen.Generated.Core2CombatKeys;
using K = Ashen.Generated.CombatKeys;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The enemies' figures on the Core 2 board (P-4, D-1540..), as src/ui/screens/combat.js drives src/ui/enemyExpansionStage.js: the rest each enemy keeps (its Block, statuses and HP, enemyPresentation), the frame
    /// it holds for the action its intent announces (presentationStance, which follows the beats as the web's display state does: a move it starts, its turn's intent, the player's turn start), and the pose it plays for
    /// the move of a beat (enemyActionPose) over the length the pace gives it scaled by its tempo.
    /// </summary>
    public sealed partial class Core2CombatScreen
    {
        /// <summary>The action category each enemy's figure holds now (the web's presentationStance of the display state), and whether a timeline is moving it (else the view's own intent is).</summary>
        private readonly Dictionary<string, string> _enemyStances = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool _enemyLedger;

        private EnemyStageData EnemyData => Ui.Data.EnemyStage;

        private string DefOfEnemy(string id) => id == null ? null : View.Enemies.FirstOrDefault(e => e.Id == id)?.DefId;

        /// <summary>The category of the enemy's intent, as the view shows it now.</summary>
        private string ViewCategory(Core2FighterView f) => EnemyStancePlans.Category(EnemyData, f.IntentRaw);

        /// <summary>The category the figure holds: the ledger's while a timeline plays, else the view's (which the ledger then takes).</summary>
        private string HeldCategory(Core2FighterView f)
        {
            if (_enemyLedger && _enemyStances.TryGetValue(f.Id, out var held)) return held;
            var category = f.Alive ? ViewCategory(f) : null;
            _enemyStances[f.Id] = category;
            return category;
        }

        /// <summary>
        /// The figure of an enemy: the expansion stage when its frames are imported, else the legacy art. The rest follows enemyPresentation of what the bars show (the hit points a number has not yet taken are still
        /// in the figure), the themes colour the silhouette and the category is the action its intent announces.
        /// </summary>
        private void ApplyEnemy(FighterData d, Core2FighterView f)
        {
            var data = EnemyData;
            if (f.DefId == null || data == null || !data.Has(f.DefId) || Ui.Texture(EnemyArt(f.DefId, EnemyRests.Idle)) == null)
            {
                ApplyRestArt(d, f);
                return;
            }
            var look = data.Presentation(d.Alive, f.Hp + HeldHp(f.Id), f.MaxHp, f.Block, f.Statuses.Select(s => (s.Id, s.Stacks, s.MeterValue.HasValue)));
            d.Enemy = new EnemyFeed
            {
                Data = data,
                EnemyId = f.DefId,
                Texture = art => Ui.Texture(art),
                ArtOf = frame => EnemyArt(f.DefId, frame),
                TickMs = Defs.Pace.TickMs,
                Reduced = ReducedMotion,
                Rest = look.Rest,
                Themes = look.Themes,
                Category = HeldCategory(f),
            };
        }

        /// <summary>The art id of a frame of an enemy at the tier the layout asks for: the light frames on a narrow layout (the web's Auto art quality), else the full ones; the full ones when the light one is missing.</summary>
        private string EnemyArt(string defId, string frame)
        {
            var args = new StringArgs().Add(ArtPlaceholders.Id, defId).Add(ArtPlaceholders.Name, frame);
            if (Nav.Layout?.Narrow ?? false)
            {
                var lite = Ui.Data.Art.Find(ArtKinds.EnemyLite, args);
                if (lite != null) return lite;
            }
            return Ui.Data.Art.Find(ArtKinds.EnemyFrame, args);
        }

        // ------------------------------------------------------------------ the display state follows the beats (applyBeatToDisp)

        /// <summary>
        /// An event of a step that moves what an enemy's figure holds (enemyMoveStarted, enemyActorTurnStarted, playerTurnStart), applied when its beat is over. False for an event that is not one.
        /// </summary>
        private bool ApplyEnemyEvent(Core2LogLine line)
        {
            var c = _flow.Session?.State;
            if (c == null || line.Event == null && line.Type != FxEvents.PlayerTurnStart) return false;
            switch (line.Type)
            {
                case FxEvents.EnemyMoveStarted:
                {
                    // Reactions return to the planned hold; a pooled follow-up replaces it.
                    if (line.Event.Is(CK.Reaction)) return true;
                    var id = line.Event.Str(FxKeys.SourceId);
                    var enemy = id == null ? null : c.Enemies.FirstOrDefault(e => e.Str(K.Id) == id);
                    var category = EnemyStancePlans.ForMove(EnemyData, c, enemy, line.Event.Str(FxKeys.MoveId), line.Event.Str(FxKeys.Kind));
                    if (id != null && enemy != null && category != null) _enemyStances[id] = category;
                    return true;
                }
                case FxEvents.EnemyActorTurnStarted:
                {
                    var id = line.Event.Str(FxKeys.SourceId);
                    if (id != null && _enemyStances.ContainsKey(id)) _enemyStances[id] = EnemyStancePlans.Category(EnemyData, line.Event.Obj(K.Intent));
                    return true;
                }
                case FxEvents.PlayerTurnStart:
                    foreach (var f in View.Enemies) _enemyStances[f.Id] = f.Alive ? ViewCategory(f) : null;
                    return true;
            }
            return false;
        }

        /// <summary>The figures take the categories the ledger holds now.</summary>
        private void RefreshEnemies()
        {
            foreach (var f in View.Enemies)
                if (_enemies.TryGetValue(f.Id, out var element) && element.Data?.Enemy != null)
                {
                    element.Data.Enemy.Category = _enemyStances.TryGetValue(f.Id, out var category) ? category : null;
                    element.RefreshEnemy();
                }
        }

        // ------------------------------------------------------------------ the pose of a beat (animateActor, playFamilyAnimation)

        private EnemyAction EnemyActionOf(FxBeat beat)
        {
            if (beat?.ActorId == null) return null;
            var def = DefOfEnemy(beat.ActorId);
            return EnemyStagePlans.ActionOf(EnemyData, beat, def);
        }

        /// <summary>The authored length of an enemy's action and when its blow lands: the pace's lunge scaled by the actor's tempo (playFamilyAnimation); null for an actor that is not an enemy with a stage.</summary>
        private StageTiming? EnemyTiming(FxBeat beat)
        {
            if (beat?.ActorId == null || !_enemies.TryGetValue(beat.ActorId, out var fighter) || !fighter.HasEnemyStage) return null;
            return EnemyStagePlans.Timing(EnemyActionOf(beat), _pace.Sprite.LungeMs, Defs.Pace.WindupFraction);
        }

        /// <summary>An enemy swings for a beat: its stage plays the pose of the move over the action's length and the figure travels as the action's family does.</summary>
        private void SwingEnemy(Fighter actor, FxItem item)
        {
            var action = EnemyActionOf(item.Beat);
            var pose = action?.Pose ?? (item.Attack ? EnemyActions.Attack : EnemyActions.Guard);
            if (item.Ms > 0 && !ReducedMotion) actor.PlayEnemy(pose, item.Ms);
            if (ReduceFlashes || ReducedMotion || item.Ms <= 0) return;
            var track = action == null ? Defs.Track(FxTrack.LungeEnemy) : EnemyData.TravelTrack(action.Motion, action.Reach, item.Ms);
            if (track != null) actor.Play(_motion, track, item.Ms, false);
        }
    }
}
