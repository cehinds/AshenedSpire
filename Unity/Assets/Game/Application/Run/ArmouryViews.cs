using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using RK = Ashen.Generated.RunKeys;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.App.Run
{
    /// <summary>A card of the run's deck as the Armoury's Cards pane and the deck editor's inspector show it: the face's facts, priced at their printed cost and worded with the authored numbers (no fight, so nothing is priced against a target).</summary>
    public static class Core2DeckCards
    {
        public static Core2CardView View(CombatData data, JObject instance, CardWords words = null)
        {
            var def = Cards.Resolve(data, instance);
            var card = new Core2CardView
            {
                InstanceId = instance.Str(K.InstanceId),
                CardId = instance.Str(K.CardId),
                Name = def.Str(K.Name) ?? instance.Str(K.CardId),
                Type = def.Str(K.Type),
                Rank = Js.IsNum(def[CK.Rank]) ? def.Num(CK.Rank) : (double?)null,
                Upgraded = Js.Truthy(instance[K.Upgraded]),
                Stamina = Js.IsNum(def[CK.StaminaCost]) ? def.Num(CK.StaminaCost) : 0d,
                Mana = Js.IsNum(def[K.ManaCost]) ? def.Num(K.ManaCost) : 0d,
                CostIsX = Js.IsStr(def[K.Cost]) && def.Str(K.Cost) == V.XCost,
                Playable = true,
            };
            foreach (var tag in Js.Items(def[CK.CardTags] ?? def[K.Tags])) card.Tags.Add(Js.IsStr(tag) ? Js.Str(tag) : Js.Str(Js.Get(tag, K.Id)));
            CardFaceQuery.CollectEffects(def[K.Effects], card.Effects);
            card.Instance = instance;
            card.FaceDef = def;
            card.Text = Text(data, def);
            if (words != null) card.Words = words.For(card);
            return card;
        }

        /// <summary>The card's text template with each tokenizable value as authored.</summary>
        private static string Text(CombatData data, JObject def)
        {
            var authored = Js.Items(def[K.Effects]).OfType<JObject>().ToList();
            var tokens = new JObject();
            foreach (var binding in CombatPreview.TokenBindings(data, def[K.Effects]))
            {
                var split = binding.Key.Split(V.KeySeparator[0]);
                var effect = authored[int.Parse(split[0], CultureInfo.InvariantCulture)];
                if (Js.IsNum(effect[split[1]])) tokens[binding.Value] = effect[split[1]].DeepClone();
            }
            return CombatViewModel.ResolveText(def.Str(K.TextTemplate), tokens);
        }
    }

    /// <summary>What an equipped piece says of itself on the Armoury's inspect panel.</summary>
    public sealed class ArmouryItemView
    {
        public string Id;
        public string Name;
        public string ArtKey;
        public string Kind;
        public string Rarity;
        public string Blurb;

        /// <summary>The piece's numbers by their string key (rating, weight ...) and value.</summary>
        public readonly List<KeyValuePair<string, string>> Stats = new List<KeyValuePair<string, string>>();
    }

    /// <summary>One position of the equipment list (web "Equipped positions"): a slot's set, its code (BODY, MH1, OH2), what it holds or why it is shut.</summary>
    public sealed class ArmouryPositionView
    {
        public string SlotId;
        public int SetIndex;
        public string Code;
        public string Name;
        public bool Empty;
        public bool Locked;

        /// <summary>The set is the slot's active one.</summary>
        public bool Active;

        /// <summary>The way to open a locked position (the unlock's hint), or empty.</summary>
        public string Hint = string.Empty;

        public ArmouryItemView Item;

        /// <summary>"Replace" on an occupied position, "Choose item" on an empty one (web armament-replace); a locked position offers neither.</summary>
        public string ReplaceLabel = string.Empty;

        /// <summary>Why Unequip is shut (the equip rules, the storage a displaced piece needs), or empty when it can be pressed; only an occupied position has Unequip.</summary>
        public string UnequipRefusal = string.Empty;

        /// <summary>Why "Make active" is shut (the swap rule, the grip), or empty; only a position that is not the slot's active one offers it.</summary>
        public string ActivateRefusal = string.Empty;
    }

    public sealed class ArmouryChip
    {
        public string Label;
        public string Value;
    }

    /// <summary>One of the three combat powers of the Character pane (web armouryLayout combatPower.cards, equipmentSurfaceReceipt roles): its label, its value and the calculation behind it.</summary>
    public sealed class ArmouryPowerView
    {
        public string Role;
        public string Label;
        public string FullLabel;
        public double Value;

        /// <summary>"+1 gear" (the rarity bonus of the piece that supplies the role) and the calculation "3 base + 5 AR (weapon) + 1 rarity".</summary>
        public string Gear;
        public string Formula;
    }

    /// <summary>The Character pane (web equipment.js character view): who, how far along, the sheet and the relics.</summary>
    public sealed class Core2CharacterView
    {
        public string ClassName;
        public string Eyebrow;
        public string Description;
        public string Portrait;
        public int Level;
        public string LevelText;
        public string XpText;
        public double XpFraction;
        public readonly List<ArmouryChip> Sheet = new List<ArmouryChip>();

        /// <summary>The Combat Power card: the three powers (Strike, Magic, Defense), and its one-line summary "Strike 11 · Magic 5 · Defense 6".</summary>
        public readonly List<ArmouryPowerView> Powers = new List<ArmouryPowerView>();
        public string PowerSummary;

        /// <summary>The Equipment cards card: each role's card package with the copies the deck holds ("Slashing Strike x4"), and its summary line.</summary>
        public readonly List<string> EquipmentCards = new List<string>();
        public string EquipmentCardsSummary;
        public string RelicsSummary;
        public readonly List<string> RelicIds = new List<string>();
        public readonly List<string> RelicNames = new List<string>();
    }

    /// <summary>The Armoury's read-only panes over a Core 2 run (D-903). Engine-free.</summary>
    public static class ArmouryPanes
    {
        private static int Int(double v) => (int)Math.Floor(v);

        public static Core2CharacterView Character(Core2RunSession session, UiData ui)
        {
            var strings = ui.Strings;
            var run = session.Run;
            var level = Int(run.Obj(K.Level)?.Num(K.Level) ?? 0);
            var className = RunHudView.ClassName(ui, session.ClassId);
            var view = new Core2CharacterView
            {
                ClassName = className,
                Eyebrow = strings.Format(StringKeys.ArmouryCharacterEyebrow, new StringArgs().Add(UiPlaceholders.Class, className).Add(UiPlaceholders.Level, level)),
                Description = strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.ClassDescriptionKey, session.ClassId)),
                Portrait = session.Portrait,
                Level = level,
                LevelText = strings.Format(StringKeys.ArmouryCharacterLevel, new StringArgs().Add(UiPlaceholders.Level, level)),
            };
            var row = run.Obj(K.Level) ?? Ashen.Domain.Rewards.LevelUp.EmptyLevel();
            var next = Ashen.Domain.Loop.Core2Rest.XpToNext(session.Core.Rewards, row.Num(K.Level));
            var capped = double.IsInfinity(next) || double.IsNaN(next) || next <= 0;
            view.XpText = capped ? strings.Get(StringKeys.ArmouryCharacterXpMax)
                : strings.Format(StringKeys.ArmouryCharacterXp, new StringArgs().Add(UiPlaceholders.Amount, Int(row.Num(K.Xp))).Add(UiPlaceholders.Total, Int(next)));
            view.XpFraction = capped ? 1d : Math.Max(0d, Math.Min(1d, row.Num(K.Xp) / next));
            void Chip(string key, double value) => view.Sheet.Add(new ArmouryChip { Label = strings.Get(key), Value = Int(value).ToString(CultureInfo.InvariantCulture) });
            Chip(StringKeys.ArmouryStatHp, run.Num(K.MaxHp));
            Chip(StringKeys.ArmouryStatSp, run.Num(K.MaxStamina));
            Chip(StringKeys.ArmouryStatMp, run.Num(K.MaxMana));
            // The derived ratings (web ProgressionInspectionModel.progressionStats: the rating receipt's totals), then the hand the attributes allow and the cards a turn draws.
            var ratings = CoreRunCombat.RatingReceipt(session.Core.Combat, run).Obj(CK.Totals);
            Chip(StringKeys.ArmouryStatAr, ratings.Num(K.Ar));
            Chip(StringKeys.ArmouryStatPr, ratings.Num(K.Pr));
            Chip(StringKeys.ArmouryStatDr, ratings.Num(K.Dr));
            Chip(StringKeys.ArmouryStatHandSize, HandSize(session));
            Chip(StringKeys.ArmouryStatDraw, run.Num(K.DrawPerTurn));
            Powers(session, ui, view);
            var data = session.CombatData;
            foreach (var id in Js.Items(run[K.Relics]).Select(Js.Str).Where(id => id != null))
            {
                view.RelicIds.Add(id);
            }
            var names = view.RelicIds.Select(id => data.Relics.Has(id) ? data.Relics.Get(id).Str(K.Name) ?? id : id).ToList();
            view.RelicNames.AddRange(names);
            view.RelicsSummary = names.Count == 0 ? strings.Get(StringKeys.ArmouryRelicsNone)
                : strings.Format(StringKeys.ArmouryRelicsSummary, new StringArgs().Add(UiPlaceholders.Count, names.Count).Add(UiPlaceholders.Names, string.Join(strings.Get(StringKeys.RestTagJoin), names)));
            return view;
        }

        /// <summary>handSizeReceipts(hand, run.attributes, level).capacity.value: the profile's handSize row over the allocated attributes.</summary>
        private static double HandSize(Core2RunSession session)
        {
            var run = session.Run;
            var d = session.Core.Combat.Run;
            var table = run.Obj(K.DerivedStatRuleSnapshot)?.Obj(K.Rules);
            var row = CoreDerivedStats.ResolvedRow(d, table, ArmouryKeys.HandSizeRow, session.ClassId);
            var classDef = d.Run.Classes.Get(session.ClassId);
            var level = Js.Truthy(run.Obj(K.Level)?[K.Level]) ? run.Obj(K.Level).Num(K.Level) : 1;
            return CoreDerivedStats.RowValue(d, row, run.Obj(K.Attributes), classDef, level, ArmouryKeys.HandSizeRow).Value;
        }

        /// <summary>
        /// The Combat Power card and the Equipment cards card (web equipment.js characterPowerEntries and equipmentReceiptPanel over equipmentSurfaceReceipt): each role's value from the kit receipt of the worn
        /// gear (the card's base, the rating of the piece that supplies it, its rarity), in the layout's order Strike, Magic, Defense; and each role's card package with the copies the deck holds.
        /// </summary>
        private static void Powers(Core2RunSession session, UiData ui, Core2CharacterView view)
        {
            var strings = ui.Strings;
            var run = session.Run;
            var loadout = run.Obj(K.Loadout);
            if (loadout == null) return;
            var rows = Loadout.KitReceipt(session.Core.Deck.Run.Run, loadout, session.ClassId, run.Obj(K.Attributes), run.Obj(K.EquipmentProfileRuleSnapshot), session.RatingAttributePart(ui));
            var held = Js.Items(run[K.Deck]).OfType<JObject>().Where(c => Js.Truthy(c[K.EquipmentRole])).GroupBy(c => c.Str(K.EquipmentRole)).ToDictionary(g => g.Key, g => g.Count());
            foreach (var role in ArmouryKeys.PowerRoles.Split(UiFormats.ListJoin[0]))
            {
                var row = rows.FirstOrDefault(r => r.Role == role);
                var receipt = row?.Receipt;
                var rating = receipt?.Obj(RK.Rating);
                view.Powers.Add(new ArmouryPowerView
                {
                    Role = role,
                    Label = strings.Get(string.Format(CultureInfo.InvariantCulture, ArmouryKeys.PowerLabelKey, role)),
                    FullLabel = strings.Get(string.Format(CultureInfo.InvariantCulture, ArmouryKeys.PowerFullKey, role)),
                    Value = receipt?.Num(K.Value) ?? 0,
                    Gear = row == null ? strings.Get(StringKeys.ArmouryPowerNone) : strings.Format(StringKeys.ArmouryPowerGear, new StringArgs().Add(UiPlaceholders.Amount, Int(receipt.Num(RK.RarityBonus)))),
                    Formula = row == null ? strings.Get(StringKeys.ArmouryPowerNoReceipt)
                        : strings.Format(StringKeys.ArmouryPowerFormula, new StringArgs().Add(UiPlaceholders.Base, Int(receipt.Num(K.Base))).Add(UiPlaceholders.Rating, Int(rating.Num(K.Value)))
                            .Add(UiPlaceholders.Id, (rating.Str(K.Id) ?? string.Empty).ToUpperInvariant()).Add(UiPlaceholders.Source, rating.Str(RK.SourceLabel)).Add(UiPlaceholders.Bonus, Int(receipt.Num(RK.RarityBonus)))),
                });
            }
            view.PowerSummary = string.Join(strings.Get(StringKeys.RestTagJoin), view.Powers.Select(p => p.Label + ArmouryKeys.Space + Int(p.Value).ToString(CultureInfo.InvariantCulture)));
            foreach (var row in rows)
            {
                held.TryGetValue(row.Role, out var copies);
                view.EquipmentCards.Add(strings.Format(StringKeys.ArmouryCardsCopies, new StringArgs().Add(UiPlaceholders.Name, row.Profile?.Str(K.DisplayName) ?? row.Role).Add(UiPlaceholders.Count, copies)));
            }
            view.EquipmentCardsSummary = string.Join(strings.Get(StringKeys.RestTagJoin), view.EquipmentCards);
        }

        /// <summary>The equipped positions: the pieces outside the hands first (body), then each hand's sets in order; a set the profile has not opened is locked and says how to open it.</summary>
        public static IReadOnlyList<ArmouryPositionView> Equipment(Core2RunSession session, UiData ui)
        {
            var strings = ui.Strings;
            var run = session.Run;
            var data = session.CombatData;
            var loadout = run.Obj(K.Loadout);
            var list = new List<ArmouryPositionView>();
            if (loadout == null) return list;
            var slots = Js.Items(data.Equipment[K.Slots]).OfType<JObject>().ToList();
            var ordered = slots.Where(s => !IsHand(s)).Concat(slots.Where(IsHand)).ToList();
            foreach (var slot in ordered)
            {
                var slotId = slot.Str(K.Id);
                var cells = loadout.Obj(K.Sets)?.Arr(slotId) ?? new JArray();
                var hand = IsHand(slot);
                var count = hand ? (int)Js.Or0(slot[K.Sets]) : cells.Count;
                if (!hand && cells.Count == 0) continue;
                var opened = Armoury.OpenedSets(session.Core.Deck.Run.Run, slot, loadout);
                var active = (int)Js.Or0(loadout.Obj(K.Active)?[slotId]);
                for (var i = 0; i < Math.Max(count, 1); i++)
                {
                    var id = i < cells.Count ? Js.Str(cells[i]) : null;
                    var item = string.IsNullOrEmpty(id) ? null : Item(session, strings, slotId, id);
                    if (!hand && i >= cells.Count) continue;
                    var position = new ArmouryPositionView
                    {
                        SlotId = slotId,
                        SetIndex = i,
                        Code = StringTable.Fill(slot.Str(ArmouryKeys.PositionCode) ?? slotId, new StringArgs().Add(ArmouryKeys.PlaceholderN, i + 1)),
                        Locked = i >= opened,
                        Active = i == active,
                        Item = item,
                        Empty = item == null,
                        Name = item?.Name ?? strings.Get(StringKeys.ArmouryRowEmpty),
                    };
                    if (position.Locked) position.Hint = UnlockHint(session, strings, slotId, i);
                    else
                    {
                        position.ReplaceLabel = strings.Get(item == null ? StringKeys.ArmouryActionChoose : StringKeys.ArmouryActionReplace);
                        if (item != null) position.UnequipRefusal = session.EquipRefusal(strings, slotId, i, null) ?? string.Empty;
                        if (!position.Active) position.ActivateRefusal = session.ActivationRefusal(strings, slotId, i) ?? string.Empty;
                    }
                    list.Add(position);
                }
            }
            return list;
        }

        private static bool IsHand(JObject slot) => slot.Str(K.Hand) == V.Right || slot.Str(K.Hand) == V.Left;

        /// <summary>
        /// How a locked position opens: the catalog lists the unlocks of a slot's racks in order (the second set, then the third), and each unlock's hint is the string "unlock.{id}.hint"
        /// ("Reach the Stitched Court.").
        /// </summary>
        private static string UnlockHint(Core2RunSession session, StringTable strings, string slotId, int setIndex)
        {
            var rows = Js.Items(session.Content.Loop.Unlocks).OfType<JObject>().Where(u => u.Str(K.Kind) == ArmouryKeys.KindSlot && u.Str(ArmouryKeys.UnlockRef) == slotId).ToList();
            if (setIndex < 1 || setIndex > rows.Count) return string.Empty;
            var key = string.Format(CultureInfo.InvariantCulture, ArmouryKeys.UnlockHintKey, rows[setIndex - 1].Str(K.Id));
            return strings.Has(key) ? strings.Get(key) : string.Empty;
        }

        internal static ArmouryItemView Item(Core2RunSession session, StringTable strings, string slotId, string id)
        {
            var data = session.CombatData;
            JObject def = Js.Items(data.Equipment[K.Armaments]).OfType<JObject>().FirstOrDefault(a => a.Str(K.Id) == id)
                ?? Js.Items(data.Equipment[K.Armour]).OfType<JObject>().FirstOrDefault(a => a.Str(K.Id) == id && a.Str(K.ClassId) == session.ClassId);
            var view = new ArmouryItemView { Id = id, Name = def?.Str(K.Name) ?? id, ArtKey = def?.Str(ArmouryKeys.ArtKey), Kind = def?.Str(K.Kind), Rarity = def?.Str(ArmouryKeys.Rarity), Blurb = def?.Str(ArmouryKeys.Blurb) };
            if (def == null) return view;
            foreach (var stat in ArmouryKeys.StatList.Split(UiFormats.ListJoin[0]))
                if (Js.IsNum(def[stat])) view.Stats.Add(new KeyValuePair<string, string>(strings.Get(string.Format(CultureInfo.InvariantCulture, ArmouryKeys.StatStringKey, stat)), Int(def.Num(stat)).ToString(CultureInfo.InvariantCulture)));
            return view;
        }
    }
}
