using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RV = Ashen.Generated.RunValues;
using WK = Ashen.Generated.RewardsKeys;
using V = Ashen.Generated.CombatValues;
using SK = Ashen.Generated.ShopKeys;
using SV = Ashen.Generated.ShopValues;
using SM = Ashen.Generated.ShopMessages;
using SS = Ashen.Generated.ShopStringKeys;

namespace Ashen.Domain.Shop
{
    /// <summary>
    /// The smith's two card services (shipped model/cardExtraction.js, over model/cardMounts.js openExtraMountKey and
    /// model/loadout.js itemMountInstances — <see cref="CardMounts"/> and <see cref="StartingDeck"/>): EXTRACT a card
    /// out of an item's mount so it becomes the run's own, and INSTALL a run-owned card into an emptied or open mount.
    /// A plan enumerates every legal transaction with its cost; a commit revalidates through the same plan before it
    /// touches the run, then restamps the deck. The one port: the merchant, the events and the rest stop call it (D-112u).
    /// </summary>
    public static class CardExtraction
    {
        private static JArray CardTags(ShopData d, string cardId) => d.Run.Cards.Get(cardId)[K.Tags] as JArray ?? new JArray();

        private static JToken CardName(ShopData d, string cardId) => d.Run.Cards.Get(cardId)[K.Name]?.DeepClone();

        /// <summary>One owned item a smith can work: its ref, the piece and whether it is worn.</summary>
        public sealed class OwnedItem
        {
            public string ItemRef;
            public JObject Piece;
            public bool Equipped;
        }

        /// <summary>ownedMountItems(registries, run): worn pieces, then carried armaments — each once, worn first.</summary>
        public static List<OwnedItem> OwnedMountItems(ShopData d, JObject run)
        {
            var out_ = new List<OwnedItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Push(JObject piece, bool equipped)
            {
                var itemRef = Combat.Equipment.PieceItemRef(piece);
                if (itemRef == null || !seen.Add(itemRef)) return;
                out_.Add(new OwnedItem { ItemRef = itemRef, Piece = piece, Equipped = equipped });
            }
            foreach (var piece in Combat.Equipment.EquippedPieces(d.Combat, run.Obj(K.Loadout), run.Str(RK.Class), run.Obj(K.ItemUpgradeLevels) ?? new JObject())) Push(piece, true);
            foreach (var id in RewardRolls.CarriedIds(run.Obj(K.Loadout)))
            {
                var piece = d.Armament(id);
                if (piece != null) Push(piece, false);
            }
            return out_;
        }

        private static JArray Accepts(JObject rules, string kind) => (rules.Obj(K.Kinds).Obj(kind ?? V.Undefined)?[RK.Accepts] as JArray ?? new JArray());

        private static double Extractions(JToken entry) => Js.Truthy(entry) && Js.IsInt(Js.Get(entry, SK.Extractions)) ? Js.D(Js.Get(entry, SK.Extractions)) : 0;

