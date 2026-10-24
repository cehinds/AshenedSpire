using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using DK = Ashen.Generated.Core2DeckKeys;
using DM = Ashen.Generated.Core2DeckMessages;
using EN = Ashen.Generated.Core2DeckEditorNumbers;
using EV = Ashen.Generated.Core2DeckEditorValues;
using K = Ashen.Generated.CombatKeys;
using TF = Ashen.Generated.Core2DeckTextFormats;
using V = Ashen.Generated.CombatValues;
using TX = Ashen.Generated.Core2DeckText;

namespace Ashen.Domain.Run
{
    /// <summary>The sentences the deck editor says: a string-table key and its named arguments (web ui/strings.js t(id, tokens)).</summary>
    public interface IDeckEditorText
    {
        bool Has(string key);

        string Say(string key, params KeyValuePair<string, string>[] args);
    }

    /// <summary>A refusal or a label as the editor states it: the resolved sentence, and the string key and arguments it came from (empty for a literal rule message).</summary>
    public sealed class DeckSentence
    {
        public static readonly DeckSentence None = new DeckSentence(string.Empty, null, null);

        public DeckSentence(string text, string key, KeyValuePair<string, string>[] args)
        {
            Text = text ?? string.Empty;
            Key = key;
            Args = args ?? new KeyValuePair<string, string>[0];
        }

        public string Text { get; }

        /// <summary>The string-table key, or null for a message the rules wrote in English (the bounds, range mechanics off).</summary>
        public string Key { get; }

        public KeyValuePair<string, string>[] Args { get; }

        public bool IsEmpty => Text.Length == 0;

        public override string ToString() => Text;
    }

    /// <summary>The answer of one editor operation (web: <c>{ ok, refusal }</c>).</summary>
    public sealed class DeckEditResult
    {
        public DeckEditResult(bool ok, DeckSentence refusal = null)
        {
            Ok = ok;
            Refusal = refusal ?? DeckSentence.None;
        }

        public bool Ok { get; }
        public DeckSentence Refusal { get; }
    }

    /// <summary>Which doors open the editor (web deckEditorDoors).</summary>
    public readonly struct DeckDoors
    {
        public DeckDoors(bool armoury, bool rest)
        {
            Armoury = armoury;
            Rest = rest;
        }

        public bool Armoury { get; }
        public bool Rest { get; }
    }

    /// <summary>The view's filter and sort state (web deckEditorView): every group is a list of active chip ids; groups AND, chips inside a group OR.</summary>
    public sealed class DeckEditorView
    {
        public IReadOnlyList<string> Type = new string[0];
        public IReadOnlyList<string> Cost = new string[0];
        public IReadOnlyList<string> Source = new string[0];
        public bool Upgraded;
        public string Sort = EV.SortCost;

        /// <summary>deckEditorView(view): an unknown sort reads as the cost sort.</summary>
        public DeckEditorView Normalized() => new DeckEditorView
        {
            Type = Type ?? new string[0],
            Cost = Cost ?? new string[0],
            Source = Source ?? new string[0],
            Upgraded = Upgraded,
            Sort = DeckEditor.Sorts.Contains(Sort) ? Sort : EV.SortCost,
        };
    }

    /// <summary>A card as the editor shows it (web <c>describe</c>; the card category waits for the combat profile port).</summary>
    public class DeckCardRow
    {
        public JObject Ref;
        public string CardId;
        public string Name;

        /// <summary>The card's cost as the registry writes it (a number, or a word such as X), or null when it has none.</summary>
        public JToken CostRaw;

        /// <summary>The cost as a number, or NaN when it is not one.</summary>
        public double Cost;
        public string CostBucket;
        public string Type;
        public string Source;
        public bool Upgraded;
    }

    /// <summary>One row of the deck pane: one card, or (unordered) the copies of one variant with a count.</summary>
    public sealed class DeckRow : DeckCardRow
    {
        public string InstanceId;
        public string TileKey;
        public int Index;
        public bool Locked;
        public string GroupKey;
        public IReadOnlyList<string> InstanceIds;
        public int Count;
        public string CountText;
        public bool Removable;
        public bool CanUp;
        public bool CanDown;
    }

    /// <summary>One tile of the collection: a card variant the run owns, or a basic it may mint.</summary>
    public sealed class DeckTile : DeckCardRow
    {
        public string Key;
        public bool Unlimited;

        /// <summary>Copies owned (PositiveInfinity for a mint tile).</summary>
        public double Owned;
        public int InDeck;
        public string CountText;
        public bool Addable;
        public DeckSentence Refusal = DeckSentence.None;

        /// <summary>Whether the equipment gate lets a copy in; null on a basic's tile (the web states no such field there).</summary>
        public bool? EquipmentEligible;

