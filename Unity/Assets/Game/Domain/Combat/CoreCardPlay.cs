using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CC = Ashen.Generated.Core2CostKeys;
using SK = Ashen.Generated.Core2StatusKeys;
using SV = Ashen.Generated.Core2StatusValues;
using SM = Ashen.Generated.Core2StatusMessages;
using DK = Ashen.Generated.Core2DamageKeys;
using DM = Ashen.Generated.Core2DamageMessages;
using DN = Ashen.Generated.Core2DamageNumbers;
using DV = Ashen.Generated.Core2DamageValues;
using CCM = Ashen.Generated.Core2CostMessages;
using CCV = Ashen.Generated.Core2CostValues;
using CM = Ashen.Generated.Core2CombatMessages;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The front half of a Core 2 card play (web engine/combat.js doPlayCard up to the effects): the cost gates, the price paid, the card leaves the
    /// hand, the play counters move, and the card's stance is entered (engine/combatCardTactics.js tacticalCarrier, engine/gameProfileCombat.js
    /// setProfileStance). The card's effects, the hit roll and the damage are the later work packages (WP-6, WP-7); <see cref="Enter"/> is the
    /// part of a play a Core 2 turn needs before them, and it refuses a card the gates refuse before it changes anything.
    /// </summary>
    public static class CoreCardPlay
    {
        /// <summary>
        /// The carrier a played card hands the stance and the hit: its instance, its tags, the metadata its definition keeps for combat, the
        /// effects' statuses and the combat profile (tacticalCarrier). The damage type a wielded weapon would resolve into the profile is the
        /// damage work package's (WP-7); the stance reads none of it.
        /// </summary>
        public static JObject Carrier(CombatState c, JObject instance, JObject def)
        {
            var tags = Js.Items(def[K.Tags]).Any() ? def[K.Tags] : null;
            var authoredTags = !Js.Nullish(def[CK.CardTags]) ? def[CK.CardTags] : tags ?? new JArray();
            var cardTags = !Js.Nullish(def[CK.CardTags]) ? def[CK.CardTags] : tags;
            var spell = Js.Items(!Js.Nullish(def[CK.CardTags]) ? def[CK.CardTags] : def[K.Tags]).Select(Js.Str).Contains(CV.TagSourceSpell);
            var carrier = new JObject();
            foreach (var key in Js.Items(c.CoreData.Section(CK.Plan)[CK.CarrierFields]).Select(Js.Str))
                if (def[key] != null) carrier[key] = def[key].DeepClone();
            var extra = Js.Obj(
                CK.AbilityKind, Js.Truthy(def[CK.AbilityKind]) ? def[CK.AbilityKind] : Js.S(spell ? CV.TypeSpell : CCV.AbilityManeuver),
                CC.AbilityFamily, def[CC.AbilityFamily], CK.AbilityRank, def[CK.AbilityRank],
                K.InstanceId, instance[K.InstanceId], K.CardId, instance[K.CardId], K.Upgraded, instance[K.Upgraded],
                K.Type, Js.S(Cards.Kind(c.Data, def)), K.Tags, cardTags, K.Attack, def[K.Attack], CC.AuthoredTags, authoredTags,
                CK.DamageSchool, instance[CK.DamageSchool] ?? def[CK.DamageSchool], CC.ExposureBuildupPerHit, instance[CC.ExposureBuildupPerHit] ?? def[CC.ExposureBuildupPerHit]);
            // The instance's own fields travel on the carrier (the lending piece, its rating, the hand and the grip it was played in).
            var ref0 = new JObject();
            void Copy(string key, JToken value) { if (value != null && value.Type != JTokenType.Null) ref0[key] = value.DeepClone(); }
            Copy(DK.SourceArmamentId, Js.Truthy(instance[DK.SourceArmamentId]) ? instance[DK.SourceArmamentId] : instance[DK.WeaponId]);
            Copy(CK.RatingId, instance[CK.RatingId]);
            Copy(DK.RatingValue, instance[DK.RatingValue]);
            Copy(DK.RatingCap, instance[DK.RatingCap]);
            Copy(DK.EquipmentRole, instance[DK.EquipmentRole]);
            if (instance.Num(DK.Rank) > 1) Copy(DK.Rank, instance[DK.Rank]);
            if (instance.Num(DK.SkillBonus) > 0) Copy(DK.SkillBonus, instance[DK.SkillBonus]);
            if (instance.Num(DK.PassiveBlock) > 0) Copy(DK.PassiveBlock, instance[DK.PassiveBlock]);
            Copy(K.SourceHand, instance[K.SourceHand]);
            ref0[DK.DerivedTags] = Equipment.GripTags(c.Data, c.Loadout, c.Player.Str(K.ClassId));
            if (Js.Truthy(instance[K.GrantedBy])) Copy(K.GrantedBy, instance[K.GrantedBy]);
            Copy(DK.CardRatingValues, def[DK.CardRatingValues]);
            foreach (var p in ref0.Properties()) carrier[p.Name] = p.Value.DeepClone();
            foreach (var p in extra.Properties()) carrier[p.Name] = p.Value.DeepClone();
            carrier[K.Tags] = (!Js.Nullish(def[CK.CardTags]) ? def[CK.CardTags] : !Js.Nullish(def[K.Tags]) ? def[K.Tags] : carrier[K.Tags])?.DeepClone();
            if (extra[K.Attack] == null && def[K.Attack] != null) carrier[K.Attack] = def[K.Attack].DeepClone();
            if (Js.IsFinite(def[CK.MagicShare])) carrier[CK.MagicShare] = def[CK.MagicShare].DeepClone();
            if (Js.IsStr(def[CK.PhysicalDamageType])) carrier[CK.PhysicalDamageType] = def[CK.PhysicalDamageType].DeepClone();
            var effects = Js.Items(def[K.Effects]).OfType<JObject>().ToList();
            var applied = effects.Where(e => e.Str(K.Op) == Op.ApplyStatus).ToList();
            carrier[CK.AppliedStatuses] = new JArray(applied.Select(e => e[K.Status]?.DeepClone()));
            carrier[CK.AppliedStatusEffects] = new JArray(applied.Select(e => e.DeepClone()));
            carrier[CK.AppliedBuildupStatuses] = new JArray(effects.Where(e => e.Str(K.Op) == CV.OpBuildup).Select(e => e[K.Status]?.DeepClone()));
            carrier[CK.CombatRiderTargets] = new JArray();
            if (carrier[CK.RatingId] == null && def[CK.RatingId] != null) carrier[CK.RatingId] = def[CK.RatingId].DeepClone();
            var profile = Js.Spread(CoreStances.ProfileFor(c, carrier), def.Obj(CK.CombatProfile));
            carrier[CK.CombatProfile] = profile;
            var player = c.Player;
            var evade = profile.Obj(CK.Evade);
            if (Js.Truthy(evade?[CK.WhileStatus])) profile[CK.EvadeEligible] = player.Obj(K.Statuses)?.Obj(evade.Str(CK.WhileStatus))?.Num(K.Stacks) > 0;
            ResolveWeaponType(c, carrier, profile, player);
            // expansionCarrierTraits: the traits the player itself carries join the card's own.
            var grounded = Js.Truthy(player.Obj(CK.CombatTraits)?[CK.Grounded]) || player.Obj(K.Statuses)?.Obj(CV.StatusGrounded)?.Num(K.Stacks) > 0;
            var traits = new List<string>(Js.Items(profile[CK.Traits]).Select(Js.Str));
            foreach (var p in (player.Obj(CK.CombatTraits) ?? new JObject()).Properties())
                if (p.Value.Type == JTokenType.Boolean && p.Value.Value<bool>() && !(grounded && p.Name == CV.TraitConductive)) traits.Add(p.Name);
            if (grounded) traits.Add(CV.TraitGrounded);
            profile[CK.Traits] = new JArray(traits.Distinct());
            return carrier;
        }

        /// <summary>
        /// tacticalCarrier's damage type: an untagged weapon card is dealt through the piece in the player's hand and takes its type (a bare-handed
        /// blow takes the unarmed profile's); a technique's physical part is the wielded weapon's blow.
        /// </summary>
        private static void ResolveWeaponType(CombatState c, JObject carrier, JObject profile, JObject source)
        {
            var weaponPart = carrier.Obj(K.Attack)?.Obj(DK.WeaponDamage) != null && carrier.Obj(K.Attack)?.Arr(CK.Components) != null;
            if ((!profile.Is(CK.DamageType) || weaponPart) && profile.Is(CK.Camp))
            {
                var attack = CoreAttack.Descriptor(carrier);
                var kind = Js.Truthy(attack[CK.Source]) ? attack.Str(CK.Source) : profile.Str(CK.Camp) == CV.CampSpell ? DV.SourceSpell : CV.SourceWeapon;
                var piece = CoreAttack.WieldedArmament(c, carrier, source, kind);
                var rules = c.CoreData.CombatRules;
                var unarmed = Js.Items(c.Data.Equipment[K.BasicCardProfiles]).OfType<JObject>().FirstOrDefault(row => row.Str(K.Id) == DV.UnarmedAttack);
                var magical = profile.Str(CK.Camp) == CV.CampSpell || (Js.Truthy(carrier[CK.DamageSchool]) && carrier.Str(CK.DamageSchool) != CV.CampPhysical);
                var bare = piece != null || !(kind == CV.SourceWeapon || kind == DV.SourceUnarmed) || magical ? null
                    : (unarmed != null ? CoreAttack.Descriptor(unarmed).Str(CK.DamageType) : null) ?? rules.Obj(DK.FallbackSource).Str(CK.DamageType);
                var family = piece != null ? rules.Obj(DK.EquipmentSources)?.Obj(DK.ItemFamilies)?.Str(piece.Str(K.Id) ?? string.Empty) ?? rules.Obj(DK.EquipmentSources)?.Obj(DK.ProfileFamilies)?.Str(piece.Str(K.AttackProfile) ?? string.Empty)
                    : rules.Obj(DK.FallbackSource).Str(DK.Family);
                var pieceType = (piece != null ? CoreAttack.Descriptor(piece).Str(CK.DamageType) : null) ?? bare;
                if (weaponPart)
                {
                    var components = CoreAttack.WeaponDamageComponents(attack, family, pieceType);
                    carrier[K.Attack] = Js.Spread(carrier.Obj(K.Attack), new JObject { [CK.Components] = components });
                    profile[CK.DamageComponents] = new JArray(components.Select(x => Js.Obj(K.Type, Js.Get(x, K.Type), CK.Weight, Js.Get(x, CK.Weight))));
                    profile[CK.DamageType] = CoreAttack.Dominant(components);
                }
                else profile[CK.DamageType] = Js.S(CoreAttack.WeaponDamageType(attack, family, pieceType));
            }
            {
                if (!carrier.Is(CK.PhysicalDamageType) && !profile.Is(CK.PhysicalDamageType) && CoreDamage.IsTechniqueProfile(c, profile)
                    && !CoreDamage.IsPhysicalDamageType(c, CoreDamage.CanonicalDamageType(c, profile.Str(CK.DamageType)))
                    && !Js.Items(profile[CK.DamageComponents]).Any(x => CoreDamage.IsPhysicalDamageType(c, CoreDamage.CanonicalDamageType(c, Js.Str(Js.Get(x, K.Type))))))
                {
                    var piece = CoreAttack.WieldedArmament(c, carrier, source, CV.SourceWeapon);
                    var type = CoreDamage.CanonicalDamageType(c, piece != null ? CoreAttack.Descriptor(piece).Str(CK.DamageType) : null);
                    if (CoreDamage.IsPhysicalDamageType(c, type)) carrier[CK.PhysicalDamageType] = type;
                }
            }
        }

        /// <summary>What <see cref="Enter"/> did: the price paid and the carrier the stance was entered with.</summary>
        public sealed class Entry
        {
            public JObject Paid;
            public JObject Carrier;
            public string CardId;
        }

        /// <summary>
        /// Plays the front half of a card: refuses (before any change) a card whose gates refuse, pays the price, takes it out of the hand, counts it
        /// and enters its stance with the entry grants and the matching-stance multiplier. The effects stay unplayed (WP-6, WP-7).
        /// </summary>
        public static Entry Enter(CombatState c, string instanceId)
        {
            var refusal = CoreCosts.PlayRefusal(c, instanceId);
            if (refusal != null) throw new InvalidOperationException(refusal);
            var player = c.Player;
            var index = c.Piles.Hand.FindIndex(card => card.Str(K.InstanceId) == instanceId);
            var instance = c.Piles.Hand[index];
            var def = CoreCosts.Resolve(c, instance);
            var paid = CoreCosts.CardPlayCosts(c, instanceId);
            var isX = def.Str(K.Cost) == CCV.CostX;
            // Stamina is the Actions pool; the receipts follow the payment as the web emits them.
            player.Put(K.Stamina, player.Num(K.Stamina) - paid.Num(K.Energy));
            if (paid.Num(K.Energy) > 0 || isX) c.Emit(E.EnergySpent, Js.Obj(K.Amount, paid[K.Energy]));
            player.Put(K.Mana, player.Num(K.Mana) - paid.Num(K.Mana));
            if (paid.Num(K.Mana) > 0) c.Emit(E.ManaSpent, Js.Obj(K.Amount, paid[K.Mana]));
            var counters = player.Obj(K.Counters);
            if (paid.Num(K.Stamina) > 0)
            {
                counters.Put(CC.StaminaSpentThisTurn, counters.Or0(CC.StaminaSpentThisTurn) + paid.Num(K.Stamina));
                c.Emit(E.StaminaSpent, Js.Obj(K.Amount, paid[K.Stamina]));
            }
            c.Piles.Hand.RemoveAt(index);
            counters.Put(CC.CardsPlayedThisTurn, counters.Num(CC.CardsPlayedThisTurn) + 1);
            counters.Put(CC.CardsPlayedThisCombat, counters.Num(CC.CardsPlayedThisCombat) + 1);
            var carrier = Carrier(c, instance, def);
            if (carrier.Str(K.Type) == CV.TypeAttack) counters.Put(CC.AttacksPlayedThisCombat, counters.Num(CC.AttacksPlayedThisCombat) + 1);
            CoreStances.SetProfileStance(c, player, carrier);
            return new Entry { Paid = paid, Carrier = carrier, CardId = instance.Str(K.CardId) };
        }

        private static void Bump(JObject map, string key) => map.Put(key, map.Or0(key) + 1);

        /// <summary>recordAbilityCard(entity, card, manaSpent): the play counts toward the turn's and the fight's ability riders.</summary>
        private static void RecordAbilityCard(JObject entity, JObject card, double manaSpent)
        {
            var state = CoreEvents.RidersOf(entity);
            Bump(state.Obj(DK.TypePlays) ?? (JObject)(state[DK.TypePlays] = new JObject()), card.Str(K.Type));
            if (Js.Truthy(card[K.InstanceId])) Bump(state.Obj(DK.InstanceUsesCombat) ?? (JObject)(state[DK.InstanceUsesCombat] = new JObject()), card.Str(K.InstanceId));
            Bump(state.Obj(DK.CardUsesCombat) ?? (JObject)(state[DK.CardUsesCombat] = new JObject()), card.Str(K.CardId));
            Bump(state, DK.CardsPlayed);
            Bump(state, DK.CardPlaysCombat);
            state.Put(DK.AttacksPlayed, state.Or0(DK.AttacksPlayed) + (card.Str(K.Type) == CV.TypeAttack ? 1 : 0));
            Bump(state.Obj(DK.SameCardPlays) ?? (JObject)(state[DK.SameCardPlays] = new JObject()), card.Str(K.CardId));
            state.Put(DK.ManaSpent, state.Num(DK.ManaSpent) + manaSpent);
            var tags = (Js.Truthy(card[DK.AuthoredTags]) ? card[DK.AuthoredTags] : card[K.Tags]) as JArray ?? new JArray();
            state.Put(DK.PreviousSpell, card.Str(DK.AbilityKind) == CV.TypeSpell || Js.Includes(card[DK.AuthoredTags] ?? card[K.Tags], CV.TagSourceSpell) ? 1 : 0);
            foreach (var tag in tags.Select(Js.Str).Distinct())
            {
                Bump(state.Obj(DK.TagPlays), tag);
                var names = state.Obj(DK.DistinctTagPlays).Obj(tag);
                if (names == null) state.Obj(DK.DistinctTagPlays)[tag] = names = new JObject();
                names[card.Str(K.CardId)] = true;
            }
        }

        /// <summary>
        /// doPlayCard(combat, intent): a card in the hand is played. The gates, then cardPreparing, the price, the stance, and the card's effects as one
        /// committed action: every damage contact is planned against its target, resolved hit by hit (the d20, the receipt, Block and Poise) and
        /// settled when the last effect has run; then the card is told played, resolved and put where it goes. The card's choice (a stance it offers),
        /// its variant, the charges it consumes and the riders they attach, its Health payments and the status plan travel with it. A Counter arms its
        /// reply instead of dealing it (<see cref="CoreCounterReturn.PrepareCounter"/>) and plays only its support. A card with an effect a later
        /// package owns (a move of the engagement, a legacy stance) is refused before anything changes.
        /// </summary>
        public static void Play(CombatState c, string instanceId, string targetId, JToken choice = null)
        {
            c.RecordSkillXp = true;
            var player = c.Player;
            if (CoreCardZones.HasChoice(c)) throw new InvalidOperationException(SM.ChooseBeforeAction);
            var refusal = CoreCosts.PlayRefusal(c, instanceId);
            if (refusal != null) throw new InvalidOperationException(refusal);
            var inst = c.Piles.Hand.First(card => card.Str(K.InstanceId) == instanceId);
            var printed = CoreCosts.Resolve(c, inst);
            var statusPlan = CoreCosts.ProposalCardPlan(c, player, printed);
            var chosen = AssertChoice(ChoiceOptions(printed), choice);
            if (printed.Num(DK.AshenBlightCost) > 0) throw new NotSupportedException(CM.AshenBlightNotPorted);
            var variantTarget = targetId != null ? Triggers.FindEntity(c, targetId) : null;
            var def = CoreVariants.Select(c, printed, CoreCosts.PlayCosts(c, printed), variantTarget);
            var effects = Js.Items(def[K.Effects]).OfType<JObject>().ToList();
            foreach (var effect in effects)
                if (CoreSets.DeferredOps.Contains(effect.Str(K.Op)) || effect.Is(CK.Script) || (effect.Str(K.Op) == Op.EnterStance && (Js.Truthy(effect[CK.Stance]) || effect.Str(DK.Choose) == SV.ClassStance)))
                    throw new NotSupportedException(string.Format(CultureInfo.InvariantCulture, CM.OpcodeNotPorted, effect.Str(K.Op)));
            var limit = CardCommandLimit(c);
            if (Js.Or0(c.Fields[CK.GameProfileCommands]) >= limit) throw new InvalidOperationException(DM.TurnWorkBudget);
            // The target: what the card names, else the first living enemy a single-target card needs. A Counter plans its target by the support that
            // resolves now, not by the reply it defers (immediateCardEffects).
            var immediate = CoreStances.ProfileFor(c, def).Str(CK.Maneuver) == CV.StanceCounter ? effects.Where(e => e.Str(K.Op) != Op.Damage && e.Str(K.Op) != Op.PoiseDamage).ToList() : effects;
            var hostile = immediate.Any(e => CoreSets.HostileSpecs.Contains(e.Str(K.Target)));
            var legal = hostile ? c.Enemies.Where(e => e.Is(K.Alive)).Select(e => e.Str(K.Id)).ToList() : new List<string> { player.Str(K.Id) };
            if (targetId != null && !legal.Contains(targetId)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.InvalidTarget, hostile ? DV.ModeEnemy : DV.ModeFriendly, targetId));
            JObject target = null;
            if (targetId != null)
            {
                target = Triggers.FindEntity(c, targetId);
                if (target == null || !target.Is(K.Alive)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DM.InvalidTargetId, targetId));
            }
            else if (immediate.Any(e => e.Str(K.Target) == DV.TargetEnemy))
            {
                target = c.Enemies.FirstOrDefault(e => e.Is(K.Alive));
                if (target == null) throw new InvalidOperationException(DM.NoLivingEnemy);
            }
            var kind = Cards.Kind(c.Data, def);
            var probe = Carrier(c, inst, def);
            var maneuver = probe.Obj(CK.CombatProfile)?.Str(CK.Maneuver);
            // What the play changes is measured from here: the books and the combatants before it, for the predicates that ask.
            var before = CoreRiders.Before(c, player);
            before[SV.CardTargetsAllEnemies] = CoreRiders.TargetsAllEnemies(def);
            PreflightHealth(c, def, player, target, before);
            c.Emit(DK.CardPreparing, Spread(Js.Obj(DK.CardInstanceId, instanceId, K.CardId, inst[K.CardId], DK.CardType, kind, DK.CardTags, probe[DK.AuthoredTags], DK.AbilityKind, probe[DK.AbilityKind], K.SourceId, player[K.Id], K.TargetId, target?[K.Id] ?? Js.Null()), before));
            CoreActions.Drain(c);
            // A charge another card holds for this one is taken now, riders and all.
            var enemyAvailable = c.Enemies.Any(e => e.Is(K.Alive));
            var charges = CoreRiders.Matching(player, probe, enemyAvailable);
            var chargedEffects = CoreRiders.ChargedEffects(c, effects, charges, enemyAvailable);
            if (!ReferenceEquals(chargedEffects, effects))
            {
                if (target != null && target.Str(K.Kind) != V.Enemy) throw new InvalidOperationException(SM.BuildupNeedsEnemy);
                target ??= c.Enemies.FirstOrDefault(e => e.Is(K.Alive));
            }
            var entry = Enter(c, instanceId);
            var cardRef = entry.Carrier;
            var paid = entry.Paid;
            c.Fields[CK.GameProfileCommands] = Js.N(Js.Or0(c.Fields[CK.GameProfileCommands]) + 1);
            var counters = player.Obj(K.Counters);
            RecordAbilityCard(player, cardRef, paid.Num(K.Mana));
            var chargeKeys = Js.Items(charges[SK.Keys]).Select(Js.Str).ToList();
            CoreRiders.Consume(player, chargeKeys);
            if (chargeKeys.Count > 0) c.Emit(SV.CardChargeConsumed, Js.Obj(SK.Keys, new JArray(chargeKeys), K.CardId, inst[K.CardId], K.SourceId, player[K.Id]));
            var snapshots = CoreRange.CardSourceSnapshots(c, def, player, cardRef);
            if (snapshots != null && (snapshots.Count > 0 || effects.Any(e => e.Str(K.Op) == Op.Damage)))
            {
                var captured = snapshots.Count > 0 ? snapshots[0] : null;
                if (captured != null) cardRef[DK.UsedArmamentId] = captured[DK.UsedArmamentId].DeepClone();
            }
            // A Counter has no damage effect to carry the source it is dealt with, so its carrier takes the first snapshot whole.
            if (maneuver == CV.StanceCounter && snapshots != null && snapshots.Count > 0)
                foreach (var p in snapshots[0].Properties()) cardRef[p.Name] = p.Value.DeepClone();
            var committed = (JObject)inst.DeepClone();
            if (cardRef.Has(DK.UsedArmamentId)) committed[DK.UsedArmamentId] = cardRef[DK.UsedArmamentId].DeepClone();
            cardRef[DK.CommittedInstance] = committed;
            c.PendingAbilityPlay = new JObject { [K.InstanceId] = instanceId, [K.CardId] = inst[K.CardId], [K.Kind] = kind, [DK.PrintedManaCost] = Js.Or0(def[K.ManaCost]), [K.TargetId] = target?[K.Id] ?? Js.Null() };
            c.PendingPlayInstance = inst;
            c.PendingPlayRef = cardRef;
            c.PendingPlayBefore = before;
            var meta = new ActionMeta
            {
                EnergySpent = paid.Num(K.Energy), ManaSpent = paid.Num(K.Mana), StaminaSpent = paid.Num(K.Stamina),
                OrdinalThisTurn = counters.Num(CC.CardsPlayedThisTurn), OrdinalThisCombat = counters.Num(CC.CardsPlayedThisCombat),
                AttackOrdinal = kind == CV.TypeAttack ? counters.Num(CC.AttacksPlayedThisCombat) : (double?)null,
                ProposalTargetDecisions = new JObject(), Before = before, Choice = chosen,
            };
            // prepareTacticalCard: the effects the play really runs (a charge's buildup, a Counterstrike's retort) and the statuses they apply.
            var tactical = CoreCardTraits.CounterstrikeEffects(c, player, target, cardRef, chargedEffects, true);
            var applied = tactical.Where(e => e.Str(K.Op) == Op.ApplyStatus).ToList();
            cardRef[DK.AppliedStatusEffects] = new JArray(applied.Select(e => e.DeepClone()));
            cardRef[DK.AppliedStatuses] = new JArray(applied.Select(e => e[K.Status]?.DeepClone()));
            cardRef[DK.AppliedBuildupStatuses] = new JArray(tactical.Where(e => e.Str(K.Op) == CV.OpBuildup).Select(e => e[K.Status]?.DeepClone()));
            // prepareTacticalCard: a Counter arms its reply from the card's damage, Poise and Ward and plays only its support effects now.
            var played = maneuver == CV.StanceCounter ? CoreCounterReturn.PrepareCounter(c, player, target, cardRef, tactical, meta) : tactical;
            // doPlayCard enters the stance twice: prepareTacticalCard accepts it, then the configured profile sets it again.
            CoreStances.SetProfileStance(c, player, cardRef);
            var actions = new List<CombatAction>();
            var damageOrdinal = 0;
            var chargeApplied = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < played.Count; index++)
            {
                var effect = played[index];
                JObject card = cardRef;
                if (effect.Str(K.Op) == Op.Damage)
                {
                    var snapshot = snapshots != null && damageOrdinal < snapshots.Count ? snapshots[damageOrdinal] : null;
                    damageOrdinal++;
                    if (snapshot != null) card = Js.Spread(cardRef, snapshot);
                }
                var actionMeta = meta.Clone();
                actionMeta.AbilityEffectIndex = index;
                var action = new CombatAction { Effect = effect, Source = player, Owner = player, Target = target, Card = card, Meta = actionMeta };
                CoreRiders.Attach(c, action, charges, chargeApplied);
                actions.Add(action);
            }
            cardRef[DK.RangeRoute] = DV.RouteAdvance;
            cardRef[DK.RangeBaseCosts] = new JObject { [K.Energy] = paid[K.Energy].DeepClone(), [K.Stamina] = paid[K.Stamina].DeepClone(), [K.Mana] = paid[K.Mana].DeepClone() };
            var group = CoreExpansion.Enqueue(c, actions, player, target, cardRef);
            var cost = paid.Num(K.Energy);
            var staminaCost = paid.Num(K.Stamina);
            if (group?.Carrier.Obj(DK.RangePlan) != null) { var extra = group.Carrier.Obj(DK.RangePlan).Num(DK.AdvanceSp); cost += extra; staminaCost += extra; }
            CoreProposal.ApplyPlan(c, statusPlan, player, target, cardRef);
            c.Emit(DK.CardPlayed, Spread(Js.Obj(DK.AbilityKind, cardRef[DK.AbilityKind], DK.PrintedManaCost, Js.Or0(def[K.ManaCost]), K.SourceId, player[K.Id], DK.CardInstance, cardRef[DK.CommittedInstance],
                DK.CardInstanceId, instanceId, K.CardId, inst[K.CardId], DK.CardType, kind, DK.CardTags, cardRef[K.Tags] ?? new JArray(), DK.DerivedTags, cardRef[DK.DerivedTags], K.TargetId, target?[K.Id] ?? Js.Null(),
                DK.OrdinalThisTurn, meta.OrdinalThisTurn, DK.OrdinalThisCombat, meta.OrdinalThisCombat, DK.EnergySpent, cost, DK.ManaSpent, paid[K.Mana], DK.StaminaSpent, staminaCost), before));
            CoreActions.Drain(c);
            FinishPlay(c);
        }

        /// <summary>The payload with the snapshot taken before the play laid under it (web <c>{ ...before, ...payload }</c>).</summary>
        private static JObject Spread(JObject payload, JObject before)
        {
            var result = new JObject();
            foreach (var p in before.Properties()) result[p.Name] = p.Value.DeepClone();
            foreach (var p in payload.Properties()) result[p.Name] = p.Value;
            return result;
        }

        // ------------------------------------------------------------------ choices

        /// <summary>cardChoice(registries, def, classId): the stances a card asks the player to pick from, or null when it plays without a choice.</summary>
        public static List<string> ChoiceOptions(JObject def)
        {
            var effect = Js.Items(def[K.Effects]).OfType<JObject>().FirstOrDefault(e => e.Str(K.Op) == Op.EnterStance && Js.Truthy(e[DK.Choose]));
            if (effect == null) return null;
            if (effect.Str(DK.Choose) == CK.Maneuver) return new List<string> { K.Attack, CV.StanceSmash, CV.StanceDefend, CV.StanceSweep, CK.Counter, CV.ManeuverCasting, CK.Evade };
            throw new NotSupportedException(SM.LegacyStanceNotPorted);
        }

        /// <summary>assertCardChoice(plan, choice): a card that offers no choice takes none; one that does takes one of its options.</summary>
        public static JToken AssertChoice(List<string> options, JToken choice)
        {
            if (options == null)
            {
                if (!Js.Nullish(choice)) throw new InvalidOperationException(SM.NoChoiceOffered);
                return null;
            }
            if (!Js.IsStr(choice) || !options.Contains(Js.Str(choice))) throw new InvalidOperationException(SM.ChooseStance + string.Join(V.ListSeparator, options));
            return choice;
        }

        /// <summary>preflightCardHp(ctx, def, source, target, meta): every authored Health payment is checked before the card, a resource, the queue or a roll changes.</summary>
        private static void PreflightHealth(CombatState c, JObject def, JObject source, JObject target, JObject before)
        {
            var remaining = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var effect in Js.Items(def[K.Effects]).OfType<JObject>())
            {
                if (effect.Str(K.Op) != Op.LoseHp) continue;
                var action = new CombatAction { Effect = effect, Source = source, Owner = source, Target = target, Card = def, Meta = new ActionMeta { Before = before } };
                if (Js.Truthy(effect[K.If]) && !Triggers.EvalPredicate(c, effect.Obj(K.If), new PredicateContext { Owner = source, Source = source, Target = target, Card = def, Meta = action.Meta })) continue;
                foreach (var entity in Actions.ResolveTargets(c, action, effect.Str(K.Target)))
                {
                    var amount = Math.Max(0, Actions.EvalNum(c, action, effect[K.Amount], 0, entity)) * Math.Max(0, Actions.EvalNum(c, action, effect[CK.Repeat], 1));
                    var hp = remaining.TryGetValue(entity.Str(K.Id), out var left) ? left : entity.Num(K.Hp);
                    if (effect.Is(DK.Nonlethal) && amount >= hp) throw new InvalidOperationException(SM.NonlethalOffering);
                    remaining[entity.Str(K.Id)] = Math.Max(0, hp - amount);
                }
            }
        }

        // ------------------------------------------------------------------ choices made after the play

        /// <summary>chooseCards: the cards a pending zone choice (look, pick, transform) asks for; the play then goes on.</summary>
        public static void ChooseCards(CombatState c, IReadOnlyList<string> ids)
        {
            c.RecordSkillXp = true;
            CoreCardZones.Choose(c, ids);
            CoreActions.Drain(c);
            FinishPlay(c);
        }

        /// <summary>chooseDiscard: the cards a chosen discard asks for; the play then goes on.</summary>
        public static void ChooseDiscard(CombatState c, IReadOnlyList<string> ids)
        {
            c.RecordSkillXp = true;
            CoreCardZones.ChooseDiscard(c, ids);
            CoreActions.Drain(c);
            FinishPlay(c);
        }

        /// <summary>recoverControl: the player spends their turn's recoveries on the control statuses they name.</summary>
        public static void RecoverControl(CombatState c, JArray selections)
        {
            c.RecordSkillXp = true;
            if (c.Phase != V.PhasePlayer) throw new InvalidOperationException(SM.RecoveryNeedsTurn);
            if (CoreCardZones.HasChoice(c)) throw new InvalidOperationException(SM.ChooseBeforeAction);
            var result = CoreStatusControl.ManualRecovery(c, c.Player, selections);
            if (!result.Is(Ashen.Generated.ShopKeys.Ok)) throw new InvalidOperationException(result.Str(K.Reason));
            CoreActions.Drain(c);
        }

        private static double CardCommandLimit(CombatState c) => Js.Coalesce(c.CoreData.Configured(CK.CardsModule)?[DK.FreeCommandLimit], DN.FreeCommandLimit);

        /// <summary>finishAbilityPlay(combat): the play is resolved (when nothing is left to choose), and the card goes to the discard pile, the exhaust pile or out of play.</summary>
        public static void FinishPlay(CombatState c)
        {
            if (c.PendingAbilityPlay == null || CoreCardZones.HasChoice(c)) return;
            var pending = c.PendingAbilityPlay;
            var inst = c.PendingPlayInstance;
            var cardRef = c.PendingPlayRef;
            var before = c.PendingPlayBefore;
            c.PendingAbilityPlay = null;
            c.PendingPlayInstance = null;
            c.PendingPlayRef = null;
            c.PendingPlayBefore = null;
            var resolved = Spread(Js.Obj(DK.CardInstanceId, inst[K.InstanceId], K.CardId, inst[K.CardId], DK.CardType, pending[K.Kind], DK.CardTags, cardRef[DK.AuthoredTags] ?? cardRef[K.Tags] ?? new JArray(), DK.AbilityKind, cardRef[DK.AbilityKind],
                DK.PrintedManaCost, pending[DK.PrintedManaCost], K.TargetId, pending[K.TargetId], K.SourceId, c.Player[K.Id], DK.MagicalResolved, cardRef.Str(DK.AbilityKind) == CV.TypeSpell), before);
            if (cardRef.Has(DK.UsedArmamentId)) resolved[DK.UsedArmamentId] = cardRef[DK.UsedArmamentId].DeepClone();
            if (Js.Truthy(cardRef[K.GrantedBy])) resolved[K.GrantedBy] = cardRef[K.GrantedBy].DeepClone();
            if (Js.Truthy(cardRef[K.SourceHand])) resolved[K.SourceHand] = cardRef[K.SourceHand].DeepClone();
            c.Emit(DK.CardResolved, resolved);
            if (c.Result != null) return;
            var def = CoreCosts.Resolve(c, inst);
            var destination = Js.Includes(def[K.Keywords], DV.KeywordExhaust) ? DV.DestinationExhaust : def.Str(K.Type) == CV.TypePower ? DV.DestinationRemoved : DV.DestinationDiscard;
            if (destination == DV.DestinationExhaust)
            {
                c.Piles.Exhaust.Add(inst);
                c.Emit(E.CardExhausted, Js.Obj(DK.CardInstanceId, inst[K.InstanceId], K.CardId, inst[K.CardId], K.Reason, DV.ReasonPlayed));
            }
            else if (destination == DV.DestinationDiscard) c.Piles.Discard.Add(inst);
            CoreActions.Drain(c);
        }
    }
}