        /// <summary>
        /// mountRows(registries, run, item): one row per mount, authored and extra alike, saying what sits in it and how
        /// it got there — authored, installed, fallback (emptied, showing its kind's fallback), empty, or open.
        /// </summary>
        public static List<JObject> MountRows(ShopData d, JObject run, string itemRef, JObject piece)
        {
            var rules = CardMounts.Rules(d.Run);
            var tag = rules.Str(RK.ExtractableTag);
            var entries = CardMounts.ItemMountEntries(run.Obj(K.ItemMounts), itemRef);
            var authored = StartingDeck.ItemMountInstances(d.Run, run.Obj(K.ItemMounts), piece, true);
            var current = StartingDeck.ItemMountInstances(d.Run, run.Obj(K.ItemMounts), piece, false);
            var rows = new List<JObject>();
            foreach (var inst in authored)
            {
                var entry = entries[inst.Str(K.InstanceId) ?? V.Undefined];
                var live = current.FirstOrDefault(row => row.Str(K.InstanceId) == inst.Str(K.InstanceId));
                var state = !Js.Truthy(entry) ? SV.MountAuthored : Js.Truthy(Js.Get(entry, K.Card)) ? SV.MountInstalled : live != null ? SV.MountFallback : SV.MountEmpty;
                var cardId = live?.Str(K.CardId);
                var kind = inst.Str(K.EquipmentRole);
                rows.Add(Js.Obj(SK.MountKey, inst[K.InstanceId], K.Kind, inst[K.EquipmentRole], SK.State, state, SK.AuthoredCardId, inst[K.CardId],
                    K.CardId, (JToken)Js.S(cardId), SK.CardName, cardId != null ? CardName(d, cardId) ?? Js.Null() : Js.Null(),
                    K.Upgraded, live != null && live[K.Upgraded]?.Type == JTokenType.Boolean && live.Is(K.Upgraded),
                    SK.Extractable, cardId != null && (state == SV.MountAuthored || state == SV.MountInstalled) && !string.IsNullOrEmpty(tag) && Js.Includes(CardTags(d, cardId), tag),
                    SK.FallbackCardId, (JToken)Js.S(CardMounts.ResolveFallbackCard(d.Run, rules, itemRef, kind)),
                    RK.Accepts, Accepts(rules, kind).DeepClone(), SK.Extractions, Extractions(entry), SK.Extra, false));
            }
            var extraKind = rules.Obj(RK.ExtraMounts).Str(K.Kind);
            foreach (var p in entries.Properties())
            {
                var entry = p.Value;
                if (!CardMounts.IsExtraMountKey(p.Name) || !Js.Truthy(entry) || !Js.Truthy(Js.Get(entry, K.Card))) continue;
                var cardId = Js.Str(Js.Get(entry, K.Card));
                rows.Add(Js.Obj(SK.MountKey, p.Name, K.Kind, extraKind, SK.State, SV.MountInstalled, SK.AuthoredCardId, Js.Null(),
                    K.CardId, cardId, SK.CardName, CardName(d, cardId) ?? Js.Null(),
                    K.Upgraded, Js.Get(entry, K.Upgraded)?.Type == JTokenType.Boolean && Js.Get(entry, K.Upgraded).Value<bool>(),
                    SK.Extractable, !string.IsNullOrEmpty(tag) && Js.Includes(CardTags(d, cardId), tag),
                    SK.FallbackCardId, Js.Null(), RK.Accepts, Accepts(rules, extraKind).DeepClone(), SK.Extractions, Extractions(entry), SK.Extra, true));
            }
            var open = CardMounts.OpenExtraMountKey(d.Run, run.Obj(K.ItemMounts), itemRef);
            if (open != null)
                rows.Add(Js.Obj(SK.MountKey, open, K.Kind, extraKind, SK.State, SV.MountOpen, SK.AuthoredCardId, Js.Null(), K.CardId, Js.Null(), SK.CardName, Js.Null(),
                    K.Upgraded, false, SK.Extractable, false, SK.FallbackCardId, Js.Null(), RK.Accepts, Accepts(rules, extraKind).DeepClone(), SK.Extractions, 0.0, SK.Extra, true));
            return rows;
        }

        /// <summary>identityFields(registries, itemRef, piece): { itemRef, itemKind, itemId, itemName, classId? }.</summary>
        internal static JObject IdentityFields(string itemRef, JToken itemName)
        {
            var identity = ItemUpgrades.Identity(itemRef);
            var o = Js.Obj(K.ItemRef, itemRef, SK.ItemKind, identity.ItemKind, SK.ItemId, identity.ItemId, SK.ItemName, itemName?.DeepClone() ?? Js.Null());
            if (!string.IsNullOrEmpty(identity.ClassId)) o[K.ClassId] = identity.ClassId;
            return o;
        }

        private static void Priced(JObject o, double cost, double stones)
        {
            var shortfall = Math.Max(0, cost - stones);
            o.Put(K.Cost, cost);
            o.Put(SK.Stones, stones);
            o.Put(SK.Shortfall, shortfall);
            o.Put(SK.Affordable, shortfall == 0);
        }

