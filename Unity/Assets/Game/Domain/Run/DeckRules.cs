using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2RunKeys;
using DK = Ashen.Generated.Core2DeckKeys;
using DM = Ashen.Generated.Core2DeckMessages;
using DV = Ashen.Generated.Core2DeckValues;
using MI = Ashen.Generated.Core2ModuleIds;
using PC = Ashen.Generated.Core2ProfileContentKeys;
using PK = Ashen.Generated.Core2ProfileKeys;
using RV = Ashen.Generated.RunValues;
using SK = Ashen.Generated.Core2RunSnapshotKeys;

namespace Ashen.Domain.Run
{
    /// <summary>
    /// Everything the Core 2 deck rules read beyond <see cref="CoreRunData"/> (WP-18, docs/design/11): the editor's rules
    /// (<c>rules/core2Deck.json</c>) and the ids of the card-domain tags (the web's <c>tagService.inDomain('card')</c>, which
    /// the equipment gate reads as the card schools). The content layer builds it; the domain only reads it.
    /// </summary>
    public sealed class CoreDeckData
    {
        private readonly HashSet<string> _cardTags;

        public CoreDeckData(CoreRunData run, JObject rules, IEnumerable<string> cardTags)
        {
            Run = run ?? throw new ArgumentNullException(nameof(run));
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _cardTags = new HashSet<string>(cardTags ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        }

        public CoreRunData Run { get; }

        /// <summary>rules/core2Deck.json.</summary>
        public JObject Rules { get; }

        public bool IsCardTag(string tag) => tag != null && _cardTags.Contains(tag);

        public JObject Defaults => Rules.Obj(DK.Defaults) ?? new JObject();
    }

    /// <summary>deckEditBounds(settings): the smallest and largest deck the editor may confirm; <c>Max</c> is infinity when unlimited.</summary>
    public readonly struct DeckBounds
    {
        public DeckBounds(double min, double max, string problem)
        {
            Min = min;
            Max = max;
            Problem = problem;
        }

        public double Min { get; }
        public double Max { get; }

        /// <summary>The Settings refusal when the maximum sits below the minimum (or a bound is malformed); empty otherwise.</summary>
        public string Problem { get; }
    }

    /// <summary>One entry of deckSettingsProblems: the setting keys at fault and the sentence Settings paints.</summary>
    public sealed class DeckSettingsProblem
    {
        public DeckSettingsProblem(string[] keys, string message)
        {
            Keys = keys;
            Message = message;
        }

        public string[] Keys { get; }
        public string Message { get; }
    }

    /// <summary>
    /// The deck editor's rules for a Core 2 run, without its screen (shipped model/deckRules.js, deckCopyLimit.js and the
    /// <c>deckCopyLimit</c> of engine/kernel/gameConfig.js; WP-18): the bounds and refusals, the sideboard moves, the begin /
    /// cancel / commit draft door, the copy limits and the equipment gate, and the opening draw pile in deck order. Settings
    /// are the profile's stored choices (<c>JObject</c>, may be null); the defaults are <c>rules/core2Deck.json</c>.
    /// OWNED MEANS DECK + SIDEBOARD: a card the editor takes out of the deck keeps every field it carries and comes back the
    /// same instance. Reads are on a <see cref="CoreDeckData"/>; moves mutate the run document in place.
    /// </summary>
    public static class DeckRules
    {
        // ------------------------------------------------------------------ settings, bounds, refusals

        private static JToken Setting(CoreDeckData d, JObject settings, string key)
        {
            var value = settings?[key];
            return Js.Nullish(value) ? d.Defaults[key] : value;
        }

        private static double WholeNumber(JToken value, string key, double min)
        {
            if (!Js.IsInt(value) || Js.D(value) < min) throw new InvalidOperationException(RunJs.Fmt(DM.WholeNumber, key, RunJs.NumStr(min), RunJs.Show(value)));
            return Js.D(value);
        }

        private static (double Min, double Max) ReadBounds(CoreDeckData d, JObject settings)
        {
            var min = Js.Truthy(Setting(d, settings, DK.DeckMinUnlimited)) ? 0 : WholeNumber(Setting(d, settings, DK.DeckMinSize), DK.DeckMinSize, 0);
            var max = Js.Truthy(Setting(d, settings, DK.DeckMaxUnlimited)) ? double.PositiveInfinity : WholeNumber(Setting(d, settings, DK.DeckMaxSize), DK.DeckMaxSize, 1);
            return (min, max);
        }

        /// <summary>deckSettingsProblems(settings): a malformed bound, or a maximum below the effective minimum, by name.</summary>
        public static List<DeckSettingsProblem> SettingsProblems(CoreDeckData d, JObject settings)
        {
            var keys = new[] { DK.DeckMinSize, DK.DeckMaxSize };
            (double Min, double Max) bounds;
            try { bounds = ReadBounds(d, settings); }
            catch (InvalidOperationException e) { return new List<DeckSettingsProblem> { new DeckSettingsProblem(keys, e.Message) }; }
            return bounds.Max < bounds.Min
                ? new List<DeckSettingsProblem> { new DeckSettingsProblem(keys, RunJs.Fmt(DM.MaxBelowMin, RunJs.NumStr(bounds.Max), RunJs.NumStr(bounds.Min))) }
                : new List<DeckSettingsProblem>();
        }

