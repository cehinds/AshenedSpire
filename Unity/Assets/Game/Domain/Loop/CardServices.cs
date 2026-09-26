using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using LM = Ashen.Generated.LoopMessages;
using LV = Ashen.Generated.LoopValues;
using RK = Ashen.Generated.RunKeys;
using RV = Ashen.Generated.RunValues;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;

namespace Ashen.Domain.Loop
{
    /// <summary>
    /// The smith's two card services (shipped model/cardExtraction.js, over cardMounts.js and loadout.js
    /// itemMountInstances): EXTRACT lifts a card out of an item's mount into the run's deck (the mount then shows its
    /// kind's fallback), INSTALL seats a run-owned card in an emptied or open mount. Each is a plan that prices every
    /// legal transaction and a commit that revalidates through it; what the smith did is <c>run.itemMounts</c>, and the
    /// receipt is kept on the run (<c>lastMountReceipt</c>).
    /// </summary>
    public static class CardServices
    {
        private sealed class OwnedItem
        {
            public string ItemRef;
            public JObject Piece;
            public bool Equipped;
        }

        private static double StoneBalance(JObject run)
        {
            var value = run[RK.SmithingStones];
            if (Js.Nullish(value)) return 0;
            if (!Js.IsInt(value) || Js.D(value) < 0) throw new InvalidOperationException(LM.SmithingStonesNotInteger);
            return Js.D(value);
        }

        private static JArray CardTags(LoopData d, string cardId) => d.Combat.Cards.Get(cardId)[K.Tags] as JArray ?? new JArray();

        private static JToken CardName(LoopData d, string cardId) => d.Combat.Cards.Get(cardId)[K.Name]?.DeepClone();

        /// <summary>ownedMountItems(registries, run): the worn pieces (at their tier), then the carried armaments, each once.</summary>
        private static List<OwnedItem> OwnedMountItems(LoopData d, JObject run)
        {
            var out_ = new List<OwnedItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Push(JObject piece, bool equipped)
            {
                var itemRef = Combat.Equipment.PieceItemRef(piece);
                if (itemRef == null || !seen.Add(itemRef)) return;
                out_.Add(new OwnedItem { ItemRef = itemRef, Piece = piece, Equipped = equipped });
            }
            foreach (var piece in Combat.Equipment.EquippedPieces(d.Combat, run.Obj(K.Loadout), run.Str(RK.Class), run.Obj(K.ItemUpgradeLevels) ?? new JObject()))
                Push(piece, true);
            foreach (var id in RewardRolls.CarriedIds(run.Obj(K.Loadout)))
            {
                var piece = d.Run.EquipmentRows(K.Armaments).FirstOrDefault(r => r.Str(K.Id) == id);
                if (piece != null) Push(piece, false);
            }
            return out_;
        }

        private static bool IsExtraMountKey(string key) => key != null && key.StartsWith(RV.ExtraMountPrefix, StringComparison.Ordinal);

        /// <summary>openExtraMountKey(registries, run, itemRef): the next open extra mount, or null when the flag or the cap says no.</summary>
        private static string OpenExtraMountKey(LoopData d, JObject run, string itemRef)
        {
            var extra = CardMounts.Rules(d.Run).Obj(RK.ExtraMounts);
            if (!extra.Is(K.Enabled)) return null;
            var entries = CardMounts.ItemMountEntries(run.Obj(K.ItemMounts), itemRef);
            var perItem = extra.Num(RK.PerItem);
            var used = entries.Properties().Count(p => IsExtraMountKey(p.Name) && Js.Truthy(p.Value) && Js.Truthy(Js.Get(p.Value, K.Card)));
            if (used >= perItem) return null;
            for (var index = 0; index < perItem; index++)
            {
                var key = RV.ExtraMountPrefix + itemRef + V.KeySeparator + RunJs.NumStr(index);
                var entry = entries[key];
                if (!Js.Truthy(entry) || !Js.Truthy(Js.Get(entry, K.Card))) return key;
            }
            return null;
        }

