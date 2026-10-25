using System.Collections.Generic;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Domain.Run;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The Armoury's receipts drawn (web equipmentReceipts.js): the card packages with their calculations, the minimums, the Poise threshold's and the equip load's sources, and a candidate's comparison with its before to
    /// after deltas. Pure construction over <see cref="EquipmentReceiptView"/> and <see cref="CandidateView"/>; no state.
    /// </summary>
    internal static class ReceiptPanel
    {
        /// <summary>The Equipment cards fold's body: Equipment card packages, Requirements, Poise threshold, Equip load.</summary>
        public static void Fill(VisualElement host, EquipmentReceiptView view, StringTable strings)
        {
            var packages = Section(host);
            Eyebrow(packages, strings.Get(StringKeys.ArmouryReceiptPackages));
            foreach (var role in view.Roles) Role(packages, role, strings);
            Requirements(host, view.Requirements, strings);
            var poise = Section(host);
            Eyebrow(poise, strings.Get(StringKeys.ArmouryReceiptPoise));
            Sources(poise, view.Poise.Sources, strings.Get(StringKeys.ArmouryReceiptPoiseNone));
            Pair(poise, view.Poise.Pair, view.Poise.Total);
            Note(poise, view.Poise.Note);
            var load = Section(host);
            Eyebrow(load, strings.Get(StringKeys.ArmouryReceiptLoad));
            Sources(load, view.Load.Sources, strings.Get(StringKeys.ArmouryReceiptLoadNone));
            Pair(load, view.Load.Pair, view.Load.Total);
            Note(load, view.Load.Note);
        }

        /// <summary>A candidate's comparison (renderCandidateComparison): the packages that change, the minimums, the effects it adds, Poise and load before and after.</summary>
        public static void Fill(VisualElement host, CandidateView view, StringTable strings)
        {
            foreach (var package in view.AttackPackages)
            {
                var row = Row(host);
                Head(row, package.Name, package.Hint);
                Delta(Values(row), package.BeforeCount, package.AfterCount, strings, count => strings.Format(StringKeys.ArmouryReceiptCopies, new StringArgs().Add(UiPlaceholders.Count, (int)count)));
            }
            foreach (var role in view.Roles)
            {
                var row = Row(host);
                var head = Head(row, To(strings, role.BeforeName, role.AfterName), role.Hint);
                var values = Values(row);
                Delta(values, role.Before, role.After, strings);
            }
            if (view.Requirement != null) Requirements(host, new List<ReceiptRequirement> { view.Requirement }, strings);
            else Requirements(host, new List<ReceiptRequirement>(), strings);
            var effects = Section(host);
            Eyebrow(effects, strings.Get(StringKeys.ArmouryReceiptEffects));
            if (view.AddedEffects.Count == 0) Note(effects, strings.Get(StringKeys.ArmouryReceiptEffectsNone));
            foreach (var effect in view.AddedEffects) Line(effects, effect);
            var resources = Section(host);
            Eyebrow(resources, strings.Get(StringKeys.ArmouryReceiptResources));
            if (view.Resources.Count == 0) Note(resources, strings.Get(StringKeys.ArmouryReceiptResourcesNone));
            foreach (var change in view.Resources)
            {
                var line = new VisualElement { pickingMode = PickingMode.Ignore };
                line.AddToClassList(ArmouryClasses.ReceiptRow);
                Text(line, change.Label, ArmouryClasses.Line, false);
                Delta(line, change.Before, change.After, strings);
                resources.Add(line);
                if (change.Note.Length > 0) Note(resources, change.Note);
            }
            var poise = Section(host);
            Eyebrow(poise, strings.Get(StringKeys.ArmouryReceiptPoise));
            var delta = Row(poise);
            Delta(Values(delta), view.PoiseBefore, view.PoiseAfter, strings);
            Note(poise, view.PoiseNote);
            var load = Section(host);
            Eyebrow(load, strings.Get(StringKeys.ArmouryReceiptLoad));
            Line(load, view.LoadLine);
            Note(load, view.LoadNote);
        }

        private static void Role(VisualElement host, ReceiptRole role, StringTable strings)
        {
            var row = Row(host);
            var head = Head(row, role.Name, role.Hint);
            var name = head.Q(className: ArmouryClasses.ReceiptName);
            var pill = new LocLabel { pickingMode = PickingMode.Ignore };
            pill.AddToClassList(ArmouryClasses.ReceiptPill);
            pill.SetResolved(strings.Format(StringKeys.ArmouryReceiptCopies, new StringArgs().Add(UiPlaceholders.Count, role.Copies)));
            name.Add(pill);
            var values = Values(row);
            Text(values, strings.Format(StringKeys.ArmouryReceiptFormula, new StringArgs().Add(UiPlaceholders.Formula, role.Formula)), ArmouryClasses.ReceiptFormula, true);
            Text(values, RunJs.NumStr(role.Value), ArmouryClasses.ReceiptValue, false);
        }

        private static void Requirements(VisualElement host, IReadOnlyList<ReceiptRequirement> rows, StringTable strings)
        {
            var section = Section(host);
            Eyebrow(section, strings.Get(StringKeys.ArmouryReceiptRequirements));
            if (rows.Count == 0)
            {
                Note(section, strings.Get(StringKeys.ArmouryReceiptRequirementsNone));
                return;
            }
            foreach (var piece in rows)
            {
                var row = Row(section);
                Head(row, piece.PieceName, null);
                var values = Values(row);
                foreach (var req in piece.Rows)
                {
                    var pair = new VisualElement { pickingMode = PickingMode.Ignore };
                    pair.AddToClassList(ArmouryClasses.ReceiptSource);
                    Text(pair, req.Label, ArmouryClasses.ReceiptPairKey, false);
                    var value = Text(pair, req.Text, ArmouryClasses.ReceiptSourceValue, false);
                    value.AddToClassList(req.Met ? ArmouryClasses.ReceiptMet : ArmouryClasses.ReceiptUnmet);
                    values.Add(pair);
                }
            }
        }

        private static void Sources(VisualElement host, IReadOnlyList<ReceiptSource> sources, string empty)
        {
            if (sources.Count == 0)
            {
                Note(host, empty);
                return;
            }
            var line = new VisualElement { pickingMode = PickingMode.Ignore };
            line.AddToClassList(ArmouryClasses.ReceiptSources);
            foreach (var source in sources)
            {
                var item = new VisualElement { pickingMode = PickingMode.Ignore };
                item.AddToClassList(ArmouryClasses.ReceiptSource);
                Text(item, source.Id, ArmouryClasses.ReceiptPairKey, false);
                Text(item, source.Value, ArmouryClasses.ReceiptSourceValue, false);
                line.Add(item);
            }
            host.Add(line);
        }

        private static void Pair(VisualElement host, string key, string value)
        {
            var pair = new VisualElement { pickingMode = PickingMode.Ignore };
            pair.AddToClassList(ArmouryClasses.ReceiptPair);
            Text(pair, key, ArmouryClasses.ReceiptPairKey, false);
            Text(pair, value, ArmouryClasses.ReceiptPairValue, false);
            host.Add(pair);
        }

        private static string To(StringTable strings, string before, string after) =>
            strings.Format(StringKeys.ArmouryReceiptTo, new StringArgs().Add(UiPlaceholders.Before, before).Add(UiPlaceholders.After, after));

        private static void Delta(VisualElement host, double before, double after, StringTable strings, System.Func<double, string> dress = null)
        {
            var cls = after > before ? ArmouryClasses.ReceiptDeltaUp : after < before ? ArmouryClasses.ReceiptDeltaDown : null;
            dress = dress ?? RunJs.NumStr;
            var text = To(strings, dress(before), dress(after));
            var label = Text(host, text, ArmouryClasses.ReceiptDelta, false);
            if (cls != null) label.AddToClassList(cls);
        }

        private static VisualElement Section(VisualElement host)
        {
            var section = new VisualElement { pickingMode = PickingMode.Ignore };
            section.AddToClassList(ArmouryClasses.ReceiptSection);
            host.Add(section);
            return section;
        }

        private static VisualElement Row(VisualElement host)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.AddToClassList(ArmouryClasses.ReceiptRow);
            host.Add(row);
            return row;
        }

        private static VisualElement Head(VisualElement row, string name, string hint)
        {
            var head = new VisualElement { pickingMode = PickingMode.Ignore };
            head.AddToClassList(ArmouryClasses.ReceiptHead);
            var title = new VisualElement { pickingMode = PickingMode.Ignore };
            title.AddToClassList(ArmouryClasses.ReceiptName);
            Text(title, name, null, false);
            head.Add(title);
            if (!string.IsNullOrEmpty(hint)) Text(head, hint, ArmouryClasses.ReceiptHint, true);
            row.Add(head);
            return head;
        }

        private static VisualElement Values(VisualElement row)
        {
            var values = new VisualElement { pickingMode = PickingMode.Ignore };
            values.AddToClassList(ArmouryClasses.ReceiptValues);
            row.Add(values);
            return values;
        }

        private static void Eyebrow(VisualElement host, string text) => Text(host, text, ArmouryClasses.Eyebrow, true);

        private static void Note(VisualElement host, string text) => Text(host, text, ArmouryClasses.ReceiptNote, false);

        private static void Line(VisualElement host, string text) => Text(host, text, ArmouryClasses.Line, false);

        private static LocLabel Text(VisualElement host, string text, string cls, bool upper)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            if (cls != null) label.AddToClassList(cls);
            if (upper) label.AddToClassList(UiClasses.Upper);
            label.SetResolved(text ?? string.Empty);
            host.Add(label);
            return label;
        }
    }
}
