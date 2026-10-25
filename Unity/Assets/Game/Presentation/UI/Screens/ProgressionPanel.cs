using System;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The Progression door drawn (web ui/screens/characterSheet.js characterPane): the XP meter, the cadence line, the Attributes card with its points, the Feats card and the Level progression ladder. Pure construction over
    /// <see cref="CharacterProgressionView"/>; the folds are the Armoury's own (<paramref name="fold"/>), so they stay open across redraws.
    /// </summary>
    internal static class ProgressionPanel
    {
        public static void Fill(VisualElement host, CharacterProgressionView view, StringTable strings, Action<VisualElement, string, string, string, Action<VisualElement>> fold)
        {
            Text(host, view.Label, ArmouryClasses.Eyebrow, true);
            Text(host, view.Value, ArmouryClasses.Line, false);
            var bar = new VisualElement { pickingMode = PickingMode.Ignore };
            bar.AddToClassList(ArmouryClasses.XpBar);
            var fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.AddToClassList(ArmouryClasses.XpFill);
            fill.style.width = Length.Percent((float)(view.Fraction * UiMath.Percent));
            bar.Add(fill);
            host.Add(bar);
            Text(host, view.Sense, ArmouryClasses.Muted, false);
            Text(host, view.Cadence, ArmouryClasses.Muted, false);

            fold(host, ArmouryValues.FoldAttributes, strings.Get(StringKeys.ProgressionAttributes), view.Summary, open =>
            {
                Text(open, strings.Get(StringKeys.ProgressionAttributesNote), ArmouryClasses.Muted, false);
                foreach (var attribute in view.Attributes)
                {
                    var row = new VisualElement { pickingMode = PickingMode.Ignore };
                    row.AddToClassList(ArmouryClasses.ReceiptPair);
                    Text(row, attribute.Label, ArmouryClasses.ReceiptPairKey, false);
                    Text(row, attribute.Value, ArmouryClasses.ReceiptPairValue, false);
                    open.Add(row);
                }
            });
            var points = Text(host, view.PointsLabel, ArmouryClasses.ProgressionPoints, false);
            points.EnableInClassList(ArmouryClasses.ReceiptMet, view.Points > 0);

            var feats = new VisualElement { pickingMode = PickingMode.Ignore };
            feats.AddToClassList(ArmouryClasses.ProgressionCard);
            Text(feats, strings.Get(StringKeys.ProgressionFeats), ArmouryClasses.Eyebrow, true);
            Text(feats, strings.Get(StringKeys.ProgressionNoFeats), ArmouryClasses.Line, false);
            Text(feats, strings.Get(StringKeys.ProgressionFeatsHint), ArmouryClasses.Muted, false);
            host.Add(feats);

            fold(host, ArmouryValues.FoldLevels, strings.Get(StringKeys.ProgressionLevels), string.Empty, open =>
            {
                foreach (var row in view.Rows) Ladder(open, row, strings);
            });
        }

        private static void Ladder(VisualElement host, ProgressionRow data, StringTable strings)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.AddToClassList(ArmouryClasses.LadderRow);
            row.EnableInClassList(ArmouryClasses.LadderRowCurrent, data.State == ArmouryKeys.StateCurrent);
            row.EnableInClassList(ArmouryClasses.LadderRowNext, data.State == ArmouryKeys.StateNext);
            Text(row, data.Level.ToString(System.Globalization.CultureInfo.InvariantCulture), ArmouryClasses.LadderLevel, false);
            var xp = new VisualElement { pickingMode = PickingMode.Ignore };
            xp.AddToClassList(ArmouryClasses.LadderXp);
            if (data.Step.Length > 0)
            {
                Text(xp, data.Step, ArmouryClasses.Muted, false);
                Text(xp, data.Total, ArmouryClasses.Muted, false);
            }
            row.Add(xp);
            var grants = new VisualElement { pickingMode = PickingMode.Ignore };
            grants.AddToClassList(ArmouryClasses.LadderGrants);
            foreach (var grant in data.Grants)
            {
                var chip = Text(grants, grant.Text, ArmouryClasses.LadderGrant, false);
                chip.EnableInClassList(ArmouryClasses.LadderGrantRoutine, grant.Routine);
            }
            row.Add(grants);
            if (data.State == ArmouryKeys.StateCurrent) Text(row, strings.Get(StringKeys.ProgressionRowHere), ArmouryClasses.LadderHere, false);
            host.Add(row);
        }

        private static LocLabel Text(VisualElement host, string text, string cls, bool upper)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            label.AddToClassList(cls);
            if (upper) label.AddToClassList(UiClasses.Upper);
            label.SetResolved(text ?? string.Empty);
            host.Add(label);
            return label;
        }
    }
}
