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
using MI = Ashen.Generated.Core2ModuleIds;
using PC = Ashen.Generated.Core2ProfileContentKeys;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.App.Run
{
    /// <summary>One labelled fact, bonus or tag of a poker-style card with the sentence that explains it (the web's model field(label, value, explanation)).</summary>
    public sealed class EquipmentCardLine
    {
        public string Label;

        /// <summary>The value as the face draws it (a fact's number), or empty.</summary>
        public string Value = string.Empty;

        public string Explanation;
    }

    /// <summary>
    /// What a poker-style equipment card says (web model/equipmentCard.js equipmentCardModel, then ui/models/PossessionVariantModel.js possessionVariant for an armament or armour set): the name, the type
    /// line and the requirement beside it, the facts, the tags, the effects block with its heading, the flavour line and the footer's rarity.
    /// </summary>
    public sealed class EquipmentCardModel
    {
        public string Id;
        public string Name;
        public bool Armor;
        public string Type;
        public string TypeExplanation;
        public string Requirement;
        public string RequirementExplanation;
        public string Rarity;
        public string Flavor;

        /// <summary>The registry art id of the piece's picture (an armament's icon, an armour set's painted outfit), or null.</summary>
        public string ArtId;

        /// <summary>The frame's accent: <see cref="ReceiptCardKeys.AccentStaff"/>, <see cref="ReceiptCardKeys.AccentBlade"/> (a Blade shield) or <see cref="ReceiptCardKeys.AccentGold"/>.</summary>
        public string Accent;

        public string Heading;
        public readonly List<EquipmentCardLine> Facts = new List<EquipmentCardLine>();
        public readonly List<EquipmentCardLine> Tags = new List<EquipmentCardLine>();

        /// <summary>The effects block: the weapon art the piece grants then its modifiers (a weapon), its modifiers (armour), or the budget row of a configured piece.</summary>
        public readonly List<EquipmentCardLine> Effects = new List<EquipmentCardLine>();

        /// <summary>The line shown when <see cref="Effects"/> is empty ("No granted cards", "No granted modifiers").</summary>
        public string Empty;
    }

    /// <summary>The Armoury's poker-style equipment card, engine-free: the web's equipmentCardModel and its possession variant over a Core 2 run's content and gear.</summary>
    public static class EquipmentCards
    {
        private static string Num(double v) => RunJs.NumStr(v);

        private static string Fill(StringTable strings, string key, params (string Name, string Value)[] args)
        {
            var a = new StringArgs();
            foreach (var arg in args) a.Add(arg.Name, arg.Value);
            return strings.Format(key, a);
        }

        /// <summary>The card of <paramref name="piece"/> (an armament or an armour set of the run's class; <paramref name="level"/> is the forged tier it is shown at).</summary>
        public static EquipmentCardModel For(Core2RunSession session, UiData ui, JObject piece, double level = 0)
        {
            var strings = ui.Strings;
            var data = session.CombatData;
            var d = session.Core.Combat.Run;
            var subject = d.SubjectOf(null);
            var armor = piece.Str(K.Kind) == V.Armor;
            var view = new EquipmentCardModel { Id = piece.Str(K.Id), Name = piece.Str(K.Name), Armor = armor, ArtId = ArtId(session, ui, piece) };

            // tags: the piece's own plus, under FixCore, its property tags, once each.
            var tagIds = new List<string>();
            foreach (var id in Js.Items(piece[TagKeys.Tags]).Select(Js.Str).Where(id => id != null)) if (!tagIds.Contains(id)) tagIds.Add(id);
            if (Js.D(CoreRunData.Module(subject, MI.Effects)?[Ashen.Generated.Core2RunKeys.FixCoreVersion]) == 1)
                foreach (var id in Js.Items(piece[TagKeys.PropertyTags]).Select(Js.Str).Where(id => id != null)) if (!tagIds.Contains(id)) tagIds.Add(id);
            var tagTable = session.Core.Registries.Object(RegistryKeys.TagRegistry);
            JObject Tag(string id) => Js.Items(tagTable).OfType<JObject>().FirstOrDefault(row => Js.Str(row[RegistryKeys.Id]) == id);
            foreach (var id in tagIds)
            {
                var row = Tag(id);
                var blurb = row?.Str(ArmouryKeys.Blurb);
                view.Tags.Add(new EquipmentCardLine { Label = row?.Str(K.Label) ?? id, Value = id, Explanation = string.IsNullOrEmpty(blurb) ? strings.Get(StringKeys.ArmouryCardTagDefault) : blurb });
            }

            // the type line.
            var className = armor
                ? (Js.Truthy(piece[RK.SharedSet]) ? strings.Get(StringKeys.ArmouryCardAllClasses)
                    : data.Classes.Has(piece.Str(K.ClassId)) ? data.Classes.Get(piece.Str(K.ClassId)).Str(K.Name) ?? piece.Str(K.ClassId) : piece.Str(K.ClassId))
                : string.Empty;
            var types = string.Join(strings.Get(StringKeys.ArmouryCardTypeJoin), Js.Items(piece[TagKeys.ItemTypes]).OfType<JObject>().Select(t => t.Str(TagKeys.ItemTypeLabel)));
            var kind = piece.Str(K.Kind);
            view.Type = armor ? Fill(strings, StringKeys.ArmouryCardTypeArmor, (UiPlaceholders.Class, className))
                : kind == ReceiptCardKeys.WeaponKind ? Fill(strings, StringKeys.ArmouryCardTypeWeapon, (ReceiptPlaceholders.Types, types))
                : kind == ReceiptCardKeys.StaffKind ? Fill(strings, StringKeys.ArmouryCardTypeStaff, (ReceiptPlaceholders.Types, types))
                : string.IsNullOrEmpty(types) ? kind : types;
            var typeBlurbs = Js.Items(piece[TagKeys.ItemTypes]).OfType<JObject>().Select(t => Tag(t.Str(TagKeys.ItemTypeTag))?.Str(ArmouryKeys.Blurb)).Where(b => !string.IsNullOrEmpty(b)).ToList();
            view.TypeExplanation = typeBlurbs.Count > 0 ? string.Join(ArmouryKeys.Space, typeBlurbs) : Fill(strings, StringKeys.ArmouryCardTypeExplanation, (ReceiptPlaceholders.Type, view.Type));
            view.Accent = kind == ReceiptCardKeys.StaffKind ? ReceiptCardKeys.AccentStaff
                : kind == ReceiptCardKeys.ShieldKind && types.Contains(ReceiptCardKeys.BladeType) ? ReceiptCardKeys.AccentBlade : ReceiptCardKeys.AccentGold;
            view.Flavor = string.IsNullOrEmpty(piece.Str(ArmouryKeys.Blurb)) ? strings.Get(StringKeys.ArmouryCardNoFlavor) : piece.Str(ArmouryKeys.Blurb);
            view.Rarity = string.IsNullOrEmpty(piece.Str(ArmouryKeys.Rarity)) ? strings.Get(StringKeys.ArmouryCardArmorSet) : piece.Str(ArmouryKeys.Rarity);

            // the requirement line.
            var requirements = (piece.Obj(RK.Requirements)?.Obj(K.Attributes) ?? new JObject()).Properties()
                .Select(p => (data.Attributes.Has(p.Name) ? data.Attributes.Get(p.Name).Str(RK.ShortLabel) ?? p.Name : p.Name) + ArmouryKeys.Space + Num(Js.D(p.Value))).ToList();
            var requirementText = string.Join(strings.Get(StringKeys.ArmouryCardRequirementJoin), requirements);
            view.Requirement = requirements.Count > 0 ? Fill(strings, StringKeys.ArmouryCardRequires, (ReceiptPlaceholders.List, requirementText))
                : armor ? Fill(strings, StringKeys.ArmouryCardOutfit, (UiPlaceholders.Class, className)) : strings.Get(StringKeys.ArmouryCardEitherHand);
            view.RequirementExplanation = requirements.Count > 0 ? Fill(strings, StringKeys.ArmouryCardRequiresExplanation, (ReceiptPlaceholders.List, requirementText))
                : armor ? Fill(strings, StringKeys.ArmouryCardOutfitExplanation, (UiPlaceholders.Class, className)) : strings.Get(StringKeys.ArmouryCardHandExplanation);

            var fields = data.Equipment.Obj(K.ModFields) ?? new JObject();
            bool IsArmourBlock(ParsedMod m) => m != null && m.Prefix == ReceiptCardKeys.DefendPrefix && m.Field == ReceiptCardKeys.BlockField && m.Add;
            var mods = Js.Items(piece[K.Mods]).Select(Js.Str).ToList();

            // facts and bonuses: the configured gear's resolved points when the profile prices gear, else the authored numbers.
            var gear = CoreRunData.Module(subject, MI.Gear);
            var catalog = (CoreRunData.Content(subject, PC.Gear) as JArray) ?? new JArray();
            var gearRow = gear?.Obj(Ashen.Generated.Core2RunKeys.ArmorBudget) == null ? null : catalog.OfType<JObject>().FirstOrDefault(row =>
                (Js.Truthy(row.Obj(Ashen.Generated.Core2RunKeys.Source)?[K.Id]) ? row.Obj(Ashen.Generated.Core2RunKeys.Source).Str(K.Id) : row.Str(K.Id)) == piece.Str(K.Id)
                && (row.Str(K.Kind) == V.Armor) == armor
                && (!Js.Truthy(row[Ashen.Generated.Core2RunKeys.Classes]) || Js.Includes(row[Ashen.Generated.Core2RunKeys.Classes], piece.Str(K.ClassId))));
            var bonuses = new List<EquipmentCardLine>();
            if (gearRow != null)
            {
                var rank = (Js.Nullish(gearRow[Ashen.Generated.Core2RunKeys.Rank]) ? 1 : gearRow.Num(Ashen.Generated.Core2RunKeys.Rank)) + level;
                var receipt = CoreGear.ResolveItem(d, gearRow, gear, rank);
                var tierAr = !armor && kind != ReceiptCardKeys.ShieldKind ? level * Js.Or0(CoreRunData.Module(subject, MI.Equipment)?.Obj(ReceiptCardKeys.UpgradeBonuses)?[Ashen.Generated.Core2CombatKeys.WeaponArPerTier]) : 0d;
                var points = receipt.Obj(Ashen.Generated.Core2RunKeys.Points);
                var pointValues = gear.Obj(Ashen.Generated.Core2RunKeys.PointValues);
                foreach (var p in receipt.Obj(Ashen.Generated.Core2RunKeys.Values).Properties())
                {
                    var id = p.Name;
                    var value = Js.D(p.Value) + (id == ReceiptCardKeys.ArFact ? tierAr : 0d);
                    if (value == 0) continue;
                    var label = Label(strings, id);
                    view.Facts.Add(new EquipmentCardLine
                    {
                        Label = label,
                        Value = Num(value),
                        Explanation = id == ReceiptCardKeys.ArFact && tierAr != 0
                            ? Fill(strings, StringKeys.ArmouryCardGearTier, (UiPlaceholders.Points, Num(Js.D(points[id]))), (UiPlaceholders.Value, Num(Js.D(pointValues[id]))), (ReceiptPlaceholders.Tier, Num(tierAr)),
                                (UiPlaceholders.Level, Num(level)), (UiPlaceholders.Total, Num(value)))
                            : Fill(strings, StringKeys.ArmouryCardGear, (UiPlaceholders.Points, Num(Js.D(points[id]))), (UiPlaceholders.Value, Num(Js.D(pointValues[id]))), (UiPlaceholders.Total, Num(value)), (UiPlaceholders.Label, label)),
                    });
                }
                var total = Js.Nullish(receipt[Ashen.Generated.Core2RunKeys.Budget]) ? points.Properties().Sum(p => Js.D(p.Value)) + receipt.Num(Ashen.Generated.Core2RunKeys.TagCost) : receipt.Num(Ashen.Generated.Core2RunKeys.Budget);
                var perRank = Js.Or0(gear.Obj(Ashen.Generated.Core2RunKeys.ArmorBudget)?[Ashen.Generated.Core2RunKeys.PerRank]);
                if (armor || kind == ReceiptCardKeys.ShieldKind)
                    bonuses.Add(new EquipmentCardLine
                    {
                        Label = Fill(strings, StringKeys.ArmouryCardRank, (ReceiptPlaceholders.Rank, Num(receipt.Num(Ashen.Generated.Core2RunKeys.Rank))), (UiPlaceholders.Points, Num(total))),
                        Value = Num(total),
                        Explanation = armor
                            ? Fill(strings, StringKeys.ArmouryCardBudget, (ReceiptPlaceholders.Budget, Num(receipt.Num(Ashen.Generated.Core2RunKeys.Budget))), (UiPlaceholders.Base, Num(gear.Obj(Ashen.Generated.Core2RunKeys.ArmorBudget).Num(K.Baseline))),
                                (UiPlaceholders.Bonus, Num(receipt.Num(Ashen.Generated.Core2RunKeys.Budget) - gear.Obj(Ashen.Generated.Core2RunKeys.ArmorBudget).Num(K.Baseline))))
                            : Fill(strings, StringKeys.ArmouryCardStatPoints, (UiPlaceholders.Points, Num(total)), (UiPlaceholders.Amount, Num(perRank))),
                    });
            }
            else
            {
                if (armor)
                {
                    var block = mods.Select(m => Loadout.ParseMod(m)).Where(IsArmourBlock).Sum(m => m.Value);
                    view.Facts.Add(new EquipmentCardLine { Label = Label(strings, ReceiptCardKeys.DrFact), Value = Num(block), Explanation = strings.Get(StringKeys.ArmouryCardExplainDr) });
                    view.Facts.Add(new EquipmentCardLine { Label = Label(strings, ReceiptCardKeys.PoiseFact), Value = Num(Js.Or0(piece[K.PoiseThreshold])), Explanation = strings.Get(StringKeys.ArmouryCardExplainPoise) });
                }
                else
                {
                    view.Facts.Add(new EquipmentCardLine { Label = Label(strings, ReceiptCardKeys.AttackFact), Value = Num(Js.Or0(piece[K.AttackRating])), Explanation = strings.Get(StringKeys.ArmouryCardExplainAttack) });
                    view.Facts.Add(new EquipmentCardLine { Label = Label(strings, ReceiptCardKeys.DefenseFact), Value = Num(Js.Or0(piece[K.DefenseRating])), Explanation = strings.Get(StringKeys.ArmouryCardExplainDefense) });
                    view.Facts.Add(new EquipmentCardLine { Label = Label(strings, ReceiptCardKeys.WeightFact), Value = Num(Equipment.PieceWeight(data, piece)), Explanation = strings.Get(StringKeys.ArmouryCardExplainWeight) });
                }
                foreach (var raw in mods)
                {
                    var mod = Loadout.ParseMod(raw);
                    if (armor && IsArmourBlock(mod)) continue;
                    var spec = mod == null ? null : fields.Obj(mod.Field);
                    if (spec == null) throw new InvalidOperationException(raw);
                    bonuses.Add(Bonus(strings, mod, spec));
                }
            }

            // the possession variant: the hand's weapon arts or the armour's modifiers headline the block.
            var modLines = bonuses;
            var isArmour = armor || Js.Items(piece[TagKeys.ItemTypeTags]).Any(t => Js.Str(t) == ReceiptCardKeys.ArmourItemType);
            var isWeapon = !isArmour && Js.IsStr(piece[K.Hand]) && piece.Str(K.Hand).Length > 0;
            if (isWeapon)
            {
                var handKey = string.Format(CultureInfo.InvariantCulture, ReceiptCardKeys.HandKey, piece.Str(K.Hand));
                var hand = strings.Has(handKey) ? strings.Get(handKey) : null;
                if (requirements.Count > 0) view.Requirement = Fill(strings, StringKeys.ArmouryCardRequires, (ReceiptPlaceholders.List, requirementText));
                else if (hand != null) view.Requirement = hand;
                view.Heading = strings.Get(StringKeys.ArmouryCardHeadingPackage);
                view.Empty = strings.Get(StringKeys.ArmouryCardNoPackage);
                foreach (var cardId in Js.Items(piece.Obj(K.WeaponCardPackage)?[RK.WeaponArtDefaults]).Select(Js.Str))
                {
                    var name = cardId != null && data.Cards.Has(cardId) ? data.Cards.Get(cardId).Str(K.Name) : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    view.Effects.Add(new EquipmentCardLine { Label = Fill(strings, StringKeys.ArmouryCardWeaponArt, (UiPlaceholders.Name, name)), Explanation = strings.Get(StringKeys.ArmouryCardWeaponArtExplanation) });
                }
                view.Effects.AddRange(modLines);
            }
            else if (isArmour)
            {
                view.Heading = strings.Get(StringKeys.ArmouryCardHeadingModifiers);
                view.Empty = strings.Get(StringKeys.ArmouryCardNoModifiers);
                view.Effects.AddRange(modLines);
            }
            else
            {
                view.Heading = strings.Get(StringKeys.ArmouryCardHeadingBonuses);
                view.Empty = strings.Get(StringKeys.ArmouryCardNoBonuses);
                view.Effects.AddRange(modLines);
            }
            if (armor) view.Empty = strings.Get(StringKeys.ArmouryCardNoAdditional);
            return view;
        }

        /// <summary>
        /// equipmentCardArt(piece): an armament's icon by its inventory art key (else its id); an armour set's own icon when it names one, else its painted outfit's menu pose (the set every class shares, else
        /// the wearer's art class and key, else the class's own outfit). Null when the art release holds none of them.
        /// </summary>
        public static string ArtId(Core2RunSession session, UiData ui, JObject piece)
        {
            var layout = ui.EquipmentCard;
            var art = ui.Art;
            var inventoryKey = piece.Str(ReceiptCardKeys.InventoryArtKey);
            if (piece.Str(K.Kind) != V.Armor)
            {
                var id = StringTable.Fill(layout.ArtIcon, new StringArgs().Add(UiPlaceholders.Art, string.IsNullOrEmpty(inventoryKey) ? piece.Str(K.Id) : inventoryKey));
                return art.Has(id) ? id : null;
            }
            if (!string.IsNullOrEmpty(inventoryKey))
            {
                var icon = StringTable.Fill(layout.ArtIcon, new StringArgs().Add(UiPlaceholders.Art, inventoryKey));
                if (art.Has(icon)) return icon;
            }
            var classId = piece.Str(K.ClassId) ?? session.ClassId;
            var shared = StringTable.Fill(layout.ArtShared, new StringArgs().Add(UiPlaceholders.Class, classId).Add(UiPlaceholders.Id, piece.Str(K.Id)));
            if (art.Has(shared)) return shared;
            var artClass = string.IsNullOrEmpty(piece.Str(ReceiptCardKeys.ArtClassId)) ? classId : piece.Str(ReceiptCardKeys.ArtClassId);
            var key = string.IsNullOrEmpty(piece.Str(ArmouryKeys.ArtKey)) ? piece.Str(K.Id) : piece.Str(ArmouryKeys.ArtKey);
            var outfit = key == ReceiptCardKeys.DefaultKey ? artClass : artClass + ReceiptCardKeys.OutfitJoin + key;
            var id2 = StringTable.Fill(layout.ArtOutfit, new StringArgs().Add(ReceiptPlaceholders.Key, outfit));
            if (art.Has(id2)) return id2;
            var own = StringTable.Fill(layout.ArtOutfit, new StringArgs().Add(ReceiptPlaceholders.Key, artClass));
            return art.Has(own) ? own : null;
        }

        private static string Label(StringTable strings, string id)
        {
            var key = string.Format(CultureInfo.InvariantCulture, ReceiptCardKeys.FactKey, id);
            return strings.Has(key) ? strings.Get(key) : id;
        }

        /// <summary>One authored mod worded as the web words it: "Class power: +2 potency", "Max HP +10", "starting Bleed +1".</summary>
        private static EquipmentCardLine Bonus(StringTable strings, ParsedMod mod, JObject spec)
        {
            var target = mod.Prefix == ReceiptCardKeys.SelfPrefix ? string.Empty : mod.Prefix == ReceiptCardKeys.PowerPrefix ? strings.Get(StringKeys.ArmouryCardClassPower)
                : Fill(strings, StringKeys.ArmouryCardTarget, (UiPlaceholders.Name, mod.Prefix.Substring(0, 1).ToUpperInvariant() + mod.Prefix.Substring(1)));
            var value = mod.Add ? (mod.Value >= 0 ? RunValues.Plus : string.Empty) + Num(mod.Value) : Fill(strings, StringKeys.ArmouryCardSetTo, (UiPlaceholders.Value, Num(mod.Value)));
            var label = spec.Str(K.Apply) == ReceiptCardKeys.StartStatusApply ? Fill(strings, StringKeys.ArmouryCardStarting, (UiPlaceholders.Label, spec.Str(K.Label)))
                : mod.Field == ReceiptCardKeys.PoiseField ? strings.Get(StringKeys.ArmouryCardPoiseDamage) : spec.Str(K.Label);
            var where = mod.Prefix == ReceiptCardKeys.SelfPrefix ? strings.Get(StringKeys.ArmouryCardOnWearer)
                : mod.Prefix == ReceiptCardKeys.PowerPrefix ? strings.Get(StringKeys.ArmouryCardOnPower) : Fill(strings, StringKeys.ArmouryCardOnTarget, (UiPlaceholders.Name, mod.Prefix));
            return new EquipmentCardLine
            {
                Label = target + value + ArmouryKeys.Space + label,
                Explanation = Fill(strings, StringKeys.ArmouryCardModExplanation, (ReceiptPlaceholders.Verb, strings.Get(mod.Add ? StringKeys.ArmouryCardAdjusts : StringKeys.ArmouryCardReplaces)),
                    (ReceiptPlaceholders.Where, where), (ReceiptPlaceholders.Blurb, spec.Str(ArmouryKeys.Blurb) ?? string.Empty)),
            };
        }
    }
}
