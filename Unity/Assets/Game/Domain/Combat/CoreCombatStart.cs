using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Random;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using EK = Ashen.Generated.Core2EnemyKeys;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using Op = Ashen.Generated.CombatOps;
using PC = Ashen.Generated.Core2ProfileContentKeys;
using RngStream = Ashen.Generated.RngStream;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The start of a Core 2 fight (web engine/combat.js createCombat for a configured profile with FixCore, docs/design/11 WP-4): the
    /// player and the enemies built as combatants (ratings, persistent Poise, Barrier, full Stamina as the shared Actions pool, enemy SP
    /// and MP from their pinned rows), the property mounts, the formation, engagement and knowledge state, the deck shuffled with Innate
    /// cards on top, the combat-start triggers, every enemy's first plan and telegraphed intent, and the first player turn drawing 4 +
    /// Intelligence cards. HP is rolled on <c>enemyHP</c>, the shuffle on <c>shuffle</c>, the plans on <c>enemyAI</c> and the reads on
    /// the two intent streams, in the web's order.
    /// </summary>
    public static class CoreCombatStart
    {
        // ------------------------------------------------------------------ the combatants

        private static JObject Clone(JToken value) => value == null ? null : (JObject)value.DeepClone();

        /// <summary>createPlayerCombatEntity + stampPlayerPoiseMax: the player as a combatant (Actions and Stamina are one pool, kept as Stamina).</summary>
        private static JObject PlayerEntity(CombatState c, JObject p, double maxMana, double poiseMax)
        {
            if (!Js.IsInt(p[K.EnergyMax]) || p.Num(K.EnergyMax) < 0) throw new ArgumentException(M.EnergyMaxRequired);
            if (!Js.IsInt(p[K.DrawPerTurn]) || p.Num(K.DrawPerTurn) < 0) throw new ArgumentException(M.DrawPerTurnRequired);
            var maxStamina = !Js.Nullish(p[K.MaxStamina]) ? p[K.MaxStamina].DeepClone() : p[K.EnergyMax].DeepClone();
            var schoolAdd = p.Obj(K.DamageBySchoolAdd) ?? new JObject();
            var bySchool = new JObject();
            foreach (var school in Js.Items(c.Data.Engine[K.DamageSchools]).Select(Js.Str)) bySchool.Put(school, Js.Or0(schoolAdd[school]));
            var entity = new JObject
            {
                [K.Id] = V.Player,
                [K.Kind] = V.Player,
                [K.ClassId] = p[K.ClassId]?.DeepClone(),
            };
            if (Js.Truthy(p[CK.ClassUnequipped])) entity[CK.ClassUnequipped] = true;
            entity[K.Hp] = (Js.Nullish(p[K.Hp]) ? p[K.MaxHp] : p[K.Hp])?.DeepClone();
            entity[K.MaxHp] = p[K.MaxHp]?.DeepClone();
            entity[K.Mana] = !Js.Nullish(p[K.Mana]) ? p[K.Mana].DeepClone() : Js.N(maxMana);
            entity[K.MaxMana] = Js.N(maxMana);
            entity[K.Stamina] = !Js.Nullish(p[K.Stamina]) ? p[K.Stamina].DeepClone() : maxStamina.DeepClone();
            entity[K.MaxStamina] = maxStamina;
            entity[K.Block] = 0;
            entity[K.DrawPerTurn] = p[K.DrawPerTurn].DeepClone();
            entity[K.Statuses] = new JObject();
            entity[K.StanceId] = Js.Null();
            entity[K.RelicIds] = p[K.RelicIds]?.DeepClone() ?? new JArray();
            entity[K.ItemUpgradeLevels] = p[K.ItemUpgradeLevels]?.DeepClone() ?? new JObject();
            entity[K.DamageBySchoolAdd] = bySchool;
            entity[K.Flasks] = p[K.Flasks]?.DeepClone() ?? new JArray();
            entity[K.FlaskCharges] = Js.Truthy(p[K.FlaskCharges]) ? p[K.FlaskCharges].DeepClone() : Js.Null();
            if (p[CK.CritRules] is JArray crit && crit.Count > 0) entity[CK.CritRules] = crit.DeepClone();
            entity[K.Counters] = Js.Obj(K.CardsPlayedThisTurn, 0, K.CardsPlayedThisCombat, 0, K.AttacksPlayedThisCombat, 0, K.StaminaSpentThisTurn, 0);
            entity[K.Alive] = true;
            CombatStart.StampPoiseMax(c.Data, entity, poiseMax);
            return entity;
        }

        /// <summary>createEnemyCombatEntity: an enemy as a combatant, before its profile row lays its attributes, pools and ratings over it.</summary>
        private static JObject EnemyEntity(string instanceId, string enemyId, double level, double hp, JObject def, double damageMult)
        {
            var entity = new JObject
            {
                [K.Id] = instanceId,
                [K.Kind] = V.Enemy,
                [K.EnemyId] = enemyId,
                [K.Level] = Js.N(Js.IsInt(new JValue(level)) && level > 0 ? level : 1),
                [K.Hp] = Js.N(hp),
                [K.MaxHp] = Js.N(hp),
                [K.Block] = 0,
                [K.Statuses] = new JObject(),
                [K.PoiseMeter] = Js.Obj(K.Value, 0, K.Max, def[K.PoiseMax]),
                [K.MovesHistory] = new JArray(),
                [K.PerformedMoves] = new JArray(),
                [K.Intent] = Js.Null(),
                [K.PendingMove] = Js.Null(),
                [K.SkipNextTurn] = false,
                [K.UnlockedMoves] = new JArray(),
                [K.Alive] = true,
            };
            var exposure = def.Obj(K.ArcaneExposure);
            if (exposure != null)
            {
                if (exposure.Str(K.Mode) == V.Configured)
                {
                    var cfg = (JObject)exposure.DeepClone();
                    cfg.Put(K.Value, 0);
                    entity[K.ArcaneExposure] = cfg;
                }
                else entity[K.ArcaneExposure] = Js.Obj(K.Mode, V.Immune);
            }
            if (def.Obj(K.DamageResistanceBySchool) != null) entity[K.DamageResistanceBySchool] = Js.Spread(def.Obj(K.DamageResistanceBySchool));
            if (damageMult != 1) entity[CK.DamageMult] = Js.N(damageMult);
            return entity;
        }

        /// <summary>The player's Poise vessel (playerPoiseThresholdReceipt without combat ratings): the Poise row on the attributes, the worn armour and the relics.</summary>
        private static double PoiseVessel(CombatState c, JObject player)
        {
            var d = c.CoreData;
            var run = d.Run;
            var loadout = player.Obj(K.Loadout);
            if (loadout == null) throw new InvalidOperationException(M.PoiseNeedsLoadout);
            var levels = player.Obj(K.ItemUpgradeLevels) ?? new JObject();
            var snapshot = player.Obj(K.DerivedStatRuleSnapshot)?.Obj(K.Rules);
            var rule = snapshot?.Obj(K.Rules) != null ? Ashen.Domain.Run.CoreDerivedStats.ResolvedRow(run, snapshot, K.Poise, null) : null;
            var attributes = new JObject();
            if (rule != null)
                foreach (var weight in Ashen.Domain.Run.CoreDerivedStats.RuleWeights(run, rule))
                    attributes.Put(weight.Key, Js.IsFinite(player.Obj(K.Attributes)?[weight.Key]) ? player.Obj(K.Attributes).Num(weight.Key) : 0);
            var level = Js.IsInt(player[K.Level]) && player.Num(K.Level) >= 1 ? player.Num(K.Level) : 1;
            double attribute = 0;
            if (rule != null)
            {
                var row = Js.Spread(rule);
                row.Put(K.Base, Js.IsFinite(rule[K.Base]) ? rule.Num(K.Base) : 0);
                attribute = Ashen.Domain.Run.CoreDerivedStats.RowValue(run, row, attributes, null, level, K.Poise).Value;
            }
            double equipment = 0;
            foreach (var piece in Equipment.EquippedPieces(c.Data, loadout, player.Str(K.ClassId), levels).Where(piece => piece.Str(K.Kind) == V.Armor))
                equipment += Js.IsNum(piece[K.PoiseThreshold]) ? piece.Num(K.PoiseThreshold) : double.NaN;
            var relic = Cards.PassiveSum(c.Data, player[K.RelicIds] as JArray, K.PoiseThresholdAdd, levels, null);
            return attribute + equipment + relic;
        }

        // ------------------------------------------------------------------ create

        /// <summary>createCombat(args) for a configured Core 2 profile: the whole start sequence; <paramref name="args"/> has the web's createCombat shape.</summary>
        public static CombatState Create(CoreCombatData d, Rng rng, JObject args)
        {
            var player = args.Obj(K.Player) ?? throw new ArgumentException(K.Player);
                        var maxMana = Js.IsFinite(player[K.MaxMana]) ? player.Num(K.MaxMana) : 0;
            var reactions = d.Configured(CK.ReactionsModule);
            var reactionVersion = reactions != null && reactions.Num(CK.ChainLimit) > 0 ? d.ReactionVersion : (double?)null;
            var poiseMax = Js.IsInt(player[K.PoiseMax]) ? player.Num(K.PoiseMax) : player.Obj(K.Loadout) != null ? PoiseVessel(new CombatState { CoreData = d, Data = d.Combat }, player) : 0;
            var hpMult = Js.Coalesce(args[K.HpMult], 1);

            var c = new CombatState
            {
                Data = d.Combat,
                CoreData = d,
                Rng = rng,
                RatingsRules = null,
                HandRules = Clone(args[K.HandRules]),
                PendingDiscardDraw = 0,
                EquipmentProfileRuleSnapshot = (JObject)player[K.EquipmentProfileRuleSnapshot].DeepClone(),
                RemovedAttackSlotIds = player[K.RemovedAttackSlotIds]?.DeepClone() ?? new JArray(),
                EquipmentAttackSlotCount = Js.IsFinite(player[K.EquipmentAttackSlotCount]) ? player[K.EquipmentAttackSlotCount].DeepClone() : null,
                ItemUpgradeLevels = (JObject)(player.Obj(K.ItemUpgradeLevels)?.DeepClone() ?? new JObject()),
                ItemMounts = player.Obj(K.ItemMounts)?.DeepClone() ?? new JObject(),
                EquipmentPoolDeficits = player.Obj(K.EquipmentPoolDeficits) != null ? Js.Spread(player.Obj(K.EquipmentPoolDeficits)) : DefaultDeficits(player, maxMana),
                EquipmentChanged = false,
                Turn = 0,
                Phase = V.PhaseSetup,
                Result = null,
                DrawPerTurn = player[K.DrawPerTurn]?.DeepClone(),
                Loadout = player.Obj(K.Loadout),
                Attributes = player.Obj(K.Attributes) != null ? Js.Spread(player.Obj(K.Attributes)) : null,
                DerivedStatRuleSnapshot = Js.Truthy(player[K.DerivedStatRuleSnapshot]) ? player[K.DerivedStatRuleSnapshot] : null,
                Skills = (JObject)(player.Obj(K.Skills)?.DeepClone() ?? new JObject()),
                CoreTags = player[K.CoreTags] is JArray coreTags ? (JArray)coreTags.DeepClone() : new JArray(),
                SwapCostRule = Js.Truthy(args[K.SwapCostRule]) ? args[K.SwapCostRule].DeepClone() : Equipment.ResolveSwapCostRule(d.Combat, null),
                SwapsLeft = 0,
                RecordSkillXp = false,
            };
            var f = c.Fields;
            f[CK.CombatExpansionVersion] = Js.N(d.ExpandedVersion);
            f[CK.CombatKey] = args[CK.CombatKey]?.DeepClone();
            f[CK.CombatExpansionRules] = Clone(args[CK.CombatExpansionRules]);
            f[CK.AllocatedAttributes] = Clone(player[CK.AllocatedAttributes]) ?? Clone(player[K.Attributes]) ?? new JObject();
            f[CK.CombatMatchupRules] = d.Combat.Balance[CK.CombatMatchups]?.DeepClone();
            f[CK.CombatIntentRules] = d.Combat.Balance[CK.CombatIntent]?.DeepClone();
            if (Js.D(args[CK.BreakMeterVersion]) == d.BreakMeterVersion) f[CK.BreakMeterVersion] = args[CK.BreakMeterVersion].DeepClone();
            if (reactionVersion.HasValue)
            {
                f[CK.ReactionRulesVersion] = Js.N(reactionVersion.Value);
                f[CK.ReactionRules] = Clone(args[CK.ReactionRules]);
            }
            f[CK.EquipmentAttackSlotCount] = null;
            f.Remove(CK.EquipmentAttackSlotCount);
            if (Js.IsInt(player[K.Level])) f[CK.CharacterLevel] = Js.N(player.Num(K.Level));
            f[CK.SideboardedEquipmentCardIds] = player[CK.SideboardedEquipmentCardIds]?.DeepClone() ?? new JArray();
            c.Player = PlayerEntity(c, player, maxMana, poiseMax);
            f[CK.AttributeMode] = Js.Truthy(player[CK.AttributeMode]) ? player[CK.AttributeMode].DeepClone() : Js.Null();
            f[CK.SkillFeats] = player[CK.SkillFeats] is JArray feats ? feats.DeepClone() : new JArray();
            f[CK.Consumables] = player[CK.Consumables] is JObject consumables ? consumables.DeepClone() : Js.Null();
            f[CK.Companions] = player[CK.CompanionIds] is JArray companions ? companions.DeepClone() : new JArray();
            f[CK.SigilSlots] = player[CK.SigilSlots] is JObject slots ? slots.DeepClone() : new JObject();
            f[CK.AttunedSigils] = player[CK.AttunedSigils] is JArray attuned ? attuned.DeepClone() : new JArray();
            f[CK.OrderedDraw] = Js.Null();
            f[CK.GameProfileCommands] = 0;
            f[CK.FormationPolicy] = null;
            f.Remove(CK.FormationPolicy);

            // FixCore: the fight snapshots its rules before anything reads them.
            if (CoreFixCore.Enabled(c))
            {
                f[CK.FixCoreRules] = d.FixCore.DeepClone();
                if (reactionVersion == d.ReactionVersion) f.Obj(CK.ReactionRules)[CK.ContactPolicy] = CV.ContactPerHit;
            }
            c.Player[CK.AshenBlight] = Clone(player[CK.AshenBlight]) ?? new JObject { [CK.SchemaVersion] = 1, [K.Value] = 0, [CK.ThresholdOutcome] = Js.Null(), [CK.ConvertedAtCombat] = Js.Null(), [CK.Milestones] = new JArray(), [CK.Payments] = new JArray(), [CK.Entries] = new JArray() };
            c.Player[K.Attributes] = Clone(player[K.Attributes]) ?? new JObject();
            c.Player[CK.CombatExpansionVersion] = Js.N(d.ExpandedVersion);
            c.Player[CK.BaseResourceMaxima] = Clone(player[CK.BaseResourceMaxima]) ?? new JObject
            {
                [CK.MaxHp] = player[K.MaxHp]?.DeepClone(), [CK.MaxMana] = player[K.MaxMana]?.DeepClone(),
                [CK.MaxStamina] = Js.Truthy(player[K.MaxStamina]) ? player[K.MaxStamina].DeepClone() : player[K.EnergyMax]?.DeepClone(),
            };

            // Property carriers: the loadout, the relics, the class card (feats, companions and sigils are empty in a Core 2 run).
            Properties.SyncLoadout(c);
            Properties.SyncRelics(c);
            Properties.SyncClass(c);
            if (Js.Items(f[CK.SkillFeats]).Any() || Js.Items(f[CK.Companions]).Any() || Js.Items(f[CK.AttunedSigils]).Any()) throw new NotSupportedException(CM.CarriersNotPorted);

            // Enemies: HP rolled on enemyHP, then the formation, the engagement bands and the knowledge state.
            var enemyIds = Js.Items(args[K.EnemyIds]).Select(Js.Str).ToList();
            var levels = Js.Items(args[CK.EnemyLevels]).ToList();
            var damageMult = Js.Coalesce(args[CK.EnemyDamageMult], 1);
            for (var i = 0; i < enemyIds.Count; i++)
            {
                var def = d.Combat.Enemies.Get(enemyIds[i]);
                var range = def.Arr(K.Hp);
                double hp = rng.Int(RngStream.EnemyHP, (int)Js.D(range[0]), (int)Js.D(range[1]));
                if (hpMult != 1) hp = Math.Max(1, Math.Floor(hp * hpMult + Ashen.Generated.CombatMath.Half));
                var instanceId = V.EnemyInstancePrefix + (i + 1).ToString(CultureInfo.InvariantCulture);
                c.Enemies.Add(EnemyEntity(instanceId, enemyIds[i], i < levels.Count ? Js.D(levels[i]) : 1, hp, def, damageMult));
                c.Emit(E.EnemySpawned, Js.Obj(K.TargetId, instanceId, K.EnemyId, enemyIds[i]));
            }
            CoreRange.InitializeFormation(c);
            CoreRange.InitializeRange(c, args.Obj(CK.RangePolicy));
            var knowledge = args.Obj(EK.Knowledge);
            if (knowledge != null) CoreKnowledge.Initialize(c, knowledge.Obj(CK.Rules), knowledge.Obj(CK.Encounter), knowledge.Is(CK.Bankable), knowledge.Obj(EK.Profiles));
            else CoreKnowledge.Initialize(c, d.EnemyKnowledgeRules);

            // The configured ratings: the player's from the run's receipt, each enemy's from its pinned profile row.
            c.Player[CK.Ratings] = Js.Spread(new JObject { [CK.RatingAr] = 0, [CK.RatingDr] = 0, [CK.RatingPr] = 0, [CK.RatingWard] = 0, [CK.RatingPoise] = Js.N(poiseMax) }, player.Obj(CK.Ratings));
            c.Player[K.Attributes] = Js.Spread(player.Obj(K.Attributes));
            c.Player[CK.RatingSources] = player[CK.RatingSources] != null ? player[CK.RatingSources].DeepClone() : new JArray();
            if (CoreFixCore.Enabled(c))
            {
                c.Player[CK.BaseEvasionBonus] = Js.N(Js.Or0(player[CK.BaseEvasionBonus]));
                c.Player[CK.BasePrecisionBonus] = Js.N(Js.Or0(player[CK.BasePrecisionBonus]));
                foreach (var enemy in c.Enemies)
                {
                    var definition = d.Combat.Enemies.Get(enemy.Str(K.EnemyId));
                    enemy[K.Attributes] = definition.Obj(K.Attributes) != null ? Js.Spread(definition.Obj(K.Attributes)) : new JObject();
                    enemy[CK.BaseEvasionBonus] = Js.N(Js.Truthy(definition[CK.BaseEvasionBonus]) ? definition.Num(CK.BaseEvasionBonus) : Js.Or0(definition[CK.EvasionBonus]));
                    enemy[CK.BasePrecisionBonus] = Js.N(Js.Truthy(definition[CK.BasePrecisionBonus]) ? definition.Num(CK.BasePrecisionBonus) : Js.Or0(definition[CK.PrecisionBonus]));
                    if (!Js.Truthy(enemy[CK.Ratings]))
                        enemy[CK.Ratings] = new JObject
                        {
                            [CK.RatingAr] = Js.Or0(definition[CK.RatingAr]), [CK.RatingDr] = Js.Or0(definition[CK.RatingDr]), [CK.RatingPr] = 0,
                            [CK.RatingWard] = Js.Truthy(definition[CK.RatingWard]) ? definition.Num(CK.RatingWard) : Js.Or0(definition[K.PoiseMax]),
                            [CK.RatingPoise] = Js.Truthy(definition[CK.RatingPoise]) ? definition.Num(CK.RatingPoise) : Js.Or0(definition[K.PoiseMax]),
                        };
                    var assigned = CoreEnemies.Ratings(c, enemy);
                    if (assigned != null) foreach (var p in assigned.Properties()) enemy[p.Name] = p.Value.DeepClone();
                }
            }
            CoreEnemies.SyncConfiguredProperties(c);
            foreach (var entity in new[] { c.Player }.Concat(c.Enemies))
            {
                var open = !CoreFixCore.Enabled(c);
                CorePools.InitializeWard(entity, open ? 0 : CorePools.WardMaximum(c, entity));
                CorePools.InitializePoise(entity, open ? 0 : CorePools.PoiseMaximum(c, entity));
                entity.Put(CK.Barrier, 0);
            }

            // The deck, shuffled on the shuffle stream, Innate cards on top.
            var cardsModule = d.Configured(CK.CardsModule);
            var source = cardsModule == null ? new List<JObject>() : Js.Items(player[K.Deck]).OfType<JObject>().ToList();
            var deck = source.Select(card => DeckInstance(c, card)).ToList();
            var shuffled = rng.Shuffle(RngStream.Shuffle, deck);
            var innate = shuffled.Where(card => CoreCards.Keywords(c, card).Contains(CV.KeywordInnate)).ToList();
            c.Piles.Draw = innate.Concat(shuffled.Where(card => !innate.Contains(card))).ToList();

            c.HandMax = CoreHand.HandSize(c);
            if (d.Configured(CK.StancesModule) == null) foreach (var entity in new[] { c.Player }.Concat(c.Enemies)) entity[CK.CombatStance] = new JObject { [CK.Maneuver] = CV.StanceNeutral };

            c.Emit(E.CombatStart, new JObject());
            foreach (var s in Js.Items(args[K.PlayerStatuses]).OfType<JObject>())
                c.Enqueue(new CombatAction { Effect = StatusEffect(s), Source = c.Player, Owner = c.Player, Target = c.Player, Meta = ActionMeta.Empty() });
            foreach (var enemy in c.Enemies)
                foreach (var s in Js.Items(args[K.EnemyStatuses]).OfType<JObject>())
                    c.Enqueue(new CombatAction { Effect = StatusEffect(s), Source = enemy, Owner = enemy, Target = enemy, Meta = ActionMeta.Empty() });
            CoreActions.Drain(c);
            CoreIntents.Roll(c, true);
            if (c.Result == null) CoreTurns.StartPlayerTurn(c);
            return c;
        }

        private static JObject StatusEffect(JObject s) => Js.Obj(K.Op, Op.ApplyStatus, K.Target, V.TargetSelf, K.Status, s[K.Status], K.Stacks, s[K.Stacks]);

        private static JObject DefaultDeficits(JObject player, double maxMana)
        {
            var maxStamina = Js.Or0(player[K.MaxStamina]);
            var stamina = !Js.Nullish(player[K.Stamina]) ? player.Num(K.Stamina) : !Js.Nullish(player[K.MaxStamina]) ? player.Num(K.MaxStamina) : 0;
            var mana = !Js.Nullish(player[K.Mana]) ? player.Num(K.Mana) : maxMana;
            return Js.Obj(K.Hp, Math.Max(0, player.Num(K.MaxHp) - player.Num(K.Hp)), K.Mana, Math.Max(0, maxMana - mana), K.Stamina, Math.Max(0, maxStamina - stamina));
        }

        /// <summary>A deck card as it enters the fight: identity plus the numbers that ride on the instance (the break-meter stamp first).</summary>
        private static JObject DeckInstance(CombatState c, JObject card)
        {
            var inst = new JObject();
            var breakVersion = c.Fields[CK.BreakMeterVersion];
            if (breakVersion != null && (Js.D(breakVersion) == 1 || Js.D(breakVersion) == c.CoreData.BreakMeterVersion)) inst[CK.BreakMeterVersion] = breakVersion.DeepClone();
            inst[K.InstanceId] = card[K.InstanceId]?.DeepClone();
            inst[K.CardId] = card[K.CardId]?.DeepClone();
            inst[K.Upgraded] = card.Is(K.Upgraded);
            foreach (var key in new[] { CK.AbilityRank, CK.RankField, CK.SkillBonus, CK.PassiveBlock })
            {
                if (key == CK.AbilityRank && Js.IsInt(card[key])) inst[key] = card[key].DeepClone();
                else if (key == CK.RankField && Js.IsInt(card[key]) && card.Num(key) > 1) inst[CK.Rank] = card[key].DeepClone();
                else if ((key == CK.SkillBonus || key == CK.PassiveBlock) && Js.IsInt(card[key]) && card.Num(key) > 0) inst[key] = card[key].DeepClone();
            }
            if (card.Is(CK.LegacyAbility)) inst[CK.LegacyAbility] = true;
            if (Js.Truthy(card[CK.AbilityOfferId])) inst[CK.AbilityOfferId] = card[CK.AbilityOfferId].DeepClone();
            if (Js.Truthy(card[CK.RewardReceiptId])) inst[CK.RewardReceiptId] = card[CK.RewardReceiptId].DeepClone();
            if (card[K.AcquiredAt] != null) inst[K.AcquiredAt] = card[K.AcquiredAt].DeepClone();
            if (card[K.Mods] is JArray mods && mods.Count > 0) inst[K.Mods] = mods.DeepClone();
            if (Js.IsStr(card[K.DamageSchool])) inst[K.DamageSchool] = card[K.DamageSchool].DeepClone();
            if (Js.IsFinite(card[CK.MagicShare])) inst[CK.MagicShare] = card[CK.MagicShare].DeepClone();
            if (Js.IsStr(card[CK.PhysicalDamageType])) inst[CK.PhysicalDamageType] = card[CK.PhysicalDamageType].DeepClone();
            if (Js.IsInt(card[K.ExposureBuildupPerHit])) inst[K.ExposureBuildupPerHit] = card[K.ExposureBuildupPerHit].DeepClone();
            if (Js.Truthy(card[K.EquipmentRole]))
            {
                inst[K.EquipmentRole] = card[K.EquipmentRole].DeepClone();
                if (card[K.ProfileId] != null) inst[K.ProfileId] = card[K.ProfileId].DeepClone();
                if (card[K.ProfileReceipt] != null) inst[K.ProfileReceipt] = card[K.ProfileReceipt].DeepClone();
            }
            if (Js.Truthy(card[K.RatingId])) inst[K.RatingId] = card[K.RatingId].DeepClone();
            if (Js.IsFinite(card[K.RatingValue])) inst[K.RatingValue] = card[K.RatingValue].DeepClone();
            if (Js.IsFinite(card[K.RatingCap])) inst[K.RatingCap] = card[K.RatingCap].DeepClone();
            if (Js.Truthy(card[K.KitRole])) inst[K.KitRole] = card[K.KitRole].DeepClone();
            if (Js.Truthy(card[K.GrantedBy]))
            {
                inst[K.GrantedBy] = card[K.GrantedBy].DeepClone();
                if (card[K.GrantSource] != null) inst[K.GrantSource] = card[K.GrantSource].DeepClone();
            }
            foreach (var key in new[] { K.EquipmentAttackSlotId, K.EquipmentPlanFingerprint, K.SourceHand, K.WeaponId, K.SourceArmamentId })
                if (Js.Truthy(card[key])) inst[key] = card[key].DeepClone();
            if (Js.IsInt(card[K.SmithingLevel])) inst[K.SmithingLevel] = card[K.SmithingLevel].DeepClone();
            if (Js.Truthy(card[K.SourceEquipmentInstanceId])) inst[K.SourceEquipmentInstanceId] = card[K.SourceEquipmentInstanceId].DeepClone();
            return inst;
        }
    }
}
