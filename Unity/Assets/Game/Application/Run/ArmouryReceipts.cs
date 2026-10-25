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
using RV = Ashen.Generated.RunValues;
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

    /// <summary>One card package of the attack role before and after (web attackPackageChanges): the card, how many copies the plan holds each way and the hands that supply it.</summary>
    public sealed class CandidateAttackPackage
    {
        public string CardId;
        public string ProfileId;
        public string Name;
        public int BeforeCount;
        public int AfterCount;
        public readonly List<string> SourceHands = new List<string>();

        /// <summary>"Right hand package" (the web's "{hands} hand package"), or empty when no hand supplies it.</summary>
        public string Hint = string.Empty;
    }

    /// <summary>One resource a swap would change (web resourceChanges): the pool maximum, or a slot's swap price in Stamina, before and after, with the sentence a declined talisman or relic delta adds.</summary>
    public sealed class CandidateResourceChange
    {
        public string Id;
        public string Label;
        public double Before;
        public double After;
        public string RuleId;
        public string Note = string.Empty;
    }

    /// <summary>The comparison of an item against what a position holds (web equipmentReceipts.js renderCandidateComparison): the card packages, the minimums, the effects it adds, the resource changes, Poise and load before and after.</summary>
    public sealed class CandidateView
    {
        public readonly List<CandidateAttackPackage> AttackPackages = new List<CandidateAttackPackage>();

        /// <summary>Every role, changed or not (the web's candidate.roles lists the three).</summary>
        public readonly List<CandidateRoleChange> Roles = new List<CandidateRoleChange>();
        public ReceiptRequirement Requirement;
        public readonly List<string> AddedEffects = new List<string>();
        public readonly List<CandidateResourceChange> Resources = new List<CandidateResourceChange>();
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


        public static EquipmentReceiptView For(Core2RunSession session, UiData ui) => For(session, ui, session.Run, null);

        /// <summary>The receipt of the worn kit, or of <paramref name="loadoutOverride"/> (a trial loadout) when given, over <paramref name="run"/> (a snapshot of the session's run).</summary>
        public static EquipmentReceiptView For(Core2RunSession session, UiData ui, JObject run, JObject loadoutOverride)
        {
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
                var requirement = Requirement(session, ui, run, piece);
                if (requirement.Rows.Count > 0) view.Requirements.Add(requirement);
            }
            view.Poise = Poise(session, ui, run, loadout, pieces);
            view.Load = Load(session, ui, run, loadout, pieces);
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
        public static ReceiptRequirement Requirement(Core2RunSession session, UiData ui, JObject run, JObject piece)
        {
            var strings = ui.Strings;
            var result = new ReceiptRequirement { PieceName = piece.Str(K.Name) ?? piece.Str(K.Id) };
            var receipt = Loadout.RequirementReceipt(session.Core.Deck.Run.Run, piece, run.Obj(K.Attributes), run.Obj(K.ItemUpgradeLevels) ?? new JObject());
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

        private static PoiseReceipt Poise(Core2RunSession session, UiData ui, JObject run, JObject loadout, IReadOnlyList<JObject> pieces)
        {
            var strings = ui.Strings;
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
            if (attribute > 0) receipt.Sources.Add(new ReceiptSource { Id = AttributeNames(session, run), Value = Num(attribute) });
            receipt.Sources.AddRange(sources);
            receipt.Sources.AddRange(relicSources);
            receipt.Pair = strings.Format(StringKeys.ArmouryReceiptPoisePair, new StringArgs().Add(UiPlaceholders.Items, Num(items)).Add(UiPlaceholders.Relics, Num(relicTotal)));
            return receipt;
        }

        /// <summary>The attributes the derived Poise row answers to, joined as the web names them ("constitution").</summary>
        private static string AttributeNames(Core2RunSession session, JObject run)
        {
            var d = session.Core.Combat.Run;
            var table = run.Obj(K.DerivedStatRuleSnapshot)?.Obj(K.Rules);
            var row = CoreDerivedStats.ResolvedRow(d, table, ArmouryKeys.PoiseRow, session.ClassId);
            var ids = row == null ? new List<string>() : CoreDerivedStats.RuleWeights(d, row).Select(p => p.Key).ToList();
            return ids.Count == 0 ? ArmouryKeys.AttributesWord : string.Join(ArmouryKeys.Plus, ids);
        }

        // ------------------------------------------------------------------ equip load

        private static LoadReceipt Load(Core2RunSession session, UiData ui, JObject run, JObject loadout, IReadOnlyList<JObject> pieces)
        {
            var strings = ui.Strings;
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
        /// The comparison of putting <paramref name="itemId"/> (or nothing, an unequip) into a position (candidateReceipt, equipmentPresentation.js): the roles, the attack package counts, the minimums, the effects it adds, the
        /// resource changes and swap prices, Poise and load before and after. The loadout it is tried on is the equip transition alone, with none of the equip gates (the web compares what the seal refuses too).
        /// Null when the web throws: the piece is unknown, the position does not exist, or a plan cannot be made of the trial loadout.
        /// </summary>
        public static CandidateView Candidate(Core2RunSession session, UiData ui, string slotId, int setIndex, string itemId, string swapRuleId = null)
        {
            var strings = ui.Strings;
            var run = session.Run;
            var gear = session.Core.Deck.Run.Run;
            var data = session.CombatData;
            var loadout = run.Obj(K.Loadout);
            var slot = gear.EquipmentRows(K.Slots).FirstOrDefault(s => s.Str(K.Id) == slotId);
            if (loadout == null || slot == null || setIndex < 0 || !(loadout.Obj(K.Sets)?[slotId] is JArray cells) || setIndex >= cells.Count) return null;
            var piece = string.IsNullOrEmpty(itemId) ? null : FindPiece(data, session.ClassId, slot, itemId);
            if (!string.IsNullOrEmpty(itemId) && piece == null) return null;
            var trial = (JObject)loadout.DeepClone();
            try
            {
                if (Armoury.ApplyEquipTransition(gear, trial, slotId, setIndex, itemId)) trial.Obj(K.Active).Put(slotId, setIndex);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            EquipmentReceiptView before;
            EquipmentReceiptView after;
            List<CandidateAttackPackage> packages;
            var view = new CandidateView();
            try
            {
                before = For(session, ui, run, loadout);
                after = For(session, ui, run, trial);
                packages = AttackPackages(session, run, loadout, trial);
                view.AddedEffects.AddRange(AddedEffects(data, Equipment.EquippedIn(data, loadout, session.ClassId, slotId), piece));
                view.Resources.AddRange(ResourceChanges(session, ui, run, loadout, trial, slotId, setIndex, swapRuleId));
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            view.PoiseBefore = before.Poise.Value;
            view.PoiseAfter = after.Poise.Value;
            view.PoiseNote = after.Poise.Note;
            view.LoadBefore = before.Load;
            view.LoadAfter = after.Load;
            view.ChangesClass = before.Load.ClassId != after.Load.ClassId;
            foreach (var package in packages)
            {
                if (package.SourceHands.Count > 0) package.Hint = strings.Format(StringKeys.ArmouryReceiptHandPackage, new StringArgs().Add(UiPlaceholders.Hands, string.Join(strings.Get(StringKeys.ArmouryReceiptHandJoin), package.SourceHands)));
                view.AttackPackages.Add(package);
            }
            foreach (var a in after.Roles)
            {
                var b = before.Roles.FirstOrDefault(r => r.Role == a.Role);
                if (b == null) continue;
                view.Roles.Add(new CandidateRoleChange { Role = a.Role, BeforeName = b.Name, AfterName = a.Name, Hint = a.Hint, Before = b.Value, After = a.Value });
            }
            if (piece != null)
            {
                var requirement = Requirement(session, ui, run, piece);
                if (requirement.Rows.Count > 0) view.Requirement = requirement;
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

        /// <summary>pieceFor(registries, classId, pieceId, slot): an armour set of the class for an armour slot, else an armament; null when there is none.</summary>
        private static JObject FindPiece(CombatData data, string classId, JObject slot, string id)
        {
            if (Js.Items(slot[K.Kinds]).Any(k => Js.Str(k) == V.Armor))
                return Js.Items(data.Equipment[K.Armour]).OfType<JObject>().FirstOrDefault(p => p.Str(K.Id) == id && p.Str(K.ClassId) == classId);
            return Js.Items(data.Equipment[K.Armaments]).OfType<JObject>().FirstOrDefault(p => p.Str(K.Id) == id);
        }

        /// <summary>attackPackageCounts(registries, run), the plan the run holds (its attack slot count and the slots it has retired), grouped by card and profile, in first-seen order. A configured deck owns its explicit cards, so its plan has no slots: the web's rows are empty there.</summary>
        private static List<CandidateAttackPackage> AttackPackages(Core2RunSession session, JObject run, JObject beforeLoadout, JObject afterLoadout)
        {
            var gear = session.Core.Deck.Run.Run;
            var data = session.CombatData;
            double? count = Js.IsFinite(run[K.EquipmentAttackSlotCount]) ? run.Num(K.EquipmentAttackSlotCount) : (double?)null;
            Dictionary<string, CandidateAttackPackage> Group(JObject loadout)
            {
                var plan = WeaponCards.BuildPlan(gear, loadout, session.ClassId, count, run[K.RemovedAttackSlotIds], true);
                var groups = new Dictionary<string, CandidateAttackPackage>(StringComparer.Ordinal);
                foreach (var planned in plan.Slots)
                {
                    var key = planned.Str(K.CardId) + ReceiptKeys.KeySeparator + planned.Str(K.ProfileId);
                    if (!groups.TryGetValue(key, out var row))
                    {
                        var profile = Loadout.ProfileById(gear, planned[K.ProfileId]);
                        var card = planned.Str(K.CardId);
                        groups[key] = row = new CandidateAttackPackage
                        {
                            CardId = card,
                            ProfileId = planned.Str(K.ProfileId),
                            Name = profile != null && profile.Str(RK.BaseCardId) == card ? profile.Str(K.DisplayName) : data.Cards.Get(card).Str(K.Name),
                        };
                    }
                    row.AfterCount++;
                    var hand = planned.Str(K.SourceHand);
                    if (!string.IsNullOrEmpty(hand) && !row.SourceHands.Contains(hand)) row.SourceHands.Add(hand);
                }
                return groups;
            }
            var was = Group(beforeLoadout);
            var now = Group(afterLoadout);
            var result = new List<CandidateAttackPackage>();
            foreach (var key in was.Keys.Concat(now.Keys.Where(k => !was.ContainsKey(k))))
            {
                now.TryGetValue(key, out var live);
                var row = live ?? was[key];
                var package = new CandidateAttackPackage { CardId = row.CardId, ProfileId = row.ProfileId, Name = row.Name, BeforeCount = was.TryGetValue(key, out var old) ? old.AfterCount : 0, AfterCount = live?.AfterCount ?? 0 };
                if (live != null) package.SourceHands.AddRange(live.SourceHands);
                result.Add(package);
            }
            return result;
        }

        /// <summary>explicitEffects(registries, beforePiece, afterPiece): the mods the new piece carries that the worn one does not, worded "Max HP +10".</summary>
        private static List<string> AddedEffects(CombatData data, JObject wornPiece, JObject piece)
        {
            var list = new List<string>();
            if (piece == null) return list;
            var had = new HashSet<string>(Js.Items(wornPiece?[K.Mods]).Select(Js.Str), StringComparer.Ordinal);
            var fields = data.Equipment.Obj(K.ModFields) ?? new JObject();
            foreach (var raw in Js.Items(piece[K.Mods]).Select(Js.Str).Where(m => m != null && !had.Contains(m)))
            {
                var mod = Loadout.ParseMod(raw);
                var field = mod == null ? null : fields.Obj(mod.Field);
                if (field == null) throw new InvalidOperationException(raw);
                list.Add(field.Str(K.Label) + ArmouryKeys.Space + (mod.Add && mod.Value >= 0 ? RunValues.Plus : string.Empty) + Num(mod.Value));
            }
            return list;
        }

        /// <summary>
        /// The run's resource changes of a swap: each pool maximum the worn gear moves, then each combat-swappable slot's swap price in Actions (swapPriceChanges). Each slot is priced at the same set in both loadouts, the
        /// candidate's own set for its own slot and the active one for the others; a row shows when the price moves or when the amount a gear-off rule declined moves.
        /// </summary>
        private static List<CandidateResourceChange> ResourceChanges(Core2RunSession session, UiData ui, JObject run, JObject beforeLoadout, JObject afterLoadout, string candidateSlot, int candidateSet, string swapRuleId)
        {
            var list = new List<CandidateResourceChange>();
            var strings = ui.Strings;
            var gear = session.Core.Deck.Run.Run;
            var data = session.CombatData;
            var d = session.Core.Combat.Run;
            var beforeMods = CoreRunRules.RunMods(d, beforeLoadout, session.ClassId, null);
            var afterMods = CoreRunRules.RunMods(d, afterLoadout, session.ClassId, null);
            foreach (var field in Loadout.PoolFields(gear))
            {
                if (beforeMods.Pool(field) == afterMods.Pool(field)) continue;
                var now = run.Num(field);
                list.Add(new CandidateResourceChange
                {
                    Id = field,
                    Label = strings.Get(string.Format(CultureInfo.InvariantCulture, ReceiptKeys.ResourceKey, field)),
                    Before = now,
                    After = now + afterMods.Pool(field) - beforeMods.Pool(field),
                });
            }
            var rule = Equipment.ResolveSwapCostRule(data, swapRuleId == null ? Js.Null() : new JValue(swapRuleId));
            var energy = gear.DerivedStatRules?.Obj(ReceiptKeys.Presentation)?.Obj(ReceiptKeys.EnergyRow);
            var unit = energy?.Str(ReceiptKeys.FaceLabel) ?? energy?.Str(K.Label) ?? throw new InvalidOperationException(ReceiptKeys.NoEnergyRow);
            var relicDelta = Cards.PassiveSum(data, run[K.Relics] as JArray ?? new JArray(), RK.SwapCostDelta, new JObject(), null);
            foreach (var slot in gear.EquipmentRows(K.Slots).Where(s => s.Str(RK.Swap) == RV.SwapInCombat))
            {
                var id = slot.Str(K.Id);
                var index = id == candidateSlot ? candidateSet : (int)Js.Or0(afterLoadout.Obj(K.Active)?[id]);
                var was = Armoury.SwapCostFor(gear, rule, beforeLoadout, session.ClassId, id, index, relicDelta);
                var now = Armoury.SwapCostFor(gear, rule, afterLoadout, session.ClassId, id, index, relicDelta);
                var declined = now.Num(RK.GearIgnored) - was.Num(RK.GearIgnored);
                if (was.Num(K.Cost) == now.Num(K.Cost) && declined == 0) continue;
                var subject = Js.Truthy(rule) && Js.Truthy(Js.Get(rule, K.Label))
                    ? strings.Format(StringKeys.ArmouryReceiptSwapSubject, new StringArgs().Add(UiPlaceholders.Rule, Js.Str(Js.Get(rule, K.Label))))
                    : strings.Get(StringKeys.ArmouryReceiptSwapSubjectNone);
                list.Add(new CandidateResourceChange
                {
                    Id = string.Format(CultureInfo.InvariantCulture, ReceiptKeys.SwapCostId, id),
                    Label = strings.Format(StringKeys.ArmouryReceiptSwapLabel, new StringArgs().Add(UiPlaceholders.Slot, slot.Str(K.Label) ?? id).Add(ReceiptPlaceholders.Unit, unit)),
                    Before = was.Num(K.Cost),
                    After = now.Num(K.Cost),
                    RuleId = now.Str(RK.RuleId),
                    Note = declined == 0 ? string.Empty : strings.Format(StringKeys.ArmouryReceiptSwapNote, new StringArgs().Add(ReceiptPlaceholders.Subject, subject)
                        .Add(UiPlaceholders.Amount, (declined > 0 ? RunValues.Plus : string.Empty) + Num(declined)).Add(ReceiptPlaceholders.Unit, unit)),
                });
            }
            return list;
        }
    }
}