        private static double ServiceCost(ShopData d, string service) => SmithServices.Rules(d).Obj(service).Num(K.Cost);

        /// <summary>extractionPlan(registries, run): every extractable mount on every owned item, priced.</summary>
        public static JObject ExtractionPlan(ShopData d, JObject run)
        {
            var cost = ServiceCost(d, SV.ServiceExtract);
            var stones = Rewards.Smithing.StoneBalance(run);
            var candidates = new JArray();
            foreach (var item in OwnedMountItems(d, run))
            {
                var mounts = MountRows(d, run, item.ItemRef, item.Piece).Where(r => r.Is(SK.Extractable)).ToList();
                if (mounts.Count == 0) continue;
                var c = IdentityFields(item.ItemRef, item.Piece[K.Name]);
                c.Put(SK.Equipped, item.Equipped);
                Priced(c, cost, stones);
                c[RK.Mounts] = new JArray(mounts);
                candidates.Add(c);
            }
            return Js.Obj(RK.SchemaVersion, d.RuleNum(SK.Smith, SK.MountReceiptSchemaVersion), SK.Service, SV.ServiceExtract, SK.Stones, stones, K.Cost, cost, SK.Candidates, candidates);
        }

        /// <summary>installPlan(registries, run): every open mount on every owned item with the deck cards it would take, priced.</summary>
        public static JObject InstallPlan(ShopData d, JObject run)
        {
            var cost = ServiceCost(d, SV.ServiceInstall);
            var stones = Rewards.Smithing.StoneBalance(run);
            var openStates = d.RuleList(SK.Smith, SK.OpenMountStates);
            var candidates = new JArray();
            foreach (var item in OwnedMountItems(d, run))
            {
                var mounts = new JArray();
                foreach (var row in MountRows(d, run, item.ItemRef, item.Piece))
                {
                    if (!openStates.Contains(row.Str(SK.State))) continue;
                    var cards = InstallableCards(d, run, row.Arr(RK.Accepts));
                    if (cards.Count == 0) continue;
                    var mount = Js.Spread(row);
                    mount[K.Cards] = cards;
                    mounts.Add(mount);
                }
                if (mounts.Count == 0) continue;
                var c = IdentityFields(item.ItemRef, item.Piece[K.Name]);
                c.Put(SK.Equipped, item.Equipped);
                Priced(c, cost, stones);
                c[RK.Mounts] = mounts;
                candidates.Add(c);
            }
            return Js.Obj(RK.SchemaVersion, d.RuleNum(SK.Smith, SK.MountReceiptSchemaVersion), SK.Service, SV.ServiceInstall, SK.Stones, stones, K.Cost, cost, SK.Candidates, candidates);
        }

        /// <summary>installableCards(registries, run, accepts): the run-owned deck cards a mount of `accepts` could seat.</summary>
        private static JArray InstallableCards(ShopData d, JObject run, JArray accepts)
        {
            var out_ = new JArray();
            foreach (var inst in Js.Items(run[K.Deck]).OfType<JObject>())
            {
                if (StartingDeck.IsItemOwned(d.Run, inst) || Js.Truthy(inst[K.EquipmentRole])) continue;
                if (!Js.Items(CardTags(d, inst.Str(K.CardId))).Any(t => Js.IsStr(t) && Js.Includes(accepts, Js.Str(t)))) continue;
                out_.Add(Js.Obj(K.InstanceId, inst[K.InstanceId], K.CardId, inst[K.CardId], SK.CardName, CardName(d, inst.Str(K.CardId)) ?? Js.Null(),
                    K.Upgraded, inst[K.Upgraded]?.Type == JTokenType.Boolean && inst.Is(K.Upgraded)));
            }
            return out_;
        }

        private static double NextTransaction(JObject run)
        {
            var n = SmithServices.Integer(Js.Nullish(run[SK.MountTransactions]) ? Js.N(0) : run[SK.MountTransactions], string.Join(RV.PathDot, RK.Run, SK.MountTransactions)) + 1;
            run.Put(SK.MountTransactions, n);
            return n;
        }