        /// <summary>The last copy in the deck this tile's minus takes out, or null.</summary>
        public string RemoveInstanceId;

        public bool Removable;
    }

    public sealed class DeckCounter
    {
        public int Count;
        public double Min;
        public double Max;
        public string Text;
        public bool OutOfBounds;
        public string Refusal;
    }

    public sealed class DeckCurveBar
    {
        public string Bucket;
        public int Count;
        public double Share;
    }

    public sealed class DeckChip
    {
        public string Id;
        public string Label;
        public bool On;
    }

    /// <summary>The editor's projection (web deckEditorModel): the live counter and its refusal, the cost curve, the deck rows and the collection tiles, each carrying whether it may be added or removed and why not.</summary>
    public sealed class DeckEditorModelData
    {
        public DeckCounter Counter;
        public bool DoneDisabled;
        public string DoneRefusal;
        public IReadOnlyList<DeckCurveBar> Curve;
        public bool Ordered;
        public IReadOnlyList<DeckRow> Deck;
        public IReadOnlyList<DeckTile> Collection;
        public IReadOnlyList<DeckChip> FilterType;
        public IReadOnlyList<DeckChip> FilterCost;
        public IReadOnlyList<DeckChip> FilterSource;
        public DeckChip FilterUpgraded;
        public IReadOnlyList<DeckChip> Sorts;
        public DeckEditorView View;
    }

    /// <summary>
    /// The deck editor (docs/design/11 WP-18; web ui/models/DeckEditorModel.js, the engine-facing halves). <see cref="Model"/> is the projection;
    /// <see cref="DeckEditSession"/> is the session: it takes the snapshot when the editor opens, applies each add, remove and move
    /// through <see cref="DeckRules"/> (the one set of rules), and either confirms (only inside the bounds) or cancels (everything the
    /// session can change put back exactly). Nothing here decides a rule. Engine-free; the words come from an <see cref="IDeckEditorText"/>.
    /// </summary>
    public static class DeckEditor
    {
        public static readonly IReadOnlyList<string> CostBuckets = new[] { EV.Bucket0, EV.Bucket1, EV.Bucket2, EV.Bucket3, EV.Bucket4, EV.Bucket5Plus, EV.BucketX };
        public static readonly IReadOnlyList<string> Sources = new[] { EV.SourceBasic, EV.SourceArt, EV.SourceTechnique, EV.SourceReward, EV.SourceItem };
        public static readonly IReadOnlyList<string> Sorts = new[] { EV.SortCost, EV.SortName, EV.SortType, EV.SortSource };
        public static readonly IReadOnlyList<string> Panes = new[] { EV.PaneCollection, EV.PaneDeck };

        private static KeyValuePair<string, string> A(string name, string value) => new KeyValuePair<string, string>(name, value);

        private static DeckSentence Say(IDeckEditorText t, string key, params KeyValuePair<string, string>[] args) => new DeckSentence(t.Say(key, args), key, args);

        // ------------------------------------------------------------------ keys and rows

        /// <summary>deckVariantKey(card): the collection key of a card's variant (its id, upgrade, mods, lender and rank).</summary>
        public static string VariantKey(JObject card)
        {
            var mods = card[DK.Mods] is JArray list ? string.Join(EV.ModsSeparator, list.Select(m => RunJs.Key(m))) : string.Empty;
            var rank = Js.IsInt(card[DK.Rank]) && card.Num(DK.Rank) > 1 ? EV.RankMark + RunJs.NumStr(card.Num(DK.Rank)) : string.Empty;
            var lender = Js.Truthy(card[DK.GrantedBy]) ? EV.VariantSeparator + RunJs.Key(card[DK.GrantedBy]) : string.Empty;
            return RunJs.Key(card[DK.CardId]) + EV.VariantSeparator + (Js.Truthy(card[DK.Upgraded]) ? EV.UpgradedMark : string.Empty) + EV.VariantSeparator + mods + lender + rank;
        }

        private static string CostBucket(double cost)
        {
            if (double.IsNaN(cost) || double.IsInfinity(cost) || cost < 0) return EV.BucketX;
            return cost >= EN.CostCap ? EV.Bucket5Plus : RunJs.NumStr(cost);
        }

        private static string SourceOf(CoreDeckData d, JObject card, JObject def, JObject run)
        {
            if (Js.Truthy(card[DK.GrantedBy]) || StartingDeck.IsItemOwned(d.Run.Run, card)) return EV.SourceItem;
            if (DeckRules.IsBasicCard(d, card, run)) return EV.SourceBasic;
            var tags = Js.Items(def[DK.Tags]).Select(Js.Str).ToList();
            if (tags.Contains(EV.ArtTag)) return EV.SourceArt;
            if (tags.Any(tag => tag != null && tag.StartsWith(EV.TechniqueTagPrefix, StringComparison.Ordinal))) return EV.SourceTechnique;
            return EV.SourceReward;
        }

