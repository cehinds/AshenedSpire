using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using MK = Ashen.Generated.MapKeys;
using P = Ashen.Generated.MetaPlaceholders;
using RK = Ashen.Generated.RunKeys;
using S = Ashen.Generated.MetaStringKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;

namespace Ashen.App.Run
{
    /// <summary>One rail item of W-16 (its id and label with the count).</summary>
    public sealed class JournalRailItem
    {
        public string Id;
        public string Label;
    }

    /// <summary>One history row of W-16 and its detail pane (US-13.1).</summary>
    public sealed class JournalRunRow
    {
        public bool Victory;
        public string Outcome;
        public string Title;
        public string Where;
        public readonly List<string> Pills = new List<string>();
        public string Seed;
        public readonly List<string> Detail = new List<string>();
        public string DeckTitle;
        public readonly List<string> Deck = new List<string>();
    }

    /// <summary>W-16 <c>screen:journal</c>: history (newest first, with the per-class win-rate strip), the profile tally and the found armaments.</summary>
    public sealed class JournalViewState
    {
        public string Title;
        public readonly List<JournalRailItem> Rail = new List<JournalRailItem>();
        public readonly List<string> WinRates = new List<string>();
        public readonly List<JournalRunRow> History = new List<JournalRunRow>();
        public string HistoryEmpty;
        public readonly List<string> Profile = new List<string>();
        public readonly List<string> Bosses = new List<string>();
        public readonly List<string> Unlocks = new List<string>();
        public readonly List<string> Armaments = new List<string>();
        public string ArmamentsEmpty;
    }

    /// <summary>
    /// Builds W-16 from the stored profile (results, progress, unlocked, discoveredArmaments) and the run content's
    /// names. Engine-free; a record from before D-148 (no killer, duration or deck) reads as unknown.
    /// </summary>
    public static class JournalView
    {
        public static JournalViewState Build(JObject profile, RunContent content, UiData ui)
        {
            var strings = ui.Strings;
            profile = profile ?? new JObject();
            var results = Js.Items(profile[LK.Results]).OfType<JObject>().ToList();
            var armaments = Js.Items(profile[RK.DiscoveredArmaments]).Select(Js.Str).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            var view = new JournalViewState
            {
                Title = strings.Get(S.JournalTitle),
                HistoryEmpty = results.Count == 0 ? strings.Get(S.JournalHistoryEmpty) : null,
                ArmamentsEmpty = armaments.Count == 0 ? strings.Get(S.JournalArmamentsEmpty) : null,
            };
            view.Rail.Add(new JournalRailItem { Id = MetaValues.JournalHistory, Label = strings.Format(S.JournalRailHistory, new StringArgs().Add(P.Count, results.Count)) });
            view.Rail.Add(new JournalRailItem { Id = MetaValues.JournalProfile, Label = strings.Get(S.JournalRailProfile) });
            view.Rail.Add(new JournalRailItem { Id = MetaValues.JournalArmaments, Label = strings.Format(S.JournalRailArmaments, new StringArgs().Add(P.Count, armaments.Count)) });

            // The win-rate strip: one entry per class with a recorded run, in the class table's order.
            foreach (var classId in content.ClassIds)
            {
                var runs = results.Count(r => r.Str(RK.Class) == classId);
                if (runs == 0) continue;
                var wins = results.Count(r => r.Str(RK.Class) == classId && r.Is(V.Victory));
                view.WinRates.Add(strings.Format(S.JournalWinRate, new StringArgs().Add(P.Class, RunHudView.ClassName(ui, classId)).Add(P.Wins, wins).Add(P.Runs, runs)));
            }

            for (var i = results.Count - 1; i >= 0; i--) view.History.Add(Row(results[i], content, ui));

            var progress = profile.Obj(LK.Progress) ?? Ashen.Domain.Loop.RunEnd.EmptyProgress();
            view.Profile.Add(strings.Format(S.JournalProfileRuns, new StringArgs().Add(P.Count, (int)Js.Or0(progress[LK.Runs]))));
            view.Profile.Add(strings.Format(S.JournalProfileWins, new StringArgs().Add(P.Count, (int)Js.Or0(progress[LK.Wins]))));
            view.Profile.Add(strings.Format(S.JournalProfileMaxAct, new StringArgs().Add(P.Act, (int)Math.Max(1, Js.Or0(progress[LK.MaxAct])))));
            view.Profile.Add(strings.Format(S.JournalProfileMaxClassLevel, new StringArgs().Add(P.Level, (int)Js.Or0(progress[LK.MaxClassLevel]))));
            foreach (var id in Js.Items(progress[LK.Bosses]).Select(Js.Str).Where(s => s != null)) view.Bosses.Add(Name(strings, MetaFormats.EnemyName, id));
            view.Profile.Add(strings.Format(S.JournalProfileBosses, new StringArgs().Add(P.Count, view.Bosses.Count)));
            foreach (var id in Js.Items(profile[RK.Unlocked]).Select(Js.Str).Where(s => s != null)) view.Unlocks.Add(Name(strings, MetaFormats.UnlockName, id));
            view.Profile.Add(strings.Format(S.JournalProfileUnlocks, new StringArgs().Add(P.Count, view.Unlocks.Count)));
            if (view.Bosses.Count == 0) view.Bosses.Add(strings.Get(S.JournalProfileNone));
            if (view.Unlocks.Count == 0) view.Unlocks.Add(strings.Get(S.JournalProfileNone));

            foreach (var id in armaments) view.Armaments.Add(Name(strings, MetaFormats.ArmamentName, id));
            return view;
        }

