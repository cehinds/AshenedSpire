using System;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The combat-start sequence (shipped createCombat, SPEC §4.1(1–2)): property mounts, enemy HP rolled on the
    /// enemyHP stream, ratings stamped, the deck shuffled on the shuffle stream with Innate cards on top, combatStart
    /// triggers, start statuses, the opening intents on enemyAI, then the first player turn.
    /// </summary>
    public static class CombatStart
    {
        /// <summary>
        /// createCombat(args): <paramref name="args"/> has the shipped shape — { player, enemyIds, hpMult?,
        /// enemyStatuses?, playerStatuses?, swapCostRule?, handRules?, ratingsRules? }; the player is the run's combat
        /// view (class, pools, deck, relics, flasks, loadout, attributes, rule snapshots).
        /// </summary>
        public static CombatState Create(CombatData data, Rng rng, JObject args)
        {
            var player = args.Obj(K.Player) ?? throw new ArgumentException(K.Player);
            var ratingsRules = args.Obj(K.RatingsRules);
            var handRules = args.Obj(K.HandRules);
            var hpMult = Js.Coalesce(args[K.HpMult], 1);
            var maxMana = Js.IsFinite(player[K.MaxMana]) ? player.Num(K.MaxMana) : 0;
            // A player handed without the run's profile snapshot (a headless fixture) gets the host rows resolved now.
            var profileSnapshot = Js.Truthy(player[K.EquipmentProfileRuleSnapshot])
                ? (JObject)player[K.EquipmentProfileRuleSnapshot].DeepClone()
                : (data.EquipmentPort ?? throw new NotSupportedException(M.ProfileSnapshotNeedsRunData)).CreateProfileSnapshot();
            var poiseMax = Js.IsInt(player[K.PoiseMax]) ? player.Num(K.PoiseMax)
                : player.Obj(K.Loadout) != null
                    ? Equipment.PoiseThreshold(data, player.Obj(K.Loadout), player[K.RelicIds] ?? new JArray(), player.Str(K.ClassId),
                        player.Obj(K.ItemUpgradeLevels), player.Obj(K.Attributes), Js.Truthy(player[K.DerivedStatRuleSnapshot]) ? player[K.DerivedStatRuleSnapshot] : null)
                    : 0;

            var c = new CombatState
            {
                Data = data,
                Rng = rng,
                RatingsRules = ratingsRules != null && ratingsRules.Is(K.Enabled) ? (JObject)ratingsRules.DeepClone() : null,
                HandRules = handRules != null ? (JObject)handRules.DeepClone() : null,
                PendingDiscardDraw = 0,
                EquipmentProfileRuleSnapshot = profileSnapshot,
                RemovedAttackSlotIds = player[K.RemovedAttackSlotIds]?.DeepClone() ?? new JArray(),
                EquipmentAttackSlotCount = Js.IsFinite(player[K.EquipmentAttackSlotCount]) ? player[K.EquipmentAttackSlotCount].DeepClone() : null,
                ItemUpgradeLevels = (JObject)(player.Obj(K.ItemUpgradeLevels)?.DeepClone() ?? new JObject()),
                ItemMounts = player.Obj(K.ItemMounts)?.DeepClone() ?? new JObject(),
                EquipmentPoolDeficits = PoolDeficits(player, maxMana),
                EquipmentChanged = false,
                Turn = 0,
                Phase = V.PhaseSetup,
                Result = null,
                HandMax = handRules != null
                    ? HandRules.ScaledCards(handRules.Obj(K.Capacity), player.Obj(K.Attributes))
                    : Js.Coalesce(data.Balance[K.HandMax], data.Rule(K.Defaults, K.HandMax)),
                DrawPerTurn = player[K.DrawPerTurn]?.DeepClone(),
                Loadout = player.Obj(K.Loadout),
                Attributes = player.Obj(K.Attributes) != null ? Js.Spread(player.Obj(K.Attributes)) : null,
                DerivedStatRuleSnapshot = Js.Truthy(player[K.DerivedStatRuleSnapshot]) ? player[K.DerivedStatRuleSnapshot] : null,
                Skills = (JObject)(player.Obj(K.Skills)?.DeepClone() ?? new JObject()),
                CoreTags = player[K.CoreTags] is JArray core ? (JArray)core.DeepClone() : new JArray(),
                // A fight's price rule is resolved once (swapCostRule || resolveSwapCostRule(registries, null)).
                SwapCostRule = Js.Truthy(args[K.SwapCostRule]) ? args[K.SwapCostRule].DeepClone() : Equipment.ResolveSwapCostRule(data, null),
                SwapsLeft = 0,
            };
            c.Player = PlayerEntity(data, player, maxMana, poiseMax);

            Properties.SyncLoadout(c);
            Properties.SyncRelics(c);
            Properties.SyncClass(c);

            var enemyIds = Js.Items(args[K.EnemyIds]).Select(Js.Str).ToList();
            for (var i = 0; i < enemyIds.Count; i++)
            {
                var def = data.Enemies.Get(enemyIds[i]);
                var range = def.Arr(K.Hp);
                double hp = rng.Int(RngStream.EnemyHP, (int)Js.D(range[0]), (int)Js.D(range[1]));
                if (hpMult != 1) hp = Math.Max(1, Math.Floor(hp * hpMult + CombatMath.Half));
                var instanceId = V.EnemyInstancePrefix + (i + 1).ToString(CultureInfo.InvariantCulture);
                c.Enemies.Add(EnemyEntity(instanceId, enemyIds[i], hp, def));
                c.Emit(E.EnemySpawned, Js.Obj(K.TargetId, instanceId, K.EnemyId, enemyIds[i]));
            }

            if (c.RatingsRules != null)
            {
                Ratings.Refresh(c);
                foreach (var enemy in c.Enemies)
                {
                    var fallback = Js.Or0(enemy.Obj(K.PoiseMeter)?[K.Max]);
                    var values = c.RatingsRules.Obj(K.EnemyRatings)?.Obj(enemy.Str(K.EnemyId))
                                 ?? Js.Obj(K.Poise, fallback == 0 ? 1 : fallback, K.Ward, fallback == 0 ? 1 : fallback);
                    var ratings = Js.Obj(K.Ar, 0, K.Dr, 0, K.Pr, 0);
                    foreach (var p in values.Properties()) ratings[p.Name] = p.Value.DeepClone();
                    enemy[K.Ratings] = ratings;
                    foreach (var id in new[] { K.Poise, K.Ward })
                        enemy[id + V.MeterSuffix] = Js.Obj(K.Value, 0, K.Max, Math.Max(1, values.Num(id)), K.Growths, 0);
                }
            }

            var deck = Js.Items(player[K.Deck]).OfType<JObject>().Select(DeckInstance).ToList();
            var shuffled = rng.Shuffle(RngStream.Shuffle, deck);
            var innate = shuffled.Where(card => Framework.IsInnate(data, Cards.Resolve(data, card))).ToList();
            c.Piles.Draw = innate.Concat(shuffled.Where(card => !innate.Contains(card))).ToList();

            c.Emit(E.CombatStart, new JObject());
            foreach (var s in Js.Items(args[K.PlayerStatuses]).OfType<JObject>())
                c.Enqueue(new CombatAction { Effect = StatusEffect(s), Source = c.Player, Owner = c.Player, Target = c.Player, Meta = ActionMeta.Empty() });
            foreach (var enemy in c.Enemies)
                foreach (var s in Js.Items(args[K.EnemyStatuses]).OfType<JObject>())
                    c.Enqueue(new CombatAction { Effect = StatusEffect(s), Source = enemy, Owner = enemy, Target = enemy, Meta = ActionMeta.Empty() });
            CombatEngine.DrainQueue(c);
            CombatEngine.RollIntents(c, true);
            if (c.Result == null) CombatEngine.StartPlayerTurn(c);
            return c;
        }

        private static JObject StatusEffect(JObject s) => Js.Obj(K.Op, Op.ApplyStatus, K.Target, V.TargetSelf, K.Status, s[K.Status], K.Stacks, s[K.Stacks]);

        private static JObject PoolDeficits(JObject player, double maxMana)
        {
            if (player.Obj(K.EquipmentPoolDeficits) != null) return Js.Spread(player.Obj(K.EquipmentPoolDeficits));
            var maxStamina = Js.Or0(player[K.MaxStamina]);
            var stamina = !Js.Nullish(player[K.Stamina]) ? player.Num(K.Stamina) : !Js.Nullish(player[K.MaxStamina]) ? player.Num(K.MaxStamina) : 0;
            var mana = !Js.Nullish(player[K.Mana]) ? player.Num(K.Mana) : maxMana;
            return Js.Obj(K.Hp, Math.Max(0, player.Num(K.MaxHp) - player.Num(K.Hp)), K.Mana, Math.Max(0, maxMana - mana), K.Stamina, Math.Max(0, maxStamina - stamina));
        }

        /// <summary>createPlayerCombatEntity + stampPlayerPoiseMax.</summary>
        private static JObject PlayerEntity(CombatData data, JObject p, double maxMana, double poiseMax)
        {
            if (!Js.IsInt(p[K.EnergyMax]) || p.Num(K.EnergyMax) < 0) throw new ArgumentException(M.EnergyMaxRequired);
            if (!Js.IsInt(p[K.DrawPerTurn]) || p.Num(K.DrawPerTurn) < 0) throw new ArgumentException(M.DrawPerTurnRequired);
            var maxStamina = Js.Nullish(p[K.MaxStamina]) ? Js.N(0) : p[K.MaxStamina].DeepClone();
            var schoolAdd = p.Obj(K.DamageBySchoolAdd) ?? new JObject();
            var bySchool = new JObject();
            foreach (var school in Js.Items(data.Engine[K.DamageSchools]).Select(Js.Str)) bySchool.Put(school, Js.Or0(schoolAdd[school]));
            var entity = new JObject
            {
                [K.Id] = V.Player,
                [K.Kind] = V.Player,
                [K.ClassId] = p[K.ClassId]?.DeepClone(),
                [K.Hp] = (Js.Nullish(p[K.Hp]) ? p[K.MaxHp] : p[K.Hp])?.DeepClone(),
                [K.MaxHp] = p[K.MaxHp]?.DeepClone(),
                [K.Mana] = Js.Nullish(p[K.Mana]) ? Js.N(maxMana) : p[K.Mana].DeepClone(),
                [K.MaxMana] = Js.N(maxMana),
                [K.Stamina] = Js.Nullish(p[K.Stamina]) ? maxStamina.DeepClone() : p[K.Stamina].DeepClone(),
                [K.MaxStamina] = maxStamina,
                [K.Block] = 0,
                [K.Energy] = 0,
                [K.EnergyMax] = p[K.EnergyMax].DeepClone(),
                [K.DrawPerTurn] = p[K.DrawPerTurn].DeepClone(),
                [K.Statuses] = new JObject(),
                [K.StanceId] = Js.Null(),
                [K.RelicIds] = p[K.RelicIds]?.DeepClone() ?? new JArray(),
                [K.ItemUpgradeLevels] = p[K.ItemUpgradeLevels]?.DeepClone() ?? new JObject(),
                [K.DamageBySchoolAdd] = bySchool,
                [K.Flasks] = p[K.Flasks]?.DeepClone() ?? new JArray(),
                [K.FlaskCharges] = Js.Truthy(p[K.FlaskCharges]) ? p[K.FlaskCharges].DeepClone() : Js.Null(),
                [K.Counters] = Js.Obj(K.CardsPlayedThisTurn, 0, K.CardsPlayedThisCombat, 0, K.AttacksPlayedThisCombat, 0, K.StaminaSpentThisTurn, 0),
                [K.Alive] = true,
            };
            StampPoiseMax(data, entity, poiseMax);
            return entity;
        }

        /// <summary>stampPlayerPoiseMax: (re)size the player's Poise vessel, keeping counted growth; a non-positive max removes it.</summary>
        public static void StampPoiseMax(CombatData data, JObject entity, double max)
        {
            if (Math.Floor(max) == max && max > 0)
            {
                var prior = entity.Obj(K.PoiseMeter);
                var growths = Js.Or0(prior?[K.Growths]);
                var step = Js.Truthy(prior?[K.GrowthMult]) ? prior.Num(K.GrowthMult) : data.Rule(K.Defaults, K.PoiseGrowthMult);
                var legacy = Js.Truthy(prior?[K.Growth]) ? prior.Num(K.Growth) : 1;
                var grown = Math.Ceiling(max * legacy);
                for (var i = 0; i < growths; i++) grown = Math.Ceiling(grown * step);
                var value = prior != null ? Math.Max(0, Math.Min(prior.Num(K.Value), grown)) : 0;
                var meter = Js.Obj(K.Value, value, K.Max, grown);
                if (growths != 0)
                {
                    meter.Put(K.Growths, growths);
                    meter.Put(K.GrowthMult, step);
                }
                if (legacy != 1) meter.Put(K.Growth, legacy);
                entity[K.PoiseMeter] = meter;
            }
            else entity.Remove(K.PoiseMeter);
        }

        /// <summary>createEnemyCombatEntity.</summary>
        private static JObject EnemyEntity(string instanceId, string enemyId, double hp, JObject def)
        {
            var entity = new JObject
            {
                [K.Id] = instanceId,
                [K.Kind] = V.Enemy,
                [K.EnemyId] = enemyId,
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
            return entity;
        }

        /// <summary>A deck card as it enters the fight: identity plus the equipment numbers that ride on the instance.</summary>
        private static JObject DeckInstance(JObject c)
        {
            var inst = Js.Obj(K.InstanceId, c[K.InstanceId], K.CardId, c[K.CardId], K.Upgraded, c.Is(K.Upgraded));
            if (c[K.AcquiredAt] != null) inst[K.AcquiredAt] = c[K.AcquiredAt].DeepClone();
            if (c[K.Mods] is JArray mods && mods.Count > 0) inst[K.Mods] = mods.DeepClone();
            if (Js.IsStr(c[K.DamageSchool])) inst[K.DamageSchool] = c[K.DamageSchool].DeepClone();
            if (Js.IsInt(c[K.ExposureBuildupPerHit])) inst[K.ExposureBuildupPerHit] = c[K.ExposureBuildupPerHit].DeepClone();
            if (Js.Truthy(c[K.EquipmentRole]))
            {
                inst[K.EquipmentRole] = c[K.EquipmentRole].DeepClone();
                if (c[K.ProfileId] != null) inst[K.ProfileId] = c[K.ProfileId].DeepClone();
                if (c[K.ProfileReceipt] != null) inst[K.ProfileReceipt] = c[K.ProfileReceipt].DeepClone();
            }
            if (Js.Truthy(c[K.RatingId])) inst[K.RatingId] = c[K.RatingId].DeepClone();
            if (Js.IsFinite(c[K.RatingValue])) inst[K.RatingValue] = c[K.RatingValue].DeepClone();
            if (Js.IsFinite(c[K.RatingCap])) inst[K.RatingCap] = c[K.RatingCap].DeepClone();
            if (Js.Truthy(c[K.KitRole])) inst[K.KitRole] = c[K.KitRole].DeepClone();
            if (Js.Truthy(c[K.GrantedBy]))
            {
                inst[K.GrantedBy] = c[K.GrantedBy].DeepClone();
                if (c[K.GrantSource] != null) inst[K.GrantSource] = c[K.GrantSource].DeepClone();
            }
            foreach (var key in new[] { K.EquipmentAttackSlotId, K.EquipmentPlanFingerprint, K.SourceHand, K.WeaponId, K.SourceArmamentId })
                if (Js.Truthy(c[key])) inst[key] = c[key].DeepClone();
            if (Js.IsInt(c[K.SmithingLevel])) inst[K.SmithingLevel] = c[K.SmithingLevel].DeepClone();
            if (Js.Truthy(c[K.SourceEquipmentInstanceId])) inst[K.SourceEquipmentInstanceId] = c[K.SourceEquipmentInstanceId].DeepClone();
            return inst;
        }
    }
}
