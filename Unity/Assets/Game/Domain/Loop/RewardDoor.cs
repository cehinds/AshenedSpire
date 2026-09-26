using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;
using LK = Ashen.Generated.LoopKeys;
using LM = Ashen.Generated.LoopMessages;
using LV = Ashen.Generated.LoopValues;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;
using WV = Ashen.Generated.RewardsValues;

namespace Ashen.Domain.Loop
{
    /// <summary>What a reward door did: the row keys taken in order, the card chosen, and where the door leads after.</summary>
    public sealed class ClaimReceipt
    {
        public List<string> Taken = new List<string>();
        public string ChosenCardId;

        /// <summary>The checkpoint's <c>after</c>: 'advanceAct' (the caller then advances the act), 'map', or null (a treasure room).</summary>
        public string After;

        public JObject ToJson() => Js.Obj(LK.Taken, new JArray(Taken), WK.ChosenCardId, Js.S(ChosenCardId), WK.After, Js.S(After));
    }

    /// <summary>
    /// One open reward door (shipped ui/screens/reward.js mountRewards over model/rewardplan.js, with main.js
    /// collectArmament and the checkpoint's onDone): the rows the offer carries, their states, the picks made. Cinders
    /// are granted on arrival; a tap takes or skips one row; Continue takes every pending row under the auto-collect dial
    /// (a choice picked on 'cardRewards') and closes the checkpoint. Every take writes the run (and, for flasks, relics
    /// and armaments, the profile's seen and found records) through one apply per kind.
    /// </summary>
    public sealed class RewardDoor
    {
        private readonly LoopContext _ctx;
        private readonly JObject _checkpoint;
        private readonly string _source;
        private readonly JObject _states;
        private readonly JObject _chosenDraftCardIds;
        private readonly JObject _chosenDraftNodeIds;
        private readonly List<string> _taken = new List<string>();
        private string _chosenCardId;

        /// <summary>The plan's rows ({ kind, key, blockedBy, … }), in the closed kind order.</summary>
        public JArray Rows { get; }

        private RewardDoor(LoopContext ctx, JObject rewards, JObject checkpoint, string source)
        {
            _ctx = ctx;
            _checkpoint = checkpoint;
            _source = source;
            Rows = Plan(ctx, rewards);
            _states = Js.Spread(checkpoint?.Obj(WK.States));
            if (Js.IsInt(rewards.Obj(WK.SmithingStoneReceipt)?[K.Amount]) && rewards.Obj(WK.SmithingStoneReceipt).Num(K.Amount) > 0) _states[WK.SmithingStone] = WV.Taken;
            _chosenCardId = checkpoint != null && Js.Truthy(checkpoint[WK.ChosenCardId]) ? checkpoint.Str(WK.ChosenCardId) : null;
            _chosenDraftCardIds = Js.Spread(checkpoint?.Obj(WK.ChosenDraftCardIds));
            _chosenDraftNodeIds = Js.Spread(checkpoint?.Obj(WK.ChosenDraftNodeIds));
        }

        /// <summary>The row states (key → 'taken' | 'skipped'; absent = pending).</summary>
        public JObject States => (JObject)_states.DeepClone();

        /// <summary>Opens the run's pending-reward checkpoint (a fight's spoils, a dungeon cache) and grants its cinders.</summary>
        public static RewardDoor OpenPending(LoopContext ctx)
        {
            var checkpoint = ctx.Run.Obj(WK.PendingReward) ?? throw new InvalidOperationException(LM.NoPendingReward);
            return Open(ctx, checkpoint.Obj(WK.Rewards), checkpoint, checkpoint.Str(RK.Source));
        }

        /// <summary>Opens a door on an offer (a treasure room's, with no checkpoint) and grants its cinders.</summary>
        public static RewardDoor Open(LoopContext ctx, JObject rewards, JObject checkpoint, string source)
        {
            var door = new RewardDoor(ctx, rewards ?? new JObject(), checkpoint, source);
            door.GrantCinders();
            return door;
        }

        // ------------------------------------------------------------------ the plan (model/rewardplan.js)