        private static void WriteMount(JObject run, string itemRef, string mountKey, JObject entry)
        {
            var entries = Js.Spread(CardMounts.ItemMountEntries(run.Obj(K.ItemMounts), itemRef));
            if (entry == null) entries.Remove(mountKey);
            else entries[mountKey] = entry;
            var all = Js.Spread(run.Obj(K.ItemMounts));
            if (entries.Count > 0) all[itemRef] = entries;
            else all.Remove(itemRef);
            run[K.ItemMounts] = all;
        }

        private static JObject Candidate(JObject plan, string itemRef) => Js.Items(plan[SK.Candidates]).OfType<JObject>().FirstOrDefault(c => c.Str(K.ItemRef) == itemRef);

        /// <summary>The receipt's item name: the owned piece's after the commit (<c>piece.name</c>, even when absent), else the plan's.</summary>
        private static JToken PieceName(ShopData d, JObject run, string itemRef, JObject candidate)
        {
            var owned = OwnedMountItems(d, run).FirstOrDefault(i => i.ItemRef == itemRef);
            return owned != null ? owned.Piece[K.Name] : candidate[SK.ItemName];
        }

        /// <summary>
        /// commitExtraction(registries, run, itemRef, mountKey, rules, { free }): lift the card out of one mount — the
        /// mount empties (an extra mount is deleted), a run-owned instance joins the deck and the deck is restamped.
        /// </summary>
        public static JObject CommitExtraction(ShopData d, JObject run, string itemRef, string mountKey, bool free = false)
        {
            var plan = ExtractionPlan(d, run);
            var candidate = Candidate(plan, itemRef) ?? throw new RefusalException(SS.SmithRefusalNoExtractableMount, itemRef);
            var mount = Js.Items(candidate[RK.Mounts]).OfType<JObject>().FirstOrDefault(r => r.Str(SK.MountKey) == mountKey)
                        ?? throw new RefusalException(SS.SmithRefusalMountNotExtractable, mountKey, itemRef);
            if (!free && !candidate.Is(SK.Affordable)) throw new RefusalException(SS.SmithRefusalInsufficientStones, RunJs.NumStr(candidate.Num(SK.Shortfall)));
            var before = plan.Num(SK.Stones);
            run.Put(RK.SmithingStones, free ? before : before - candidate.Num(K.Cost));
            var n = NextTransaction(run);
            var extra = mount.Is(SK.Extra);
            WriteMount(run, itemRef, mountKey, extra ? null : Js.Obj(K.Card, Js.Null(), SK.Extractions, mount.Num(SK.Extractions) + 1));
            var instanceId = RunJs.Fmt(SV.ExtractedInstanceFormat, RunJs.NumStr(n), mount.Str(K.CardId));
            if (!(run[K.Deck] is JArray)) run[K.Deck] = new JArray();
            run.Arr(K.Deck).Add(Js.Obj(K.InstanceId, instanceId, K.CardId, mount[K.CardId], K.Upgraded, mount[K.Upgraded]?.Type == JTokenType.Boolean && mount.Is(K.Upgraded)));
            StartingDeck.StampDeck(d.Run, run);
            var spent = free ? 0 : candidate.Num(K.Cost);
            var receipt = Js.Obj(RK.SchemaVersion, d.RuleNum(SK.Smith, SK.MountReceiptSchemaVersion), SK.Service, SV.ServiceExtract);
            foreach (var p in IdentityFields(itemRef, PieceName(d, run, itemRef, candidate)).Properties()) receipt[p.Name] = p.Value;
            receipt[SK.MountKey] = mountKey;
            if (mount[K.Kind] != null) receipt[K.Kind] = mount[K.Kind].DeepClone();
            receipt[K.CardId] = mount[K.CardId]?.DeepClone();
            receipt[SK.CardName] = mount[SK.CardName]?.DeepClone();
            receipt[K.InstanceId] = instanceId;
            receipt[SK.FallbackCardId] = extra ? Js.Null() : mount[SK.FallbackCardId]?.DeepClone();
            receipt.Put(SK.AuthoredCost, candidate.Num(K.Cost));
            receipt.Put(SK.Spent, spent);
            receipt.Put(K.Cost, spent);
            receipt.Put(SK.StoneBalanceBefore, before);
            receipt[WK.StoneBalanceAfter] = run[RK.SmithingStones].DeepClone();
            receipt.Put(SK.Free, free);
            receipt.Put(SK.Transaction, n);
            run[SK.LastMountReceipt] = receipt.DeepClone();
            return receipt;
        }

