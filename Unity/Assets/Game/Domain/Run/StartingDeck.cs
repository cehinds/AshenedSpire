using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RM = Ashen.Generated.RunMessages;
using RV = Ashen.Generated.RunValues;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Run
{
    /// <summary>startingDeckPlan: the composed deck's arithmetic, no cards.</summary>
    public sealed class DeckPlan
    {
        public double Size;
        public List<JObject> Grants;
        public double Filler;
        public double AttackCount;
        public double GuardCount;
        public double Bias;
        public bool OddGoesToAttack;
        public double Cap;
        public double PackageCards;
    }

    /// <summary>
    /// The composed starting deck and the cards items own (shipped model/loadout.js: startingDeckConfig/Settings,
    /// grantRefsFor, sortBySourceOrder, startingDeckPlan, startingDeckRefs, orderStartingDeck, boundGrantCardIds,
    /// reconcileGrantedCards, desiredGrantInstances and the three minters, stampDeck).
    /// </summary>
    public static class StartingDeck
    {
        /// <summary>startingDeckSettings: balance.equipment.startingDeck, without the enabled gate.</summary>
        public static JObject Settings(RunData d) => d.EquipmentBalance.Obj(RK.StartingDeck);

        /// <summary>startingDeckConfig: the composed-deck config, or null when the legacy roleCopies path is live.</summary>
        public static JObject Config(RunData d)
        {
            var cfg = Settings(d);
            if (cfg == null || !(cfg[K.Enabled]?.Type == JTokenType.Boolean && cfg.Is(K.Enabled))) return null;
            return cfg;
        }

        /// <summary>grantSourceFor(cfg, role): the grantSource tag a minting seam stamps, or null.</summary>
        public static JToken GrantSourceFor(JObject cfg, string role)
        {
            var bound = cfg?.Obj(RK.Sources)?[role];
            return Js.IsStr(bound) && Js.Str(bound).Length > 0 ? bound : Js.Null();
        }

        private static string ScopeOfPiece(RunData d, string family, JObject piece)
        {
            var spec = d.TagFamilies.OfType<JObject>().FirstOrDefault(row => row.Str(RK.Family) == family && family != null);
            if (spec == null || !Js.Truthy(spec[RK.ScopeField])) return string.Empty;
            var v = piece?[spec.Str(RK.ScopeField)];
            return Js.Truthy(v) ? Js.Str(v) : string.Empty;
        }

        /// <summary>boundGrantCardIds(registries, object, family): the cards a `bound`-tagged piece carries.</summary>
        public static List<string> BoundGrantCardIds(RunData d, JObject piece, string family)
        {
            if (piece == null || !Js.Includes(piece[K.Tags], RV.BoundTag)) return new List<string>();
            var row = d.EquipmentRows(RK.EquipmentGrants).FirstOrDefault(entry => entry.Str(K.SourceId) == piece.Str(K.Id)
                && (family == null || !Js.Truthy(entry[RK.Family]) || entry.Str(RK.Family) == family)
                && (Js.Truthy(entry[RK.Scope]) ? entry.Str(RK.Scope) : string.Empty) == ScopeOfPiece(d, Js.Truthy(entry[RK.Family]) ? entry.Str(RK.Family) : family, piece));
            return row != null && Js.Truthy(row[K.Cards]) ? RunJs.Strs(row[K.Cards]) : new List<string>();
        }

        /// <summary>sortBySourceOrder(cfg, rows, sourceOf): a stable sort by startingDeck.sourceOrder; unnamed sources last.</summary>
        public static List<JObject> SortBySourceOrder(JObject cfg, List<JObject> rows, Func<JObject, JToken> sourceOf)
        {
            var order = cfg?[RK.SourceOrder] is JArray a ? a.ToList() : new List<JToken>();
            int Rank(JObject row)
            {
                var source = sourceOf(row);
                var at = order.FindIndex(o => JToken.DeepEquals(o, source ?? JValue.CreateUndefined()) && source != null);
                return at == -1 ? order.Count : at;
            }
            return rows.Select((row, index) => new { row, index, rank = Rank(row) }).OrderBy(e => e.rank).ThenBy(e => e.index).Select(e => e.row).ToList();
        }

        /// <summary>grantRefsFor: the cards a source hands over outright, one copy each, before any filler.</summary>
        private static List<JObject> GrantRefsFor(RunData d, JObject loadout, string classId, JObject cfg, KitRow techniqueRow)
        {
            var cls = d.Classes.Get(classId);
            var grants = new List<JObject>();
            var hasCombatKit = d.RuleList(K.Loadout, RK.HandOrder).Any(hand => WeaponCards.Hand(d, loadout, classId, hand).Package?.CombatKit != null);
            if (techniqueRow != null && techniqueRow.Profile != null && !hasCombatKit)
                grants.Add(Js.Obj(RK.Source, GrantSourceFor(cfg, RK.Weapon), K.CardId, techniqueRow.Profile[RK.BaseCardId],
                    K.EquipmentRole, RV.RoleTechnique, K.ProfileId, techniqueRow.Profile[K.Id]));
            if (cfg.Obj(RK.Global)?[RK.Grants] is JArray globalGrants)
                foreach (var cardId in globalGrants) grants.Add(Js.Obj(RK.Source, GrantSourceFor(cfg, RK.Global), K.CardId, cardId.DeepClone()));
            if (Js.Truthy(cls[RK.StartingSignatureCard])) grants.Add(Js.Obj(RK.Source, GrantSourceFor(cfg, RK.Class), K.CardId, cls[RK.StartingSignatureCard]));
            if (Js.Truthy(cls[RK.AbilityCard])) grants.Add(Js.Obj(RK.Source, GrantSourceFor(cfg, RK.Class), K.CardId, cls[RK.AbilityCard]));
            return SortBySourceOrder(cfg, grants, g => g[RK.Source]);
        }

        /// <summary>startingDeckPlan(registries, loadout, classId): grants first, filler split by the class bias.</summary>
        public static DeckPlan Plan(RunData d, JObject loadout, string classId)
        {
            var cfg = Config(d);
            if (cfg == null) return null;
            var cap = RunJs.Number(d.Balance[RK.StartingDeckSize]);
            var rows = Loadout.KitPlan(d, loadout, classId);
            var grants = GrantRefsFor(d, loadout, classId, cfg, rows.FirstOrDefault(r => r.Role == RV.RoleTechnique));
            var packageCards = DesiredGrantInstances(d, loadout, classId, null, null).Count;
            var bound = grants.Count + packageCards;
            var filler = Math.Max(0, cap - bound);
            var biasToken = RunJs.Coalesce(cfg.Obj(K.Classes)?.Obj(classId)?[RK.StrikeBias], cfg[RK.DefaultStrikeBias]);
            var bias = biasToken == null ? d.RuleNum(K.Loadout, RK.DefaultStrikeBias) : RunJs.Number(biasToken);
            var odd = RunJs.Coalesce(cfg[RK.OddFillerGoesTo]);
            var oddGoesToAttack = (odd == null ? d.RuleStr(K.Loadout, RK.DefaultOddFillerGoesTo) : Js.Str(odd)) != RV.RoleGuard;
            var exact = filler * bias;
            var attackCount = Math.Min(filler, Math.Max(0, oddGoesToAttack ? RunJs.Round(exact) : Math.Ceiling(exact - Ashen.Generated.CombatMath.Half)));
            return new DeckPlan
            {
                Size = bound + filler, Grants = grants, Filler = filler, AttackCount = attackCount, GuardCount = filler - attackCount,
                Bias = bias, OddGoesToAttack = oddGoesToAttack, Cap = cap, PackageCards = packageCards,
            };
        }

        /// <summary>startingDeckRefs(registries, loadout, classId): the starting deck as instance-ready refs.</summary>
        public static List<JObject> Refs(RunData d, JObject loadout, string classId)
        {
            var cls = d.Classes.Get(classId);
            var plan = Plan(d, loadout, classId);
            var copies = d.EquipmentBalance.Obj(RK.RoleCopies) ?? new JObject();
            var refs = new List<JObject>();
            double? attackSlotCount = plan != null ? plan.AttackCount : (double?)null;
            var guardCopies = plan != null ? plan.GuardCount : Js.Or0(copies[RV.RoleGuard]);
            foreach (var row in Loadout.KitPlan(d, loadout, classId))
            {
                if (row.Role == RV.RoleAttack)
                {
                    var attackPlan = WeaponCards.BuildPlan(d, loadout, classId, attackSlotCount, null);
                    foreach (var slot in attackPlan.Slots)
                    {
                        var r = Js.Obj(K.CardId, slot[K.CardId], K.EquipmentRole, RV.RoleAttack, K.ProfileId, slot[K.ProfileId],
                            K.EquipmentAttackSlotId, slot[K.EquipmentAttackSlotId], K.EquipmentPlanFingerprint, attackPlan.Fingerprint);
                        if (Js.Truthy(slot[K.SourceHand])) r[K.SourceHand] = slot[K.SourceHand].DeepClone();
                        if (Js.Truthy(slot[K.WeaponId])) r[K.WeaponId] = slot[K.WeaponId].DeepClone();
                        refs.Add(r);
                    }
                    continue;
                }
                if (row.Role == RV.RoleGuard)
                {
                    for (var i = 0; i < guardCopies; i++) refs.Add(Js.Obj(K.CardId, row.Profile[RK.BaseCardId], K.EquipmentRole, RV.RoleGuard, K.ProfileId, row.Profile[K.Id]));
                    continue;
                }
                if (plan != null) continue;
                for (var i = 0; i < Js.Or0(copies[row.Role]); i++) refs.Add(Js.Obj(K.CardId, row.Profile[RK.BaseCardId], K.EquipmentRole, row.Role, K.ProfileId, row.Profile[K.Id]));
            }
            if (plan == null)
            {
                for (var i = 0; i < Js.Or0(copies[RK.Signature]); i++) refs.Add(Js.Obj(K.CardId, cls[RK.StartingSignatureCard]));
                if (Js.Truthy(cls[RK.AbilityCard])) for (var i = 0; i < Js.Or0(copies[RK.Ability]); i++) refs.Add(Js.Obj(K.CardId, cls[RK.AbilityCard]));
                return refs;
            }
            foreach (var grant in plan.Grants)
            {
                var r = new JObject();
                foreach (var p in grant.Properties()) if (p.Name != RK.Source) r[p.Name] = p.Value.DeepClone();
                r[K.GrantSource] = grant[RK.Source]?.DeepClone();
                refs.Add(r);
            }
            return refs;
        }

        /// <summary>orderStartingDeck(registries, run): bound cards first, in sourceOrder; base cards keep their order.</summary>
        public static void OrderStartingDeck(RunData d, JObject run)
        {
            var cfg = Config(d);
            if (cfg == null || !(run[K.Deck] is JArray deck)) return;
            JToken SourceOf(JObject card) => card != null && Js.Truthy(card[K.GrantSource]) ? card[K.GrantSource] : null;
            var bound = new List<JObject>();
            var baseCards = new List<JObject>();
            foreach (var card in deck.OfType<JObject>()) (SourceOf(card) == null ? baseCards : bound).Add(card);
            var ordered = SortBySourceOrder(cfg, bound, SourceOf).Concat(baseCards).ToList();
            deck.RemoveAll();
            foreach (var card in ordered) deck.Add(card);
        }

        /// <summary>isItemOwned(inst): the instance rides with an item rather than belonging to the run.</summary>
        public static bool IsItemOwned(RunData d, JObject inst) => inst != null && d.RuleList(K.Loadout, RK.ItemOwnedRoles).Contains(inst.Str(K.EquipmentRole));

        private static JObject AdoptWanted(JObject inst, JObject wanted)
        {
            if (inst.Str(K.CardId) != wanted.Str(K.CardId) || (inst[K.Upgraded]?.Type == JTokenType.Boolean && inst.Is(K.Upgraded)) != (wanted[K.Upgraded]?.Type == JTokenType.Boolean && wanted.Is(K.Upgraded)))
                return wanted;
            Cards.SetOrRemove(inst, K.GrantedBy, wanted[K.GrantedBy]);
            Cards.SetOrRemove(inst, K.GrantSource, wanted[K.GrantSource]);
            if (Js.Truthy(wanted[K.KitRole]))
            {
                inst[K.KitRole] = wanted[K.KitRole].DeepClone();
                Cards.SetOrRemove(inst, K.ProfileId, wanted[K.ProfileId]);
            }
            return inst;
        }

        /// <summary>reconcileGrantedCards(registries, run): item-owned instances follow the worn equipment, in place.</summary>
        public static void ReconcileGrantedCards(RunData d, JObject run)
        {
            if (!(run[K.Deck] is JArray deck)) run[K.Deck] = deck = new JArray();
            var desired = DesiredGrantInstances(d, run.Obj(K.Loadout), run.Str(RK.Class), run.Obj(K.ItemUpgradeLevels), run.Obj(K.ItemMounts));
            var wanted = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var w in desired) wanted[w.Str(K.InstanceId) ?? V.Undefined] = w;
            var present = new HashSet<string>(StringComparer.Ordinal);
            var kept = new List<JObject>();
            foreach (var inst in deck.OfType<JObject>().ToList())
            {
                if (!IsItemOwned(d, inst))
                {
                    kept.Add(inst);
                    continue;
                }
                if (!wanted.TryGetValue(inst.Str(K.InstanceId) ?? V.Undefined, out var want)) continue;
                present.Add(inst.Str(K.InstanceId));
                kept.Add(AdoptWanted(inst, want));
            }
            deck.RemoveAll();
            foreach (var inst in kept) deck.Add(inst);
            foreach (var w in desired) if (!present.Contains(w.Str(K.InstanceId) ?? V.Undefined)) deck.Add(w);
        }

        internal static string PieceFamily(JObject piece) => piece != null && piece.Str(K.Kind) == V.Armor ? V.ArmourKind : V.ArmamentRefPrefix;

        private static string MountKey(params string[] parts) => string.Join(V.KeySeparator, parts);

        /// <summary>boundMountInstances: the bound table's cards for one piece, copies numbered.</summary>
        internal static List<JObject> BoundMountInstances(RunData d, JObject settings, JObject piece)
        {
            var family = PieceFamily(piece);
            var owner = Combat.Equipment.PieceItemRef(piece);
            var copies = new Dictionary<string, int>(StringComparer.Ordinal);
            var out_ = new List<JObject>();
            foreach (var cardId in BoundGrantCardIds(d, piece, family))
            {
                copies.TryGetValue(cardId, out var i);
                copies[cardId] = i + 1;
                out_.Add(Js.Obj(K.InstanceId, MountKey(RV.BoundMount, owner, cardId, i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    K.CardId, cardId, K.Upgraded, false, K.EquipmentRole, RV.RoleGranted, K.GrantedBy, owner,
                    K.GrantSource, GrantSourceFor(settings, family == V.ArmourKind ? RK.Armor : RK.Weapon)));
            }
            return out_;
        }

        /// <summary>packageGrantInstances: a package's grantedCards as item-owned instances.</summary>
        internal static List<JObject> PackageGrantInstances(WeaponCardPackage pkg, JToken weaponSource)
        {
            var out_ = new List<JObject>();
            foreach (var grant in pkg.GrantedCards)
                for (var i = 0; i < grant.Value; i++)
                    out_.Add(Js.Obj(K.InstanceId, MountKey(RV.RoleGranted, pkg.WeaponId, grant.Key, i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        K.CardId, grant.Key, K.Upgraded, false, K.EquipmentRole, RV.RoleGranted, K.GrantedBy, pkg.WeaponId, K.GrantSource, weaponSource));
            return out_;
        }

        /// <summary>weaponArtInstance: one authored weapon art as an item-owned instance.</summary>
        internal static JObject WeaponArtInstance(string weaponId, string artId, JToken weaponSource) =>
            Js.Obj(K.InstanceId, MountKey(RV.RoleWeaponArt, weaponId, artId), K.CardId, artId, K.Upgraded, false,
                K.EquipmentRole, RV.RoleWeaponArt, K.GrantedBy, weaponId, K.GrantSource, weaponSource);

        /// <summary>
        /// itemMountInstances(registries, run, piece, { authored }): the item-owned instances one piece lends by its own
        /// authoring (bound cards, package grants, weapon-art defaults); unless <paramref name="authored"/>, with the smith's
        /// overrides applied and its filled extra mounts appended.
        /// </summary>
        public static List<JObject> ItemMountInstances(RunData d, JObject itemMounts, JObject piece, bool authored)
        {
            if (piece == null) return new List<JObject>();
            var settings = Settings(d);
            var weaponSource = GrantSourceFor(settings, RK.Weapon);
            var list = BoundMountInstances(d, settings, piece);
            var pkg = piece.Str(K.Kind) == V.Armor ? null : WeaponCards.FromPiece(d, piece);
            if (pkg != null)
            {
                list.AddRange(PackageGrantInstances(pkg, weaponSource));
                foreach (var artId in pkg.WeaponArtDefaults) list.Add(WeaponArtInstance(pkg.WeaponId, artId, weaponSource));
            }
            if (authored) return list;
            var result = CardMounts.ApplyMountOverrides(d, itemMounts, list);
            result.AddRange(CardMounts.ExtraMountInstances(d, itemMounts, Combat.Equipment.PieceItemRef(piece),
                GrantSourceFor(settings, PieceFamily(piece) == V.ArmourKind ? RK.Armor : RK.Weapon)));
            return result;
        }

        /// <summary>
        /// desiredGrantInstances(registries, run): every item-owned instance the worn equipment lends — bound-table
        /// cards, package grants, weapon arts (split when both hands are armed), smith overrides, kit basics, the empty
        /// hand's art and filled extra mounts.
        /// </summary>
        public static List<JObject> DesiredGrantInstances(RunData d, JObject loadout, string classId, JObject itemUpgradeLevels, JObject itemMounts)
        {
            var settings = Settings(d);
            var weaponSource = GrantSourceFor(settings, RK.Weapon);
            var desired = new List<JObject>();
            foreach (var piece in Combat.Equipment.EquippedPieces(d.Combat, loadout, classId, itemUpgradeLevels ?? new JObject()))
                desired.AddRange(BoundMountInstances(d, settings, piece));

            var hands = d.RuleList(K.Loadout, RK.HandOrder);
            var sources = new Dictionary<string, HandSource>(StringComparer.Ordinal);
            foreach (var hand in hands)
            {
                var source = WeaponCards.Hand(d, loadout, classId, hand);
                sources[hand] = source.Package != null ? source : null;
            }
            foreach (var hand in hands)
            {
                var source = sources[hand];
                if (source == null) continue;
                desired.AddRange(PackageGrantInstances(source.Package, weaponSource));
            }
            List<string> OptionalArts(HandSource source) =>
                source.Package.WeaponArtDefaults.Where(id => id != source.Package.CombatKit?.Str(RK.ArtCardId)).ToList();
            var right = sources[V.Right];
            var left = sources[V.Left];
            var arts = right != null && left != null
                ? WeaponCards.SplitAuthoredWeaponArts(OptionalArts(right), OptionalArts(left))
                : hands.SelectMany(hand => sources[hand] != null ? OptionalArts(sources[hand]).Select(id => new KeyValuePair<string, string>(id, hand)) : Enumerable.Empty<KeyValuePair<string, string>>()).ToList();
            foreach (var hand in hands)
            {
                var kit = sources[hand]?.Package.CombatKit;
                if (kit != null) arts.Add(new KeyValuePair<string, string>(kit.Str(RK.ArtCardId), hand));
            }
            foreach (var art in arts) desired.Add(WeaponArtInstance(sources[art.Value].Package.WeaponId, art.Key, weaponSource));

            desired = CardMounts.ApplyMountOverrides(d, itemMounts, desired);
            foreach (var hand in hands)
            {
                var source = sources[hand];
                var kit = source?.Package.CombatKit;
                if (kit == null) continue;
                foreach (var role in d.RuleList(K.Loadout, RK.KitRoles))
                {
                    var profile = Loadout.ProfileById(d, kit[role + RV.ProfileIdSuffix]);
                    desired.Add(Js.Obj(K.InstanceId, MountKey(RV.KitMount, source.Piece.Str(K.Id), role), K.CardId, profile[RK.BaseCardId],
                        K.Upgraded, false, K.EquipmentRole, RV.RoleGranted, K.KitRole, role, K.ProfileId, profile[K.Id],
                        K.GrantedBy, source.Piece[K.Id], K.GrantSource, weaponSource));
                }
            }

            var armed = hands.Where(hand => WeaponCards.Hand(d, loadout, classId, hand).Piece != null).ToList();
            var twoHandedHands = d.RuleNum(K.Loadout, RK.TwoHandedHands);
            var twoHanded = right?.Package.HandsRequired == twoHandedHands || left?.Package.HandsRequired == twoHandedHands;
            if (armed.Count == 1 && !twoHanded)
            {
                var empty = armed[0] == V.Right ? V.Left : V.Right;
                var profile = Loadout.ProfileById(d, d.EquipmentBalance.Obj(RK.UnarmedProfiles)?[RV.RoleTechnique]);
                var installed = profile != null && desired.Any(x => x.Str(K.EquipmentRole) == RV.RoleWeaponArt && JToken.DeepEquals(x[K.CardId], profile[RK.BaseCardId]));
                if (profile != null && Js.Truthy(profile[RK.BaseCardId]) && !installed)
                    desired.Add(Js.Obj(K.InstanceId, MountKey(RV.RoleWeaponArt, RV.Unarmed, empty, profile.Str(RK.BaseCardId)), K.CardId, profile[RK.BaseCardId],
                        K.Upgraded, false, K.EquipmentRole, RV.RoleWeaponArt, K.GrantedBy, MountKey(RV.Unarmed, empty), K.GrantSource, weaponSource));
            }

            foreach (var piece in Combat.Equipment.EquippedPieces(d.Combat, loadout, classId, itemUpgradeLevels ?? new JObject()))
                desired.AddRange(CardMounts.ExtraMountInstances(d, itemMounts, Combat.Equipment.PieceItemRef(piece),
                    GrantSourceFor(settings, PieceFamily(piece) == V.ArmourKind ? RK.Armor : RK.Weapon)));
            return desired;
        }

        /// <summary>
        /// stampDeck(registries, run) — the authoritative full-deck restamp: reconcile the pools, rebind the attack
        /// slots, reconcile item-owned cards, then stamp every instance's profile receipt, carriers and mods.
        /// Returns the number of instances re-stamped.
        /// </summary>
        public static int StampDeck(RunData d, JObject run, bool adoptEquipmentBonuses = true, bool reconcileEquipmentPools = true)
        {
            if (reconcileEquipmentPools) Loadout.ReconcileRunLoadoutHp(d, run, adoptEquipmentBonuses);
            // `run.deck || []`: without a deck the list is a detached empty array (the reconcile below then creates
            // run.deck, and nothing it adds is stamped this pass — as shipped).
            var list = run[K.Deck] as JArray ?? new JArray();
            if (!Js.Truthy(run[K.Attributes])) throw new InvalidOperationException(RM.StampNeedsAttributes);
            var loadout = run.Obj(K.Loadout);
            var classId = run.Str(RK.Class);
            var snapshot = run.Obj(K.EquipmentProfileRuleSnapshot);
            double? bornWith = Js.IsFinite(run[K.EquipmentAttackSlotCount]) ? run.Num(K.EquipmentAttackSlotCount) : (double?)null;
            if (bornWith == null && run[K.Deck] is JArray born && born.Count > 0) bornWith = born.OfType<JObject>().Count(c => c.Str(K.EquipmentRole) == RV.RoleAttack);
            var attackPlan = WeaponCards.BuildPlan(d, loadout, classId, bornWith, run[K.RemovedAttackSlotIds]);
            foreach (var inst in list.OfType<JObject>().Where(c => c.Str(K.EquipmentRole) == RV.RoleAttack).ToList())
            {
                var prior = Js.Truthy(inst[K.ProfileId]) ? snapshot.Obj(RK.Profiles).Obj(inst.Str(K.ProfileId)) : null;
                var desiredSlot = attackPlan.Slots.FirstOrDefault(s => s.Str(K.EquipmentAttackSlotId) == inst.Str(K.EquipmentAttackSlotId));
                var next = desiredSlot != null ? snapshot.Obj(RK.Profiles).Obj(desiredSlot.Str(K.ProfileId)) : null;
                if (prior != null && next != null && prior.Str(RK.Compatibility) != next.Str(RK.Compatibility))
                    throw new InvalidOperationException(RunJs.Fmt(RM.IncompatibleAttackSwap, inst.Str(K.ProfileId), prior.Str(RK.Compatibility), desiredSlot.Str(K.ProfileId), next.Str(RK.Compatibility)));
            }
            WeaponCards.ApplyPlan(attackPlan, list.OfType<JObject>().ToList(), false);
            ReconcileGrantedCards(d, run);
            var rolePlan = new Dictionary<string, KitRow>(StringComparer.Ordinal);
            foreach (var row in Loadout.KitReceipt(d, loadout, classId, run.Obj(K.Attributes), snapshot)) rolePlan[row.Role] = row;
            var n = 0;
            foreach (var inst in list.OfType<JObject>().ToList())
            {
                KitRow row = null;
                if (Js.Truthy(inst[K.EquipmentRole])) rolePlan.TryGetValue(inst.Str(K.EquipmentRole), out row);
                var kitRole = Js.Truthy(inst[K.KitRole]) ? inst.Str(K.KitRole) : null;
                if (inst.Str(K.EquipmentRole) == RV.RoleAttack || kitRole != null)
                {
                    var profile = Loadout.ProfileById(d, inst[K.ProfileId]);
                    var owner = kitRole != null ? inst[K.GrantedBy] : inst[K.WeaponId];
                    var piece = Js.Truthy(owner) ? d.EquipmentRows(K.Armaments).FirstOrDefault(c => JToken.DeepEquals(c[K.Id], owner)) : null;
                    row = new KitRow { Role = kitRole ?? RV.RoleAttack, Profile = profile, Piece = piece };
                    row.Receipt = Loadout.RoleAmountReceipt(d, row, run.Obj(K.Attributes), snapshot);
                }
                if (row != null && row.Profile != null)
                {
                    var prior = Js.Truthy(inst[K.ProfileId]) ? snapshot.Obj(RK.Profiles).Obj(inst.Str(K.ProfileId)) : null;
                    var nextCompatibility = snapshot.Obj(RK.Profiles).Obj(row.Profile.Str(K.Id)).Str(RK.Compatibility);
                    if (prior != null && prior.Str(RK.Compatibility) != nextCompatibility)
                        throw new InvalidOperationException(RunJs.Fmt(RM.IncompatibleRoleSwap, inst.Str(K.EquipmentRole), inst.Str(K.ProfileId), prior.Str(RK.Compatibility), row.Profile.Str(K.Id), nextCompatibility));
                    if (inst.Str(K.EquipmentRole) != RV.RoleAttack) inst[K.CardId] = row.Profile[RK.BaseCardId]?.DeepClone();
                    inst[K.ProfileId] = row.Profile[K.Id].DeepClone();
                    var rating = row.Receipt.Obj(RK.Rating);
                    Cards.SetOrRemove(inst, K.RatingId, rating[K.Id]);
                    Cards.SetOrRemove(inst, K.RatingValue, rating[K.Value]);
                    if (Js.IsFinite(row.Receipt[RK.Cap])) inst[K.RatingCap] = row.Receipt[RK.Cap].DeepClone();
                    else inst.Remove(K.RatingCap);
                    inst[K.ProfileReceipt] = row.Receipt.DeepClone();
                    var sourceArmamentId = row.Piece?[K.Id];
                    if (Js.Truthy(sourceArmamentId))
                    {
                        inst[K.SourceArmamentId] = sourceArmamentId.DeepClone();
                        var itemRef = V.ArmamentRefPrefix + V.ItemRefSeparator + Js.Str(sourceArmamentId);
                        inst[K.SmithingLevel] = (RunJs.Coalesce(run.Obj(K.ItemUpgradeLevels)?[itemRef], run.Obj(RK.ArmamentLevels)?[Js.Str(sourceArmamentId)]) ?? Js.N(0)).DeepClone();
                        inst[K.Upgraded] = false;
                    }
                    else
                    {
                        inst.Remove(K.SourceArmamentId);
                        inst.Remove(K.SmithingLevel);
                    }
                }
                JObject carrier;
                if (row != null && row.Profile != null) carrier = snapshot.Obj(RK.Profiles).Obj(row.Profile.Str(K.Id));
                else
                {
                    var schoolAbsent = inst[K.DamageSchool] == null;
                    var buildupAbsent = inst[K.ExposureBuildupPerHit] == null;
                    if (schoolAbsent != buildupAbsent) throw new InvalidOperationException(RunJs.Fmt(RM.CarrierHalfPresent, inst.Str(K.InstanceId)));
                    carrier = schoolAbsent ? d.Cards.Get(inst.Str(K.CardId)) : inst;
                }
                var priorSchool = inst[K.DamageSchool]?.DeepClone();
                var priorBuildup = inst[K.ExposureBuildupPerHit]?.DeepClone();
                if (Js.IsStr(carrier[K.DamageSchool])) inst[K.DamageSchool] = carrier[K.DamageSchool].DeepClone();
                else inst.Remove(K.DamageSchool);
                if (Js.IsInt(carrier[K.ExposureBuildupPerHit])) inst[K.ExposureBuildupPerHit] = carrier[K.ExposureBuildupPerHit].DeepClone();
                else inst.Remove(K.ExposureBuildupPerHit);
                OrderedMap<List<string>> mods;
                if (kitRole == RV.RoleAttack) mods = Loadout.CardMods(d, loadout, classId, true, inst.Str(K.GrantedBy));
                else if (inst.Str(K.EquipmentRole) == RV.RoleAttack) mods = Loadout.CardMods(d, loadout, classId, true, Js.Truthy(inst[K.WeaponId]) ? inst.Str(K.WeaponId) : null);
                else mods = Loadout.CardMods(d, loadout, classId);
                string amountMod = null;
                if (row != null && row.Role == RV.RoleAttack) amountMod = K.Damage + RV.ModAssign + RunJs.NumStr(row.Receipt.Num(RK.EffectBase));
                else if (row != null && row.Role == RV.RoleGuard) amountMod = K.Block + RV.ModAssign + RunJs.NumStr(row.Receipt.Num(RK.EffectBase));
                var next = new List<string>();
                if (amountMod != null) next.Add(amountMod);
                if (row != null) next.AddRange(Js.Items(row.Profile?[K.Mods]).Select(m => Js.IsStr(m) ? Js.Str(m) : m.ToString()));
                if (mods.TryGetValue(inst.Str(K.CardId) ?? V.Undefined, out var cardMods)) next.AddRange(cardMods);
                var prev = Js.Items(inst[K.Mods]).Select(m => Js.IsStr(m) ? Js.Str(m) : m.ToString()).ToList();
                var carrierChanged = !SameValue(priorSchool, inst[K.DamageSchool]) || !SameValue(priorBuildup, inst[K.ExposureBuildupPerHit]);
                if (!carrierChanged && next.Count == prev.Count && next.SequenceEqual(prev)) continue;
                if (next.Count > 0) inst[K.Mods] = new JArray(next);
                else inst.Remove(K.Mods);
                n += 1;
            }
            return n;
        }

        /// <summary>JS <c>===</c> on two optional scalars (undefined equals only undefined).</summary>
        private static bool SameValue(JToken a, JToken b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (Js.IsNum(a) && Js.IsNum(b)) return Js.D(a) == Js.D(b);
            return JToken.DeepEquals(a, b);
        }
    }
}
