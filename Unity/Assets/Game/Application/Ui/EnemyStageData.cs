using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>A clip of the enemy stage: the frames it plays and how long each is held (content/enemyExpansion.js actions), in the stage's reference length.</summary>
    public sealed class EnemyClip
    {
        public string Name;
        public IReadOnlyList<string> Poses;
        public IReadOnlyList<double> Durations;
    }

    /// <summary>One enemy's figure: the size of its idle drawing, the frame it holds for each action category and the landmarks of its frames.</summary>
    public sealed class EnemyFigure
    {
        public string Id;
        public double IdleWidth;
        public double IdleHeight;
        public Dictionary<string, string> Stances = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, (double HeadY, double FeetY)> Marks = new Dictionary<string, (double HeadY, double FeetY)>(StringComparer.Ordinal);
    }

    /// <summary>The action family of an enemy's move and whether it is a cast (model/actionAnimation.js resolveActionAnimation).</summary>
    public struct EnemyMoveAction
    {
        public string Family;
        public bool Casting;
    }

    /// <summary>What an enemy's figure shows by its state (ui/enemyStates.js enemyPresentation): the rest it keeps and the themes of the buffs that colour its silhouette.</summary>
    public sealed class EnemyLook
    {
        public string Rest;
        public IReadOnlyList<string> Themes;
    }

    /// <summary>The tempo and reach of an enemy's action animation (content/config/ui/presentation/actionAnimations.json actors).</summary>
    public struct EnemyActor
    {
        public double Tempo;
        public double Reach;
    }

    /// <summary>
    /// ui/enemyStage.json (P-4, D-1540..): the enemies' combat figure as the web draws it, the expansion stage (src/ui/enemyExpansionStage.js). One canvas of frames
    /// (assets/enemy-poses/expansion/&lt;id&gt;/&lt;pose&gt;.webp) per enemy whose floor is at the frame's own landmark, drawn so the idle figure is the stage's height tall; the clips,
    /// aliases, held stance frames, the rest rules (ui/enemyStates.js enemyPresentation) and the rule that chooses the attack pose of a move (model/enemyActionPose.js).
    /// Engine-free, tested without the editor.
    /// </summary>
    public sealed class EnemyStageData
    {
        public readonly double Height;
        public readonly double Canvas;
        public readonly double Floor;
        public readonly double ClipMs;
        public readonly IReadOnlyDictionary<string, string> Aliases;
        public readonly IReadOnlyCollection<string> Poses;
        public readonly IReadOnlyDictionary<string, EnemyClip> Clips;
        public readonly IReadOnlyList<string> Families;
        public readonly IReadOnlyDictionary<string, string> StanceOf;
        public readonly IReadOnlyDictionary<string, string> AuraColors;
        public readonly IReadOnlyDictionary<string, string> AuraThemes;
        public readonly IReadOnlyCollection<string> Negative;
        public readonly double WoundedShare;
        public readonly double BlurBase;
        public readonly double BlurStep;
        public readonly IReadOnlyDictionary<string, string> Motion;

        /// <summary>The direction an enemy travels in (-1, toward the player) and the distances of its recoil and of its travel at a reach of 1, in pixels (ui/screens/combat.js playFamilyAnimation).</summary>
        public readonly double TravelDirection;
        public readonly double TravelPx;
        public readonly double RecoilPx;
        public readonly string TravelEasing;

        /// <summary>The stops of the translate of each motion (sweep, lunge, impact), as the family-* keyframes of styles/combat.css name them.</summary>
        public readonly IReadOnlyDictionary<string, IReadOnlyList<(double T, string At)>> Travel;
        private readonly Dictionary<string, EnemyFigure> _figures = new Dictionary<string, EnemyFigure>(StringComparer.Ordinal);
        private readonly Dictionary<string, EnemyActor> _actors = new Dictionary<string, EnemyActor>(StringComparer.Ordinal);
        private readonly Dictionary<string, EnemyMoveAction> _moves = new Dictionary<string, EnemyMoveAction>(StringComparer.Ordinal);

        public EnemyStageData(JObject json)
        {
            var stage = (JObject)json[EnemyStageKeys.Stage];
            Height = stage.Value<double>(EnemyStageKeys.Height);
            Canvas = stage.Value<double>(EnemyStageKeys.Canvas);
            Floor = stage.Value<double>(EnemyStageKeys.Floor);
            ClipMs = stage.Value<double>(EnemyStageKeys.ClipMs);
            Aliases = ((JObject)json[EnemyStageKeys.Aliases]).Properties().ToDictionary(p => p.Name, p => p.Value.Value<string>(), StringComparer.Ordinal);
            Poses = new HashSet<string>(((JArray)json[EnemyStageKeys.Poses]).Select(t => t.Value<string>()), StringComparer.Ordinal);
            Clips = ((JObject)json[EnemyStageKeys.Actions]).Properties().ToDictionary(p => p.Name, p => new EnemyClip
            {
                Name = p.Name,
                Poses = ((JArray)p.Value[EnemyStageKeys.Poses]).Select(t => t.Value<string>()).ToList(),
                Durations = ((JArray)p.Value[EnemyStageKeys.Durations]).Select(t => t.Value<double>()).ToList(),
            }, StringComparer.Ordinal);
            Families = ((JArray)json[EnemyStageKeys.Families]).Select(t => t.Value<string>()).ToList();
            StanceOf = ((JObject)json[EnemyStageKeys.StanceOf]).Properties().ToDictionary(p => p.Name, p => p.Value.Type == JTokenType.Null ? null : p.Value.Value<string>(), StringComparer.Ordinal);
            foreach (var row in (JObject)json[EnemyStageKeys.Enemies])
            {
                var o = (JObject)row.Value;
                var figure = new EnemyFigure { Id = row.Key, IdleWidth = o.Value<double>(EnemyStageKeys.IdleWidth), IdleHeight = o.Value<double>(EnemyStageKeys.IdleHeight) };
                foreach (var s in (JObject)o[EnemyStageKeys.Stances]) figure.Stances[s.Key] = s.Value.Value<string>();
                foreach (var m in (JObject)o[EnemyStageKeys.Marks]) figure.Marks[m.Key] = (m.Value[0].Value<double>(), m.Value[1].Value<double>());
                _figures[row.Key] = figure;
            }
            var aura = (JObject)json[EnemyStageKeys.Aura];
            AuraColors = ((JObject)aura[EnemyStageKeys.Colors]).Properties().ToDictionary(p => p.Name, p => p.Value.Value<string>(), StringComparer.Ordinal);
            AuraThemes = ((JObject)aura[EnemyStageKeys.Themes]).Properties().SelectMany(p => ((JArray)p.Value).Select(t => (Status: t.Value<string>(), Theme: p.Name))).ToDictionary(x => x.Status, x => x.Theme, StringComparer.Ordinal);
            Negative = new HashSet<string>(((JArray)aura[EnemyStageKeys.Negative]).Select(t => t.Value<string>()), StringComparer.Ordinal);
            WoundedShare = aura.Value<double>(EnemyStageKeys.WoundedShare);
            BlurBase = aura.Value<double>(EnemyStageKeys.BlurBase);
            BlurStep = aura.Value<double>(EnemyStageKeys.BlurStep);
            var animation = (JObject)json[EnemyStageKeys.Animation];
            Motion = ((JObject)animation[EnemyStageKeys.Motion]).Properties().ToDictionary(p => p.Name, p => p.Value.Value<string>(), StringComparer.Ordinal);
            var travel = (JObject)animation[EnemyStageKeys.Travel];
            TravelDirection = travel.Value<double>(EnemyStageKeys.Direction);
            TravelPx = travel.Value<double>(EnemyStageKeys.TravelPx);
            RecoilPx = travel.Value<double>(EnemyStageKeys.RecoilPx);
            TravelEasing = travel.Value<string>(EnemyStageKeys.Easing);
            Travel = ((JObject)travel[EnemyStageKeys.Tracks]).Properties().ToDictionary(p => p.Name,
                p => (IReadOnlyList<(double, string)>)((JArray)p.Value).Select(t => (t.Value<double>(EnemyStageKeys.T), t.Value<string>(EnemyStageKeys.At))).ToList(), StringComparer.Ordinal);
            foreach (var a in (JObject)animation[EnemyStageKeys.Actors])
                _actors[a.Key] = new EnemyActor { Tempo = a.Value.Value<double>(EnemyStageKeys.Tempo), Reach = a.Value.Value<double>(EnemyStageKeys.Reach) };
            foreach (var m in (JObject)animation[EnemyStageKeys.Moves])
                _moves[m.Key] = new EnemyMoveAction { Family = m.Value.Value<string>(EnemyStageKeys.Family), Casting = m.Value.Value<bool>(EnemyStageKeys.Casting) };
        }

        /// <summary>An enemy that has an expansion stage (every enemy of the Core 2 roster).</summary>
        public bool Has(string enemyId) => enemyId != null && _figures.ContainsKey(enemyId);

        public EnemyFigure Figure(string enemyId) => enemyId != null && _figures.TryGetValue(enemyId, out var f) ? f : null;

        public IEnumerable<string> EnemyIds => _figures.Keys;

        /// <summary>The tempo and reach of the actor's actions (1 and 1 for an actor with no row).</summary>
        public EnemyActor Actor(string enemyId) => enemyId != null && _actors.TryGetValue(enemyId, out var a) ? a : new EnemyActor { Tempo = 1, Reach = 1 };

        /// <summary>The family and cast flag of an enemy's move (the neutral family for a move with none).</summary>
        public EnemyMoveAction Move(string enemyId, string moveId) =>
            enemyId != null && moveId != null && _moves.TryGetValue(enemyId + EnemyStageKeys.MoveJoiner + moveId, out var m) ? m : default;

        /// <summary>The stage's own spelling of a pose: an alias (guard, hurt, buff, ...) answers to the clip or frame it names (resolve).</summary>
        public string Resolve(string pose) => pose != null && Aliases.TryGetValue(pose, out var key) ? key : pose;

        /// <summary>The frame a pose is drawn with: the first frame of its clip, else the pose itself (draw).</summary>
        public string FrameOf(string pose) => pose != null && Clips.TryGetValue(pose, out var clip) ? clip.Poses[0] : pose;

        /// <summary>The category of the action an intent stance announces (model/enemyStancePose.js), or null for none.</summary>
        public string Category(string stance) => stance != null && StanceOf.TryGetValue(stance, out var category) ? category : null;

        /// <summary>The frame an enemy holds for an action category, or null when it has none.</summary>
        public string StanceFrame(string enemyId, string category)
        {
            var figure = Figure(enemyId);
            return figure != null && category != null && figure.Stances.TryGetValue(category, out var frame) ? frame : null;
        }

        /// <summary>The rectangle a frame is drawn in, in the stage's units from the idle figure's centre (left) and the stage's top (top). Rest frames are normalized to the idle body's height (settle).</summary>
        public StageRect Place(EnemyFigure figure, string frame, bool normalizeBody)
        {
            var scale = Height / figure.IdleHeight;
            var factor = 1d;
            var has = figure.Marks.TryGetValue(frame, out var marks);
            if (normalizeBody && has && figure.Marks.TryGetValue(EnemyRests.Idle, out var idle) && idle.FeetY - idle.HeadY > 0 && marks.FeetY - marks.HeadY > 0)
                factor = (idle.FeetY - idle.HeadY) / (marks.FeetY - marks.HeadY);
            var draw = scale * factor;
            var floor = has ? marks.FeetY : Floor;
            return new StageRect { Left = -Canvas * draw * P1Math.Half, Top = Height - floor * draw, Width = Canvas * draw, Height = Canvas * draw };
        }

        // ------------------------------------------------------------------ ui/enemyStates.js enemyPresentation

        /// <summary>
        /// enemyPresentation: the rest of a living enemy (guard while it holds Block, afflicted under a hostile status, wounded at or under the wounded share of its HP, else idle) and the
        /// themes of the buffs that colour its silhouette. A downed enemy rests defeated.
        /// </summary>
        public EnemyLook Presentation(bool alive, double hp, double maxHp, double block, IEnumerable<(string Id, double Stacks, bool Meter)> statuses)
        {
            if (!alive || hp == 0) return new EnemyLook { Rest = EnemyRests.Defeated, Themes = Array.Empty<string>() };
            var active = statuses.Where(s => !s.Meter && s.Stacks > 0).Select(s => s.Id).ToList();
            var themes = active.Where(AuraThemes.ContainsKey).Select(id => AuraThemes[id]).Distinct().ToList();
            var rest = block > 0 ? EnemyRests.Guard : active.Any(Negative.Contains) ? EnemyRests.Afflicted : maxHp > 0 && hp / maxHp <= WoundedShare ? EnemyRests.Wounded : EnemyRests.Idle;
            return new EnemyLook { Rest = rest, Themes = themes };
        }

        // ------------------------------------------------------------------ model/enemyActionPose.js

        /// <summary>enemyActionPose: the pose an enemy plays for a move of this kind and action family.</summary>
        public string ActionPose(string kind, EnemyMoveAction action)
        {
            if (kind != EnemyKinds.Attack) return kind == EnemyKinds.Block || action.Family == EnemyFamilies.Guard ? EnemyActions.Guard : EnemyActions.Buff;
            if (action.Casting || action.Family == EnemyFamilies.Spell) return EnemyActions.MagicAttack;
            if (action.Family == EnemyFamilies.Projectile) return EnemyActions.Projectile;
            if (action.Family == EnemyFamilies.Sweep) return EnemyActions.Sweep;
            return EnemyActions.Attack;
        }

        /// <summary>
        /// The translate an enemy's action animation plays while it acts (family-slash, family-thrust, family-strike at the actor's reach and the actor's length of <paramref name="ms"/>), as a track the figure
        /// plays; null for a motion that does not travel (the release of a projectile, a cast, a brace and a sidestep are brightness and opacity only).
        /// </summary>
        public TrackDef TravelTrack(string motion, double reach, double ms)
        {
            if (motion == null || !Travel.TryGetValue(motion, out var stops)) return null;
            var track = new TrackDef { Ms = ms, Ease = TravelEasing };
            foreach (var stop in stops)
                track.Stops.Add(new FxStop { T = stop.T, X = stop.At == EnemyTravel.Travel ? TravelDirection * TravelPx * reach : stop.At == EnemyTravel.Recoil ? -TravelDirection * RecoilPx * reach : 0 });
            return track;
        }

        /// <summary>The css of the enemy's silhouette glow: the colour of each theme, drawn with the stage's blur (drop-shadow(0 0 base + i * step px colour)).</summary>
        public double AuraBlur(int index) => BlurBase + index * BlurStep;
    }
}