        /// <summary>rewardPlan(rewards, facts): one row per kind the offer carries (drafts one row each), blocked rows named.</summary>
        public static JArray Plan(LoopContext ctx, JObject rewards)
        {
            var d = ctx.Data;
            var run = ctx.Run;
            var flaskSlotsFree = Math.Max(0, FlaskSlotCap(d) - (run.Arr(K.Flasks)?.Count ?? 0));
            var armamentSlotsFree = Math.Max(0, StorageSlots(d) - (run.Obj(K.Loadout)?.Arr(RK.Storage)?.Count ?? 0));
            var rows = new JArray();
            void Push(string kind, JObject fields, string blockedBy)
            {
                var row = Js.Obj(K.Kind, kind, LK.Key, RowKey(kind, fields), LK.BlockedBy, Js.S(blockedBy));
                foreach (var p in fields.Properties()) row[p.Name] = p.Value.DeepClone();
                rows.Add(row);
            }
            foreach (var kind in d.RuleList(WK.Rewards, LK.KindOrder))
            {
                switch (kind)
                {
                    case RK.Cinders:
                        if (Js.IsFinite(rewards[RK.Cinders]) && rewards.Num(RK.Cinders) > 0) Push(kind, Js.Obj(K.Amount, rewards[RK.Cinders]), null);
                        break;
                    case WK.SmithingStone:
                    {
                        var receipt = rewards.Obj(WK.SmithingStoneReceipt);
                        if (receipt != null && Js.IsInt(receipt[K.Amount]) && receipt.Num(K.Amount) > 0) Push(kind, Js.Spread(receipt), null);
                        break;
                    }
                    case LV.ClassDraft:
                    case LV.SkillDraft:
                    {
                        var isClass = kind == LV.ClassDraft;
                        var owner = isClass ? K.ClassId : WK.SkillId;
                        var ids = isClass ? WK.NodeIds : WK.CardIds;
                        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (var draft in Js.Items(rewards[isClass ? WK.ClassDrafts : WK.SkillDrafts]).OfType<JObject>())
                        {
                            if (!(draft[ids] is JArray list) || list.Count == 0) continue;
                            var who = RunJs.Key(draft[owner]);
                            seen.TryGetValue(who, out var n);
                            seen[who] = n + 1;
                            Push(kind, Js.Obj(owner, draft[owner]?.DeepClone(), LK.Ordinal, (double)n, K.Level, draft[K.Level]?.DeepClone(), ids, list.DeepClone(),
                                LK.Choice, list.Count > 1), null);
                        }
                        break;
                    }
                    case K.Card:
                        if (rewards[WK.CardIds] is JArray cards && cards.Count > 0) Push(kind, Js.Obj(WK.CardIds, cards.DeepClone(), LK.Choice, cards.Count > 1), null);
                        break;
                    case LV.FlaskKind:
                        if (Js.Truthy(rewards[K.FlaskId])) Push(kind, Js.Obj(K.FlaskId, rewards[K.FlaskId]), flaskSlotsFree > 0 ? null : LV.SlotsBlocked);
                        break;
                    case LV.ArmamentKind:
                        if (Js.Truthy(rewards[WK.ArmamentId])) Push(kind, Js.Obj(WK.ArmamentId, rewards[WK.ArmamentId]), armamentSlotsFree > 0 ? null : LV.StorageBlocked);
                        break;
                    case V.RelicKind:
                        if (Js.Truthy(rewards[K.RelicId])) Push(kind, Js.Obj(K.RelicId, rewards[K.RelicId]), null);
                        break;
                }
            }
            return rows;
        }

        /// <summary>rowKey(kind, row): the kind, or <c>skillDraft:&lt;skillId&gt;:&lt;ordinal&gt;</c> / <c>classDraft:&lt;classId&gt;:&lt;ordinal&gt;</c>.</summary>
        private static string RowKey(string kind, JObject row)
        {
            if (kind == LV.SkillDraft) return string.Join(V.KeySeparator, kind, RunJs.Key(row[WK.SkillId]), RunJs.NumStr(Js.Or0(row[LK.Ordinal])));
            if (kind == LV.ClassDraft) return string.Join(V.KeySeparator, kind, RunJs.Key(row[K.ClassId]), RunJs.NumStr(Js.Or0(row[LK.Ordinal])));
            return kind;
        }

        /// <summary>flaskSlotCap(balance): balance.flaskSlots, a positive integer.</summary>
        public static int FlaskSlotCap(LoopData d)
        {
            var n = d.Balance[LK.FlaskSlots];
            if (!Js.IsInt(n) || Js.D(n) <= 0) throw new InvalidOperationException(LM.FlaskSlotsNotPositive);
            return (int)Js.D(n);
        }

