using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RV = Ashen.Generated.Core2RewardsValues;
using V = Ashen.Generated.CombatValues;

namespace Ashen.App.Run
{
    /// <summary>One piece of the review's kit row (web creation review: ARMOUR, MAIN HAND, RELIC): its heading and the face of what the class starts with.</summary>
    public sealed class CreationPiece
    {
        public string Heading;
        public Core2ItemFace Face;
        public ArmouryItemView Item;
    }

    /// <summary>A starting-deck card with its copies ("x4" under the face).</summary>
    public sealed class CreationDeckCard
    {
        public Core2CardView Card;
        public int Count;
    }

    /// <summary>The Review pane of W-04 for a class (web src/ui/screens/creation: the character card, the armour, main hand and relic it starts with, the starting deck).</summary>
    public sealed class CreationReviewView
    {
        public string ClassId;
        public string ClassName;

        /// <summary>The character card's gold name (the class's default character name).</summary>
        public string Epithet;

        public readonly List<KeyValuePair<string, string>> Attributes = new List<KeyValuePair<string, string>>();
        public readonly List<KeyValuePair<string, string>> Resources = new List<KeyValuePair<string, string>>();

        /// <summary>The class card's figures in the web's order (HP, SP, MP, hand, draw).</summary>
        public readonly List<KeyValuePair<string, string>> ClassStats = new List<KeyValuePair<string, string>>();

        /// <summary>The name of the relic the class starts with (the class card's link).</summary>
        public string RelicName;
        public CreationPiece Armour;
        public CreationPiece MainHand;
        public CreationPiece Relic;
        public readonly List<CreationDeckCard> Deck = new List<CreationDeckCard>();
        public int DeckSize;

        /// <summary>"Begin as Reaver with Forsaken Medallion." (the Begin button's tooltip).</summary>
        public string BeginHint;
    }

    /// <summary>
    /// The Review pane's data, read from the run the class would start with (<see cref="Core2RunSession.Preview"/>): nothing is written. Engine-free.
    /// </summary>
    public static class Core2CreationReview
    {
        public static CreationReviewView Build(Core2RunSession preview, UiData ui)
        {
            var strings = ui.Strings;
            var classId = preview.ClassId;
            var className = RunHudView.ClassName(ui, classId);
            var view = new CreationReviewView { ClassId = classId, ClassName = className, Epithet = strings.Get(StringKeys.CreationEpithet) };
            var run = preview.Run;

            foreach (var attribute in run.Obj(K.Attributes)?.Properties() ?? Enumerable.Empty<JProperty>())
                view.Attributes.Add(new KeyValuePair<string, string>(strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.AttributeShortKey, attribute.Name)), Int(attribute.Value.Value<double>())));
            var hp = run.Num(K.MaxHp);
            var mp = run.Num(K.MaxMana);
            var sp = run.Num(K.MaxStamina);
            var hand = run.Num(K.HandMax);
            var draw = run.Num(K.DrawPerTurn);
            var size = Js.Items(run[K.Deck]).Count();
            string Label(string key) => strings.Get(key);
            void Add(List<KeyValuePair<string, string>> list, string key, double value) => list.Add(new KeyValuePair<string, string>(Label(key), Int(value)));
            Add(view.Resources, StringKeys.ArmouryStatHp, hp);
            Add(view.Resources, StringKeys.ArmouryStatMp, mp);
            Add(view.Resources, StringKeys.ArmouryStatSp, sp);
            Add(view.Resources, StringKeys.CreationReviewHand, hand);
            Add(view.Resources, StringKeys.ArmouryStatDraw, draw);
            Add(view.Resources, StringKeys.CreationReviewSize, size);
            Add(view.ClassStats, StringKeys.CreationStatHp, hp);
            Add(view.ClassStats, StringKeys.CreationStatSp, sp);
            Add(view.ClassStats, StringKeys.CreationStatMp, mp);
            Add(view.ClassStats, StringKeys.CreationStatHand, hand);
            Add(view.ClassStats, StringKeys.CreationStatDraw, draw);

            var data = preview.CombatData;
            var slots = Js.Items(data.Equipment[K.Slots]).OfType<JObject>().ToDictionary(s => s.Str(K.Id), s => s);
            var positions = ArmouryPanes.Equipment(preview, ui).Where(p => !p.Empty && !p.Locked && p.Item != null).ToList();
            var armour = positions.FirstOrDefault(p => slots.TryGetValue(p.SlotId, out var slot) && !IsHand(slot));
            var main = positions.FirstOrDefault(p => slots.TryGetValue(p.SlotId, out var slot) && slot.Str(K.Hand) == V.Right);
            if (armour != null) view.Armour = Piece(preview, ui, StringKeys.CreationReviewArmour, RV.KindArmor, armour.Item);
            if (main != null) view.MainHand = Piece(preview, ui, StringKeys.CreationReviewMainHand, RV.KindWeapons, main.Item);
            var relicId = Js.Items(run[K.Relics]).Select(Js.Str).FirstOrDefault(id => id != null);
            if (relicId != null)
            {
                var name = data.Relics.Has(relicId) ? data.Relics.Get(relicId).Str(K.Name) ?? relicId : relicId;
                view.RelicName = name;
                view.Relic = new CreationPiece { Heading = strings.Get(StringKeys.CreationReviewRelic), Face = Core2ItemFaces.Build(preview, ui, RV.KindRelics, relicId, name) };
            }

            var deck = Js.Items(run[K.Deck]).OfType<JObject>().ToList();
            var words = CardWords.Outside(preview.Core.Combat, ui.CardText);
            view.DeckSize = deck.Count;
            foreach (var group in deck.GroupBy(c => c.Str(K.CardId) + Js.Truthy(c[K.Upgraded])))
                view.Deck.Add(new CreationDeckCard { Card = Core2DeckCards.View(data, group.First(), words), Count = group.Count() });

            view.BeginHint = strings.Format(StringKeys.CreationReviewBeginHint, new StringArgs()
                .Add(UiPlaceholders.Class, className).Add(UiPlaceholders.Name, view.Relic?.Face.Name ?? view.Epithet));
            return view;
        }

        private static CreationPiece Piece(Core2RunSession session, UiData ui, string headingKey, string kind, ArmouryItemView item) =>
            new CreationPiece { Heading = ui.Strings.Get(headingKey), Item = item, Face = Core2ItemFaces.Build(session, ui, kind, item.Id, item.Name) };

        private static bool IsHand(JObject slot) => slot.Str(K.Hand) == V.Right || slot.Str(K.Hand) == V.Left;

        private static string Int(double value) => ((int)Math.Floor(value)).ToString(CultureInfo.InvariantCulture);
    }
}