        private static T Describe<T>(CoreDeckData d, JObject card, JObject run) where T : DeckCardRow, new()
        {
            var def = Cards.Resolve(d.Run.Run.Combat, card);
            return new T
            {
                Ref = (JObject)card.DeepClone(),
                CardId = card.Str(DK.CardId),
                Name = def.Str(K.Name),
                CostRaw = def[K.Cost]?.DeepClone(),
                Cost = Js.IsNum(def[K.Cost]) ? def.Num(K.Cost) : double.NaN,
                CostBucket = CostBucket(Js.IsNum(def[K.Cost]) ? def.Num(K.Cost) : double.NaN),
                Type = def.Str(DK.Type),
                Source = SourceOf(d, card, def, run),
                Upgraded = Js.Truthy(card[DK.Upgraded]),
            };
        }

        private static string NameOf(CoreDeckData d, JObject card) => Cards.Resolve(d.Run.Run.Combat, card).Str(K.Name);

        // ------------------------------------------------------------------ filters and sorts

        private static bool Passes(DeckCardRow row, DeckEditorView view) =>
            (view.Type.Count == 0 || view.Type.Contains(row.Type))
            && (view.Cost.Count == 0 || view.Cost.Contains(row.CostBucket))
            && (view.Source.Count == 0 || view.Source.Contains(row.Source))
            && (!view.Upgraded || row.Upgraded);

        /// <summary>The web's <c>a || b</c> over numbers: the second when the first is 0 or NaN.</summary>
        private static double Or(double first, Func<double> second) => first == 0 || double.IsNaN(first) ? second() : first;

        private static Comparison<DeckCardRow> Sorter(string sort)
        {
            int Bucket(DeckCardRow row) => CostBuckets.ToList().IndexOf(row.CostBucket);
            double Cost(DeckCardRow a, DeckCardRow b) => Or(Bucket(a) - Bucket(b), () => a.Cost - b.Cost);
            int Sign(double x) => x < 0 ? -1 : x > 0 ? 1 : 0;
            switch (sort)
            {
                case EV.SortName:
                    return (a, b) => { var n = LocaleCompare(a.Name, b.Name); return n != 0 ? n : Bucket(a) - Bucket(b); };
                case EV.SortType:
                    return (a, b) => { var n = LocaleCompare(a.Type ?? V.Undefined, b.Type ?? V.Undefined); return n != 0 ? n : LocaleCompare(a.Name, b.Name); };
                case EV.SortSource:
                    return (a, b) => { var n = Sources.ToList().IndexOf(a.Source) - Sources.ToList().IndexOf(b.Source); return n != 0 ? n : LocaleCompare(a.Name, b.Name); };
                default:
                    return (a, b) => { var n = Sign(Or(Cost(a, b), () => LocaleCompare(a.Name, b.Name))); return n; };
            }
        }

        /// <summary>A stable sort with a JS-style comparator.</summary>
        private static List<T> SortBy<T>(IEnumerable<T> items, Comparison<DeckCardRow> compare) where T : DeckCardRow =>
            items.OrderBy(x => x, Comparer<T>.Create((a, b) => compare(a, b))).ToList();

        /// <summary>
        /// String.prototype.localeCompare for the card names the editor sorts (ICU root order, as the browser and node give it): punctuation,
        /// then digits, then letters, the letters by base letter ignoring case and accent; ties fall to the accent, then lower case before upper.
        /// </summary>
        public static int LocaleCompare(string a, string b)
        {
            a = a ?? string.Empty;
            b = b ?? string.Empty;
            var pa = Primary(a);
            var pb = Primary(b);
            var n = Math.Min(pa.Count, pb.Count);
            for (var i = 0; i < n; i++)
                if (pa[i] != pb[i]) return pa[i] < pb[i] ? -1 : 1;
            if (pa.Count != pb.Count) return pa.Count < pb.Count ? -1 : 1;
            var accentA = a.Normalize(NormalizationForm.FormD);
            var accentB = b.Normalize(NormalizationForm.FormD);
            var lowerA = accentA.ToLowerInvariant();
            var lowerB = accentB.ToLowerInvariant();
            var c = string.CompareOrdinal(lowerA, lowerB);
            if (c != 0) return c < 0 ? -1 : 1;
            for (var i = 0; i < Math.Min(accentA.Length, accentB.Length); i++)
                if (accentA[i] != accentB[i]) return char.IsLower(accentA[i]) ? -1 : 1;
            return 0;
        }