        /// <summary><c>balance.equipment.storageSlots || 8</c>: the bag's cap.</summary>
        public static int StorageSlots(LoopData d)
        {
            var n = d.Run.EquipmentBalance[RK.StorageSlots];
            return Js.Truthy(n) ? (int)Js.D(n) : (int)d.RuleNum(WK.Rewards, RK.StorageSlots);
        }

        // ------------------------------------------------------------------ taking rows

        private void GrantCinders()
        {
            var row = Rows.OfType<JObject>().FirstOrDefault(r => r.Str(K.Kind) == RK.Cinders);
            if (row == null || Js.Truthy(_states[RK.Cinders]) || Js.Truthy(row[LK.BlockedBy])) return;
            if (Apply(row))
            {
                _states[RK.Cinders] = WV.Taken;
                PersistProgress();
                _taken.Add(RK.Cinders);
            }
        }

        /// <summary>A tap on one row (the screen's take): a choice row names its pick; refused rows and taken rows land nothing.</summary>
        public bool Take(string key, string pickId = null)
        {
            var row = Rows.OfType<JObject>().FirstOrDefault(r => r.Str(LK.Key) == key);
            if (row == null || Js.Truthy(_states[key])) return false;
            var kind = row.Str(K.Kind);
            var taken = (JObject)row.DeepClone();
            if (pickId != null) taken[kind == LV.ClassDraft ? K.NodeId : K.CardId] = pickId;
            if (!Apply(taken)) return false;
            _states[key] = WV.Taken;
            PersistProgress();
            _taken.Add(key);
            if (kind == K.Card || kind == LV.SkillDraft) RecordSeen(K.Card, taken.Str(K.CardId));
            return true;
        }

        /// <summary>A Skip on one row: Continue leaves it even under auto-collect.</summary>
        public void Skip(string key)
        {
            _states[key] = LV.Skipped;
            PersistProgress();
        }

        /// <summary>
        /// Continue (resolveContinue): under 'auto' every pending, unblocked, unskipped row is taken (a choice resolved on
        /// 'cardRewards'); under 'manual' nothing more. Then the checkpoint closes (its onDone): the pending reward leaves
        /// the run and the receipt names where the door leads.
        /// </summary>
        public ClaimReceipt Continue(string mode)
        {
            foreach (var row in Rows.OfType<JObject>().ToList())
            {
                var key = row.Str(LK.Key);
                var state = _states.Str(key);
                if (state == WV.Taken) continue;
                if (Js.Truthy(row[LK.BlockedBy])) continue;
                if (mode != LV.CollectAuto || state == LV.Skipped) continue;
                var kind = row.Str(K.Kind);
                var take = (JObject)row.DeepClone();
                if (kind == K.Card || kind == LV.SkillDraft || kind == LV.ClassDraft)
                {
                    var ids = row[WK.NodeIds] is JArray nodes ? nodes : row.Arr(WK.CardIds);
                    var id = row.Is(LK.Choice) ? ids[_ctx.Rng.Int(Ashen.Generated.RngStream.CardRewards, 0, ids.Count - 1) % ids.Count] : ids[0];
                    take[kind == LV.ClassDraft ? K.NodeId : K.CardId] = id.DeepClone();
                }
                if (Apply(take))
                {
                    _states[key] = WV.Taken;
                    PersistProgress();
                    _taken.Add(key);
                }
            }
            var receipt = new ClaimReceipt { Taken = new List<string>(_taken), ChosenCardId = _chosenCardId };
            if (_checkpoint != null)
            {
                receipt.After = _checkpoint.Str(WK.After);
                _ctx.Run.Remove(WK.PendingReward);
            }
            return receipt;
        }

        private void PersistProgress()
        {
            if (_checkpoint == null) return;
            _checkpoint[WK.States] = _states.DeepClone();
            _checkpoint[WK.ChosenCardId] = Js.S(_chosenCardId);
            _checkpoint[WK.ChosenDraftCardIds] = _chosenDraftCardIds.DeepClone();
            _checkpoint[WK.ChosenDraftNodeIds] = _chosenDraftNodeIds.DeepClone();
        }