        /// <summary>
        /// commitInstall(registries, run, itemRef, mountKey, instanceId, rules, { free }): seat one run-owned deck card in
        /// one open mount — the deck instance leaves and the restamp mints the item-owned one if the item is worn.
        /// </summary>
        public static JObject CommitInstall(ShopData d, JObject run, string itemRef, string mountKey, string instanceId, bool free = false)
        {
            var plan = InstallPlan(d, run);
            var candidate = Candidate(plan, itemRef) ?? throw new RefusalException(SS.SmithRefusalNoOpenMount, itemRef);
            var mount = Js.Items(candidate[RK.Mounts]).OfType<JObject>().FirstOrDefault(r => r.Str(SK.MountKey) == mountKey)
                        ?? throw new RefusalException(SS.SmithRefusalMountNotOpen, mountKey, itemRef);
            var card = Js.Items(mount[K.Cards]).OfType<JObject>().FirstOrDefault(r => r.Str(K.InstanceId) == instanceId)
                       ?? throw new RefusalException(SS.SmithRefusalCardNotSeatable, instanceId, mountKey);
            if (!free && !candidate.Is(SK.Affordable)) throw new RefusalException(SS.SmithRefusalInsufficientStones, RunJs.NumStr(candidate.Num(SK.Shortfall)));
            var before = plan.Num(SK.Stones);
            run.Put(RK.SmithingStones, free ? before : before - candidate.Num(K.Cost));
            var n = NextTransaction(run);
            var deck = run.Arr(K.Deck);
            var at = deck.OfType<JObject>().ToList().FindIndex(inst => inst.Str(K.InstanceId) == instanceId);
            if (at == -1) throw new InvalidOperationException(RunJs.Fmt(SM.DeckCardVanished, instanceId));
            deck.RemoveAt(at);
            WriteMount(run, itemRef, mountKey, Js.Obj(K.Card, card[K.CardId], K.Upgraded, card[K.Upgraded], SK.Extractions, mount[SK.Extractions]));
            StartingDeck.StampDeck(d.Run, run);
            var spent = free ? 0 : candidate.Num(K.Cost);
            var receipt = Js.Obj(RK.SchemaVersion, d.RuleNum(SK.Smith, SK.MountReceiptSchemaVersion), SK.Service, SV.ServiceInstall);
            foreach (var p in IdentityFields(itemRef, PieceName(d, run, itemRef, candidate)).Properties()) receipt[p.Name] = p.Value;
            receipt[SK.MountKey] = mountKey;
            if (mount[K.Kind] != null) receipt[K.Kind] = mount[K.Kind].DeepClone();
            receipt[K.CardId] = card[K.CardId]?.DeepClone();
            receipt[SK.CardName] = card[SK.CardName]?.DeepClone();
            receipt[K.InstanceId] = instanceId;
            receipt[SK.ReplacedFallbackCardId] = mount.Str(SK.State) == SV.MountFallback ? mount[K.CardId]?.DeepClone() : Js.Null();
            receipt.Put(SK.AuthoredCost, candidate.Num(K.Cost));
            receipt.Put(SK.Spent, spent);
            receipt.Put(K.Cost, spent);
            receipt.Put(SK.StoneBalanceBefore, before);
            receipt[WK.StoneBalanceAfter] = run[RK.SmithingStones].DeepClone();
            receipt.Put(SK.Free, free);
            receipt.Put(SK.Transaction, n);
            run[SK.LastMountReceipt] = receipt.DeepClone();
            return receipt;
        }
    }
}