        private static JournalRunRow Row(JObject r, RunContent content, UiData ui)
        {
            var strings = ui.Strings;
            var victory = r.Is(V.Victory);
            var classId = r.Str(RK.Class);
            var className = r.Str(LK.ClassName);
            if (string.IsNullOrEmpty(className)) className = RunHudView.ClassName(ui, classId);
            var name = Js.Str(r[K.Name]);
            var row = new JournalRunRow
            {
                Victory = victory,
                Outcome = strings.Get(r.Is(MetaKeys.Abandoned) ? S.JournalHistoryAbandoned : victory ? S.JournalHistoryVictory : S.JournalHistoryDefeat),
                Title = string.IsNullOrEmpty(name) ? className : strings.Format(S.JournalHistoryTitle, new StringArgs().Add(P.Name, name).Add(P.Class, className)),
                Where = strings.Format(S.JournalHistoryWhere, new StringArgs().Add(P.Act, (int)Js.Or0(r[LK.Act])).Add(P.Floor, (int)Js.Or0(r[MK.Floor]))),
                Seed = strings.Format(S.JournalDetailSeed, new StringArgs().Add(P.Seed, Js.Str(r[RK.Seed]) ?? string.Empty)),
            };
            if (r.Is(RK.Custom)) row.Pills.Add(strings.Get(S.JournalHistoryCustom));
            var ascension = (int)Js.Or0(r[WK.Ascension]);
            if (ascension > 0) row.Pills.Add(strings.Format(S.JournalHistoryAscension, new StringArgs().Add(P.N, ascension)));

            row.Detail.Add(row.Seed);
            var foes = Js.Items(r[MetaKeys.Killer]).Select(Js.Str).Where(s => s != null)
                .Select(id => content.Combat.Enemies.Has(id) ? content.Combat.Enemies.Get(id).Str(K.Name) ?? id : id).Distinct().ToList();
            row.Detail.Add(foes.Count == 0
                ? strings.Get(S.JournalDetailKillerUnknown)
                : strings.Format(victory ? S.JournalDetailFelled : S.JournalDetailKiller, new StringArgs().Add(P.Name, string.Join(content.Loop.Map.Rules.EnemySeparator, foes))));
            if (r[MetaKeys.Duration] != null)
                row.Detail.Add(strings.Format(S.JournalDetailDuration, new StringArgs()
                    .Add(P.Time, TimeSpan.FromSeconds(Js.Or0(r[MetaKeys.Duration])).ToString(UiFormats.Duration, CultureInfo.InvariantCulture))));
            row.Detail.Add(strings.Format(S.JournalDetailFights, new StringArgs().Add(P.Count, (int)Js.Or0(r[WK.FightsWon]))));
            row.Detail.Add(strings.Format(S.JournalDetailDamage, new StringArgs().Add(P.Dealt, (int)Js.Or0(r[LK.DamageDealt])).Add(P.Taken, (int)Js.Or0(r[LK.DamageTaken]))));

            if (r[MetaKeys.Deck] is JArray deck)
            {
                foreach (var card in deck.OfType<JObject>())
                {
                    var id = card.Str(MetaKeys.CardId);
                    var cardName = id != null && content.Combat.Cards.Has(id) ? content.Combat.Cards.Get(id).Str(K.Name) ?? id : id;
                    row.Deck.Add(strings.Format(card.Is(MetaKeys.Upgraded) ? S.JournalDetailCardUpgraded : S.JournalDetailCard, new StringArgs().Add(P.Name, cardName)));
                }
                row.DeckTitle = strings.Format(S.JournalDetailDeck, new StringArgs().Add(P.Count, row.Deck.Count));
            }
            else row.DeckTitle = strings.Get(S.JournalDetailDeckUnknown);
            return row;
        }

        private static string Name(StringTable strings, string format, string id)
        {
            var key = string.Format(CultureInfo.InvariantCulture, format, id);
            return strings.Has(key) ? strings.Get(key) : id;
        }
    }
}