        /// <summary>The one apply per kind (tap and auto-collect share it): false when the row landed nothing.</summary>
        private bool Apply(JObject row)
        {
            var d = _ctx.Data;
            var run = _ctx.Run;
            switch (row.Str(K.Kind))
            {
                case RK.Cinders:
                    run.Put(RK.Cinders, run.Num(RK.Cinders) + row.Num(K.Amount));
                    return true;
                case WK.SmithingStone:
                    return false;
                case K.Card:
                    AddDeckCard(row.Str(K.CardId), false);
                    _chosenCardId = row.Str(K.CardId);
                    return true;
                case LV.ClassDraft:
                {
                    if (!PickClassNode(d, run, row.Str(K.NodeId))) return false;
                    if (!SpendSkillDraft(run, Skills.ClassSkillId(RunJs.Key(run[RK.Class]))))
                    {
                        var tags = run.Arr(K.CoreTags);
                        tags.RemoveAt(tags.Count - 1);
                        return false;
                    }
                    _chosenDraftNodeIds[row.Str(LK.Key)] = row[K.NodeId]?.DeepClone();
                    return true;
                }
                case LV.SkillDraft:
                {
                    var skillId = row.Str(WK.SkillId);
                    if (!SpendSkillDraft(run, skillId)) return false;
                    AddDeckCard(row.Str(K.CardId), Skills.UpgradesCards(d.Rewards, Skills.LevelOf(run, skillId)));
                    _chosenDraftCardIds[row.Str(LK.Key)] = row[K.CardId]?.DeepClone();
                    return true;
                }
                case LV.FlaskKind:
                    run.Arr(K.Flasks).Add(Js.Obj(K.FlaskId, row[K.FlaskId]));
                    RecordSeen(LV.FlaskKind, row.Str(K.FlaskId));
                    return true;
                case V.RelicKind:
                    run.Arr(K.Relics).Add(row[K.RelicId]?.DeepClone());
                    Creation.SyncFlaskGrowth(d.Run, run);
                    RecordSeen(V.RelicKind, row.Str(K.RelicId));
                    return true;
                case LV.ArmamentKind:
                    return CollectArmament(_ctx, row.Str(WK.ArmamentId), _source);
                default:
                    throw new InvalidOperationException(RunJs.Fmt(LM.UnknownRewardKind, row.Str(K.Kind)));
            }
        }

        /// <summary>A card joins the deck as <c>r&lt;deck length&gt;_&lt;cardId&gt;</c>.</summary>
        private void AddDeckCard(string cardId, bool upgraded)
        {
            var deck = _ctx.Run.Arr(K.Deck);
            var d = _ctx.Data;
            var id = d.RuleStr(WK.Rewards, LK.CardInstancePrefix) + RunJs.NumStr(deck.Count) + d.RuleStr(WK.Rewards, LK.CardInstanceSeparator) + cardId;
            deck.Add(Js.Obj(K.InstanceId, id, K.CardId, cardId, K.Upgraded, upgraded));
        }

        /// <summary>The profile's seen record (a best-effort write on take): cards, relics and flasks by id.</summary>
        private void RecordSeen(string kind, string id)
        {
            if (id == null) return;
            var key = kind == K.Card ? K.Cards : kind == V.RelicKind ? K.Relics : kind == LV.FlaskKind ? K.Flasks : null;
            if (key == null) return;
            var meta = _ctx.Profile;
            var seen = Js.Spread(meta.Obj(LK.Seen));
            var list = new JArray(Js.Items(seen[key]).Select(t => t.DeepClone()));
            if (!Js.Includes(list, id)) list.Add(id);
            seen[key] = list;
            meta[LK.Seen] = seen;
            RunEnd.SaveMeta(_ctx.Data, meta);
        }

        /// <summary>pickClassNode(registries, run, nodeId): a node the class may draft joins the core card's tags.</summary>
        public static bool PickClassNode(LoopData d, JObject run, string nodeId)
        {
            if (run == null || !run.Is(RK.Class)) return false;
            var level = Skills.LevelOf(run, Skills.ClassSkillId(RunJs.Key(run[RK.Class])));
            if (!ClassTree.DraftPool(d.Rewards, run.Str(RK.Class), run[K.CoreTags] ?? new JArray(), level).Contains(nodeId)) return false;
            if (!(run[K.CoreTags] is JArray tags)) run[K.CoreTags] = tags = new JArray();
            tags.Add(nodeId);
            return true;
        }