        /// <summary>mountRows(registries, run, item): every mount on the item — authored, filled extra, the open extra — and what sits in it.</summary>
        private static List<JObject> MountRows(LoopData d, JObject run, OwnedItem item)
        {
            var rules = CardMounts.Rules(d.Run);
            var itemMounts = run.Obj(K.ItemMounts);
            var entries = CardMounts.ItemMountEntries(itemMounts, item.ItemRef);
            var authored = StartingDeck.ItemMountInstances(d.Run, itemMounts, item.Piece, true);
            var current = StartingDeck.ItemMountInstances(d.Run, itemMounts, item.Piece, false);
            var tag = rules.Str(RK.ExtractableTag);
            JToken Accepts(string kind) => rules.Obj(K.Kinds)?.Obj(kind ?? V.Undefined)?[RK.Accepts]?.DeepClone() ?? new JArray();
            double Extractions(JToken entry) => Js.IsInt(Js.Get(entry, LK.Extractions)) ? Js.D(Js.Get(entry, LK.Extractions)) : 0;
            var rows = new List<JObject>();
            foreach (var inst in authored)
            {
                var key = inst.Str(K.InstanceId);
                var entry = entries[key ?? V.Undefined];
                var live = current.FirstOrDefault(r => r.Str(K.InstanceId) == key);
                var state = !Js.Truthy(entry) ? LV.AuthoredMount : Js.Truthy(Js.Get(entry, K.Card)) ? LV.InstalledMount : live != null ? LV.FallbackMount : LV.EmptyMount;
                var cardId = live?.Str(K.CardId);
                rows.Add(Js.Obj(LK.MountKey, inst[K.InstanceId]?.DeepClone(), K.Kind, inst[K.EquipmentRole]?.DeepClone(), LK.State, state,
                    LK.AuthoredCardId, inst[K.CardId]?.DeepClone(), K.CardId, Js.S(cardId), LK.CardName, cardId != null ? CardName(d, cardId) : Js.Null(),
                    K.Upgraded, live != null && live[K.Upgraded]?.Type == JTokenType.Boolean && live.Value<bool>(K.Upgraded),
                    LK.Extractable, cardId != null && (state == LV.AuthoredMount || state == LV.InstalledMount) && !string.IsNullOrEmpty(tag) && Js.Includes(CardTags(d, cardId), tag),
                    LK.FallbackCardId, Js.S(CardMounts.ResolveFallbackCard(d.Run, rules, item.ItemRef, inst.Str(K.EquipmentRole))),
                    RK.Accepts, Accepts(inst.Str(K.EquipmentRole)), LK.Extractions, Extractions(entry), LK.Extra, false));
            }
            var extraKind = rules.Obj(RK.ExtraMounts).Str(K.Kind);
            foreach (var p in entries.Properties())
            {
                if (!IsExtraMountKey(p.Name) || !Js.Truthy(p.Value) || !Js.Truthy(Js.Get(p.Value, K.Card))) continue;
                var card = Js.Str(Js.Get(p.Value, K.Card));
                rows.Add(Js.Obj(LK.MountKey, p.Name, K.Kind, extraKind, LK.State, LV.InstalledMount, LK.AuthoredCardId, Js.Null(), K.CardId, card,
                    LK.CardName, CardName(d, card), K.Upgraded, Js.Get(p.Value, K.Upgraded)?.Type == JTokenType.Boolean && Js.Get(p.Value, K.Upgraded).Value<bool>(),
                    LK.Extractable, !string.IsNullOrEmpty(tag) && Js.Includes(CardTags(d, card), tag), LK.FallbackCardId, Js.Null(),
                    RK.Accepts, Accepts(extraKind), LK.Extractions, Extractions(p.Value), LK.Extra, true));
            }
            var open = OpenExtraMountKey(d, run, item.ItemRef);
            if (open != null)
                rows.Add(Js.Obj(LK.MountKey, open, K.Kind, extraKind, LK.State, LV.OpenMount, LK.AuthoredCardId, Js.Null(), K.CardId, Js.Null(), LK.CardName, Js.Null(),
                    K.Upgraded, false, LK.Extractable, false, LK.FallbackCardId, Js.Null(), RK.Accepts, Accepts(extraKind), LK.Extractions, 0.0, LK.Extra, true));
            return rows;
        }

        private static JObject IdentityFields(string itemRef, JObject piece)
        {
            var identity = ItemSmithing.Identity(itemRef);
            var o = Js.Obj(K.ItemRef, itemRef, LK.ItemKind, identity.ItemKind, LK.ItemId, identity.ItemId, LK.ItemName, piece[K.Name]?.DeepClone());
            if (identity.ClassId != null) o[K.ClassId] = identity.ClassId;
            return o;
        }

        private static JObject Candidate(string itemRef, JObject piece, bool equipped, double cost, double stones, JArray mounts)
        {
            var o = IdentityFields(itemRef, piece);
            var shortfall = Math.Max(0, cost - stones);
            o[LK.Equipped] = equipped;
            o.Put(K.Cost, cost);
            o.Put(LK.Stones, stones);
            o.Put(LK.Shortfall, shortfall);
            o[LK.Affordable] = shortfall == 0;
            o[RK.Mounts] = mounts;
            return o;
        }

        private static double Price(LoopData d, string service) => SmithServices.Rules(d).Obj(service).Num(K.Cost);

