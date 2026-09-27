using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// Previews — the SAME math the engine executes (shipped previewCard/previewIntent, SPEC §3.13, §4.2): damage
    /// through computeAttackDamage, block through computeBlockGain, formulas through the execution evaluator, and
    /// no RNG consumed (a random target previews as the first living enemy).
    /// </summary>
    public static class CombatPreview
    {
        /// <summary>computeTokenBindings: the text-template token each tokenizable effect value binds to.</summary>
        public static Dictionary<string, string> TokenBindings(CombatData data, JToken effects)
        {
            var tokenizable = data.Engine[K.TokenizableOps];
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            void Push(string baseName, int index, string field)
            {
                counts[baseName] = counts.TryGetValue(baseName, out var n) ? n + 1 : 1;
                var token = counts[baseName] == 1 ? baseName : baseName + V.TokenRepeatSeparator + counts[baseName].ToString(CultureInfo.InvariantCulture);
                map[BindingKey(index, field)] = token;
            }
            var i = -1;
            foreach (var token in Js.Items(effects))
            {
                i++;
                if (!(token is JObject eff) || !Js.IsStr(eff[K.Op])) continue;
                var op = eff.Str(K.Op);
                if (!Js.Includes(tokenizable, op)) continue;
                var field = ValueField(op);
                var baseName = op == Op.ApplyStatus ? eff.Str(K.Status) : op;
                if (baseName == null) continue;
                Push(baseName, i, field);
                if (op == Op.Damage && !Js.Nullish(eff[K.Hits])) Push(K.Hits, i, K.Hits);
            }
            return map;
        }

        private static string ValueField(string op) => op == Op.ApplyStatus ? K.Stacks : op == Op.LoseMaxHpPct ? K.Pct : K.Amount;

        private static string BindingKey(int index, string field) => index.ToString(CultureInfo.InvariantCulture) + V.KeySeparator + field;

        private static double EvalPreview(CombatState c, CombatAction action, JToken value, JObject target)
        {
            if (Js.Nullish(value)) return 0;
            if (Js.IsNum(value)) return Math.Floor(Js.D(value));
            return Formulas.Evaluate(value, Actions.FormulaCtxFor(c, action, target));
        }

        private static JObject FirstResolvedTarget(CombatState c, CombatAction action, JObject eff)
        {
            try
            {
                var spec = eff.Str(K.Target) == V.TargetRandomEnemy ? V.TargetAllEnemies : eff.Str(K.Target);
                return Actions.ResolveTargets(c, action, spec).FirstOrDefault();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>previewCard(combat, cardInstanceId, targetId?) → the resolved numbers the card shows.</summary>
        public static JObject PreviewCard(CombatState c, string cardInstanceId, string targetId = null)
        {
            var inst = c.Piles.Hand.Concat(c.Piles.Draw).Concat(c.Piles.Discard).Concat(c.Piles.Exhaust)
                .FirstOrDefault(x => x.Str(K.InstanceId) == cardInstanceId)
                ?? throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnknownCardInstance, cardInstanceId));
            var def = Cards.Resolve(c.Data, inst);
            var p = c.Player;
            var isX = def.Str(K.Cost) == V.XCost;
            var shownCost = isX ? p.Num(K.Energy) : CombatEngine.EffectiveCost(c, def);
            var target = targetId != null ? Triggers.FindEntity(c, targetId) : null;
            var living = c.Enemies.Where(e => e.Is(K.Alive)).ToList();
            var needsTarget = Js.Items(def[K.Effects]).OfType<JObject>().Any(e => e.Str(K.Target) == V.RefEnemy);
            var ownTags = !Js.Nullish(def[K.CardTags]) ? def[K.CardTags] : (def[K.Tags] is JArray t && t.Count > 0 ? def[K.Tags] : null);
            var card = Js.Obj(
                K.SourceArmamentId, Js.Truthy(inst[K.SourceArmamentId]) ? inst[K.SourceArmamentId] : inst[K.WeaponId],
                K.RatingId, inst[K.RatingId], K.RatingValue, inst[K.RatingValue], K.RatingCap, inst[K.RatingCap],
                K.EquipmentRole, inst[K.EquipmentRole], K.InstanceId, inst[K.InstanceId], K.CardId, inst[K.CardId], K.Upgraded, inst[K.Upgraded],
                K.Type, Cards.Kind(c.Data, def), K.Tags, ownTags, K.Attack, def[K.Attack], K.SourceHand, inst[K.SourceHand],
                K.DerivedTags, Equipment.GripTags(c.Data, c.Loadout, p.Str(K.ClassId)), K.AuthoredTags, ownTags ?? new JArray(),
                K.DamageSchool, !Js.Nullish(inst[K.DamageSchool]) ? inst[K.DamageSchool] : def[K.DamageSchool],
                K.ExposureBuildupPerHit, !Js.Nullish(inst[K.ExposureBuildupPerHit]) ? inst[K.ExposureBuildupPerHit] : def[K.ExposureBuildupPerHit],
                K.CardRatingValues, Js.Truthy(def[K.CardRatingValues]) ? def[K.CardRatingValues] : null);
            var action = new CombatAction
            {
                Source = p,
                Owner = p,
                Target = target ?? (needsTarget ? living.FirstOrDefault() : null),
                Card = card,
                Meta = new ActionMeta { EnergySpent = isX ? p.Num(K.Energy) : shownCost },
            };

            var bindings = TokenBindings(c.Data, def[K.Effects]);
            var values = new JArray();
            var tokens = new JObject();
            var index = -1;
            foreach (var token in Js.Items(def[K.Effects]))
            {
                index++;
                if (!(token is JObject eff) || !Js.IsStr(eff[K.Op])) continue;
                var op = eff.Str(K.Op);
                var entry = Js.Obj(K.Op, op, K.Target, Js.Truthy(eff[K.Target]) ? eff[K.Target] : Js.Null());
                var primary = FirstResolvedTarget(c, action, eff);
                double? value = null;
                double? hits = null;
                switch (op)
                {
                    case Op.Damage:
                    {
                        var attackTags = Actions.AttackTagsFor(c, action, eff);
                        var enemyPrimary = primary != null && primary.Str(K.Kind) == V.Enemy ? primary : null;
                        value = Actions.ComputeAttackDamage(c, p, enemyPrimary, EvalPreview(c, action, eff[K.Amount], primary), attackTags, card);
                        hits = EvalPreview(c, action, Js.Nullish(eff[K.Hits]) ? Js.N(1) : eff[K.Hits], primary);
                        entry.Put(K.Hits, hits.Value);
                        var perTarget = new JObject();
                        foreach (var e in living) perTarget.Put(e.Str(K.Id), Actions.ComputeAttackDamage(c, p, e, EvalPreview(c, action, eff[K.Amount], e), attackTags, card));
                        entry[K.PerTarget] = perTarget;
                        if (attackTags.Count > 0 && enemyPrimary != null)
                        {
                            foreach (var st in enemyPrimary.Obj(K.Statuses).Properties())
                            {
                                if (!(st.Value is JObject sInst) || Statuses.LiveValue(sInst) <= 0) continue;
                                var sdef = c.Data.Statuses.Get(st.Name);
                                var tv = sdef.Obj(K.TaggedVulnerability);
                                if (tv != null && Js.Items(tv[K.Tags]).Any(x => Js.Includes(attackTags, Js.Str(x))))
                                {
                                    entry[K.BoostTint] = Js.Truthy(sdef[K.Tint]) ? sdef[K.Tint].DeepClone() : Js.Null();
                                    break;
                                }
                            }
                        }
                        break;
                    }
                    case Op.Block:
                        value = Actions.ComputeBlockGain(c, p, EvalPreview(c, action, eff[K.Amount], primary), card);
                        break;
                    case Op.ApplyStatus:
                        entry[K.Status] = eff[K.Status]?.DeepClone();
                        value = EvalPreview(c, action, Js.Nullish(eff[K.Stacks]) ? Js.N(1) : eff[K.Stacks], primary);
                        break;
                    case Op.Heal:
                    {
                        var amount = EvalPreview(c, action, eff[K.Amount], primary);
                        value = amount + Ratings.CardBonus(c, p, card, V.BonusHeal, amount);
                        break;
                    }
                    case Op.LoseHp:
                    case Op.Draw:
                    case Op.GainEnergy:
                    case Op.RestoreMana:
                    case Op.RestoreStamina:
                    case Op.PoiseDamage:
                    case Op.AddCinders:
                        value = EvalPreview(c, action, Js.Nullish(eff[K.Amount]) ? Js.N(1) : eff[K.Amount], primary);
                        break;
                    case Op.LoseMaxHpPct:
                        value = EvalPreview(c, action, Js.Nullish(eff[K.Pct]) ? Js.N(0) : eff[K.Pct], primary);
                        break;
                }
                entry[K.Value] = value.HasValue ? (JToken)Js.N(value.Value) : Js.Null();
                if (value.HasValue)
                {
                    if (bindings.TryGetValue(BindingKey(index, ValueField(op)), out var bound))
                    {
                        entry[K.Token] = bound;
                        tokens.Put(bound, value.Value);
                    }
                    if (hits.HasValue && bindings.TryGetValue(BindingKey(index, K.Hits), out var hitsToken)) tokens.Put(hitsToken, hits.Value);
                }
                values.Add(entry);
            }

            var pools = Framework.Costs(c.Data, def, 0, Equipment.PlayerWeightClass(c).WeightClass);
            return new JObject
            {
                [K.CardId] = inst[K.CardId]?.DeepClone(),
                [K.Upgraded] = inst[K.Upgraded]?.DeepClone(),
                [K.Name] = def[K.Name]?.DeepClone(),
                [K.Type] = def[K.Type]?.DeepClone(),
                [K.Cost] = Js.N(shownCost),
                [K.CostIsX] = isX,
                [K.ManaCost] = Js.N(def.Or0(K.ManaCost)),
                [K.StaminaCost] = Js.N(pools.Stamina),
                [K.NeedsTarget] = needsTarget,
                [K.Values] = values,
                [K.Tokens] = tokens,
            };
        }

        /// <summary>
        /// What a set swap would cost (the shipped swapCostFor receipt: cost, ruleId, base, baseCost, categoryTag, gearOn,
        /// gearDelta, gearIgnored, floored), or null where doSwapArmament refuses before pricing (us-5.11).
        /// </summary>
        public static JObject SwapPrice(CombatState c, string slotId, int setIndex) =>
            c.Result == null ? c.Data.EquipmentPort?.SwapPrice(c, slotId, setIndex) : null;

        /// <summary>What an equipment change would cost (priced on the loadout it leaves), or null where doChangeEquipment refuses before pricing.</summary>
        public static JObject ChangePrice(CombatState c, string slotId, int setIndex, string pieceId) =>
            c.Result == null ? c.Data.EquipmentPort?.ChangePrice(c, slotId, setIndex, pieceId) : null;

        /// <summary>previewIntent(combat, enemyInstanceId) → the live intent numbers (the §4.2 math against the player).</summary>
        public static JObject PreviewIntent(CombatState c, string enemyInstanceId)
        {
            var enemy = Triggers.FindEntity(c, enemyInstanceId);
            if (enemy == null || enemy.Str(K.Kind) != V.Enemy) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnknownEnemyInstance, enemyInstanceId));
            var intent = enemy.Obj(K.Intent) ?? Js.Obj(K.Kind, V.IntentUnknown, K.MoveId, Js.Null());
            var result = Js.Spread(intent);
            if (!Js.Nullish(intent[K.Damage]))
            {
                var moveId = intent.Str(K.MoveId);
                var attackType = c.RatingsRules?.Obj(K.EnemyAttackType)?.Str(enemy.Str(K.EnemyId) + V.KeySeparator + moveId);
                var move = c.Data.Enemies.Get(enemy.Str(K.EnemyId)).Obj(K.Moves)?.Obj(moveId ?? string.Empty);
                var effect = Js.Items(move?[K.Effects]).OfType<JObject>().FirstOrDefault(e => e.Str(K.Op) == Op.Damage);
                JObject carrier;
                if (!string.IsNullOrEmpty(attackType) && attackType != V.AttackTypeAuto) carrier = Js.Obj(K.DamageSchool, attackType);
                else
                {
                    var school = Js.Truthy(effect?[K.DamageSchool]) ? effect[K.DamageSchool] : move?[K.DamageSchool];
                    var tags = Js.Truthy(effect?[K.Tags]) ? effect[K.Tags] : Js.Truthy(move?[K.Tags]) ? move[K.Tags] : new JArray();
                    carrier = Js.Obj(K.DamageSchool, Js.Truthy(school) ? school : null, K.Tags, tags);
                }
                var damage = Actions.ComputeAttackDamage(c, enemy, c.Player, intent.Num(K.Damage), new JArray(), carrier);
                var hits = Js.Coalesce(intent[K.Hits], 1);
                result.Put(K.Damage, damage);
                result.Put(K.Hits, hits);
                result.Put(K.TotalDamage, damage * hits);
            }
            result.Put(K.Pending, Js.Truthy(enemy[K.PendingMove]));
            return result;
        }
    }
}