        /// <summary>spendSkillDraft(run, skillId): one queued draft of the track is spent.</summary>
        public static bool SpendSkillDraft(JObject run, string skillId)
        {
            var row = run?.Obj(K.Skills)?.Obj(skillId);
            if (row == null || !(row.Num(WK.PendingDrafts) > 0)) return false;
            row.Put(WK.PendingDrafts, row.Num(WK.PendingDrafts) - 1);
            return true;
        }

        // ------------------------------------------------------------------ armaments (main.js collectArmament)

        /// <summary>
        /// collectArmament(id, source): the piece goes into the run's storage (refused at the cap and on a duplicate) and,
        /// once stored, into the profile's found set with its discovery receipt.
        /// </summary>
        public static bool CollectArmament(LoopContext ctx, string id, string source)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (!AddToStorage(ctx.Run.Obj(K.Loadout), id, StorageSlots(ctx.Data))) return false;
            RecordCollectedArmament(ctx, id, source);
            return true;
        }

        /// <summary>addToStorage(loadout, itemId, cap): false at the cap and on a duplicate.</summary>
        public static bool AddToStorage(JObject loadout, string itemId, int cap)
        {
            if (loadout == null || string.IsNullOrEmpty(itemId)) return false;
            if (!(loadout[RK.Storage] is JArray storage) || !Js.Truthy(loadout[RK.Storage])) loadout[RK.Storage] = storage = new JArray();
            if (Js.Includes(storage, itemId)) return false;
            if (storage.Count >= cap) return false;
            storage.Add(itemId);
            return true;
        }

        /// <summary>recordCollectedArmament(id, source): a carried piece joins meta.found (permanentOnFind) with its discovery receipt.</summary>
        public static void RecordCollectedArmament(LoopContext ctx, string id, string source)
        {
            var d = ctx.Data;
            var run = ctx.Run;
            if (!RewardRolls.CarriedIds(run.Obj(K.Loadout)).Contains(id)) return;
            if (!(d.Run.EquipmentBalance.Obj(RK.Drops)?.Is(LK.PermanentOnFind) ?? false)) return;
            var meta = ctx.Profile;
            if (Js.Includes(meta[LK.Found], id)) return;
            var found = new JArray(Js.Items(meta[LK.Found]).Select(t => t.DeepClone())) { id };
            meta[LK.Found] = found;
            var mode = RunEnd.IsCustomRun(run.Obj(RK.Custom)) ? LV.CustomProgression : LV.NormalProgression;
            RecordArmamentDiscovery(d, meta, id, mode, source, run[RK.SeedString],
                d.Run.EquipmentBalance.Obj(LK.StartingKitDiscovery)?[LK.ReceiptLimit]);
            RunEnd.SaveMeta(d, meta);
        }

        /// <summary>
        /// recordArmamentDiscovery(meta, pieceId, { progressionMode, source, runSeed, receiptLimit }): a first find in a
        /// normal climb appends to discoveredArmaments and writes a numbered receipt (the newest receiptLimit kept).
        /// </summary>
        public static void RecordArmamentDiscovery(LoopData d, JObject meta, string pieceId, string mode, string source, JToken runSeed, JToken receiptLimit)
        {
            var discovered = new JArray(Js.Items(meta[RK.DiscoveredArmaments]).Select(Js.Str).Distinct().Select(s => (JToken)s));
            var receipts = new JArray(Js.Items(meta[LK.DiscoveryReceipts]).Select(t => t.DeepClone()));
            if (mode != LV.NormalProgression || Js.Includes(discovered, pieceId))
            {
                meta[RK.DiscoveredArmaments] = discovered;
                meta[LK.DiscoveryReceipts] = receipts;
                return;
            }
            var receipt = Js.Obj(K.Kind, LV.ArmamentDiscovery, K.PieceId, pieceId, MK.First, true, RK.Source, source,
                LK.RunSeed, Js.Nullish(runSeed) ? Js.Null() : Js.S(RunJs.Key(runSeed)), LK.Sequence, (double)(receipts.Count + 1));
            discovered.Add(pieceId);
            receipts.Add(receipt);
            var limit = Js.IsInt(receiptLimit) && Js.D(receiptLimit) > 0 ? (int)Js.D(receiptLimit) : (int)d.RuleNum(WK.Rewards, LK.ReceiptLimit);
            meta[RK.DiscoveredArmaments] = discovered;
            meta[LK.DiscoveryReceipts] = new JArray(receipts.Skip(Math.Max(0, receipts.Count - limit)).Select(t => t.DeepClone()));
        }
    }
}
