using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using DK = Ashen.Generated.Core2DeckKeys;
using K = Ashen.Generated.CombatKeys;
using RV = Ashen.Generated.Core2RewardsValues;

namespace Ashen.App.Run
{
    /// <summary>
    /// One thing a Core 2 screen shows as a face (VP-7 spoils, VP-9 shop): a card (the VP-3 face's data), or a collectible (relic, flask, armament, armour) with its picture, one line of what it
    /// is, and, for an armament or armour, the numbers and the card package it grants (the web's inspect panel).
    /// </summary>
    public sealed class Core2ItemFace
    {
        public string Kind;
        public string Id;
        public string Name;

        /// <summary>"Card", "Relic", "Flask", "Armament", "Armour", "Weapon art".</summary>
        public string KindLabel;

        /// <summary>What it does or is, in the web's words (the card's text, a relic's or flask's sentence, an armament's blurb).</summary>
        public string Effect = string.Empty;

        public bool IsCard;

        /// <summary>The registry art id of a collectible's picture, or null for a card or a thing with no art.</summary>
        public string ArtId;

        public RewardPickView Card;

        /// <summary>An armament's or armour's numbers (the Armoury's stat rows: label, value).</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Stats = Array.Empty<KeyValuePair<string, string>>();

        /// <summary>"Granted card package" and "Weapon art: Blight Touch" for an armament that grants one; empty otherwise.</summary>
        public string PackageHeading = string.Empty;

        public string PackageLine = string.Empty;
    }

    /// <summary>The faces of the things a run can be offered (web ui/components card, equipmentCard, collectibleCard; Core 2 data), built from the registries once for the spoils door and the shop.</summary>
    public static class Core2ItemFaces
    {
        public static Core2ItemFace Build(Core2RunSession session, UiData ui, string kind, string id, string name)
        {
            var strings = ui.Strings;
            var face = new Core2ItemFace
            {
                Kind = kind,
                Id = id,
                Name = name,
                KindLabel = strings.Get(string.Format(CultureInfo.InvariantCulture, SpoilsFormats.KindKey, kind)),
                IsCard = kind == RV.KindCombatCards || kind == RV.KindWeaponArts,
            };
            var data = session.CombatData;
            var components = ui.Components;
            try
            {
                switch (kind)
                {
                    case RV.KindCombatCards:
                    case RV.KindWeaponArts:
                        face.Card = RewardsView.CardPick(data, strings, components.CostKinds, id, false);
                        face.Effect = face.Card.Text;
                        break;
                    case RV.KindRelics:
                    {
                        var def = data.Relics.Has(id) ? data.Relics.Get(id) : new JObject();
                        face.Effect = RewardsView.Sentence(data, def.Str(K.TextTemplate), RewardsView.RelicTokens(data, def, components));
                        face.ArtId = StringTable.Fill(components.RewardRelicArt, new StringArgs().Add(UiPlaceholders.Id, id));
                        break;
                    }
                    case RV.KindPotions:
                    {
                        var def = data.Flasks.Has(id) ? data.Flasks.Get(id) : new JObject();
                        face.Effect = RewardsView.Sentence(data, def.Str(K.TextTemplate), RewardsView.EffectTokens(data, def[K.Effects]));
                        face.ArtId = RewardsView.ArtFromKey(components.RewardFlaskArt, def.Str(RunFlowKeys.ArtKey), components.RewardFlaskArtPrefix);
                        break;
                    }
                    case RV.KindWeapons:
                    {
                        var def = session.Core.Rewards.RunData.EquipmentRows(DK.Armaments).FirstOrDefault(r => r.Str(DK.Id) == id) ?? new JObject();
                        face.Effect = def.Str(RunFlowKeys.Blurb);
                        face.ArtId = RewardsView.ArtFromKey(components.RewardArmamentArt, def.Str(RunFlowKeys.ArtKey) ?? id, null);
                        Detail(session, ui, face, def);
                        break;
                    }
                    case RV.KindArmor:
                    {
                        var def = session.Core.Rewards.RunData.EquipmentRows(DK.Armour).FirstOrDefault(r => r.Str(DK.Id) == id && r.Str(K.ClassId) == session.ClassId) ?? new JObject();
                        // Armour has no icon of its own in the art release (the web draws a painted outfit): the screen falls back to the kind's icon.
                        face.Effect = def.Str(RunFlowKeys.Blurb);
                        Detail(session, ui, face, def);
                        break;
                    }
                }
            }
            catch (Exception e) when (e is InvalidOperationException || e is NullReferenceException || e is KeyNotFoundException || e is FormatException)
            {
                // A row the display helpers cannot read still shows its name: the claim is the rules' business, not the picture's.
            }
            face.Effect = face.Effect ?? string.Empty;
            if (face.IsCard && face.Card == null) face.Card = new RewardPickView { Id = id, Name = name, Text = string.Empty };
            return face;
        }

        /// <summary>The numbers of an armament or armour and the weapon art its card package grants.</summary>
        private static void Detail(Core2RunSession session, UiData ui, Core2ItemFace face, JObject def)
        {
            var item = ArmouryPanes.Item(session, ui.Strings, null, face.Id);
            face.Stats = item.Stats;
            var art = def.Obj(K.WeaponCardPackage)?.Arr(RunKeys.WeaponArtDefaults)?.FirstOrDefault();
            var cardId = art == null ? null : Js.Str(art);
            if (string.IsNullOrEmpty(cardId)) return;
            var data = session.CombatData;
            var cardName = data.Cards.Has(cardId) ? data.Cards.Get(cardId).Str(K.Name) ?? cardId : cardId;
            face.PackageHeading = ui.Strings.Get(StringKeys.Core2ShopPackage);
            face.PackageLine = ui.Strings.Format(StringKeys.Core2ShopPackageArt, new StringArgs().Add(UiPlaceholders.Name, cardName));
        }
    }
}
