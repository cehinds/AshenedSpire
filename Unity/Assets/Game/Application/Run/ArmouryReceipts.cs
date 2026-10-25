using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.App.Run
{
    /// <summary>One card package of the equipment receipt (web equipmentReceipts.js renderRoleCopies): the role's card, the copies the deck holds, what it is, and the calculation behind its number.</summary>
    public sealed class ReceiptRole
    {
        public string Role;
        public string Name;
        public int Copies;

        /// <summary>"physical · blade · source:weapon" (the web's damage school then the profile's tags).</summary>
        public string Hint;

        /// <summary>"5 base + 5 AR (weapon) + 1 rarity =".</summary>
        public string Formula;
        public double Value;
    }

    /// <summary>One attribute minimum of a piece, with what the character has.</summary>
    public sealed class ReceiptRequirementRow
    {
        public string Label;
        public double? Actual;
        public double Required;
        public bool Met;

        /// <summary>"3/2" or "?/2" (the web's actual over required).</summary>
        public string Text;
    }

    /// <summary>The minimums of one piece (web equipmentRequirementReceipt).</summary>
    public sealed class ReceiptRequirement
    {
        public string PieceName;
        public readonly List<ReceiptRequirementRow> Rows = new List<ReceiptRequirementRow>();
    }

    /// <summary>One contribution to a total: what it is named (an attribute, an armour, a relic) and its value in words.</summary>
    public sealed class ReceiptSource
    {
        public string Id;
        public string Value;
    }

    /// <summary>The player's Poise threshold receipt (web statProjection playerPoiseThresholdReceipt): the sources, "Items 8 + relics 0", the total and the note.</summary>
    public sealed class PoiseReceipt
    {
        public readonly List<ReceiptSource> Sources = new List<ReceiptSource>();
        public string Pair;
        public string Total;
        public double Value;
        public string Note;
    }

    /// <summary>The equip load receipt (web statProjection playerLoadReceipt): the worn pieces' weights, "Hands 1.6 + armour 1.6", "3.2 of 4 · Heavy" and the note.</summary>
    public sealed class LoadReceipt
    {
        public readonly List<ReceiptSource> Sources = new List<ReceiptSource>();
        public string Pair;
        public string Total;
        public string Note;
        public double Load;
        public double Capacity;
        public double Percent;
        public string ClassId;
        public string Word;
    }

    /// <summary>The whole equipment receipt of a run (web equipmentSurfaceReceipt, the Character pane's Equipment cards): card packages, requirements, Poise, load.</summary>
    public sealed class EquipmentReceiptView
    {
        public readonly List<ReceiptRole> Roles = new List<ReceiptRole>();
        public readonly List<ReceiptRequirement> Requirements = new List<ReceiptRequirement>();
        public PoiseReceipt Poise = new PoiseReceipt();
        public LoadReceipt Load = new LoadReceipt();
    }

    /// <summary>One role's number before and after a candidate piece goes on (web candidateReceipt roles).</summary>
    public sealed class CandidateRoleChange
    {
        public string Role;
        public string BeforeName;
        public string AfterName;
        public string Hint;
        public double Before;
        public double After;
        public bool Changed => Math.Abs(Before - After) > double.Epsilon;
    }

    /// <summary>The comparison of an item against what a position holds (web equipmentReceipts.js renderCandidateComparison): the card packages, the minimums, the effects it adds, Poise and load before and after.</summary>
    public sealed class CandidateView
    {
        public readonly List<CandidateRoleChange> Roles = new List<CandidateRoleChange>();
        public ReceiptRequirement Requirement;
        public readonly List<string> AddedEffects = new List<string>();
        public double PoiseBefore;
        public double PoiseAfter;
        public string PoiseNote;
        public LoadReceipt LoadBefore;
        public LoadReceipt LoadAfter;
        public string LoadLine;
        public string LoadNote;
        public bool ChangesClass;
    }

    /// <summary>
    /// The Armoury's receipts, engine-free (web model/equipmentPresentation.js equipmentSurfaceReceipt, model/statProjection.js playerPoiseThresholdReceipt and playerLoadReceipt, equipmentReceipts.js): what the worn kit
    /// makes of each card package, what each piece asks of the character, where the Poise threshold and the equip load come from, and, for a piece about to go on, the same before and after.
    /// </summary>
    public static class ArmouryReceipts
    {
        private static int Int(double v) => (int)Math.Floor(v);

        private static string Num(double v) => RunJs.NumStr(v);


        public static EquipmentReceiptView For(Core2RunSession session, UiData ui) => For(session, ui, null);

        /// <summary>The receipt of the worn kit, or of <paramref name="loadoutOverride"/> (a trial loadout) when given.</summary>
        public static EquipmentReceiptView For(Core2RunSession session, UiData ui, JObject loadoutOverride)
        {
            var run = session.Run;
            var loadout = loadoutOverride ?? run.Obj(K.Loadout);
            var view = new EquipmentReceiptView();
            if (loadout == null) return view;
            var strings = ui.Strings;
            var gear = session.Core.Deck.Run.Run;
            var held = Js.Items(run[K.Deck]).OfType<JObject>().Where(c => Js.Truthy(c[K.EquipmentRole])).GroupBy(c => c.Str(K.EquipmentRole)).ToDictionary(g => g.Key, g => g.Count());
            foreach (var row in Loadout.KitReceipt(gear, loadout, session.ClassId, run.Obj(K.Attributes), run.Obj(K.EquipmentProfileRuleSnapshot))) 
            {
                held.TryGetValue(row.Role, out var copies);
                view.Roles.Add(Role(strings, row, copies));
            }
            var pieces = Equipment.EquippedPieces(session.CombatData, loadout, session.ClassId, run.Obj(K.ItemUpgradeLevels) ?? new JObject());
            foreach (var piece in pieces)
            {
                var requirement = Requirement(session, ui, piece);
                if (requirement.Rows.Count > 0) view.Requirements.Add(requirement);
            }
            view.Poise = Poise(session, ui, loadout, pieces);
            view.Load = Load(session, ui, loadout, pieces);
            return view;
        }

        private static ReceiptRole Role(StringTable strings, KitRow row, int copies)
        {
            var receipt = row.Receipt;
            var rating = receipt?.Obj(RK.Rating);
            var hint = new List<string> { row.Profile?.Str(K.DamageSchool) ?? string.Empty };
            hint.AddRange(Js.Items(row.Profile?[K.Tags]).Select(Js.Str));
            return new ReceiptRole
            {
                Role = row.Role,
                Name = row.Profile?.Str(K.DisplayName) ?? row.Role,
                Copies = copies,
                Hint = string.Join(strings.Get(StringKeys.RestTagJoin), hint.Where(h => !string.IsNullOrEmpty(h))),
                Formula = rating == null ? string.Empty : strings.Format(StringKeys.ArmouryPowerFormula, new StringArgs().Add(UiPlaceholders.Base, Int(receipt.Num(K.Base))).Add(UiPlaceholders.Rating, Int(rating.Num(K.Value)))
                    .Add(UiPlaceholders.Id, (rating.Str(K.Id) ?? string.Empty).ToUpperInvariant()).Add(UiPlaceholders.Source, rating.Str(RK.SourceLabel)).Add(UiPlaceholders.Bonus, Int(receipt.Num(RK.RarityBonus)))),
                Value = receipt?.Num(K.Value) ?? 0d,
            };
        }

        /// <summary>The attribute minimums of one piece against the character's attributes (equipmentRequirementReceipt with the attributes' short labels).</summary>
        public static ReceiptRequirement Requirement(Core2RunSession session, UiData ui, JObject piece)
        {
            var strings = ui.Strings;
            var result = new ReceiptRequirement { PieceName = piece.Str(K.Name) ?? piece.Str(K.Id) };
            var receipt = Loadout.RequirementReceipt(session.Core.Deck.Run.Run, piece, session.Run.Obj(K.Attributes), session.Run.Obj(K.ItemUpgradeLevels) ?? new JObject());
            foreach (var row in receipt.Rows)
            {
                var id = row.Str(RK.AttributeId);
                var actual = Js.IsFinite(row[RK.Actual]) ? Js.D(row[RK.Actual]) : (double?)null;
                var required = row.Num(RK.Required);
                result.Rows.Add(new ReceiptRequirementRow
                {
                    Label = strings.Get(string.Format(CultureInfo.InvariantCulture, ArmouryKeys.AttributeShortKey, id)),
                    Actual = actual,
                    Required = required,
                    Met = actual != null && actual >= required,
                    Text = (actual == null ? ArmouryKeys.Unknown : Num(actual.Value)) + ArmouryKeys.Over + Num(required),
                });
            }
            return result;
        }

        // ------------------------------------------------------------------ Poise

        private static PoiseReceipt Poise(Core2RunSession session, UiData ui, JObject loadout, IReadOnlyList<JObject> pieces)
        {
            var strings = ui.Strings;
            var run = session.Run;
            var data = session.CombatData;
            var levels = run.Obj(K.ItemUpgradeLevels) ?? new JObject();
            var relics = run[K.Relics] as JArray ?? new JArray();
            var receipt = new PoiseReceipt { Note = strings.Get(StringKeys.ArmouryReceiptPoiseNote) };
            double items = 0;
            var sources = new List<ReceiptSource>();
            foreach (var piece in pieces.Where(p => p.Str(K.Kind) == V.Armor))
            {
                var value = Js.IsNum(piece[K.PoiseThreshold]) ? piece.Num(K.PoiseThreshold) : 0d;
                items += value;
                sources.Add(new ReceiptSource { Id = piece.Str(K.Id), Value = Num(value) });
            }
            var relicSources = new List<ReceiptSource>();
            double relicTotal = 0;
            foreach (var relicId in Js.Items(relics).Select(Js.Str).Where(id => id != null))
            {
                var level = Js.Or0(levels[ArmouryKeys.RelicRefPrefix + relicId]);
                var def = Equipment.ResolveUpgradedRelic(data, ArmouryKeys.RelicRefPrefix + relicId, level);
                var add = def?.Obj(K.Passives)?[K.PoiseThresholdAdd];
                if (!Js.IsFinite(add)) continue;
                relicTotal += Js.D(add);
                relicSources.Add(new ReceiptSource { Id = def.Str(K.Id) ?? relicId, Value = Num(Js.D(add)) });
            }
            var attribute = session.DerivedStat(ArmouryKeys.PoiseRow) ?? 0d;
            var total = attribute + items + relicTotal;
            receipt.Value = total;
            receipt.Total = Num(total);
            if (attribute > 0) receipt.Sources.Add(new ReceiptSource { Id = AttributeNames(session), Value = Num(attribute) });
            receipt.Sources.AddRange(sources);
            receipt.Sources.AddRange(relicSources);
            receipt.Pair = strings.Format(StringKeys.ArmouryReceiptPoisePair, new StringArgs().Add(UiPlaceholders.Items, Num(items)).Add(UiPlaceholders.Relics, Num(relicTotal)));
            return receipt;
        }

        /// <summary>The attributes the derived Poise row answers to, joined as the web names them ("constitution").</summary>
        private static string AttributeNames(Core2RunSession session)
        {
            var run = session.Run;
            var d = session.Core.Combat.Run;
            var table = run.Obj(K.DerivedStatRuleSnapshot)?.Obj(K.Rules);
            var row = CoreDerivedStats.ResolvedRow(d, table, ArmouryKeys.PoiseRow, session.ClassId);
            var ids = row == null ? new List<string>() : CoreDerivedStats.RuleWeights(d, row).Select(p => p.Key).ToList();
            return ids.Count == 0 ? ArmouryKeys.AttributesWord : string.Join(ArmouryKeys.Plus, ids);
        }

        // ------------------------------------------------------------------ equip load

        private static LoadReceipt Load(Core2RunSession session, UiData ui, JObject loadout, IReadOnlyList<JObject> pieces)
        {
            var strings = ui.Strings;
            var run = session.Run;
            var data = session.CombatData;
            var receipt = new LoadReceipt();
            double hands = 0, armour = 0;
            foreach (var piece in pieces)
            {
                var weight = Equipment.PieceWeight(data, piece);
                receipt.Sources.Add(new ReceiptSource { Id = piece.Str(K.Id), Value = Num(weight) });
                if (piece.Str(K.Kind) == V.Armor && !Js.Nullish(piece[K.ClassId])) armour += weight;
                else hands += weight;
            }
            hands = Equipment.RoundToTenth(hands);
            armour = Equipment.RoundToTenth(armour);
            var attributes = run.Obj(K.Attributes);
            var decided = Equipment.ComputeWeightClass(data, attributes?.Num(K.Constitution) ?? 0d, attributes?.Num(K.Strength) ?? 0d, 0, hands + armour);
            var load = decided.Load;
            var capacity = decided.Capacity;
            var percent = decided.Percent;
            var cls = decided.WeightClass;
            receipt.ClassId = cls.Str(K.Id);
            receipt.Word = strings.Get(string.Format(CultureInfo.InvariantCulture, ArmouryKeys.WeightWordKey, receipt.ClassId));
            receipt.Load = load;
            receipt.Capacity = capacity;
            receipt.Percent = percent;
            receipt.Pair = strings.Format(StringKeys.ArmouryReceiptLoadPair, new StringArgs().Add(UiPlaceholders.Hands, Num(hands)).Add(UiPlaceholders.Armour, Num(armour)));
            receipt.Total = strings.Format(StringKeys.ArmouryReceiptLoadValue, new StringArgs().Add(UiPlaceholders.Load, Num(load)).Add(UiPlaceholders.Capacity, Num(capacity)).Add(UiPlaceholders.Word, receipt.Word));
            var active = Js.Items(run[K.Deck]).OfType<JObject>().Any(card =>
            {
                var id = card.Str(K.CardId);
                if (id == null || !data.Cards.Has(id)) return false;
                var def = data.Cards.Get(id);
                return Js.Truthy(def[ArmouryKeys.WeightClassPriced]) || Js.Items(def[K.Effects]).OfType<JObject>().Any(e => e.Str(K.Op) == K.DodgeRoll);
            });
            receipt.Note = strings.Format(StringKeys.ArmouryReceiptLoadNote, new StringArgs().Add(UiPlaceholders.Capacity, Num(capacity)).Add(UiPlaceholders.Percent, Num(percent)).Add(UiPlaceholders.Word, receipt.Word)
                .Add(UiPlaceholders.Tail, strings.Get(active ? StringKeys.ArmouryReceiptLoadActive : StringKeys.ArmouryReceiptLoadReadout)));
            return receipt;
        }

        // ------------------------------------------------------------------ the comparison

        /// <summary>
        /// The comparison of putting <paramref name="itemId"/> into a position (candidateReceipt): the packages before and after, the minimums, the effects it adds, Poise and load before and after. Null when the
        /// trial loadout cannot be made (the item does not fit, a gate refuses it).
        /// </summary>
        public static CandidateView Candidate(Core2RunSession session, UiData ui, string slotId, int setIndex, string itemId)
        {
            var trial = session.TrialLoadout(slotId, setIndex, itemId);
            if (trial == null) return null;
            var strings = ui.Strings;
            var before = For(session, ui);
            var after = For(session, ui, trial);
            var view = new CandidateView { PoiseBefore = before.Poise.Value, PoiseAfter = after.Poise.Value, PoiseNote = after.Poise.Note, LoadBefore = before.Load, LoadAfter = after.Load, ChangesClass = before.Load.ClassId != after.Load.ClassId };
            foreach (var b in before.Roles)
            {
                var a = after.Roles.FirstOrDefault(r => r.Role == b.Role);
                if (a == null) continue;
                var role = new CandidateRoleChange { Role = b.Role, BeforeName = b.Name, AfterName = a.Name, Hint = a.Hint, Before = b.Value, After = a.Value };
                if (role.Changed || b.Name != a.Name) view.Roles.Add(role);
            }
            var piece = session.CombatData.Equipment == null ? null : Js.Items(session.CombatData.Equipment[K.Armaments]).OfType<JObject>().FirstOrDefault(p => p.Str(K.Id) == itemId)
                ?? Js.Items(session.CombatData.Equipment[K.Armour]).OfType<JObject>().FirstOrDefault(p => p.Str(K.Id) == itemId && p.Str(K.ClassId) == session.ClassId);
            if (piece != null)
            {
                var requirement = Requirement(session, ui, piece);
                if (requirement.Rows.Count > 0) view.Requirement = requirement;
                var worn = Js.Str((session.Run.Obj(K.Loadout)?.Obj(K.Sets)?.Arr(slotId))?[setIndex]);
                var wornPiece = string.IsNullOrEmpty(worn) ? null : Js.Items(session.CombatData.Equipment[K.Armaments]).OfType<JObject>().FirstOrDefault(p => p.Str(K.Id) == worn)
                    ?? Js.Items(session.CombatData.Equipment[K.Armour]).OfType<JObject>().FirstOrDefault(p => p.Str(K.Id) == worn && p.Str(K.ClassId) == session.ClassId);
                var had = new HashSet<string>(Js.Items(wornPiece?[K.Mods]).Select(Js.Str), StringComparer.Ordinal);
                var fields = session.CombatData.Equipment.Obj(K.ModFields) ?? new JObject();
                foreach (var raw in Js.Items(piece[K.Mods]).Select(Js.Str).Where(m => m != null && !had.Contains(m)))
                {
                    var mod = Loadout.ParseMod(raw);
                    var field = mod == null ? null : fields.Obj(mod.Field);
                    if (field == null) continue;
                    view.AddedEffects.Add(field.Str(K.Label) + ArmouryKeys.Space + (mod.Add && mod.Value >= 0 ? RunValues.Plus : string.Empty) + Num(mod.Value));
                }
            }
            var load = after.Load;
            var beforeLoad = before.Load;
            view.LoadLine = strings.Format(StringKeys.ArmouryReceiptLoadChange, new StringArgs()
                .Add(UiPlaceholders.Before, Num(beforeLoad.Load)).Add(UiPlaceholders.BeforePercent, Num(beforeLoad.Percent))
                .Add(UiPlaceholders.After, Num(load.Load)).Add(UiPlaceholders.AfterPercent, Num(load.Percent))
                .Add(UiPlaceholders.Capacity, Num(load.Capacity))
                .Add(UiPlaceholders.Word, view.ChangesClass ? strings.Format(StringKeys.ArmouryReceiptLoadCrossing, new StringArgs().Add(UiPlaceholders.Before, beforeLoad.Word).Add(UiPlaceholders.After, load.Word)) : load.Word));
            view.LoadNote = load.Note;
            return view;
        }
    }
}
