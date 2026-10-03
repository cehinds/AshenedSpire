using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using P = Ashen.Generated.MetaPlaceholders;
using PV = Ashen.Generated.ProgressionValues;
using RK = Ashen.Generated.RunKeys;
using S = Ashen.Generated.MetaStringKeys;

namespace Ashen.App.Run
{
    /// <summary>One class-tree node of W-22: its tile, its state and the node it is exclusive with.</summary>
    public sealed class ClassTreeNodeView
    {
        public string Id;
        public string Name;
        public string Text;
        public string State;
        public string StateText;
    }

    /// <summary>One tier of the class tree: when it opens, and its nodes (an exclusive pair is named).</summary>
    public sealed class ClassTreeTierView
    {
        public int Tier;
        public bool Open;
        public string Title;
        public readonly List<ClassTreeNodeView> Nodes = new List<ClassTreeNodeView>();
        public string Exclusive;
    }

    /// <summary>W-22 progression: the character level and banked points, the skill tracks, and the class tree (US-12.1-12.3, US-12.5).</summary>
    public sealed class ProgressionViewState
    {
        public string Title;
        public string Level;
        public string Points;
        public string TracksTitle;
        public readonly List<string> Tracks = new List<string>();
        public string ClassTreeTitle;
        public readonly List<ClassTreeTierView> Tiers = new List<ClassTreeTierView>();
    }

    /// <summary>
    /// W-22 as display data over the run document. The level-up allocator is W-10's level pane and the drafts are W-08's
    /// rows; this view shows where they stand. Class-tree states: chosen (in the core card's tags), can be drafted (in
    /// the class's draft pool at its level), closed by a chosen node it excludes, or not yet (its tier is closed or a
    /// requirement is missing). Engine-free.
    /// </summary>
    public static class ProgressionView
    {
        public static ProgressionViewState Build(RunSession session, UiData ui) => Build(session, ui, session.Run);

        /// <summary>The view of a given run document (the session's own, or a review copy).</summary>
        public static ProgressionViewState Build(RunSession session, UiData ui, JObject run)
        {
            var strings = ui.Strings;
            var rewards = session.Content.Loop.Rewards;
            var view = new ProgressionViewState { Title = strings.Get(S.ProgressionTitle), TracksTitle = strings.Get(S.ProgressionTracks) };

            var level = run.Obj(K.Level) ?? LevelUp.EmptyLevel();
            var next = LevelUp.XpToNext(rewards, level[K.Level]);
            view.Level = double.IsInfinity(next) || double.IsNaN(next)
                ? strings.Format(S.ProgressionLevelMax, new StringArgs().Add(P.Level, (int)level.Num(K.Level)))
                : strings.Format(S.ProgressionLevel, new StringArgs().Add(P.Level, (int)level.Num(K.Level)).Add(P.Xp, (int)level.Num(K.Xp)).Add(P.Next, (int)next));
            view.Points = strings.Format(S.ProgressionPoints, new StringArgs().Add(P.N, (int)level.Num(RK.UnspentPoints)));

            var ledger = run.Obj(K.Skills) ?? new JObject();
            foreach (var track in Skills.Tracks(rewards))
            {
                var row = ledger.Obj(track.Id);
                if (row == null || (Js.Or0(row[K.Level]) <= 0 && Js.Or0(row[K.Xp]) <= 0)) continue;
                var toNext = Skills.XpToNext(rewards, track.Kind, row[K.Level]);
                view.Tracks.Add(strings.Format(S.ProgressionTrack, new StringArgs().Add(P.Name, RewardsView.TrackName(session, track.Id))
                    .Add(P.Level, (int)Js.Or0(row[K.Level])).Add(P.Xp, (int)Js.Or0(row[K.Xp])).Add(P.Next, double.IsInfinity(toNext) || double.IsNaN(toNext) ? 0 : (int)toNext)));
            }
            if (view.Tracks.Count == 0) view.Tracks.Add(strings.Get(S.ProgressionTracksNone));

            var classId = session.ClassId;
            var classLevel = Skills.LevelOf(run, Skills.ClassSkillId(classId));
            view.ClassTreeTitle = strings.Format(S.ClassTreeTitle, new StringArgs().Add(P.Class, RunHudView.ClassName(ui, classId)).Add(P.Level, (int)classLevel));
            var picked = new HashSet<string>(Js.Items(run[K.CoreTags]).Select(RunJs.Key), StringComparer.Ordinal);
            var pool = new HashSet<string>(ClassTree.DraftPool(rewards, classId, run[K.CoreTags] ?? new JArray(), classLevel), StringComparer.Ordinal);
            var data = session.Content.Combat;
            foreach (var group in ClassTree.Rows(rewards, classId).GroupBy(r => r.Tier).OrderBy(g => g.Key))
            {
                var opens = ClassTree.TierOpensAt(rewards, group.Key);
                var open = classLevel >= opens;
                var tier = new ClassTreeTierView
                {
                    Tier = (int)group.Key,
                    Open = open,
                    Title = strings.Format(open ? S.ClassTreeTier : S.ClassTreeTierClosed, new StringArgs().Add(P.N, (int)group.Key)
                        .Add(P.Level, double.IsInfinity(opens) ? 0 : (int)opens)),
                };
                foreach (var row in group)
                {
                    var pick = RewardsView.NodePick(session, row.NodeId);
                    var rule = data.PropertyRules.Has(row.NodeId) ? data.PropertyRules.Get(row.NodeId) : null;
                    var excluder = Js.Items(rule?[K.Excludes]).Select(RunJs.Key).FirstOrDefault(picked.Contains)
                        ?? picked.FirstOrDefault(p => data.PropertyRules.Has(p) && Js.Items(data.PropertyRules.Get(p)[K.Excludes]).Select(RunJs.Key).Contains(row.NodeId));
                    var state = picked.Contains(row.NodeId) ? PV.Picked : pool.Contains(row.NodeId) ? PV.Available : excluder != null ? PV.Excluded : PV.Closed;
                    tier.Nodes.Add(new ClassTreeNodeView
                    {
                        Id = row.NodeId,
                        Name = pick.Name,
                        Text = pick.Text,
                        State = state,
                        StateText = state == PV.Picked ? strings.Get(S.ClassTreePicked)
                            : state == PV.Available ? strings.Get(S.ClassTreeAvailable)
                            : state == PV.Excluded ? strings.Format(S.ClassTreeExcluded, new StringArgs().Add(P.Name, RewardsView.NodePick(session, excluder).Name))
                            : strings.Get(S.ClassTreeClosed),
                    });
                }
                // A tier whose nodes exclude one another reads as one choice (the tier-3 subclasses).
                // (Exclusion is written on one side only, so a node counts when it excludes or is excluded by a tier-mate.)
                List<string> Excludes(string id) => data.PropertyRules.Has(id) ? Js.Items(data.PropertyRules.Get(id)[K.Excludes]).Select(RunJs.Key).ToList() : new List<string>();
                var exclusive = tier.Nodes.Where(n => tier.Nodes.Any(o => o.Id != n.Id && (Excludes(n.Id).Contains(o.Id) || Excludes(o.Id).Contains(n.Id))))
                    .Select(n => n.Name).ToList();
                if (exclusive.Count > 1)
                    tier.Exclusive = strings.Format(S.ClassTreeExclusive, new StringArgs().Add(P.List, string.Join(strings.Get(S.ClassTreeListJoin), exclusive)));
                view.Tiers.Add(tier);
            }
            return view;
        }
    }
}
