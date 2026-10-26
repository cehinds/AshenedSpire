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
using RK = Ashen.Generated.RunKeys;

namespace Ashen.App.Run
{
    /// <summary>One reward a level pays: its sentence, and whether it is the routine one every level repeats (the web mutes those so a milestone's own reward is what the eye lands on).</summary>
    public sealed class ProgressionGrant
    {
        public string Text;
        public bool Routine;
    }

    /// <summary>One level of the ladder (web CharacterSheetModel.characterLadder rows).</summary>
    public sealed class ProgressionRow
    {
        public int Level;
        public string Step;
        public string Total;

        /// <summary>reached | current | next | locked.</summary>
        public string State;
        public readonly List<ProgressionGrant> Grants = new List<ProgressionGrant>();
    }

    /// <summary>An attribute of the character as the Attributes card lists it.</summary>
    public sealed class ProgressionAttribute
    {
        public string Label;
        public string Short;
        public string Value;
    }

    /// <summary>The Progression door of the Character pane (web ui/screens/characterSheet.js characterPane over model/progression.js levelProgress and ui/models/CharacterSheetModel.js characterLadder): the XP meter, the cadence line, the attributes, the feats and every level with what it grants.</summary>
    public sealed class CharacterProgressionView
    {
        public string Label;
        public string Value;
        public string Sense;
        public double Fraction;
        public string Cadence;
        public int Points;
        public string PointsLabel;
        public string Summary;
        public readonly List<ProgressionAttribute> Attributes = new List<ProgressionAttribute>();
        public readonly List<ProgressionRow> Rows = new List<ProgressionRow>();

        /// <summary>The Character Sheet: the stat grid and the attribute cards (web progressionStats).</summary>
        public ProgressionStatsView Stats;

        /// <summary>The Character Sheet button's summary line: each stat's face label and its number.</summary>
        public readonly List<KeyValuePair<string, string>> StatChips = new List<KeyValuePair<string, string>>();
    }

    public static class CharacterProgression
    {
        private static string Num(double v) => RunJs.NumStr(v);

        private static double Level(JObject run) => Js.Truthy(run.Obj(K.Level)?[K.Level]) ? run.Obj(K.Level).Num(K.Level) : 1;

        private static string State(int row, double held) => row < held ? ArmouryKeys.StateReached : row == held ? ArmouryKeys.StateCurrent : row == held + 1 ? ArmouryKeys.StateNext : ArmouryKeys.StateLocked;

        /// <summary>deckMinimum(registries, run) at a level: base plus floor(level / step) times per step (model/loadout.js).</summary>
        private static double DeckFloor(JObject deck, double level)
        {
            var step = Js.IsInt(deck[ArmouryKeys.DeckStepLevels]) && deck.Num(ArmouryKeys.DeckStepLevels) > 0 ? deck.Num(ArmouryKeys.DeckStepLevels) : 1;
            var min = Js.IsInt(deck[ArmouryKeys.DeckMinimum]) ? deck.Num(ArmouryKeys.DeckMinimum) : 0;
            var per = Js.IsInt(deck[ArmouryKeys.DeckPerStep]) ? deck.Num(ArmouryKeys.DeckPerStep) : 0;
            return min + Math.Floor(level / step) * per;
        }