        /// <summary>deckEditBounds(settings): { min, max, problem }.</summary>
        public static DeckBounds EditBounds(CoreDeckData d, JObject settings)
        {
            var problems = SettingsProblems(d, settings);
            if (problems.Count > 0)
            {
                var min = Js.D(d.Defaults[DK.DeckMinSize]);
                try { min = ReadBounds(d, settings).Min; }
                catch (InvalidOperationException) { /* fall back to the default */ }
                return new DeckBounds(min, min, problems[0].Message);
            }
            var read = ReadBounds(d, settings);
            return new DeckBounds(read.Min, read.Max, string.Empty);
        }

        private static string CardCount(double n) => RunJs.Fmt(n == 1 ? DM.CardCountOne : DM.CardCountMany, RunJs.NumStr(n));

        /// <summary>deckEditRefusal(count, settings): empty when the editor may confirm, else one sentence.</summary>
        public static string EditRefusal(CoreDeckData d, double count, JObject settings)
        {
            var bounds = EditBounds(d, settings);
            if (bounds.Problem.Length > 0) return bounds.Problem;
            if (count < bounds.Min) return RunJs.Fmt(DM.TooFew, CardCount(count), RunJs.NumStr(bounds.Min));
            if (count > bounds.Max) return RunJs.Fmt(DM.TooMany, CardCount(count), RunJs.NumStr(bounds.Max));
            return string.Empty;
        }

        public static bool EditingOn(CoreDeckData d, JObject settings) => Js.Truthy(Setting(d, settings, DK.DeckEditing));

        /// <summary>'free' | 'restOnly': an unknown stored value reads as the default.</summary>
        public static string EditingWhere(CoreDeckData d, JObject settings)
        {
            var where = Setting(d, settings, DK.DeckEditingWhere);
            return Js.Includes(d.Rules[DK.Where], Js.Str(where)) ? Js.Str(where) : Js.Str(d.Defaults[DK.DeckEditingWhere]);
        }

        public static bool PlayInDeckOrder(CoreDeckData d, JObject settings) => Js.Truthy(Setting(d, settings, DK.PlayInDeckOrder));

        /// <summary>playInDeckOrder(settings) for a fight that has the run data but no editor: the setting, else the rules file's default.</summary>
        public static bool PlayInDeckOrder(CoreRunData d, JObject settings)
        {
            var value = settings?[DK.PlayInDeckOrder];
            return Js.Truthy(Js.Nullish(value) ? d.DeckRules?.Obj(DK.Defaults)?[DK.PlayInDeckOrder] : value);
        }

        // ------------------------------------------------------------------ the run's profile

        private static JObject Subject(JObject run) => run?.Obj(SK.GameProfile);

        private static string ModeOf(JObject run, string module) => CoreRunData.Module(Subject(run), module)?.Str(CK.Mode);

        private static bool CardsConfigured(JObject run) => ModeOf(run, MI.Cards) == Ashen.Generated.Core2RunValues.Configured;

        private static bool TrainingConfigured(JObject run) => ModeOf(run, MI.DeckTraining) == Ashen.Generated.Core2RunValues.Configured;

        // ------------------------------------------------------------------ the configured copy limit (gameConfig.js deckCopyLimit)

        private static List<string> CharacterTags(JObject row)
        {
            if (row[DK.Tags] is JArray tags) return tags.Select(Js.Str).ToList();
            var output = new List<string> { DV.CharacterPrefix + (Js.Truthy(row[DK.Player]) ? DV.PlayerRole : DV.EnemyRole) };
            if (Js.Truthy(row[DK.Tier])) output.Add(DV.TierPrefix + RunJs.Key(row[DK.Tier]));
            return output;
        }