        /// <summary>extractionPlan(registries, run): every extractable mount on every owned item, priced.</summary>
        public static List<JObject> ExtractionPlan(LoopData d, JObject run)
        {
            var cost = Price(d, LV.ExtractService);
            var stones = StoneBalance(run);
            var candidates = new List<JObject>();
            foreach (var item in OwnedMountItems(d, run))
            {
                var mounts = MountRows(d, run, item).Where(r => r.Is(LK.Extractable)).ToList();
                if (mounts.Count == 0) continue;
                candidates.Add(Candidate(item.ItemRef, item.Piece, item.Equipped, cost, stones, new JArray(mounts)));
            }
            return candidates;
        }

        /// <summary>installPlan(registries, run): every open or emptied mount with the run-owned deck cards it would take, priced.</summary>
        public static List<JObject> InstallPlan(LoopData d, JObject run)
        {
            var cost = Price(d, LV.InstallService);
            var stones = StoneBalance(run);
            var candidates = new List<JObject>();
            foreach (var item in OwnedMountItems(d, run))
            {
                var mounts = new JArray();
                foreach (var row in MountRows(d, run, item))
                {
                    var state = row.Str(LK.State);
                    if (state != LV.FallbackMount && state != LV.EmptyMount && state != LV.OpenMount) continue;
                    var accepts = row.Arr(RK.Accepts);
                    var cards = new JArray(Js.Items(run[K.Deck]).OfType<JObject>()
                        .Where(inst => !StartingDeck.IsItemOwned(d.Run, inst) && !Js.Truthy(inst[K.EquipmentRole]))
                        .Where(inst => CardTags(d, inst.Str(K.CardId)).Any(t => Js.Includes(accepts, Js.Str(t))))
                        .Select(inst => (JToken)Js.Obj(K.InstanceId, inst[K.InstanceId]?.DeepClone(), K.CardId, inst[K.CardId]?.DeepClone(),
                            LK.CardName, CardName(d, inst.Str(K.CardId)), K.Upgraded, inst[K.Upgraded]?.Type == JTokenType.Boolean && inst.Value<bool>(K.Upgraded))));
                    if (cards.Count == 0) continue;
                    var withCards = Js.Spread(row);
                    withCards[K.Cards] = cards;
                    mounts.Add(withCards);
                }
                if (mounts.Count == 0) continue;
                candidates.Add(Candidate(item.ItemRef, item.Piece, item.Equipped, cost, stones, mounts));
            }
            return candidates;
        }