        public static CharacterProgressionView For(Core2RunSession session, UiData ui)
        {
            var strings = ui.Strings;
            var run = session.Run;
            var rewards = session.Core.Rewards;
            var balance = rewards.RunData.Balance;
            var levelUp = balance.Obj(ArmouryKeys.LevelUpBalance) ?? new JObject();
            var held = Level(run);
            var ledger = run.Obj(K.Level) ?? LevelUp.EmptyLevel();
            var xp = Math.Max(0d, Math.Floor(ledger.Num(K.Xp)));
            var max = Js.IsInt(levelUp[ArmouryKeys.MaxLevels]) && levelUp.Num(ArmouryKeys.MaxLevels) > 0 ? levelUp.Num(ArmouryKeys.MaxLevels) : ui.Components.DefaultMaxLevel;
            var capped = held >= max;
            var cost = Ashen.Domain.Loop.Core2Rest.XpToNext(rewards, held);
            var points = Js.IsInt(ledger[RK.UnspentPoints]) && ledger.Num(RK.UnspentPoints) > 0 ? (int)ledger.Num(RK.UnspentPoints) : 0;
            var view = new CharacterProgressionView
            {
                Label = strings.Format(StringKeys.ArmouryCharacterLevel, new StringArgs().Add(UiPlaceholders.Level, (int)held)),
                Fraction = capped ? 1d : cost > 0 ? Math.Max(0d, Math.Min(1d, xp / cost)) : 0d,
                Cadence = strings.Format(StringKeys.ProgressionCadence, new StringArgs().Add(UiPlaceholders.Max, (int)max)),
                Points = points,
                PointsLabel = strings.Format(points == 1 ? StringKeys.ProgressionPointsOne : StringKeys.ProgressionPointsMany, new StringArgs().Add(UiPlaceholders.Number, points)),
            };
            view.Value = capped ? strings.Get(StringKeys.ProgressionMeterCap) : strings.Format(StringKeys.ArmouryCharacterXp, new StringArgs().Add(UiPlaceholders.Amount, (int)xp).Add(UiPlaceholders.Total, (int)cost));
            var waiting = points > 0 ? ArmouryKeys.Space + strings.Format(StringKeys.ProgressionSensePoints, new StringArgs().Add(UiPlaceholders.Points, view.PointsLabel)) : string.Empty;
            view.Sense = (capped
                ? strings.Format(StringKeys.ProgressionSenseCap, new StringArgs().Add(UiPlaceholders.Level, (int)held))
                : strings.Format(StringKeys.ProgressionSense, new StringArgs().Add(UiPlaceholders.Remaining, (int)Math.Max(0d, cost - xp)).Add(UiPlaceholders.Next, (int)held + 1))) + waiting;

            var attributes = run.Obj(K.Attributes) ?? new JObject();
            var parts = new List<string>();
            foreach (var attribute in CreationStats.OrderedAttributes(session.Core.Combat.Run.Run))
            {
                var id = attribute.Str(K.Id);
                var row = new ProgressionAttribute
                {
                    Label = attribute.Str(K.Label) ?? id,
                    Short = strings.Get(string.Format(CultureInfo.InvariantCulture, ArmouryKeys.AttributeShortKey, id)),
                    Value = Num(attributes.Num(id)),
                };
                view.Attributes.Add(row);
                parts.Add(row.Short + ArmouryKeys.Colon + ArmouryKeys.Space + row.Value);
            }
            view.Summary = string.Join(ArmouryKeys.Space, parts);

            view.Stats = ProgressionStats.For(session, ui);
            foreach (var stat in view.Stats.Rows) view.StatChips.Add(new KeyValuePair<string, string>(stat.FaceLabel, Num(stat.Value)));

            var deck = balance.Obj(ArmouryKeys.DeckBalance) ?? new JObject();
            var perLevel = Js.IsInt(levelUp[ArmouryKeys.PointsPerLevel]) && levelUp.Num(ArmouryKeys.PointsPerLevel) > 0 ? levelUp.Num(ArmouryKeys.PointsPerLevel) : 1;
            double total = 0;
            for (var level = 1; level <= max; level++)
            {
                var step = level == 1 ? 0d : Ashen.Domain.Loop.Core2Rest.XpToNext(rewards, level - 1);
                total += step;
                var row = new ProgressionRow
                {
                    Level = level,
                    State = State(level, held),
                    Step = level == 1 ? string.Empty : strings.Format(StringKeys.ProgressionRowStep, new StringArgs().Add(UiPlaceholders.Number, Group(step))),
                    Total = level == 1 ? string.Empty : strings.Format(StringKeys.ProgressionRowTotal, new StringArgs().Add(UiPlaceholders.Number, Group(total))),
                };
                if (level > 1)
                {
                    row.Grants.Add(new ProgressionGrant { Text = strings.Format(StringKeys.ProgressionGrantPoints, new StringArgs().Add(UiPlaceholders.Number, Num(perLevel))), Routine = true });
                    foreach (var stat in new[] { ArmouryKeys.HpRow, ArmouryKeys.StaminaRow, ArmouryKeys.ManaRow, ArmouryKeys.DrawRow, ArmouryKeys.HandSizeRow })
                    {
                        var before = session.DerivedStat(stat, level - 1);
                        var after = session.DerivedStat(stat, level);
                        if (before != null && after != null && after > before)
                            row.Grants.Add(new ProgressionGrant { Text = strings.Format(string.Format(CultureInfo.InvariantCulture, ArmouryKeys.GrantStatKey, stat), new StringArgs().Add(UiPlaceholders.Number, Num(after.Value - before.Value))) });
                    }
                    var floor = DeckFloor(deck, level);
                    if (floor > DeckFloor(deck, level - 1))
                        row.Grants.Add(new ProgressionGrant { Text = strings.Format(StringKeys.ProgressionGrantDeckMinimum, new StringArgs().Add(UiPlaceholders.Number, Num(floor))) });
                }
                else row.Grants.Add(new ProgressionGrant { Text = strings.Get(StringKeys.ProgressionRowStart), Routine = true });
                view.Rows.Add(row);
            }
            return view;
        }

        /// <summary>toLocaleString('en-US'): thousands grouped by commas.</summary>
        private static string Group(double v) => ((long)Math.Floor(v)).ToString(ArmouryKeys.GroupFormat, CultureInfo.InvariantCulture);
    }
}
