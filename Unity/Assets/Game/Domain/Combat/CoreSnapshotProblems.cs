using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DV = Ashen.Generated.Core2DamageValues;
using K = Ashen.Generated.CombatKeys;
using NK = Ashen.Generated.Core2SnapshotKeys;
using NM = Ashen.Generated.Core2SnapshotMessages;
using NN = Ashen.Generated.Core2SnapshotNumbers;
using NV = Ashen.Generated.Core2SnapshotValues;
using RK = Ashen.Generated.Core2ReactionKeys;
using RV = Ashen.Generated.Core2ReactionValues;
using SK = Ashen.Generated.Core2StatusKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// combatSnapshotProblems for a Core 2 fight (web model/combatSnapshot.js, combatReactionState.js, combatStatusState.js, enemyKnowledgeCombat.js,
    /// combatEngagement.js rangeStateProblems): the field-addressed reasons a stored fight is refused. An empty list means the stored shape is sound. The
    /// checks the web makes of rule tables a Core 2 fight copies whole from its profile (matchup, intent, expansion, reaction rules) are of their shape
    /// here, not of every number: the profile pin (D-171) and the save's hash vouch for the numbers. The receipt graph of a paused range plan
    /// (frozen contact ids, lineage sites) is read for its shape only (D-282).
    /// </summary>
    public static class CoreSnapshotProblems
    {
        private static readonly Regex ItemRef = new Regex(NV.ItemRefPattern, RegexOptions.CultureInvariant);

        private static readonly string[] PileNames = { K.Draw, K.Hand, K.Discard, K.Exhaust };

        private static readonly string[] RatingIds = { CK.RatingAr, CK.RatingDr, CK.RatingPr, CK.RatingWard, CK.RatingPoise };

        private static readonly string[] CursorStages = { RV.StageStart, RV.StagePayload, RV.StageDrain };

        private static readonly string[] RiderCounts =
        {
            CK.CardsPlayed, NK.ManaSpent, CK.Discarded, DK.HpLostSinceTurnStart, DK.HpLostThisRound, CK.OfferingsPaid, CK.PreviousSpell, CK.AttacksPlayed, CK.CardPlaysCombat,
        };

        private static bool Record(JToken t) => t is JObject;

        private static bool Text(JToken t) => Js.IsStr(t) && Js.Str(t).Length > 0;

        private static bool Whole(JToken t) => Js.IsInt(t) && Js.D(t) >= 0;

        private static bool Finite(JToken t) => Js.IsFinite(t);

        private static bool Bool(JToken t) => t != null && t.Type == JTokenType.Boolean;

        private static string At(string path, string key) => path + NV.Dot + key;

        private static string At(string path, int index) => path + string.Format(CultureInfo.InvariantCulture, NV.IndexFormat, index);

        private static string Must(string path, string what) => string.Format(CultureInfo.InvariantCulture, NM.MustBe, path, what);

        /// <summary>The problems of a snapshot of a fight of <paramref name="d"/>'s profile.</summary>
        public static List<string> Check(CoreCombatData d, JToken snapshot)
        {
            var problems = new List<string>();
            if (!(snapshot is JObject s)) { problems.Add(Must(NV.PathSnapshot, NV.DescObject)); return problems; }
            var data = d.Combat;
            var enemies = s[K.Enemies] as JArray;
            if (!Js.IsNum(s[K.Version]) || Js.D(s[K.Version]) != data.Engine.Num(K.SnapshotVersion)) problems.Add(Must(K.Version, string.Format(CultureInfo.InvariantCulture, NV.DescVersion, data.Engine.Num(K.SnapshotVersion))));
            var pin = s[NK.GameProfile];
            if (pin != null)
            {
                if (!Record(pin) || !Text(pin[K.Id]) || !Text(pin[NK.Hash])) problems.Add(Must(NK.GameProfile, NV.DescPin));
                if (!Whole(s[NK.GameProfileCommands])) problems.Add(Must(NK.GameProfileCommands, NV.DescWhole));
                var interrupts = s[DK.GameProfileInterrupts] as JObject;
                if (interrupts == null || interrupts.Properties().Any(p => !Whole(p.Value))) problems.Add(Must(DK.GameProfileInterrupts, NV.DescCounts));
            }
            if (!Js.IsInt(s[K.Turn]) || s.Num(K.Turn) < 1) problems.Add(Must(K.Turn, NV.DescPositive));
            if (s[K.EquipmentAttackSlotCount] != null && !Whole(s[K.EquipmentAttackSlotCount])) problems.Add(Must(K.EquipmentAttackSlotCount, NV.DescWhole));
            if (s[K.RemovedAttackSlotIds] != null && !Distinct(s[K.RemovedAttackSlotIds])) problems.Add(Must(K.RemovedAttackSlotIds, NV.DescDistinctIds));
            if (s[CK.SideboardedEquipmentCardIds] != null && !Distinct(s[CK.SideboardedEquipmentCardIds])) problems.Add(Must(CK.SideboardedEquipmentCardIds, NV.DescDistinctIds));
            var phase = s.Str(K.Phase);
            if (phase != V.PhasePlayer && phase != V.PhaseEnemy && phase != V.PhaseEnded) problems.Add(Must(K.Phase, string.Join(NV.ListSeparator, V.PhasePlayer, V.PhaseEnemy, V.PhaseEnded)));
            var result = s[K.Result];
            if (!Js.Nullish(result) && Js.Str(result) != V.Victory && Js.Str(result) != V.Defeat) problems.Add(Must(K.Result, NV.DescResult));
            if ((phase == V.PhaseEnded) != !Js.Nullish(result)) problems.Add(NM.PhaseResult);
            foreach (var key in new[] { K.HandMax, K.DrawPerTurn, K.SwapsLeft, K.IdCounter }) if (!Whole(s[key])) problems.Add(Must(key, NV.DescWhole));
            if (!Js.IsNum(s[K.EmitDepth]) || s.Num(K.EmitDepth) != 0) problems.Add(Must(K.EmitDepth, NV.DescZero));
            if (s[CK.CharacterLevel] != null && (!Js.IsInt(s[CK.CharacterLevel]) || s.Num(CK.CharacterLevel) < 1)) problems.Add(Must(CK.CharacterLevel, NV.DescPositive));
            if (s[K.HandRules] != null)
            {
                if (!Record(s[K.HandRules])) problems.Add(Must(K.HandRules, NV.DescObject));
                var draw = s[K.PendingDiscardDraw];
                if (draw != null && (!Js.IsInt(draw) || Js.D(draw) < 0 || Js.D(draw) > NN.PendingDiscardDrawMax)) problems.Add(Must(K.PendingDiscardDraw, string.Format(CultureInfo.InvariantCulture, NV.DescRange, 0, NN.PendingDiscardDrawMax)));
            }
            if (s[CK.OrderedDraw] != null && !Js.Nullish(s[CK.OrderedDraw]))
            {
                var order = (s[CK.OrderedDraw] as JObject)?[NK.Order] as JArray;
                if (order == null || !Distinct(order)) problems.Add(Must(CK.OrderedDraw, NV.DescDistinctIds));
            }
            if (!Js.Nullish(s[K.DerivedStatRuleSnapshot])) problems.AddRange(DerivedProblems(d, s[K.DerivedStatRuleSnapshot], s[K.Attributes] as JObject));
            var proposals = s[SK.ProposalRewards];
            if (proposals != null && (!Record(proposals) || !Whole(proposals[NK.Cinders]) || Js.D(proposals[NK.Cinders]) > NN.ProposalRewardMax || (proposals[NK.Settled] != null && !Bool(proposals[NK.Settled])))) problems.Add(Must(SK.ProposalRewards, NV.DescValid));
            problems.AddRange(ChoiceProblems(s));
            problems.AddRange(ReactionProblems(d, s));
            problems.AddRange(RangeProblems(d, s));
            if (!Bool(s[K.EquipmentChanged])) problems.Add(Must(K.EquipmentChanged, NV.DescBoolean));
            if (s[K.Skills] != null && !(s[K.Skills] is JObject skills && skills.Properties().All(p => Record(p.Value)))) problems.Add(Must(K.Skills, NV.DescValid));
            if (s[CK.SkillFeats] != null && !Distinct(s[CK.SkillFeats])) problems.Add(Must(CK.SkillFeats, NV.DescDistinctIds));
            if (s[K.CoreTags] != null && !(s[K.CoreTags] is JArray)) problems.Add(Must(K.CoreTags, NV.DescArray));
            if (s[CK.Consumables] != null && !(s[CK.Consumables] is JObject consumables && consumables.Properties().All(p => Js.IsInt(p.Value) && Js.D(p.Value) >= 1))) problems.Add(Must(CK.Consumables, NV.DescCounts));
            if (s[CK.SigilSlots] != null && !(s[CK.SigilSlots] is JObject slots && slots.Properties().All(p => p.Value is JArray))) problems.Add(Must(CK.SigilSlots, NV.DescValid));
            if (s[CK.AttunedSigils] != null && !Distinct(s[CK.AttunedSigils])) problems.Add(Must(CK.AttunedSigils, NV.DescDistinctIds));
            if (s[CK.Companions] != null && !Distinct(s[CK.Companions])) problems.Add(Must(CK.Companions, NV.DescDistinctIds));
            if (s[K.SkillXp] != null)
            {
                if (!(s[K.SkillXp] is JObject xp)) problems.Add(Must(K.SkillXp, NV.DescObject));
                else foreach (var owner in xp.Properties())
                    if (!Record(owner.Value) || !Record(owner.Value[NK.Xp]) || ((JObject)owner.Value[NK.Xp]).Properties().Any(p => !Finite(p.Value) || Js.D(p.Value) < 0))
                        problems.Add(Must(At(K.SkillXp, owner.Name), NV.DescValid));
            }
            if (s[K.ItemUpgradeLevels] != null)
            {
                if (!(s[K.ItemUpgradeLevels] is JObject levels)) problems.Add(Must(K.ItemUpgradeLevels, NV.DescObject));
                else foreach (var p in levels.Properties()) if (!ItemRef.IsMatch(p.Name) || !Whole(p.Value)) problems.Add(Must(At(K.ItemUpgradeLevels, p.Name), NV.DescTier));
            }
            if (!Record(s[K.EquipmentPoolDeficits])) problems.Add(Must(K.EquipmentPoolDeficits, NV.DescObject));
            if (!Js.Nullish(s[K.Loadout]) && !Record(s[K.Loadout])) problems.Add(Must(K.Loadout, NV.DescObjectOrNull));
            if (!Js.Nullish(s[K.Attributes]) && !Record(s[K.Attributes])) problems.Add(Must(K.Attributes, NV.DescObjectOrNull));
            if (!Js.Nullish(s[CK.AttributeMode]) && !Text(s[CK.AttributeMode])) problems.Add(Must(CK.AttributeMode, NV.DescStringOrNull));
            if (!Record(s[K.SwapCostRule])) problems.Add(Must(K.SwapCostRule, NV.DescObject));
            if (!(s[K.EventLog] is JArray)) problems.Add(Must(K.EventLog, NV.DescArray));
            if (!(s[K.TriggerState] is JArray gates) || gates.Any(g => !(g is JArray pair) || pair.Count != NN.PairLength || !Text(pair[0]))) problems.Add(Must(K.TriggerState, NV.DescEntries));
            problems.AddRange(EntityProblems(d, s[K.Player], K.Player, true, s));
            if (enemies == null) problems.Add(Must(K.Enemies, NV.DescArray));
            else for (var i = 0; i < enemies.Count; i++) problems.AddRange(EntityProblems(d, enemies[i], At(K.Enemies, i), false, s));
            if (enemies != null) problems.AddRange(KnowledgeProblems(s[CK.EnemyKnowledge], enemies));
            if (s[CK.CombatExpansionVersion] != null && Js.D(s[CK.CombatExpansionVersion]) != d.ExpandedVersion) problems.Add(Must(CK.CombatExpansionVersion, NV.DescExpanded));
            if (s[CK.CombatExpansionVersion] != null && !Record(s[CK.CombatExpansionRules])) problems.Add(Must(CK.CombatExpansionRules, NV.DescObject));
            if (s[CK.FixCoreRules] != null && !Record(s[CK.FixCoreRules])) problems.Add(Must(CK.FixCoreRules, NV.DescObject));
            problems.AddRange(PileProblems(s));
            problems.AddRange(FormationProblems(s));
            return problems;
        }

        private static bool Distinct(JToken token) => token is JArray a && a.All(Text) && a.Select(Js.Str).Distinct().Count() == a.Count;

        // ------------------------------------------------------------------ the derived-stat receipt

        /// <summary>restoreDerivedStatRuleSnapshot for the one envelope a Core 2 run writes (D-138: the retired envelopes are not carried).</summary>
        private static IEnumerable<string> DerivedProblems(CoreCombatData d, JToken token, JObject attributes)
        {
            var path = K.DerivedStatRuleSnapshot;
            if (!(token is JObject snapshot)) { yield return Must(path, NV.DescObject); yield break; }
            var envelope = d.Run.RuleSection(Ashen.Generated.Core2RunKeys.Derived).Num(K.SnapshotVersion);
            if (!Js.IsNum(snapshot[K.SnapshotVersion]) || snapshot.Num(K.SnapshotVersion) != envelope) yield return Must(At(path, K.SnapshotVersion), string.Format(CultureInfo.InvariantCulture, NV.DescVersion, envelope));
            var rules = snapshot.Obj(K.Rules);
            if (!Whole(snapshot[Ashen.Generated.RunKeys.RulesetVersion]) || rules == null || !JToken.DeepEquals(rules[Ashen.Generated.RunKeys.RulesetVersion], snapshot[Ashen.Generated.RunKeys.RulesetVersion]))
            {
                yield return NM.RulesetDisagrees;
                yield break;
            }
            foreach (var row in (rules.Obj(K.Rules) ?? new JObject()).Properties())
            {
                var at = At(At(path, K.Rules), row.Name);
                if (!(row.Value is JObject r) || !Finite(r[K.Base]) || (r[NK.Gain] != null && !Finite(r[NK.Gain]))) { yield return Must(at, NV.DescRow); continue; }
            }
            var modifiers = snapshot.Obj(Ashen.Generated.RunKeys.RelicModifiers);
            var school = modifiers?.Obj(K.DamageBySchoolAdd);
            if (modifiers == null || school == null || !(modifiers[Ashen.Generated.RunKeys.Sources] is JArray)) { yield return Must(At(path, Ashen.Generated.RunKeys.RelicModifiers), NV.DescValid); yield break; }
            foreach (var name in Js.Items(d.Combat.Engine[K.DamageSchools]).Select(Js.Str))
                if (school[name] == null) yield return string.Format(CultureInfo.InvariantCulture, NM.MissingSchool, name);
            foreach (var p in school.Properties())
                if (!Finite(p.Value) || Js.D(p.Value) < 0) yield return Must(At(At(At(path, Ashen.Generated.RunKeys.RelicModifiers), K.DamageBySchoolAdd), p.Name), NV.DescNonNegative);
        }

        // ------------------------------------------------------------------ a fighter

        private static IEnumerable<string> EntityProblems(CoreCombatData d, JToken token, string path, bool player, JObject s)
        {
            if (!(token is JObject e)) { yield return Must(path, NV.DescObject); yield break; }
            if (e[NK.ReactionsEnabled] != null && !Bool(e[NK.ReactionsEnabled])) yield return Must(At(path, NK.ReactionsEnabled), NV.DescBoolean);
            if (e[CK.CombatCounter] != null && !(e[CK.CombatCounter] is JObject counter && counter[CK.Carrier] is JObject carrier && carrier[CK.CombatProfile] is JObject)) yield return Must(At(path, CK.CombatCounter), NV.DescCounter);
            if (e[CK.CombatTraitGuard] != null)
            {
                var guard = e[CK.CombatTraitGuard] as JObject;
                if (guard == null || !Whole(guard[K.Poise]) || !Whole(guard[K.Ward]) || guard.Properties().Any(p => p.Name != K.Poise && p.Name != K.Ward)) yield return Must(At(path, CK.CombatTraitGuard), NV.DescGuard);
            }
            if (e[CK.IntentRevealed] != null && !Bool(e[CK.IntentRevealed])) yield return Must(At(path, CK.IntentRevealed), NV.DescBoolean);
            if (e[NK.IntentReads] != null && !(e[NK.IntentReads] is JObject reads && reads.Properties().All(p => Text(JValue.CreateString(p.Name)) && Bool(p.Value)))) yield return Must(At(path, NK.IntentReads), NV.DescBooleans);
            var riders = e[CK.AbilityRiders];
            if (riders != null)
            {
                if (!Record(riders)) yield return Must(At(path, CK.AbilityRiders), NV.DescObject);
                else foreach (var key in RiderCounts)
                    if (riders[key] != null && !Whole(riders[key])) yield return Must(At(At(path, CK.AbilityRiders), key), NV.DescWhole);
            }
            if (!Text(e[K.Id])) yield return Must(At(path, K.Id), NV.DescText);
            if (e.Str(K.Kind) != (player ? V.Player : V.Enemy)) yield return Must(At(path, K.Kind), player ? V.Player : V.Enemy);
            var defKey = player ? K.ClassId : K.EnemyId;
            if (!Text(e[defKey])) yield return Must(At(path, defKey), NV.DescText);
            foreach (var key in new[] { K.Hp, K.MaxHp, K.Block }) if (!Finite(e[key])) yield return Must(At(path, key), NV.DescFinite);
            if (Finite(e[K.MaxHp]) && e.Num(K.MaxHp) <= 0) yield return Must(At(path, K.MaxHp), NV.DescPositiveNumber);
            if (Finite(e[K.Hp]) && Finite(e[K.MaxHp]) && (e.Num(K.Hp) < 0 || e.Num(K.Hp) > e.Num(K.MaxHp))) yield return Must(At(path, K.Hp), NV.DescWithinMax);
            if (!Record(e[K.Statuses])) yield return Must(At(path, K.Statuses), NV.DescObject);
            if (e[CK.Ratings] != null && !(e[CK.Ratings] is JObject ratings && RatingIds.All(id => Finite(ratings[id]) && Js.D(ratings[id]) >= 0))) yield return Must(At(path, CK.Ratings), NV.DescRatings);
            if (!Bool(e[K.Alive])) yield return Must(At(path, K.Alive), NV.DescBoolean);
            if (e[CK.CritRules] != null && !(e[CK.CritRules] is JArray)) yield return Must(At(path, CK.CritRules), NV.DescArray);
            foreach (var pool in new[] { CK.PersistentWard, CK.PersistentPoise })
            {
                var state = e[pool];
                var required = pool == CK.PersistentWard && s[CK.CombatExpansionVersion] != null;
                if (state == null && !required) continue;
                if (!(state is JObject row) || !Whole(row[K.Max]) || !Whole(row[K.Value]) || row.Num(K.Value) > row.Num(K.Max)) { yield return Must(At(path, pool), NV.DescPool); continue; }
                if (row.Properties().Any(p => p.Name != K.Max && p.Name != K.Value && p.Name != NK.Missing)) yield return string.Format(CultureInfo.InvariantCulture, NM.UnknownField, At(path, pool), NK.Missing);
                if (row[NK.Missing] != null && (!Whole(row[NK.Missing]) || row.Num(K.Value) != 0)) yield return Must(At(At(path, pool), NK.Missing), NV.DescMissing);
            }
            var control = e[CK.StatusControl];
            if (control != null || s[CK.CombatExpansionVersion] != null)
            {
                if (!(control is JObject sc)) yield return Must(At(path, CK.StatusControl), NV.DescObject);
                else
                {
                    var at = At(path, CK.StatusControl);
                    if (!Whole(sc[NK.OwnerCycle])) yield return Must(At(at, NK.OwnerCycle), NV.DescWhole);
                    if (!Record(sc[NK.Gauges])) yield return Must(At(at, NK.Gauges), NV.DescObject);
                    foreach (var key in new[] { NK.WardAllowances, NK.CompletedActions })
                        if (!(sc[key] is JObject map && map.Properties().All(p => p.Name.Length > 0 && Bool(p.Value)))) yield return Must(At(at, key), NV.DescBooleans);
                }
            }
            if (e[NK.BurdenPiles] != null)
            {
                var piles = e[NK.BurdenPiles] as JObject;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var zone in PileNames)
                {
                    var rows = piles?[zone] as JArray;
                    if (rows == null || rows.Count > NN.BurdenPileMax) { yield return Must(At(At(path, NK.BurdenPiles), zone), NV.DescPile); continue; }
                    foreach (var card in rows)
                        if (!(card is JObject c) || !Text(c[K.InstanceId]) || !seen.Add(c.Str(K.InstanceId)) || !d.Combat.Cards.Has(c.Str(K.CardId))) yield return Must(At(At(path, NK.BurdenPiles), zone), NV.DescBurden);
                }
            }
            if (e[CK.CombatStance] != null && !(e[CK.CombatStance] is JObject stance && Text(stance[CK.Maneuver]))) yield return Must(At(path, CK.CombatStance), NV.DescStance);
        }

        // ------------------------------------------------------------------ the cards

        private static IEnumerable<string> CardProblems(JToken token, string path)
        {
            if (!(token is JObject card)) { yield return Must(path, NV.DescObject); yield break; }
            if (!Text(card[K.InstanceId])) yield return Must(At(path, K.InstanceId), NV.DescText);
            if (!Text(card[K.CardId])) yield return Must(At(path, K.CardId), NV.DescText);
            if (!Bool(card[K.Upgraded])) yield return Must(At(path, K.Upgraded), NV.DescBoolean);
            if (card[CK.Rank] != null && !(Js.IsInt(card[CK.Rank]) && card.Num(CK.Rank) >= 1)) yield return Must(At(path, CK.Rank), NV.DescPositive);
        }

        private static IEnumerable<string> PileProblems(JObject s)
        {
            if (!(s[K.Piles] is JObject piles)) { yield return Must(K.Piles, NV.DescObject); yield break; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var turn = s.Num(K.Turn);
            foreach (var row in Js.Items(s[SK.HiddenCards]))
            {
                if (!(row is JObject hidden) || !Js.IsInt(hidden[SK.ReturnTurn]) || hidden.Num(SK.ReturnTurn) < turn || hidden.Num(SK.ReturnTurn) > turn + NN.HiddenReturnTurns) yield return Must(SK.HiddenCards, NV.DescHidden);
                foreach (var problem in CardProblems((row as JObject)?[K.Card], At(SK.HiddenCards, K.Card))) yield return problem;
                if (!seen.Add((row as JObject)?[K.Card]?[K.InstanceId]?.ToString())) yield return NM.DuplicateHidden;
            }
            foreach (var pile in PileNames)
            {
                if (!(piles[pile] is JArray cards)) { yield return Must(At(K.Piles, pile), NV.DescArray); continue; }
                for (var i = 0; i < cards.Count; i++)
                {
                    foreach (var problem in CardProblems(cards[i], At(At(K.Piles, pile), i))) yield return problem;
                    var id = (cards[i] as JObject)?.Str(K.InstanceId);
                    if (id != null && !seen.Add(id)) yield return string.Format(CultureInfo.InvariantCulture, NM.DuplicateCard, id);
                }
            }
        }

        // ------------------------------------------------------------------ a choice a card left open

        private static IEnumerable<string> ChoiceProblems(JObject s)
        {
            var pending = s[SK.PendingAbilityDiscard] ?? s[SK.PendingCardChoice];
            var reaction = s[DK.PendingReaction] != null;
            if (pending != null)
            {
                var handCount = (s.Obj(K.Piles)?.Arr(K.Hand)?.Count) ?? 0;
                var from = (pending as JObject)?.Obj(NK.Effect)?.Str(NK.From);
                var limit = s[SK.PendingCardChoice] != null ? (s.Obj(K.Piles)?.Arr(from ?? string.Empty)?.Count ?? 0) : handCount;
                if (!(pending is JObject p) || !Js.IsInt(p[K.Count]) || p.Num(K.Count) < 1 || p.Num(K.Count) > limit) yield return Must(SK.PendingAbilityDiscard, NV.DescHandCount);
                if (!(s[NK.PendingAbilityPlay] is JObject play)) yield return Must(NK.PendingAbilityPlay, NV.DescPaidCard);
                else foreach (var problem in CardProblems(play[NK.Instance], At(NK.PendingAbilityPlay, NK.Instance))) yield return problem;
                if (!Queued(s[NK.AbilityQueue])) yield return Must(NK.AbilityQueue, NV.DescQueued);
            }
            else if ((s[NK.AbilityQueue] != null && !reaction) || s[NK.PendingAbilityPlay] != null) yield return NM.QueueNeedsChoice;
        }

        private static bool Queued(JToken token) => token is JArray a && a.All(action => action is JObject o && o[NK.Effect] is JObject effect && Text(effect[K.Op]));

        // ------------------------------------------------------------------ the reaction state

        private static IEnumerable<string> ReactionProblems(CoreCombatData d, JObject s)
        {
            var fields = new[] { DK.PendingReaction, DK.ReactionResume, DK.ReactionCursor, CK.ReactionHandCleanup, CK.EnemyReactionRolls, DK.ReactionRootPlay, DK.ReactionStack, CK.ReactionRules };
            if (s[CK.ReactionRulesVersion] == null)
            {
                if (fields.Any(f => s[f] != null)) yield return NM.PausedNeedsRules;
                yield break;
            }
            if (!Js.IsNum(s[CK.ReactionRulesVersion]) || (s.Num(CK.ReactionRulesVersion) != 1 && s.Num(CK.ReactionRulesVersion) != d.ReactionVersion) || Js.D(s[CK.CombatExpansionVersion]) != d.ExpandedVersion) yield return NM.ReactionNeedsExpanded;
            if (s.Num(CK.ReactionRulesVersion) == d.ReactionVersion && !Record(s[CK.ReactionRules])) yield return string.Format(CultureInfo.InvariantCulture, NM.ReactionRulesProblem, Must(CK.ReactionRules, NV.DescObject));
            else if (s.Num(CK.ReactionRulesVersion) != d.ReactionVersion && s[CK.ReactionRules] != null) yield return NM.ReactionTuningNeedsTwo;
            if (s[NK.FoundationNextSerial] != null && !Whole(s[NK.FoundationNextSerial])) yield return NM.BadFoundationSerial;
            var enemies = s[K.Enemies] as JArray ?? new JArray();
            var cursor = s[DK.ReactionCursor];
            if (cursor != null && (!(cursor is JObject cur) || !Whole(cur[RK.Index]) || cur.Num(RK.Index) >= enemies.Count || !CursorStages.Contains(cur.Str(RK.Stage)))) yield return NM.BadCursor;
            if (s[CK.ReactionHandCleanup] != null && !Record(s[CK.ReactionHandCleanup])) yield return NM.BadCleanup;
            var queue = (s[NK.AbilityQueue] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var resume = s[DK.ReactionResume] as JObject;
            var incoming = queue.Concat(resume?[RK.Queue] is JArray rq ? rq.OfType<JObject>() : Enumerable.Empty<JObject>()).ToList();
            JObject GroupOf(JObject action) => action.Obj(NK.Meta)?.Obj(NK.ExpansionGroup);
            var pending = s[DK.PendingReaction];
            var paused = pending != null || resume != null;
            var phase = s.Str(K.Phase);
            var enemyPayload = incoming.Any(a => GroupOf(a)?[NK.EnemyMoveStart] != null);
            if (paused && (phase == V.PhaseEnemy || enemyPayload) && cursor == null) yield return NM.NeedsCursor;
            if (cursor != null && phase != V.PhaseEnemy) yield return NM.CursorOutsidePhase;
            if (cursor != null && Js.Nullish(s[K.Result]) && !Record(s[CK.ReactionHandCleanup])) yield return NM.NeedsCleanup;
            if (s[CK.EnemyReactionRolls] != null && (!(s[CK.EnemyReactionRolls] is JObject rolls) || rolls.Properties().Any(p => !Bool(p.Value)))) yield return NM.BadRolls;
            var root = s[DK.ReactionRootPlay];
            if (root != null && (!(root is JObject r) || !Record(r[NK.Instance]) || !Text(r[NK.Instance][K.CardId]) || !(pending != null || s[DK.ReactionStack] != null || resume != null))) yield return NM.BadRootPlay;
            foreach (var group in incoming.Select(GroupOf).Where(g => g?[NK.ContactSequence] != null))
            {
                var sequence = group.Obj(NK.ContactSequence);
                bool Targets(JToken t) => t is JObject o && o.Properties().All(p => p.Value is JObject row && Text(row[K.Id]) && row[NK.Contacts] is JArray contacts && contacts.Count > 0 && contacts.All(Record));
                var contacts = sequence[NK.Contacts] as JArray;
                if (s.Obj(CK.ReactionRules)?.Str(DK.ContactPolicy) != DV.PolicyPerHit || !Text(sequence[NK.RootKey]) || contacts == null || contacts.Count == 0
                    || contacts.Any(contact => !(contact is JObject co) || !Targets(co[NK.Targets])) || !Whole(sequence[NK.Index]) || sequence.Num(NK.Index) >= contacts.Count
                    || group.Str(NK.Key) != sequence.Str(NK.RootKey) + DV.HitSeparator + sequence.Num(NK.Index).ToString(CultureInfo.InvariantCulture) || !Targets(sequence[NK.RootTargets])
                    || !(sequence[NK.Gates] is JObject gates) || gates.Properties().Any(p => !Bool(p.Value)))
                {
                    yield return NM.BadSequence;
                    continue;
                }
            }
            var stack = s[DK.ReactionStack];
            if (stack != null)
            {
                var groupKey = (stack as JObject)?.Str(RK.GroupKey);
                var entries = (stack as JObject)?[CK.Entries] as JArray;
                var valid = stack is JObject so && Text(so[RK.GroupKey]) && Bool(so[RK.Resolving]) && entries != null && entries.Count > 0 && entries.All(entry => EntryOk(entry, enemies, groupKey))
                    && incoming.Any(a => GroupOf(a)?.Str(NK.Key) == groupKey)
                    && (so[RK.CounterSeats] == null || (so[RK.CounterSeats] is JArray seats && seats.Select(Js.Str).Distinct().Count() == seats.Count && seats.All(seat => Js.Str(seat) == V.Player)));
                if (s.Num(CK.ReactionRulesVersion) != d.ReactionVersion) yield return NM.StackNeedsTwo;
                else if (!valid) yield return NM.BadStack;
                else if (pending != null)
                {
                    var offer = pending as JObject;
                    var stacked = entries.OfType<JObject>().Count(entry => entry.Str(RK.Side) == V.Player && entry.Str(DK.OwnerId) == offer?.Str(DK.OwnerId));
                    if (offer == null || offer.Str(RK.GroupKey) != groupKey || offer.Str(K.Id) != groupKey + DV.KeySeparator + offer.Str(DK.OwnerId) + DV.KeySeparator + stacked.ToString(CultureInfo.InvariantCulture)) yield return NM.OfferDepth;
                }
            }
            if (pending != null)
            {
                var offer = pending as JObject;
                var options = offer?[RK.Options] as JArray;
                if (offer == null || !Text(offer[K.Id]) || !Text(offer[RK.GroupKey]) || offer.Str(DK.OwnerId) != V.Player || !enemies.OfType<JObject>().Any(e => e.Str(K.Id) == offer.Str(K.SourceId))
                    || options == null || options.Count == 0 || options.Any(option => !(option is JObject o) || !Text(o[K.Id]) || !(o[RK.Play] is JObject play) || !Text(play[RK.CardInstanceId])
                        || !Finite(o[RK.StaminaCost]) || o.Num(RK.StaminaCost) < 0 || !Finite(o[RK.ManaCost]) || o.Num(RK.ManaCost) < 0)
                    || options.Select(option => option[K.Id]?.ToString()).Distinct().Count() != options.Count) yield return NM.BadOffer;
                if (!Queued(s[NK.AbilityQueue]) || !queue.Any(a => GroupOf(a)?.Str(NK.Key) == offer?.Str(RK.GroupKey) && !GroupOf(a).Is(NK.Begun))) yield return NM.OfferNeedsPlan;
                if (phase != V.PhasePlayer && phase != V.PhaseEnemy) yield return NM.OfferOutsideCombat;
            }
            if (resume != null && (!Queued(resume[RK.Queue]) || (s[SK.PendingAbilityDiscard] == null && s[SK.PendingCardChoice] == null) || s[NK.PendingAbilityPlay] == null)) yield return NM.ResumeNeedsChoice;
            if (resume != null && !Js.Nullish(resume[RK.Foundation])) yield return NM.BadResumeFoundation;
        }

        private static bool EntryOk(JToken token, JArray enemies, string groupKey)
        {
            if (!(token is JObject entry)) return false;
            var side = entry.Str(RK.Side);
            if (side == V.Player)
                return entry.Str(DK.OwnerId) == V.Player && Text(entry[K.CardId]) && entry[RK.Play] is JObject play && Text(play[RK.CardInstanceId])
                    && (Js.Nullish(entry[RK.Maneuver]) || Js.IsStr(entry[RK.Maneuver]))
                    && (entry[RK.Incoming] == null || (entry[RK.Incoming] is JObject incoming && incoming.Properties().All(p => Finite(p.Value) && Js.D(p.Value) > 0)));
            if (side != Ashen.Generated.Core2EnemyValues.SideEnemy) return false;
            var plan = entry[Ashen.Generated.Core2EnemyKeys.Plan] as JObject;
            var group = plan?.Obj(NK.Group);
            var actions = plan?[NK.Actions] as JArray;
            bool Ref(JToken t) => Js.Nullish(t) || (t is JObject r && Text(r[K.Id]));
            return enemies.OfType<JObject>().Any(e => e.Str(K.Id) == entry.Str(DK.OwnerId)) && Text(entry[K.MoveId]) && entry.Str(Ashen.Generated.Core2EnemyKeys.TargetPlayerId) == V.Player
                && group != null && Text(group[NK.Key]) && group.Str(NK.Key) != groupKey && Record(group[NK.Targets]) && group.Obj(NK.Carrier)?.Is(DK.CombatReaction) == true
                && actions != null && actions.Count > 0 && actions.All(a => a is JObject o && o[NK.Effect] is JObject effect && Text(effect[K.Op]) && o[NK.Source] == null && o[NK.Target] == null
                    && Ref(o[Ashen.Generated.Core2EnemyKeys.SourceRef]) && Ref(o[Ashen.Generated.Core2EnemyKeys.TargetRef]) && Ref(o[Ashen.Generated.Core2EnemyKeys.OwnerRef]));
        }

        // ------------------------------------------------------------------ the range state

        private static IEnumerable<string> RangeProblems(CoreCombatData d, JObject s)
        {
            var enemies = (s[K.Enemies] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var actors = new List<KeyValuePair<JObject, string>> { new KeyValuePair<JObject, string>(s[K.Player] as JObject, CV.ActorPlayerKey) };
            actors.AddRange(enemies.Select(e => new KeyValuePair<JObject, string>(e, CV.ActorEnemyPrefix + e.Str(K.Id))));
            var state = s[CK.RangeState];
            if (state != null)
            {
                foreach (var problem in RangeStateProblems(state, actors.Select(a => a.Value).ToList())) yield return problem;
                if (s.Num(CK.CombatExpansionVersion) != d.ExpandedVersion || s.Obj(CK.FixCoreRules)?[CK.Enabled]?.Type == JTokenType.Boolean && !s.Obj(CK.FixCoreRules).Is(CK.Enabled)) yield return NM.EngagementNeedsCore;
                foreach (var pair in actors)
                    if (pair.Key?.Str(CK.RangeActorKey) != pair.Value) yield return string.Format(CultureInfo.InvariantCulture, NM.BadActorKey, pair.Value);
            }
            else if (actors.Any(pair => pair.Key?[CK.RangeActorKey] != null)) yield return NM.ActorKeyNeedsState;
        }

        private static IEnumerable<string> RangeStateProblems(JToken token, IReadOnlyList<string> actorKeys)
        {
            if (!(token is JObject state) || state.Properties().Count() != NN.RangeStateFields || !Js.IsNum(state[CK.Version]) || state.Num(CK.Version) != 1 || !Record(state[CK.Policy]) || !Record(state[CK.Engagements]))
            {
                yield return NM.BadEngagement;
                yield break;
            }
            var known = new HashSet<string>(actorKeys, StringComparer.Ordinal);
            var engagements = (JObject)state[CK.Engagements];
            foreach (var p in engagements.Properties())
            {
                JArray pair = null;
                try { pair = JArray.Parse(p.Name); } catch (Exception) { }
                var bad = pair == null || pair.Count != NN.PairLength || !Finite(p.Value) || Js.D(p.Value) < 0 || Js.D(p.Value) > NN.BandMax || Js.D(p.Value) != Math.Floor(Js.D(p.Value))
                    || pair.Any(id => !known.Contains(Js.Str(id)))
                    || pair.Count == NN.PairLength && (Js.Str(pair[0]).StartsWith(CV.ActorPlayerPrefix, StringComparison.Ordinal) == Js.Str(pair[1]).StartsWith(CV.ActorPlayerPrefix, StringComparison.Ordinal))
                    || pair.Count == NN.PairLength && CoreRange.PairKey(Js.Str(pair[0]), Js.Str(pair[1])) != p.Name;
                if (bad) yield return NM.BadPair;
            }
            foreach (var a in known)
                foreach (var b in known)
                    if (a.StartsWith(CV.ActorPlayerPrefix, StringComparison.Ordinal) && b.StartsWith(CV.ActorEnemyPrefix, StringComparison.Ordinal) && engagements[CoreRange.PairKey(a, b)] == null) yield return NM.MissingPair;
        }

        // ------------------------------------------------------------------ the formation

        private static IEnumerable<string> FormationProblems(JObject s)
        {
            if (!Js.Truthy(s[CK.FormationPolicy])) yield break;
            var fighters = new[] { s[K.Player] as JObject }.Concat((s[K.Enemies] as JArray ?? new JArray()).OfType<JObject>()).ToList();
            if (fighters.Any(f => f == null || !Text(f[CK.FormationCell]))) { yield return NM.MissingCell; yield break; }
            foreach (var side in fighters.GroupBy(f => f.Str(K.Kind)))
                if (side.Select(f => f.Str(CK.FormationCell)).Distinct().Count() != side.Count()) yield return NM.OccupiedCell;
        }

        // ------------------------------------------------------------------ the knowledge ledger

        private static IEnumerable<string> KnowledgeProblems(JToken token, JArray enemies)
        {
            var rows = enemies.OfType<JObject>().ToList();
            if (token == null)
            {
                if (rows.Any(e => e[CK.KnowledgeAction] != null)) yield return NM.ActionNeedsLedger;
                yield break;
            }
            if (!(token is JObject state) || !Js.IsNum(state[CK.Version]) || state.Num(CK.Version) != 1 || !Record(state[NK.Owners]) || !Whole(state[NK.NextSerial]) || !Bool(state[CK.Bankable]))
            {
                yield return NM.BadKnowledge;
                yield break;
            }
            var encounter = state[CK.Encounter];
            if ((state.Is(CK.Bankable) && Js.Nullish(encounter)) || (!Js.Nullish(encounter) && !(encounter is JObject en && Text(en[K.Id]) && en[NK.EnemyIds] is JArray ids && ids.Count > 0 && Distinct(ids)))) yield return NM.BadEncounter;
            foreach (var owner in ((JObject)state[NK.Owners]).Properties())
            {
                var o = owner.Value as JObject;
                if (!Text(JValue.CreateString(owner.Name)) || o == null || !Whole(o[CK.EarnedXp]) || !(o[NK.Feedback] is JArray feedback) || feedback.Count > NN.FeedbackMax || !(o[CK.CounterEnemyIds] is JArray counters) || !Distinct(counters))
                    yield return string.Format(CultureInfo.InvariantCulture, NM.BadOwner, owner.Name);
            }
            var serials = new HashSet<double>();
            foreach (var enemy in rows)
            {
                var action = enemy[CK.KnowledgeAction];
                if (action == null) continue;
                if (!(action is JObject a) || !Js.IsInt(a[NK.Serial]) || a.Num(NK.Serial) < 1 || a.Num(NK.Serial) > state.Num(NK.NextSerial) || !serials.Add(a.Num(NK.Serial))
                    || !Text(a[NK.Category]) || !Bool(a[NK.Executed]) || (a[CK.Cancelled] != null && !Bool(a[CK.Cancelled])) || !Record(a[NK.Reads]))
                    yield return string.Format(CultureInfo.InvariantCulture, NM.BadAction, enemy.Str(K.Id));
            }
        }
    }
}
