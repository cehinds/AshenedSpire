using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using CombatMath = Ashen.Generated.CombatMath;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RM = Ashen.Generated.RunMessages;
using RV = Ashen.Generated.RunValues;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Run
{
    /// <summary>A validated weapon card package (shipped WeaponCardPackageModel.fromPiece result).</summary>
    public sealed class WeaponCardPackage
    {
        public string WeaponId;
        public double HandsRequired;
        public List<KeyValuePair<string, string>> PriorityAttackRefs = new List<KeyValuePair<string, string>>();
        public List<KeyValuePair<string, double>> GrantedCards = new List<KeyValuePair<string, double>>();
        public List<string> WeaponArtDefaults = new List<string>();

        /// <summary>{ attackProfileId, guardProfileId, artCardId } or null.</summary>
        public JObject CombatKit;
        public string FillerAttackProfileId;
    }

    /// <summary>One hand's source (shipped handSource).</summary>
    public sealed class HandSource
    {
        public string Hand;
        public string SlotId;
        public JObject Piece;
        public WeaponCardPackage Package;
    }

    /// <summary>The equipped weapon card plan (shipped buildEquippedWeaponCardPlan result).</summary>
    public sealed class WeaponCardPlan
    {
        public int AttackSlotCount;
        public string Fingerprint;
        public List<JObject> Slots = new List<JObject>();
    }

    /// <summary>
    /// The weapon-package seam (shipped model/loadout.js WeaponCardPackageModel, handSource, quotaRefs,
    /// buildEquippedWeaponCardPlan, applyEquippedWeaponCardPlan; model/cardRemoval.js retiredAttackSlots;
    /// framework/deck.js splitAuthoredWeaponArts).
    /// </summary>
    public static class WeaponCards
    {
        private static readonly Regex AttackSlotPattern = new Regex(Ashen.Generated.RunPatterns.AttackSlot, RegexOptions.CultureInvariant);

        private static string Compatibility(RunData d) => d.RuleStr(K.Loadout, RK.PackageCompatibility);

        private static JObject AttackProfileFor(RunData d, JToken profileId, string owner)
        {
            var profile = Loadout.ProfileById(d, profileId);
            if (profile == null) throw new InvalidOperationException(RunJs.Fmt(RM.MissingAttackProfile, owner, RunJs.Key(profileId)));
            if (profile.Str(K.Role) != RV.RoleAttack || profile.Str(RK.Compatibility) != Compatibility(d))
                throw new InvalidOperationException(RunJs.Fmt(RM.AttackProfileIncompatible, owner, RunJs.Key(profileId), Compatibility(d)));
            if (!Js.Truthy(profile[RK.BaseCardId]) || !d.Cards.Has(profile.Str(RK.BaseCardId)))
                throw new InvalidOperationException(RunJs.Fmt(RM.AttackProfileNoFiller, owner, RunJs.Key(profileId)));
            return profile;
        }

        private static JObject CardRef(JToken raw) => Js.IsStr(raw) ? Js.Obj(K.CardId, raw.DeepClone()) : raw as JObject;

        /// <summary>WeaponCardPackageModel.fromPiece(registries, piece): the piece's package, validated, or null.</summary>
        public static WeaponCardPackage FromPiece(RunData d, JObject piece)
        {
            if (piece == null) return null;
            var id = piece.Str(K.Id);
            var explicitPackage = piece[K.WeaponCardPackage];
            if (Js.Nullish(explicitPackage) && !Js.Truthy(piece[K.AttackProfile])) return null;
            if (!Js.Nullish(explicitPackage) && !(explicitPackage is JObject)) throw new InvalidOperationException(RunJs.Fmt(RM.PackageNotObject, id));
            var source = explicitPackage as JObject ?? new JObject();
            var isExplicit = explicitPackage is JObject;
            if (isExplicit && source.Str(RK.Compatibility) != Compatibility(d)) throw new InvalidOperationException(RunJs.Fmt(RM.PackageCompatibility, id, Compatibility(d)));
            var handsToken = RunJs.Coalesce(source[K.HandsRequired], piece[K.HandsRequired]);
            var handsRequired = handsToken == null ? 1 : Js.D(handsToken);
            if (!Js.IsNum(handsToken ?? Js.N(1)) || !d.RuleNums(K.Loadout, K.HandsRequired).Contains(handsRequired))
                throw new InvalidOperationException(RunJs.Fmt(RM.PackageHandsRequired, id));
            var fillerId = isExplicit ? source[RK.FillerAttackProfileId] : piece[K.AttackProfile];
            if (!Js.Truthy(fillerId)) throw new InvalidOperationException(RunJs.Fmt(RM.PackageMissingFiller, id));
            var filler = AttackProfileFor(d, fillerId, id);
            var priorityRaw = Js.Nullish(source[RK.PriorityAttackRefs]) ? new JArray() : source[RK.PriorityAttackRefs];
            if (!(priorityRaw is JArray priorityRefs)) throw new InvalidOperationException(RunJs.Fmt(RM.PackagePrioritiesNotArray, id));
            var grantedRaw = Js.Nullish(source[RK.GrantedCards]) ? new JArray() : source[RK.GrantedCards];
            if (!(grantedRaw is JArray grantedCards)) throw new InvalidOperationException(RunJs.Fmt(RM.PackageGrantsNotArray, id));

            void Trackable(string cardId, string what)
            {
                var def = d.Cards.Get(cardId);
                var destination = Framework.AfterPlayDestination(d.Combat, def);
                if (destination == V.RemovedFromPlay)
                    throw new InvalidOperationException(RunJs.Fmt(RM.PackageCardRemovedFromPlay, id, what, cardId, def.Str(K.Type)));
            }

            var package = new WeaponCardPackage { WeaponId = id, HandsRequired = handsRequired, FillerAttackProfileId = filler.Str(K.Id) };
            var grantedSeen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < grantedCards.Count; i++)
            {
                var r = CardRef(grantedCards[i]);
                if (r == null || !Js.Truthy(r[K.CardId])) throw new InvalidOperationException(RunJs.Fmt(RM.PackageGrantNeedsCard, id, i));
                var cardId = r.Str(K.CardId);
                if (!d.Cards.Has(cardId)) throw new InvalidOperationException(RunJs.Fmt(RM.PackageGrantUnknown, id, cardId));
                var count = Js.Nullish(r[K.Count]) ? Js.N(1) : r[K.Count];
                if (!Js.IsInt(count) || Js.D(count) < 1) throw new InvalidOperationException(RunJs.Fmt(RM.PackageGrantCount, id, i));
                if (grantedSeen.Contains(cardId)) throw new InvalidOperationException(RunJs.Fmt(RM.PackageGrantDuplicate, id, cardId));
                Trackable(cardId, RV.GrantedCardLabel);
                grantedSeen.Add(cardId);
                package.GrantedCards.Add(new KeyValuePair<string, double>(cardId, Js.D(count)));
            }
            var artsRaw = Js.Nullish(source[RK.WeaponArtDefaults]) ? new JArray() : source[RK.WeaponArtDefaults];
            if (!(artsRaw is JArray arts)) throw new InvalidOperationException(RunJs.Fmt(RM.PackageArtsNotArray, id));
            var artSeen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < arts.Count; i++)
            {
                var artId = Js.Str(arts[i]);
                if (string.IsNullOrEmpty(artId)) throw new InvalidOperationException(RunJs.Fmt(RM.PackageArtNeedsCard, id, i));
                if (!d.Cards.Has(artId)) throw new InvalidOperationException(RunJs.Fmt(RM.PackageArtUnknown, id, artId));
                if (artSeen.Contains(artId)) throw new InvalidOperationException(RunJs.Fmt(RM.PackageArtDuplicate, id, artId));
                Trackable(artId, RV.WeaponArtLabel);
                artSeen.Add(artId);
                package.WeaponArtDefaults.Add(artId);
            }
            if (!Js.Nullish(source[RK.CombatKit]))
            {
                if (!(source[RK.CombatKit] is JObject kit)) throw new InvalidOperationException(RunJs.Fmt(RM.PackageKitNotObject, id));
                foreach (var role in d.RuleList(K.Loadout, RK.KitRoles))
                {
                    var profile = Loadout.ProfileById(d, kit[role + RV.ProfileIdSuffix]);
                    if (profile == null || profile.Str(K.Role) != role) throw new InvalidOperationException(RunJs.Fmt(RM.PackageKitProfile, id, role));
                }
                if (!package.WeaponArtDefaults.Contains(kit.Str(RK.ArtCardId))) throw new InvalidOperationException(RunJs.Fmt(RM.PackageKitArt, id));
                package.CombatKit = Js.Obj(RK.AttackProfileId, kit[RK.AttackProfileId], RK.GuardProfileId, kit[RK.GuardProfileId], RK.ArtCardId, kit[RK.ArtCardId]);
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < priorityRefs.Count; i++)
            {
                var r = CardRef(priorityRefs[i]);
                if (r == null || !Js.Truthy(r[K.CardId])) throw new InvalidOperationException(RunJs.Fmt(RM.PackagePriorityNeedsCard, id, i));
                var cardId = r.Str(K.CardId);
                if (!d.Cards.Has(cardId)) throw new InvalidOperationException(RunJs.Fmt(RM.PackagePriorityUnknown, id, cardId));
                var profileId = Js.Truthy(r[K.ProfileId]) ? r.Str(K.ProfileId) : filler.Str(K.Id);
                AttackProfileFor(d, profileId, id);
                var key = cardId + V.CacheKeySeparator + profileId;
                if (seen.Contains(key)) throw new InvalidOperationException(RunJs.Fmt(RM.PackagePriorityDuplicate, id, key));
                seen.Add(key);
                package.PriorityAttackRefs.Add(new KeyValuePair<string, string>(cardId, profileId));
            }
            return package;
        }

        /// <summary>handSource(registries, loadout, classId, hand): the hand's slot, piece and package.</summary>
        public static HandSource Hand(RunData d, JObject loadout, string classId, string hand)
        {
            var slot = d.EquipmentRows(K.Slots).FirstOrDefault(row => Loadout.SlotHand(d, row) == hand);
            if (slot == null) throw new InvalidOperationException(RunJs.Fmt(RM.NoSlotForHand, hand));
            var piece = Combat.Equipment.EquippedIn(d.Combat, loadout, classId, slot.Str(K.Id));
            return new HandSource { Hand = hand, SlotId = slot.Str(K.Id), Piece = piece, Package = FromPiece(d, piece) };
        }

        private static List<JObject> QuotaRefs(RunData d, HandSource source, int count)
        {
            var filler = AttackProfileFor(d, source.Package.FillerAttackProfileId, source.Package.WeaponId);
            var refs = source.Package.PriorityAttackRefs.Take(count).ToList();
            while (refs.Count < count) refs.Add(new KeyValuePair<string, string>(filler.Str(RK.BaseCardId), filler.Str(K.Id)));
            return refs.Select(r => Js.Obj(K.SourceHand, source.Hand, K.WeaponId, source.Package.WeaponId, K.CardId, r.Key, K.ProfileId, r.Value)).ToList();
        }

        /// <summary>retiredAttackSlots(count, ids): permanently removed attack slots, validated against the birth quota.</summary>
        public static HashSet<string> RetiredAttackSlots(double count, JToken ids)
        {
            var list = ids == null ? new JArray() : ids as JArray;
            var strings = list?.Select(Js.Str).ToList();
            if (list == null || strings.Distinct(StringComparer.Ordinal).Count() != strings.Count
                || list.Any(t => !Js.IsStr(t) || !AttackSlotPattern.IsMatch(Js.Str(t)) || Math.Floor(count) != count
                    || double.Parse(AttackSlotPattern.Match(Js.Str(t)).Groups[V.GroupIndex].Value, System.Globalization.CultureInfo.InvariantCulture) >= count))
                throw new InvalidOperationException(RM.RemovedAttackSlotsInvalid);
            return new HashSet<string>(strings, StringComparer.Ordinal);
        }

        /// <summary>buildEquippedWeaponCardPlan(registries, loadout, classId, { attackSlotCount, removedAttackSlotIds }).</summary>
        public static WeaponCardPlan BuildPlan(RunData d, JObject loadout, string classId, double? attackSlotCount, JToken removedAttackSlotIds)
        {
            var composed = !string.IsNullOrEmpty(classId) && StartingDeck.Config(d) != null ? StartingDeck.Plan(d, loadout, classId) : null;
            var configured = composed != null ? composed.AttackCount : RunJs.Number(d.EquipmentBalance.Obj(RK.RoleCopies)?[RV.RoleAttack]);
            var count = attackSlotCount ?? configured;
            if (Math.Floor(count) != count || count < 0 || double.IsInfinity(count))
                throw new InvalidOperationException(RunJs.Fmt(RM.AttackSlotCountInvalid, attackSlotCount.HasValue ? RunJs.NumStr(attackSlotCount.Value) : V.Undefined));
            var right = Hand(d, loadout, classId, V.Right);
            var left = Hand(d, loadout, classId, V.Left);
            if (right.Piece != null && left.Piece != null && right.Piece.Str(K.Id) == left.Piece.Str(K.Id))
                throw new InvalidOperationException(RunJs.Fmt(RM.DuplicateEquippedArmament, right.Piece.Str(K.Id)));
            var eligible = new[] { right, left }.Where(s => s.Package != null).ToList();
            var twoHandedHands = d.RuleNum(K.Loadout, RK.TwoHandedHands);
            var twoHanded = eligible.FirstOrDefault(s => s.Package.HandsRequired == twoHandedHands);
            if (twoHanded != null && ((twoHanded.Hand == V.Right ? left.Piece : right.Piece) != null || eligible.Count > 1))
                throw new InvalidOperationException(RunJs.Fmt(RM.TwoHandedConflict, twoHanded.Package.WeaponId));

            var n = (int)count;
            List<JObject> refs;
            if (twoHanded != null) refs = QuotaRefs(d, twoHanded, n);
            else if (eligible.Count == CombatMath.Two)
            {
                var nonShield = eligible.Where(s => s.Piece?.Str(K.Kind) != RV.ShieldKind).ToList();
                if (nonShield.Count == 1) refs = QuotaRefs(d, nonShield[0], n);
                else refs = QuotaRefs(d, right, (int)Math.Ceiling(count / CombatMath.Two)).Concat(QuotaRefs(d, left, (int)Math.Floor(count / CombatMath.Two))).ToList();
            }
            else if (eligible.Count == 1) refs = QuotaRefs(d, eligible[0], n);
            else
            {
                var profile = AttackProfileFor(d, d.EquipmentBalance.Obj(RK.UnarmedProfiles)?[RV.RoleAttack], RM.ZeroWeaponPlan);
                refs = Enumerable.Range(0, n).Select(_ => Js.Obj(K.SourceHand, Js.Null(), K.WeaponId, Js.Null(), K.CardId, profile[RK.BaseCardId], K.ProfileId, profile[K.Id])).ToList();
            }
            var retired = RetiredAttackSlots(count, removedAttackSlotIds);
            var slots = new List<JObject>();
            for (var i = 0; i < refs.Count; i++)
            {
                var slot = Js.Obj(K.EquipmentAttackSlotId, RV.AttackSlotPrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var p in refs[i].Properties()) slot[p.Name] = p.Value.DeepClone();
                if (!retired.Contains(slot.Str(K.EquipmentAttackSlotId))) slots.Add(slot);
            }
            string Part(JObject s, string key) => Js.Truthy(s[key]) ? s.Str(key) : V.Minus;
            var fingerprint = string.Join(V.CacheKeySeparator, slots.Select(s => string.Join(V.KeySeparator,
                s.Str(K.EquipmentAttackSlotId), Part(s, K.SourceHand), Part(s, K.WeaponId), s.Str(K.CardId), s.Str(K.ProfileId))));
            return new WeaponCardPlan { AttackSlotCount = slots.Count, Fingerprint = fingerprint, Slots = slots };
        }

        /// <summary>applyEquippedWeaponCardPlan(plan, cards, { allowSubset }): rebind the attack instances in place.</summary>
        public static int ApplyPlan(WeaponCardPlan plan, IEnumerable<JObject> cards, bool allowSubset)
        {
            if (plan == null) throw new InvalidOperationException(RM.ApplyPlanNeedsPlan);
            var attacks = (cards ?? Enumerable.Empty<JObject>()).Where(c => c.Str(K.EquipmentRole) == RV.RoleAttack).ToList();
            if (!allowSubset && attacks.Count != plan.AttackSlotCount)
                throw new InvalidOperationException(RunJs.Fmt(RM.AttackInstanceCountMismatch, attacks.Count, plan.AttackSlotCount));
            if (attacks.Any(c => !Js.Truthy(c[K.EquipmentAttackSlotId])))
            {
                if (allowSubset || attacks.Count != plan.AttackSlotCount) throw new InvalidOperationException(RM.LegacyAttackSlotsNeedFullDeck);
                for (var i = 0; i < attacks.Count; i++) attacks[i][K.EquipmentAttackSlotId] = RV.AttackSlotPrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            var byId = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var slot in plan.Slots) byId[slot.Str(K.EquipmentAttackSlotId)] = slot;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var card in attacks)
            {
                var slotId = card.Str(K.EquipmentAttackSlotId);
                if (seen.Contains(slotId)) throw new InvalidOperationException(RunJs.Fmt(RM.DuplicateAttackSlot, slotId));
                seen.Add(slotId);
                if (!byId.TryGetValue(slotId, out var slot)) throw new InvalidOperationException(RunJs.Fmt(RM.UnknownAttackSlot, slotId));
                card[K.CardId] = slot[K.CardId].DeepClone();
                card[K.ProfileId] = slot[K.ProfileId].DeepClone();
                card[K.EquipmentPlanFingerprint] = plan.Fingerprint;
                foreach (var key in new[] { K.SourceHand, K.WeaponId, K.SourceEquipmentInstanceId })
                {
                    if (Js.Truthy(slot[key])) card[key] = slot[key].DeepClone();
                    else card.Remove(key);
                }
            }
            return attacks.Count;
        }

        /// <summary>splitAuthoredWeaponArts(right, left): ceil/floor quotas, unique preference right then left.</summary>
        public static List<KeyValuePair<string, string>> SplitAuthoredWeaponArts(List<string> rightArtIds, List<string> leftArtIds)
        {
            var pool = rightArtIds.Select(id => new KeyValuePair<string, string>(id, V.Right)).Concat(leftArtIds.Select(id => new KeyValuePair<string, string>(id, V.Left))).ToList();
            var total = pool.Count;
            var rightQuota = (int)Math.Ceiling(total / CombatMath.Two);
            var leftQuota = (int)Math.Floor(total / CombatMath.Two);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            List<KeyValuePair<string, string>> Take(string hand, int quota)
            {
                var taken = new List<KeyValuePair<string, string>>();
                foreach (var art in pool)
                {
                    if (taken.Count >= quota) break;
                    if (art.Value != hand || seen.Contains(art.Key)) continue;
                    seen.Add(art.Key);
                    taken.Add(new KeyValuePair<string, string>(art.Key, hand));
                }
                return taken;
            }
            return Take(V.Right, rightQuota).Concat(Take(V.Left, leftQuota)).ToList();
        }
    }
}