        /// <summary>resolveCharacterDeck for one pool entry of one copy: the card the pool gives this character (null when it has none).</summary>
        private static string PoolCard(CoreDeckData d, JObject row, JObject content, JObject gearModule, JObject poolRules, string poolName)
        {
            var pool = content.Obj(DK.CardPools)?.Obj(poolName);
            if (pool == null) throw new InvalidOperationException(RunJs.Fmt(DM.PoolHasNoCard, Js.Str(row[DK.Id]), poolName));
            var gear = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var item in Js.Items(content[PC.Gear]).OfType<JObject>()) gear[RunJs.Key(item[DK.Id])] = item;
            var bySlot = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var entry in Js.Items(row[DK.DefaultGear]).OfType<JObject>())
                bySlot[RunJs.Key(entry[DK.Key])] = gear.TryGetValue(RunJs.Key(entry[DK.Id]), out var item) ? item : null;
            var group = d.Rules.Str(DK.TypeGroup);
            string TypeTag(JObject item) =>
                item == null ? null : Js.Items(item[DK.Tags]).Select(Js.Str).FirstOrDefault(tag => tag != null && gearModule?.Obj(DK.Tags)?.Obj(tag)?.Str(DK.Group) == group);
            JObject Slot(JToken key) => key != null && bySlot.TryGetValue(RunJs.Key(key), out var item) ? item : null;
            var classTag = CharacterTags(row).FirstOrDefault(tag => tag != null && tag.StartsWith(d.Rules.Str(DK.ClassTagPrefix), StringComparison.Ordinal));
            var type = Js.Truthy(poolRules?[DK.WeaponTypes]) ? TypeTag(Slot(pool[DK.Slot])) ?? TypeTag(Slot(pool[DK.FallbackSlot])) : null;
            var byClass = classTag != null ? pool.Obj(DK.ByClass)?.Obj(classTag) : null;
            string Pick(JObject table, string key) => key != null && table != null && Js.Truthy(table[key]) ? Js.Str(table[key]) : null;
            var id = (type != null ? Pick(byClass, type) : null) ?? Pick(byClass, DK.Default)
                ?? (type != null ? Pick(pool.Obj(DK.ByTag), type) : null) ?? Pick(pool.Obj(DK.ByTag), DK.Default);
            if (id == null) throw new InvalidOperationException(RunJs.Fmt(DM.PoolHasNoCard, Js.Str(row[DK.Id]), poolName));
            return id;
        }