        private static List<int> Primary(string s)
        {
            var keys = new List<int>();
            foreach (var ch in s.Normalize(NormalizationForm.FormD))
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category == UnicodeCategory.NonSpacingMark) continue;
                var lower = char.ToLowerInvariant(ch);
                var group = char.IsWhiteSpace(ch) ? 0 : char.IsLetter(ch) ? (int)EN.GroupLetter : char.IsDigit(ch) ? (int)EN.GroupDigit : 1;
                keys.Add(group * (int)EN.KeyShift + lower);
            }
            return keys;
        }

        // ------------------------------------------------------------------ the collection's basics

        private sealed class BasicTile
        {
            public string Key;
            public JObject Card;
            public Func<JObject, bool> Belongs;
            public int InDeck;
            public bool Mint;
            public int Count;
        }

        private static string LastInstanceId(IReadOnlyList<JObject> deck, Func<JObject, bool> belongs)
        {
            for (var at = deck.Count - 1; at >= 0; at--)
                if (deck[at] != null && belongs(deck[at])) return deck[at].Str(DK.InstanceId);
            return null;
        }

        private static List<JObject> Pile(JObject run, string key) => Js.Items(run[key]).OfType<JObject>().ToList();

        private static List<BasicTile> BasicTiles(CoreDeckData d, JObject run)
        {
            var deck = Pile(run, DK.Deck);
            var sideboard = Pile(run, DK.Sideboard);
            bool Fresh(JObject c) => c != null && !Js.Truthy(c[DK.GrantedBy]) && !DeckRules.IsSetAsideBasic(d, c, run);
            var mints = new List<BasicTile>();
            // A Core 2 run is never an equipped run (its cards are configured), so only the plain basics are minted, by card id.
            foreach (var cardId in DeckRules.UnlimitedCardIds(d, run))
            {
                var id = cardId;
                Func<JObject, bool> belongs = c => c != null && !Js.Truthy(c[DK.EquipmentRole]) && !Js.Truthy(c[DK.GrantedBy]) && c.Str(DK.CardId) == id && Fresh(c);
                mints.Add(new BasicTile { Key = EV.KindBasic + EV.KeySeparator + id, Card = new JObject { [DK.CardId] = id }, Belongs = belongs, InDeck = deck.Count(belongs), Mint = true });
            }
            var kept = new List<BasicTile>();
            foreach (var card in deck.Concat(sideboard))
            {
                if (!DeckRules.IsSetAsideBasic(d, card, run)) continue;
                var variant = VariantKey(card);
                if (kept.Any(t => t.Key == EV.KindKept + EV.KeySeparator + variant)) continue;
                Func<JObject, bool> belongs = c => DeckRules.IsSetAsideBasic(d, c, run) && VariantKey(c) == variant;
                kept.Add(new BasicTile { Key = EV.KindKept + EV.KeySeparator + variant, Card = card, InDeck = deck.Count(belongs), Belongs = belongs });
            }
            foreach (var card in sideboard)
                if (DeckRules.IsSetAsideBasic(d, card, run)) kept.First(t => t.Key == EV.KindKept + EV.KeySeparator + VariantKey(card)).Count++;
            mints.AddRange(kept);
            return mints;
        }

        // ------------------------------------------------------------------ the projection

        /// <summary>deckEditorModel({ registries, run, settings, view }).</summary>
        public static DeckEditorModelData Model(CoreDeckData d, IDeckEditorText t, JObject run, JObject settings, DeckEditorView view = null)
        {
            var state = (view ?? new DeckEditorView()).Normalized();
            var deck = Pile(run, DK.Deck);
            var sideboard = Pile(run, DK.Sideboard);
            var count = deck.Count;
            var bounds = DeckRules.EditBounds(d, settings);
            var refusal = DeckRules.EditRefusal(d, count, settings);
            var maxText = double.IsPositiveInfinity(bounds.Max) ? t.Say(TX.Unlimited) : RunJs.NumStr(bounds.Max);
            var ordered = DeckRules.PlayInDeckOrder(d, settings);

            // The curve counts the whole deck, never the filtered rows: it describes the deck the player would confirm.
            var described = deck.Select(card => new { card, row = Describe<DeckRow>(d, card, run) }).ToList();
            var tally = CostBuckets.ToDictionary(b => b, b => 0, StringComparer.Ordinal);
            foreach (var item in described)
            {
                tally.TryGetValue(item.row.CostBucket, out var n);
                tally[item.row.CostBucket] = n + 1;
            }
            var peak = Math.Max(1, tally.Values.Max());
            var curve = CostBuckets.Where(b => b != EV.BucketX || tally[EV.BucketX] > 0)
                .Select(b => new DeckCurveBar { Bucket = b, Count = tally[b], Share = (double)tally[b] / peak }).ToList();

            var basics = BasicTiles(d, run);
            var mintTiles = basics.Where(b => b.Mint).ToList();
            string TileKeyFor(JObject card) => DeckRules.IsSetAsideBasic(d, card, run) ? EV.KindKept + EV.KeySeparator + VariantKey(card)
                : mintTiles.FirstOrDefault(b => b.Belongs(card))?.Key ?? EV.KindCard + EV.KeySeparator + VariantKey(card);
            var deckRows = described.Select((item, index) =>
            {
                var row = item.row;
                row.InstanceId = item.card.Str(DK.InstanceId);
                row.TileKey = TileKeyFor(item.card);
                row.Index = index;
                row.Locked = false;
                row.Removable = true;
                return row;
            }).Where(row => Passes(row, state)).ToList();

            // Unordered, the deck list is one row per variant with a x N count (a lent card keeps its owner and role); its minus takes the last copy.
            // Play in deck order shows each copy as its own row so it can be placed.
            if (!ordered)
            {
                var groups = new List<DeckRow>();
                var byKey = new Dictionary<string, DeckRow>(StringComparer.Ordinal);
                foreach (var row in deckRows)
                {
                    var card = deck[row.Index];
                    var key = VariantKey(card) + EV.VariantSeparator + (card.Str(DK.EquipmentRole) ?? string.Empty);
                    if (byKey.TryGetValue(key, out var group))
                    {
                        ((List<string>)group.InstanceIds).Add(row.InstanceId);
                        group.InstanceId = row.InstanceId;
                        group.Index = row.Index;
                    }
                    else
                    {
                        row.GroupKey = key;
                        row.InstanceIds = new List<string> { row.InstanceId };
                        byKey[key] = row;
                        groups.Add(row);
                    }
                }
                deckRows = SortBy(groups, Sorter(state.Sort));
            }
            else
                foreach (var row in deckRows)
                {
                    row.GroupKey = row.InstanceId;
                    row.InstanceIds = new List<string> { row.InstanceId };
                }
            foreach (var row in deckRows)
            {
                row.Count = row.InstanceIds.Count;
                row.CountText = row.Count > 1 ? t.Say(TX.Count, A(TX.ArgCount, RunJs.NumStr(row.Count))) : string.Empty;
                row.CanUp = ordered && row.Index > 0;
                row.CanDown = ordered && row.Index < deck.Count - 1;
            }

            // The collection: the unlimited basics, then every limited card the run owns (deck + sideboard), one tile per variant.
            var tiles = new List<DeckTile>();
            foreach (var basic in basics)
            {
                var row = Describe<DeckTile>(d, basic.Card, run);
                var remove = LastInstanceId(deck, basic.Belongs);
                row.Source = EV.SourceBasic;
                row.Key = basic.Key;
                row.Unlimited = basic.Mint;
                row.Owned = basic.Mint ? double.PositiveInfinity : basic.Count;
                row.InDeck = basic.InDeck;
                row.CountText = basic.Mint ? t.Say(TX.TileBasic, A(TX.ArgInDeck, RunJs.NumStr(basic.InDeck)))
                    : t.Say(Js.Truthy(basic.Card[DK.Upgraded]) ? TX.TileKeptUpgraded : TX.TileKept, A(TX.ArgCount, RunJs.NumStr(basic.Count)));
                row.Addable = basic.Mint || basic.Count > 0;
                row.RemoveInstanceId = remove;
                row.Removable = remove != null;
                tiles.Add(row);
            }
            var limited = new List<KeyValuePair<string, (JObject Card, int Owned, int InDeck, int Loose)>>();
            var limitedAt = new Dictionary<string, int>(StringComparer.Ordinal);
            var inDeckById = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var card in deck) inDeckById[card.Str(DK.CardId) ?? string.Empty] = (inDeckById.TryGetValue(card.Str(DK.CardId) ?? string.Empty, out var n) ? n : 0) + 1;
            void Tally(JObject card, bool inDeck)
            {
                if (card == null || DeckRules.IsUnlimitedBasic(d, card, run)) return;
                var variant = VariantKey(card);
                if (!limitedAt.TryGetValue(variant, out var at))
                {
                    limitedAt[variant] = at = limited.Count;
                    limited.Add(new KeyValuePair<string, (JObject, int, int, int)>(variant, (card, 0, 0, 0)));
                }
                var e = limited[at].Value;
                limited[at] = new KeyValuePair<string, (JObject, int, int, int)>(variant, (e.Card, e.Owned + 1, e.InDeck + (inDeck ? 1 : 0), e.Loose + (inDeck ? 0 : 1)));
            }
            foreach (var card in deck) Tally(card, true);
            foreach (var card in sideboard) Tally(card, false);
            foreach (var pair in limited)
            {
                var variant = pair.Key;
                var entry = pair.Value;
                var row = Describe<DeckTile>(d, entry.Card, run);
                var limit = DeckRules.CopyLimit(d, entry.Card.Str(DK.CardId), settings, run);
                var eligible = DeckRules.CardEquipmentEligible(d, run, entry.Card);
                var tileRefusal = DeckSentence.None;
                var cardName = row.Name;
                if (!DeckRules.RangeCardAvailable(d, run, Cards.Resolve(d.Run.Run.Combat, entry.Card))) tileRefusal = new DeckSentence(DM.RangeDisabled, null, null);
                else if (!eligible) tileRefusal = Say(t, TX.RefuseEquipment);
                else if (entry.Loose == 0) tileRefusal = Say(t, TX.RefuseAllInDeck, A(TX.ArgName, cardName));
                else if ((inDeckById.TryGetValue(entry.Card.Str(DK.CardId) ?? string.Empty, out var have) ? have : 0) >= limit)
                    tileRefusal = Say(t, TX.RefuseCopyLimit, A(TX.ArgName, cardName), A(TX.ArgLimit, RunJs.NumStr(limit)));
                var remove = LastInstanceId(deck, c => VariantKey(c) == variant);
                row.Key = EV.KindCard + EV.KeySeparator + variant;
                row.Unlimited = false;
                row.Owned = entry.Owned;
                row.InDeck = entry.InDeck;
                row.CountText = t.Say(TX.TileOwned, A(TX.ArgOwned, RunJs.NumStr(entry.Owned)), A(TX.ArgInDeck, RunJs.NumStr(entry.InDeck)));
                row.EquipmentEligible = eligible;
                row.Addable = tileRefusal.IsEmpty;
                row.Refusal = tileRefusal;
                row.RemoveInstanceId = remove;
                row.Removable = remove != null;
                tiles.Add(row);
            }
            var collection = tiles.Where(tile => tile.Unlimited && Passes(tile, state)).ToList();
            collection.AddRange(SortBy(tiles.Where(tile => !tile.Unlimited && Passes(tile, state)), Sorter(state.Sort)));

            var typesPresent = described.Select(x => x.row.Type).Concat(tiles.Select(x => x.Type)).Distinct().OrderBy(x => x ?? V.Undefined, StringComparer.Ordinal).ToList();
            var bucketsPresent = CostBuckets.Where(b => b != EV.BucketX || described.Any(x => x.row.CostBucket == EV.BucketX) || tiles.Any(x => x.CostBucket == EV.BucketX)).ToList();
            string TypeLabel(string type)
            {
                var id = RunJs.Fmt(TF.TypeKey, type);
                return t.Has(id) ? t.Say(id) : t.Say(TX.TypeOther);
            }
            List<DeckChip> Chips(IEnumerable<string> ids, IReadOnlyList<string> active, Func<string, string> label) =>
                ids.Select(id => new DeckChip { Id = id, Label = label(id), On = active.Contains(id) }).ToList();

            return new DeckEditorModelData
            {
                Counter = new DeckCounter
                {
                    Count = count,
                    Min = bounds.Min,
                    Max = bounds.Max,
                    Text = t.Say(TX.Counter, A(TX.ArgCount, RunJs.NumStr(count)), A(TX.ArgMin, RunJs.NumStr(bounds.Min)), A(TX.ArgMax, maxText)),
                    OutOfBounds = refusal.Length > 0,
                    Refusal = refusal,
                },
                DoneDisabled = refusal.Length > 0,
                DoneRefusal = refusal,
                Curve = curve,
                Ordered = ordered,
                Deck = deckRows,
                Collection = collection,
                FilterType = Chips(typesPresent, state.Type, TypeLabel),
                FilterCost = Chips(bucketsPresent, state.Cost, bucket => t.Say(TX.FilterCost, A(TX.ArgCost, bucket))),
                FilterSource = Chips(Sources, state.Source, source => t.Say(RunJs.Fmt(TF.SourceKey, source))),
                FilterUpgraded = new DeckChip { Id = TX.FilterUpgraded, Label = t.Say(TX.FilterUpgraded), On = state.Upgraded },
                Sorts = Chips(Sorts, new[] { state.Sort }, id => t.Say(RunJs.Fmt(TF.SortKey, id))),
                View = state,
            };
        }

        // ------------------------------------------------------------------ the doors

        /// <summary>deckEditorDoors({ settings, inCombat, services }): under <c>free</c> the Armoury, out of combat only; under <c>restOnly</c> only a Rest place that offers deck editing; off, none.</summary>
        public static DeckDoors Doors(CoreDeckData d, JObject settings, bool inCombat, bool restOffersDeckEdit)
        {
            var editing = DeckRules.EditingOn(d, settings) && !inCombat;
            var where = DeckRules.EditingWhere(d, settings);
            return new DeckDoors(editing && where == EV.DoorFree, editing && where == EV.DoorRestOnly && restOffersDeckEdit);
        }

        // ------------------------------------------------------------------ the session

        /// <summary>openDeckEdit(registries, run, settings): the editor session over a run document, written in place.</summary>
        public static DeckEditSession Open(CoreDeckData d, IDeckEditorText t, JObject run, JObject settings = null) => new DeckEditSession(d, t, run, settings ?? new JObject());
    }

    public sealed class DeckEditSession
    {
        private readonly CoreDeckData _d;
        private readonly IDeckEditorText _t;
        private readonly JObject _run;
        private readonly JObject _settings;
        private readonly List<JObject> _history = new List<JObject>();

        internal DeckEditSession(CoreDeckData d, IDeckEditorText t, JObject run, JObject settings)
        {
            _d = d;
            _t = t;
            _run = run;
            _settings = settings;
            Snapshot = DeckRules.BeginEdit(d, run);
        }

        public JObject Snapshot { get; }
        public bool Closed { get; private set; }
        public bool CanUndo => !Closed && _history.Count > 0;

        /// <summary>The projection of the run as the session has it now.</summary>
        public DeckEditorModelData Model(DeckEditorView view = null) => DeckEditor.Model(_d, _t, _run, _settings, view);

        private void Live()
        {
            if (Closed) throw new InvalidOperationException(DM.ClosedSession);
        }

        private DeckSentence Said(string key, params KeyValuePair<string, string>[] args) => new DeckSentence(_t.Say(key, args), key, args);

        private DeckEditResult Remember(JObject before, DeckEditResult result)
        {
            if (result.Ok) _history.Add(before);
            return result;
        }

        /// <summary>
        /// add(key): key from a collection tile. <c>basic:id</c> brings back or mints a plain basic; <c>kept:variant</c> restores exactly that set-aside
        /// variant; <c>card:variant</c> moves a copy of exactly that variant (a bare <c>card:cardId</c> takes any loose copy).
        /// An unknown kind throws, as the web does.
        /// </summary>
        public DeckEditResult Add(string key)
        {
            Live();
            var before = DeckRules.BeginEdit(_d, _run);
            var text = key ?? string.Empty;
            var colon = text.IndexOf(EV_KeySeparator, StringComparison.Ordinal);
            // The web slices the kind with slice(0, colon): a key with no colon loses its last character.
            var kind = colon < 0 ? text.Substring(0, Math.Max(0, text.Length - 1)) : text.Substring(0, colon);
            var id = text.Substring(colon + 1);
            if (kind == Ashen.Generated.Core2DeckEditorValues.KindBasic)
            {
                var card = DeckRules.AddBasicCard(_d, _run, id, true);
                return Remember(before, new DeckEditResult(card != null));
            }
            var sideboard = Js.Items(_run[DK.Sideboard]).OfType<JObject>().ToList();
            if (kind == Ashen.Generated.Core2DeckEditorValues.KindKept)
            {
                var variant = text.Substring(Ashen.Generated.Core2DeckEditorValues.KindKept.Length + 1);
                var held = sideboard.FirstOrDefault(c => DeckRules.IsSetAsideBasic(_d, c, _run) && DeckEditor.VariantKey(c) == variant);
                if (held == null) return new DeckEditResult(false);
                return Remember(before, new DeckEditResult(DeckRules.MoveFromSideboard(_d, _run, held.Str(DK.InstanceId), _settings)));
            }
            if (kind != Ashen.Generated.Core2DeckEditorValues.KindCard) throw new InvalidOperationException(RunJs.Fmt(DM.UnknownCollectionKey, text));
            var tail = text.Substring(Ashen.Generated.Core2DeckEditorValues.KindCard.Length + 1);
            Func<JObject, bool> matches = tail.Contains(Ashen.Generated.Core2DeckEditorValues.VariantSeparator)
                ? (Func<JObject, bool>)(c => DeckEditor.VariantKey(c) == tail)
                : c => c.Str(DK.CardId) == tail;
            var cardId = tail.Split(new[] { Ashen.Generated.Core2DeckEditorValues.VariantSeparator }, StringSplitOptions.None)[0];
            var loose = sideboard.FirstOrDefault(c => c != null && matches(c) && !DeckRules.IsUnlimitedBasic(_d, c, _run));
            if (loose == null)
            {
                var any = Js.Items(_run[DK.Deck]).OfType<JObject>().FirstOrDefault(c => c != null && matches(c));
                return new DeckEditResult(false, Said(Ashen.Generated.Core2DeckText.RefuseAllInDeck, A(Ashen.Generated.Core2DeckText.ArgName, any != null ? NameOf(any) : cardId)));
            }
            if (!DeckRules.RangeCardAvailable(_d, _run, Cards.Resolve(_d.Run.Run.Combat, loose))) return new DeckEditResult(false, new DeckSentence(DM.RangeDisabled, null, null));
            if (!DeckRules.CardEquipmentEligible(_d, _run, loose)) return new DeckEditResult(false, Said(Ashen.Generated.Core2DeckText.RefuseEquipment));
            if (DeckRules.MoveFromSideboard(_d, _run, loose.Str(DK.InstanceId), _settings)) return Remember(before, new DeckEditResult(true));
            var limit = DeckRules.CopyLimit(_d, cardId, _settings, _run);
            return new DeckEditResult(false, Said(Ashen.Generated.Core2DeckText.RefuseCopyLimit, A(Ashen.Generated.Core2DeckText.ArgName, NameOf(loose)), A(Ashen.Generated.Core2DeckText.ArgLimit, RunJs.NumStr(limit))));
        }

        private const string EV_KeySeparator = Ashen.Generated.Core2DeckEditorValues.KeySeparator;

        private static KeyValuePair<string, string> A(string name, string value) => new KeyValuePair<string, string>(name, value);

        private string NameOf(JObject card) => Cards.Resolve(_d.Run.Run.Combat, card).Str(K.Name);

        /// <summary>remove(instanceId): the card leaves the deck for the sideboard.</summary>
        public DeckEditResult Remove(string instanceId)
        {
            Live();
            var card = Js.Items(_run[DK.Deck]).OfType<JObject>().FirstOrDefault(c => c != null && c.Str(DK.InstanceId) == instanceId);
            if (card == null) return new DeckEditResult(false);
            var before = DeckRules.BeginEdit(_d, _run);
            return Remember(before, new DeckEditResult(DeckRules.MoveToSideboard(_d, _run, instanceId)));
        }

        /// <summary>moveTo(instanceId, index): only under Play in deck order; the index is clamped to the deck.</summary>
        public DeckEditResult MoveTo(string instanceId, int index)
        {
            Live();
            if (!DeckRules.PlayInDeckOrder(_d, _settings)) return new DeckEditResult(false);
            var deck = _run.Arr(DK.Deck);
            var from = deck.ToList().FindIndex(c => c is JObject o && o.Str(DK.InstanceId) == instanceId);
            var to = Math.Max(0, Math.Min(deck.Count - 1, index));
            if (from < 0 || from == to) return new DeckEditResult(false);
            var before = DeckRules.BeginEdit(_d, _run);
            var moved = deck[from];
            deck.RemoveAt(from);
            deck.Insert(to, moved);
            return Remember(before, new DeckEditResult(true));
        }

        /// <summary>move(instanceId, delta): one place earlier (-1) or later (+1).</summary>
        public DeckEditResult Move(string instanceId, int delta)
        {
            var from = _run.Arr(DK.Deck).ToList().FindIndex(c => c is JObject o && o.Str(DK.InstanceId) == instanceId);
            return MoveTo(instanceId, from + delta);
        }

        /// <summary>undo(): restores the state before the last successful edit (both piles, the slot allocation and the mint counter).</summary>
        public DeckEditResult Undo()
        {
            Live();
            if (_history.Count == 0) return new DeckEditResult(false);
            var last = _history[_history.Count - 1];
            _history.RemoveAt(_history.Count - 1);
            DeckRules.CancelEdit(_d, _run, last);
            return new DeckEditResult(true);
        }

        /// <summary>confirm(): succeeds only inside the bounds; the refusal is the rules' sentence.</summary>
        public DeckEditResult Confirm()
        {
            Live();
            var refusal = DeckRules.EditRefusal(_d, _run.Arr(DK.Deck).Count, _settings);
            if (refusal.Length > 0) return new DeckEditResult(false, new DeckSentence(refusal, null, null));
            Closed = true;
            _history.Clear();
            return new DeckEditResult(true);
        }

        /// <summary>cancel(): puts back everything the session can change.</summary>
        public void Cancel()
        {
            Live();
            DeckRules.CancelEdit(_d, _run, Snapshot);
            Closed = true;
            _history.Clear();
        }
    }
}