        private static double NextTransaction(JObject run)
        {
            var current = run[LK.MountTransactions];
            if (!Js.Nullish(current) && (!Js.IsInt(current) || Js.D(current) < 0)) throw new InvalidOperationException(LM.MountTransactionsNotInteger);
            var n = (Js.Nullish(current) ? 0 : Js.D(current)) + 1;
            run.Put(LK.MountTransactions, n);
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

        private static JObject Pay(LoopData d, JObject run, JObject candidate, bool free, out double before)
        {
            if (!free && !candidate.Is(LK.Affordable)) throw new InvalidOperationException(RunJs.Fmt(LM.InsufficientStones, RunJs.Key(candidate[LK.Shortfall])));
            before = candidate.Num(LK.Stones);
            run.Put(RK.SmithingStones, free ? before : before - candidate.Num(K.Cost));
            return candidate;
        }

        private static JObject Receipt(LoopData d, JObject run, string service, string itemRef, JObject candidate)
        {
            var piece = OwnedMountItems(d, run).FirstOrDefault(i => i.ItemRef == itemRef)?.Piece ?? Js.Obj(K.Name, candidate[LK.ItemName]);
            var receipt = Js.Obj(RK.SchemaVersion, d.RuleNum(RK.Mounts, LK.ReceiptSchemaVersion), LK.Service, service);
            foreach (var p in IdentityFields(itemRef, piece).Properties()) receipt[p.Name] = p.Value.DeepClone();
            return receipt;
        }

        private static void Finish(JObject run, JObject receipt, JObject candidate, bool free, double before, double transaction)
        {
            var spent = free ? 0 : candidate.Num(K.Cost);
            receipt.Put(LK.AuthoredCost, candidate.Num(K.Cost));
            receipt.Put(LK.Spent, spent);
            receipt.Put(K.Cost, spent);
            receipt.Put(LK.StoneBalanceBefore, before);
            receipt[WK.StoneBalanceAfter] = run[RK.SmithingStones]?.DeepClone();
            receipt[LK.Free] = free;
            receipt.Put(LK.Transaction, transaction);
            run[LK.LastMountReceipt] = receipt.DeepClone();
        }

        /// <summary>
        /// commitExtraction(registries, run, itemRef, mountKey): the mount empties (an extra mount is deleted), a run-owned
        /// instance of the card joins the deck, and the restamp sweeps the item-owned one and seats the fallback.
        /// </summary>
        public static JObject CommitExtraction(LoopData d, JObject run, string itemRef, string mountKey, bool free = false)
        {
            var candidate = ExtractionPlan(d, run).FirstOrDefault(c => c.Str(K.ItemRef) == itemRef) ?? throw new InvalidOperationException(RunJs.Fmt(LM.NoExtractableMount, itemRef));
            var mount = candidate.Arr(RK.Mounts).OfType<JObject>().FirstOrDefault(m => m.Str(LK.MountKey) == mountKey)
                        ?? throw new InvalidOperationException(RunJs.Fmt(LM.MountNotExtractable, mountKey, itemRef));
            Pay(d, run, candidate, free, out var before);
            var n = NextTransaction(run);
            WriteMount(run, itemRef, mountKey, mount.Is(LK.Extra) ? null : Js.Obj(K.Card, Js.Null(), LK.Extractions, mount.Num(LK.Extractions) + 1));
            var instanceId = string.Join(V.KeySeparator, LV.ExtractedPrefix, RunJs.NumStr(n), mount.Str(K.CardId));
            if (!(run[K.Deck] is JArray deck)) run[K.Deck] = deck = new JArray();
            deck.Add(Js.Obj(K.InstanceId, instanceId, K.CardId, mount[K.CardId], K.Upgraded, mount[K.Upgraded]?.Type == JTokenType.Boolean && mount.Value<bool>(K.Upgraded)));
            StartingDeck.StampDeck(d.Run, run);
            var receipt = Receipt(d, run, LV.ExtractService, itemRef, candidate);
            receipt[LK.MountKey] = mountKey;
            receipt[K.Kind] = mount[K.Kind]?.DeepClone();
            receipt[K.CardId] = mount[K.CardId]?.DeepClone();
            receipt[LK.CardName] = mount[LK.CardName]?.DeepClone();
            receipt[K.InstanceId] = instanceId;
            receipt[LK.FallbackCardId] = mount.Is(LK.Extra) ? Js.Null() : mount[LK.FallbackCardId]?.DeepClone();
            Finish(run, receipt, candidate, free, before, n);
            return receipt;
        }

        /// <summary>
        /// commitInstall(registries, run, itemRef, mountKey, instanceId): the deck instance leaves (the card is the item's
        /// now) and the restamp mints the item-owned instance in that mount if the item is worn.
        /// </summary>
        public static JObject CommitInstall(LoopData d, JObject run, string itemRef, string mountKey, string instanceId, bool free = false)
        {
            var candidate = InstallPlan(d, run).FirstOrDefault(c => c.Str(K.ItemRef) == itemRef) ?? throw new InvalidOperationException(RunJs.Fmt(LM.NoOpenMount, itemRef));
            var mount = candidate.Arr(RK.Mounts).OfType<JObject>().FirstOrDefault(m => m.Str(LK.MountKey) == mountKey)
                        ?? throw new InvalidOperationException(RunJs.Fmt(LM.MountNotOpen, mountKey, itemRef));
            var card = mount.Arr(K.Cards).OfType<JObject>().FirstOrDefault(c => c.Str(K.InstanceId) == instanceId)
                       ?? throw new InvalidOperationException(RunJs.Fmt(LM.CardCannotSeat, instanceId, mountKey));
            Pay(d, run, candidate, free, out var before);
            var n = NextTransaction(run);
            var deck = run.Arr(K.Deck);
            var at = deck.OfType<JObject>().ToList().FindIndex(inst => inst.Str(K.InstanceId) == instanceId);
            if (at < 0) throw new InvalidOperationException(RunJs.Fmt(LM.DeckCardVanished, instanceId));
            deck.RemoveAt(at);
            WriteMount(run, itemRef, mountKey, Js.Obj(K.Card, card[K.CardId], K.Upgraded, card[K.Upgraded], LK.Extractions, mount[LK.Extractions]));
            StartingDeck.StampDeck(d.Run, run);
            var receipt = Receipt(d, run, LV.InstallService, itemRef, candidate);
            receipt[LK.MountKey] = mountKey;
            receipt[K.Kind] = mount[K.Kind]?.DeepClone();
            receipt[K.CardId] = card[K.CardId]?.DeepClone();
            receipt[LK.CardName] = card[LK.CardName]?.DeepClone();
            receipt[K.InstanceId] = instanceId;
            receipt[LK.ReplacedFallbackCardId] = mount.Str(LK.State) == LV.FallbackMount ? mount[K.CardId]?.DeepClone() : Js.Null();
            Finish(run, receipt, candidate, free, before, n);
            return receipt;
        }
    }
}