        /// <summary>deckCopyLimit(cardId, row, content, settings, gearSettings, owned) of engine/kernel/gameConfig.js.</summary>
        private static double ConfiguredLimit(CoreDeckData d, string cardId, JObject row, JObject content, JObject rules, JObject gearModule, JObject poolRules, Dictionary<string, int> owned)
        {
            var have = owned.TryGetValue(cardId ?? string.Empty, out var n) ? n : 0;
            var card = Js.Items(content[PC.Cards]).OfType<JObject>().FirstOrDefault(c => Js.Str(c[DK.Id]) == cardId);
            if (rules == null || card == null) return have;
            var tags = Js.Items(card[DK.Tags]).Select(Js.Str).ToList();
            var charTags = CharacterTags(row);
            var cache = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            HashSet<string> FromPools(JToken pools)
            {
                var key = RunJs.Json(pools);
                if (cache.TryGetValue(key, out var hit)) return hit;
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pool in Js.Items(pools).Select(Js.Str))
                    if (pool != null && Js.Truthy(content.Obj(DK.CardPools)?[pool])) set.Add(PoolCard(d, row, content, gearModule, poolRules, pool));
                return cache[key] = set;
            }
            var owns = rules.Obj(DK.Owned);
            if (Js.Items(owns?[DK.Tags]).Select(Js.Str).Any(t => tags.Contains(t)) || FromPools(owns?[DK.Pools]).Contains(cardId)) return have;
            foreach (var rule in Js.Items(rules[DK.Unlimited]).OfType<JObject>())
            {
                if (!charTags.Contains(Js.Str(rule[DK.RequiresTag]))) continue;
                var c = rule.Obj(DK.Cards) ?? new JObject();
                bool Any(JToken list, Func<string, bool> test) => Js.Items(list).Select(Js.Str).Any(test);
                bool All(JToken list, Func<string, bool> test) => Js.Items(list).Select(Js.Str).All(test);
                var matches = (!Js.Truthy(c[DK.Rarity]) || Js.Includes(c[DK.Rarity], Js.Str(card[DK.Rarity])))
                    && (!Js.Truthy(c[DK.TagsAny]) || Any(c[DK.TagsAny], tags.Contains))
                    && (!Js.Truthy(c[DK.TagsAll]) || All(c[DK.TagsAll], tags.Contains));
                if (FromPools(rule[DK.Pools]).Contains(cardId) || ((Js.Truthy(c[DK.Rarity]) || Js.Truthy(c[DK.TagsAny]) || Js.Truthy(c[DK.TagsAll])) && matches)) return double.PositiveInfinity;
            }
            return Js.Str(rules[DK.Default]) == d.Rules.Str(DK.UnlimitedMode) ? double.PositiveInfinity : have;
        }

        /// <summary>configuredDeckCopyLimit(run, cardId): the profile's copy limit, or null when the run's profile does not configure deck training.</summary>
        public static double? ConfiguredCopyLimit(CoreDeckData d, JObject run, string cardId)
        {
            var profile = Subject(run);
            if (!TrainingConfigured(run)) return null;
            var content = profile.Obj(CK.Content) ?? new JObject();
            var classId = run.Str(DK.Class);
            var row = Js.Items(content[PC.Characters]).OfType<JObject>().FirstOrDefault(r => Js.Str(r[DK.Id]) == classId)
                ?? Js.Obj(DK.Id, classId, DK.Tags, new JArray());
            var owned = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var card in Js.Items(run[DK.Deck]).Concat(Js.Items(run[DK.Sideboard])).OfType<JObject>())
            {
                var id = RunJs.Key(card[DK.CardId]);
                owned[id] = (owned.TryGetValue(id, out var n) ? n : 0) + 1;
            }
            // The web hands modules['deck-training'] as settings.deckEditing and modules['card-pools'] as cardPoolRules.
            return ConfiguredLimit(d, cardId, row, content, CoreRunData.Module(profile, MI.DeckTraining), CoreRunData.Module(profile, MI.Gear), CoreRunData.Module(profile, MI.CardPools), owned);
        }

        /// <summary>
        /// deckCopyLimit(registries, cardId, settings, run): the most copies of a card the deck may hold, or infinity. A configured
        /// run asks its profile; any other run falls back to the class-string rule.
        /// </summary>
        public static double CopyLimit(CoreDeckData d, string cardId, JObject settings, JObject run)
        {
            var configured = ConfiguredCopyLimit(d, run, cardId);
            return configured ?? CopyLimit(d, cardId, settings, run?.Str(DK.Class));
        }

        /// <summary>
        /// deckCopyLimit(registries, cardId, settings, classId) with a class id: the run's own class's spells and Powers are limited
        /// (owner ruling 2026-09-26); a card kept from another class is not. Every other limit is unbounded.
        /// </summary>
        public static double CopyLimit(CoreDeckData d, string cardId, JObject settings, string classId)
        {
            var def = d.Run.Run.Cards.Has(cardId) ? d.Run.Run.Cards.Get(cardId) : null;
            var cardClass = def?.Str(DK.Class);
            if (def == null || !Js.Truthy(def[DK.Class]) || cardClass == d.Rules.Str(DK.ColorlessClass)) return double.PositiveInfinity;
            if (!string.IsNullOrEmpty(classId) && cardClass != classId) return double.PositiveInfinity;
            var rule = d.Rules.Obj(DK.SingleCopy);
            if (!Js.Includes(rule[DK.Types], def.Str(DK.Type)) && !Js.Items(def[DK.Tags]).Any(tag => Js.Includes(rule[DK.Tags], Js.Str(tag)))) return double.PositiveInfinity;
            var limit = Js.Nullish(settings?[DK.ClassSpellPowerCopies]) ? d.Defaults[DK.ClassSpellPowerCopies] : settings[DK.ClassSpellPowerCopies];
            if (!Js.IsInt(limit) || Js.D(limit) < 1) throw new InvalidOperationException(RunJs.Fmt(DM.CopiesWholeNumber, RunJs.Show(limit)));
            return Js.D(limit);
        }

        /// <summary>How many copies of a card id the run owns (deck + sideboard).</summary>
        public static int OwnedCopies(JObject run, string cardId) =>
            Js.Items(run[DK.Deck]).Concat(Js.Items(run[DK.Sideboard])).OfType<JObject>().Count(c => Js.Str(c[DK.CardId]) == cardId);

        // ------------------------------------------------------------------ basics

        /// <summary>
        /// basicCardIds(run): the card ids this run classifies as basics, whether or not the collection may offer unowned copies.
        /// A configured deck-training profile classifies them by its unlimited copy limit.
        /// </summary>
        public static List<string> BasicCardIds(CoreDeckData d, JObject run)
        {
            if (CardsConfigured(run))
            {
                if (TrainingConfigured(run))
                    return Js.Items(Subject(run).Obj(CK.Content)?[PC.Cards]).OfType<JObject>()
                        .Where(card => double.IsPositiveInfinity(ConfiguredCopyLimit(d, run, Js.Str(card[DK.Id])) ?? 0))
                        .Select(card => Js.Str(card[DK.Id])).ToList();
                return Js.Items(Subject(run).Obj(CK.Content)?.Obj(DK.Editor)?[DK.UnlimitedBasicCards]).Select(Js.Str).ToList();
            }
            return Js.Items(d.Rules[DK.UnlimitedCardIds]).Select(Js.Str).ToList();
        }

        /// <summary>
        /// unlimitedCardIds(run): the basics the editor may mint unowned, without limit. Core sets deck-training
        /// <c>ownedOnlyCollection</c> (owner ruling 2026-10-19): the collection shows only cards the run has collected, so none is offered.
        /// </summary>
        public static List<string> UnlimitedCardIds(CoreDeckData d, JObject run)
        {
            if (CardsConfigured(run) && TrainingConfigured(run)
                && CoreRunData.Module(Subject(run), MI.DeckTraining)[DK.OwnedOnlyCollection] is JValue v && v.Type == JTokenType.Boolean && v.Value<bool>()) return new List<string>();
            return BasicCardIds(d, run);
        }

        private static bool IsItemOwned(CoreDeckData d, JObject card) => StartingDeck.IsItemOwned(d.Run.Run, card);

        private static bool IsKitRole(JObject card) => card.Str(DK.EquipmentRole) == RV.RoleAttack || card.Str(DK.EquipmentRole) == RV.RoleGuard;

        /// <summary>True for an owned plain basic, whether or not unowned copies are offered.</summary>
        public static bool IsBasicCard(CoreDeckData d, JObject card, JObject run = null)
        {
            if (card == null || Js.Truthy(card[DK.GrantedBy]) || IsItemOwned(d, card)) return false;
            if (!CardsConfigured(run) && IsKitRole(card)) return true;
            return !Js.Truthy(card[DK.EquipmentRole]) && BasicCardIds(d, run).Contains(card.Str(DK.CardId));
        }

        /// <summary>True for a card the editor adds without limit under this run's policy.</summary>
        public static bool IsUnlimitedBasic(CoreDeckData d, JObject card, JObject run = null)
        {
            if (card == null || Js.Truthy(card[DK.GrantedBy]) || IsItemOwned(d, card)) return false;
            if (CardsConfigured(run)) return !Js.Truthy(card[DK.EquipmentRole]) && UnlimitedCardIds(d, run).Contains(card.Str(DK.CardId));
            if (IsKitRole(card)) return true;
            return !Js.Truthy(card[DK.EquipmentRole]) && UnlimitedCardIds(d, run).Contains(card.Str(DK.CardId));
        }

        /// <summary>True for a run whose basics are slot-true (attack/guard by role): it has a loadout and its cards are not configured.</summary>
        public static bool IsEquippedRun(JObject run) => run != null && Js.Truthy(run[DK.Loadout]) && !CardsConfigured(run);

        /// <summary>isSetAsideBasic(card): a sideboarded basic worth keeping apart from a fresh one (upgraded, ranked or modded).</summary>
        public static bool IsSetAsideBasic(CoreDeckData d, JObject card, JObject run = null)
        {
            if (card == null || Js.Truthy(card[DK.EquipmentRole]) || !IsUnlimitedBasic(d, card, run)) return false;
            return Js.Truthy(card[DK.Upgraded]) || (card[DK.Mods] is JArray mods && mods.Count > 0) || RunJs.Number(card[DK.Rank]) > 1;
        }

        // ------------------------------------------------------------------ the equipment gate

        private static bool PureEngagementMovement(CoreDeckData d, JObject def) =>
            def?[DK.Effects] is JArray effects && effects.Count > 0 && effects.All(e => Js.Get(e, DK.Op) is JToken op && Js.Str(op) == d.Rules.Obj(DK.Movement).Str(DK.Op));

        /// <summary>rangeCardAvailable(run, def): a range-off run cannot add a card whose only effect is movement.</summary>
        public static bool RangeCardAvailable(CoreDeckData d, JObject run, JObject def)
        {
            var enabled = run?.Obj(DK.RangePolicy)?[DK.Enabled];
            return !(enabled != null && enabled.Type == JTokenType.Boolean && !enabled.Value<bool>()) || !PureEngagementMovement(d, def);
        }

        /// <summary>isCurrentEquipmentGrant(registries, run, card): the card is one the worn gear lends right now (same id and card).</summary>
        public static bool IsCurrentEquipmentGrant(CoreDeckData d, JObject run, JObject card)
        {
            if (!CardsConfigured(run) || !Ashen.Domain.Run.CoreRunData.IsConfigured(Subject(run))) throw new InvalidOperationException(DM.NeedsConfigured);
            return CoreRunRules.GearGrantInstances(d.Run, run).Any(want => want.Str(DK.InstanceId) == card.Str(DK.InstanceId) && want.Str(DK.CardId) == card.Str(DK.CardId));
        }

        /// <summary>deckCardEquipmentEligible(registries, run, card): the editor's equipment gate, shared by its projection and mutation door.</summary>
        public static bool CardEquipmentEligible(CoreDeckData d, JObject run, JObject card)
        {
            if (card == null) return false;
            var definition = Cards.Resolve(d.Run.Run.Combat, card);
            if (!RangeCardAvailable(d, run, definition)) return false;
            if (Js.Truthy(card[DK.GrantedBy]) || IsItemOwned(d, card)) return IsCurrentEquipmentGrant(d, run, card);
            if (IsUnlimitedBasic(d, card, run) || UnlimitedCardIds(d, run).Contains(card.Str(DK.CardId))) return true;
            var tags = Js.Items(definition[DK.Tags]).Select(Js.Str).ToList();
            var rules = d.Rules.Obj(DK.EquipmentEligibility);
            if (tags.Any(tag => Js.Includes(rules[DK.UniversalTags], tag))) return true;
            var pieces = Combat.Equipment.EquippedPieces(d.Run.Run.Combat, run.Obj(DK.Loadout), run.Str(DK.Class), new JObject());
            var equipmentSchools = new HashSet<string>(
                d.Run.Run.EquipmentRows(DK.Armaments).Concat(d.Run.Run.EquipmentRows(DK.Armour))
                    .SelectMany(piece => Js.Items(piece[DK.Tags]).Select(Js.Str))
                    .Where(tag => d.IsCardTag(tag) && !Js.Includes(rules[DK.UniversalTags], tag)), StringComparer.Ordinal);
            var required = tags.Where(equipmentSchools.Contains).ToList();
            if (required.Count == 0) return true;
            var held = new HashSet<string>(pieces.SelectMany(piece => Js.Items(piece[DK.Tags]).Select(Js.Str)), StringComparer.Ordinal);
            // Explicit weapon identity wins over a shared secondary school (a bow's pierce or flourish must never unlock a blade ability).
            var identities = required.Where(tag => Js.Includes(rules[DK.WeaponTags], tag)).ToList();
            return identities.Count > 0 ? identities.Any(held.Contains) : required.Any(held.Contains);
        }

        // ------------------------------------------------------------------ sideboard moves

        private static JArray Sideboard(JObject run)
        {
            if (!(run[DK.Sideboard] is JArray pile)) run[DK.Sideboard] = pile = new JArray();
            return pile;
        }

        private static double SlotCount(JObject run)
        {
            if (Js.IsFinite(run[DK.EquipmentAttackSlotCount])) return run.Num(DK.EquipmentAttackSlotCount);
            // No quota written down: the highest slot index anywhere, retired ones included, + 1.
            var ids = Js.Items(run[DK.Deck]).Concat(Js.Items(run[DK.Sideboard])).Select(c => Js.Get(c, DK.EquipmentAttackSlotId)).Where(Js.Truthy)
                .Concat(Js.Items(run[DK.RemovedAttackSlotIds]));
            return ids.Aggregate(0d, (top, id) => Math.Max(top, RunJs.Number(Js.S(RunJs.Key(id).Substring(RV.AttackSlotPrefix.Length))) + 1));
        }

        private static void Restamp(CoreDeckData d, JObject run)
        {
            if (!Js.Truthy(run[DK.Loadout]) || !Js.Truthy(run[DK.Attributes])) return;
            // stampDeck for a configured Core deck: the pools re-derived, then the gear's lent cards reconciled.
            CoreRunRules.ReconcilePools(d.Run, run, true);
            CoreRunRules.ReconcileGearGrants(d.Run, run);
        }

        /// <summary>moveToSideboard(registries, run, instanceId): true when the card left the deck. Every card may leave; an attack basic's slot is retired.</summary>
        public static bool MoveToSideboard(CoreDeckData d, JObject run, string instanceId)
        {
            var deck = run.Arr(DK.Deck);
            var index = deck.ToList().FindIndex(c => c is JObject o && o.Str(DK.InstanceId) == instanceId);
            var card = index >= 0 ? deck[index] as JObject : null;
            if (card == null) return false;
            if (Js.Truthy(card[DK.EquipmentAttackSlotId]))
            {
                var count = SlotCount(run);
                var existing = run[DK.RemovedAttackSlotIds] ?? new JArray();
                var retired = Ashen.Domain.Run.WeaponCards.RetiredAttackSlots(count, existing);
                var slot = card.Str(DK.EquipmentAttackSlotId);
                if (retired.Contains(slot)) return false;
                var ordered = Js.Items(existing).Select(Js.Str).ToList();
                ordered.Add(slot);
                run.Put(DK.EquipmentAttackSlotCount, count);
                run[DK.RemovedAttackSlotIds] = new JArray(ordered);
            }
            deck.RemoveAt(index);
            // A pristine plain basic has nothing to keep; an upgraded, modded or ranked one is kept like any owned card.
            var pristine = !Js.Truthy(card[DK.Upgraded]) && !(card[DK.Mods] is JArray mods && mods.Count > 0) && !(RunJs.Number(card[DK.Rank]) > 1);
            if (!Js.Truthy(card[DK.EquipmentRole]) && !Js.Truthy(card[DK.GrantedBy]) && UnlimitedCardIds(d, run).Contains(card.Str(DK.CardId)) && pristine) return true;
            Sideboard(run).Add(card);
            return true;
        }

        /// <summary>moveFromSideboard(registries, run, instanceId, settings): true when the card returned to the deck.</summary>
        public static bool MoveFromSideboard(CoreDeckData d, JObject run, string instanceId, JObject settings = null)
        {
            var pile = Sideboard(run);
            var index = pile.ToList().FindIndex(c => c is JObject o && o.Str(DK.InstanceId) == instanceId);
            if (index < 0) return false;
            var held = (JObject)pile[index];
            if (!CardEquipmentEligible(d, run, held)) return false;
            var inDeck = Js.Items(run[DK.Deck]).Count(c => c is JObject o && o.Str(DK.CardId) == held.Str(DK.CardId));
            if (inDeck >= CopyLimit(d, held.Str(DK.CardId), settings, run)) return false;
            pile.RemoveAt(index);
            if (Js.Truthy(held[DK.EquipmentAttackSlotId]))
                run[DK.RemovedAttackSlotIds] = new JArray(Js.Items(run[DK.RemovedAttackSlotIds]).Where(id => Js.Str(id) != held.Str(DK.EquipmentAttackSlotId)));
            run.Arr(DK.Deck).Add(held);
            if (Js.Truthy(held[DK.EquipmentRole])) Restamp(d, run);
            return true;
        }

        private static string MintId(JObject run)
        {
            var next = (Js.IsInt(run[DK.EditMintCounter]) ? run.Num(DK.EditMintCounter) : 0) + 1;
            run.Put(DK.EditMintCounter, next);
            return Ashen.Generated.Core2DeckEditorValues.EditIdPrefix + RunJs.NumStr(next);
        }

        /// <summary>
        /// addBasicCard(registries, run, role, { plain, freshOnly }) for a run whose cards are configured (the plain basics, by card id): a kept copy
        /// returns from the sideboard (a set-aside variant only when <paramref name="freshOnly"/> is off), else a new one is minted.
        /// Under Core 2 no card id is unlimited (ownedOnlyCollection), so this is refused with the web's sentence; a slot-true (equipped) run is not a Core 2 run.
        /// </summary>
        public static JObject AddBasicCard(CoreDeckData d, JObject run, string role, bool freshOnly)
        {
            if (IsEquippedRun(run)) throw new InvalidOperationException(DM.NeedsConfigured);
            var unlimited = UnlimitedCardIds(d, run);
            if (!unlimited.Contains(role)) throw new InvalidOperationException(RunJs.Fmt(DM.NotUnlimited, role, string.Join(DV.ListJoin, unlimited)));
            var kept = Sideboard(run).OfType<JObject>().FirstOrDefault(c => !Js.Truthy(c[DK.EquipmentRole]) && !Js.Truthy(c[DK.GrantedBy]) && c.Str(DK.CardId) == role
                && (!freshOnly || !IsSetAsideBasic(d, c, run)));
            if (kept != null)
            {
                MoveFromSideboard(d, run, kept.Str(DK.InstanceId));
                return kept;
            }
            var card = new JObject { [DK.InstanceId] = MintId(run), [DK.CardId] = role, [DK.Upgraded] = false };
            run.Arr(DK.Deck).Add(card);
            return card;
        }

        // ------------------------------------------------------------------ where the editor opens (model/armouryRules.js)

        /// <summary>noncombatEditing(run): the cards module carries <c>editorWhere: "noncombat"</c> (Core 2 edits deck and equipment outside combat only).</summary>
        public static bool NoncombatEditing(JObject run) => CoreRunData.Module(Subject(run), MI.Cards)?.Str(DK.EditorWhere) == DV.NoncombatEditor;

        /// <summary>editingLocked(run, combat): true while Core forbids deck and equipment changes, any fight that has not resolved (a pending reaction is still a fight).</summary>
        public static bool EditingLocked(JObject run, bool fightUnresolved) => NoncombatEditing(run) && fightUnresolved;

        /// <summary>profileOpensDeckEditor(run): Core opens the editor from the Armoury at will, unless the cards module disables manual editing.</summary>
        public static bool ProfileOpensEditor(JObject run)
        {
            var enabled = CoreRunData.Module(Subject(run), MI.Cards)?[DK.EditorEnabled];
            return NoncombatEditing(run) && !(enabled is JValue v && v.Type == JTokenType.Boolean && !v.Value<bool>());
        }

        /// <summary>armouryOpensDeckEditor(run, settings): the profile allows it and the player's Deck editing setting is on.</summary>
        public static bool ArmouryOpensEditor(CoreDeckData d, JObject run, JObject settings) => ProfileOpensEditor(run) && EditingOn(d, settings);

        /// <summary>unusedInstanceId(run, prefix, cardId): the smallest n at least the deck's length that no owned card holds.</summary>
        public static string UnusedInstanceId(JObject run, string prefix, string cardId)
        {
            var taken = new HashSet<string>(Js.Items(run[DK.Deck]).Concat(Js.Items(run[DK.Sideboard])).Select(c => Js.Str(Js.Get(c, DK.InstanceId))), StringComparer.Ordinal);
            var n = Js.Items(run[DK.Deck]).Count();
            while (taken.Contains(prefix + RunJs.NumStr(n) + DV.IdSeparator + cardId)) n++;
            return prefix + RunJs.NumStr(n) + DV.IdSeparator + cardId;
        }

        // ------------------------------------------------------------------ the draw pile in deck order

        /// <summary>orderedDrawPile(deck, isInnate): the opening draw pile in deck order, Innate cards first with their relative order kept.</summary>
        public static List<JObject> OrderedDrawPile(IEnumerable<JObject> deck, Func<JObject, bool> isInnate)
        {
            var innate = new List<JObject>();
            var rest = new List<JObject>();
            foreach (var card in deck) (isInnate(card) ? innate : rest).Add(card);
            innate.AddRange(rest);
            return innate;
        }

        /// <summary>orderedReturn(discard, order): the empty-pile return in deck order, then cards the deck never held in the order discarded.</summary>
        public static List<JObject> OrderedReturn(IEnumerable<JObject> discard, IEnumerable<string> order)
        {
            var at = new Dictionary<string, int>(StringComparer.Ordinal);
            var i = 0;
            foreach (var id in order) at[id] = i++;
            var list = discard.ToList();
            var known = list.Where(c => at.ContainsKey(c.Str(DK.InstanceId) ?? string.Empty)).OrderBy(c => at[c.Str(DK.InstanceId)]).ToList();
            known.AddRange(list.Where(c => !at.ContainsKey(c.Str(DK.InstanceId) ?? string.Empty)));
            return known;
        }

        // ------------------------------------------------------------------ begin, cancel, commit

        /// <summary>beginDeckEdit(run): a snapshot of everything an editor session can change, to hand to <see cref="CancelEdit"/>.</summary>
        public static JObject BeginEdit(CoreDeckData d, JObject run)
        {
            var snapshot = new JObject();
            foreach (var key in Js.Items(d.Rules[DK.EditState]).Select(Js.Str))
                if (run[key] != null) snapshot[key] = run[key].DeepClone();
            return snapshot;
        }

        /// <summary>cancelDeckEdit(run, snapshot): restore every editor-owned field exactly (a field the snapshot lacks is removed).</summary>
        public static void CancelEdit(CoreDeckData d, JObject run, JObject snapshot)
        {
            foreach (var key in Js.Items(d.Rules[DK.EditState]).Select(Js.Str))
            {
                if (snapshot[key] == null) run.Remove(key);
                else run[key] = snapshot[key].DeepClone();
            }
        }

        /// <summary>
        /// commitDeckEditDraft(registries, run, edited, settings): validate a draft (bounds, identities, known cards, range, no
        /// modified or invented or deleted owned card, copy limits) and write it onto the run; throws with the web's sentence when refused
        /// and leaves the run untouched. Returns the deck's size.
        /// </summary>
        public static int CommitDraft(CoreDeckData d, JObject run, JObject edited, JObject settings = null)
        {
            var editedDeck = edited[DK.Deck] as JArray;
            var refusal = EditRefusal(d, editedDeck?.Count ?? 0, settings);
            if (refusal.Length > 0) throw new InvalidOperationException(refusal);
            var runDeck = Js.Items(run[DK.Deck]).OfType<JObject>().ToList();
            var before = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var beforeOrder = new List<string>();
            foreach (var card in runDeck.Concat(Js.Items(run[DK.Sideboard]).OfType<JObject>()))
            {
                var key = RunJs.Key(card[DK.InstanceId]);
                if (!before.ContainsKey(key)) beforeOrder.Add(key);
                before[key] = card;
            }
            var after = Js.Items(edited[DK.Deck]).Concat(Js.Items(edited[DK.Sideboard])).OfType<JObject>().ToList();
            if (after.Any(card => !Js.Truthy(card[DK.InstanceId]))) throw new InvalidOperationException(DM.InvalidIdentity);
            foreach (var card in after)
                if (!(Js.Str(card[DK.CardId]) is string id) || !d.Run.Run.Cards.Has(id)) throw new InvalidOperationException(RunJs.Fmt(DM.UnknownCard, RunJs.Key(card[DK.CardId])));
            if (new HashSet<string>(after.Select(card => RunJs.Key(card[DK.InstanceId])), StringComparer.Ordinal).Count != after.Count) throw new InvalidOperationException(DM.DuplicateIdentity);
            foreach (var card in Js.Items(edited[DK.Deck]).OfType<JObject>())
                if (!runDeck.Any(held => RunJs.Key(held[DK.InstanceId]) == RunJs.Key(card[DK.InstanceId]))
                    && !RangeCardAvailable(d, run, Cards.Resolve(d.Run.Run.Combat, card))) throw new InvalidOperationException(DM.RangeDisabled);
            var allowed = new HashSet<string>(Js.Items(d.Rules[DK.InventedFields]).Select(Js.Str), StringComparer.Ordinal);
            foreach (var card in after)
            {
                before.TryGetValue(RunJs.Key(card[DK.InstanceId]), out var owned);
                if (owned != null && RunJs.Json(owned) != RunJs.Json(card)) throw new InvalidOperationException(DM.CannotModify);
                if (owned == null && (!IsUnlimitedBasic(d, card, run) || Js.Truthy(card[DK.Upgraded]) || RunJs.Number(card[DK.Rank]) > 1
                    || (card[DK.Mods] is JArray inventedMods && inventedMods.Count > 0) || card.Properties().Any(p => !allowed.Contains(p.Name)))) throw new InvalidOperationException(DM.CannotInvent);
            }
            foreach (var key in beforeOrder)
            {
                var card = before[key];
                var enhanced = !IsUnlimitedBasic(d, card, run) || Js.Truthy(card[DK.Upgraded]) || RunJs.Number(card[DK.Rank]) > 1 || (card[DK.Mods] is JArray mods && mods.Count > 0);
                if (enhanced && !after.Any(row => RunJs.Key(row[DK.InstanceId]) == key)) throw new InvalidOperationException(DM.CannotDelete);
            }
            var deckCards = Js.Items(edited[DK.Deck]).OfType<JObject>().ToList();
            foreach (var id in deckCards.Select(card => Js.Str(card[DK.CardId])).Distinct())
                if (deckCards.Count(card => Js.Str(card[DK.CardId]) == id) > CopyLimit(d, id, settings, run)) throw new InvalidOperationException(DM.CopyLimitExceeded);
            foreach (var key in Js.Items(d.Rules[DK.EditState]).Select(Js.Str))
            {
                if (edited[key] == null) run.Remove(key);
                else run[key] = edited[key].DeepClone();
            }
            return run.Arr(DK.Deck).Count;
        }
    }
}
