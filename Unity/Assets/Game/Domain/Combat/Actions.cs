using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// The effect-DSL opcode implementations and shared damage/block math (shipped engine/actions.js, SPEC §3.4,
    /// §4.2). Nothing mutates HP, block, piles or statuses except an executed action.
    /// </summary>
    public static class Actions
    {
        // ------------------------------------------------------------------ damage math

        /// <summary>computeAttackDamage: base (+ rating) + school add + attackDamageAdd, × dealt/taken/resistance mults, floored.</summary>
        public static double ComputeAttackDamage(CombatState c, JObject source, JObject target, double baseAmount, JArray attackTags, JObject carrier)
        {
            var dmg = baseAmount + Ratings.CardBonus(c, source, carrier, V.BonusDamage, baseAmount);
            var school = carrier?.Str(K.DamageSchool);
            if (source != null && source.Str(K.Kind) == V.Player && Js.Truthy(carrier?[K.DamageSchool]))
            {
                var add = source.Obj(K.DamageBySchoolAdd)?[school];
                dmg += Js.IsFinite(add) ? Js.D(add) : 0;
            }
            dmg += Statuses.GetAdd(c, source, K.AttackDamageAdd);
            dmg *= Statuses.GetMult(c, source, K.DamageDealtMult);
            if (target != null) dmg *= Statuses.GetMult(c, target, K.DamageTakenMult);
            if (target != null) dmg *= Ratings.DamageMultiplier(c, target, Ratings.IsMagicalAttack(c, carrier));
            if (target != null && attackTags != null && attackTags.Count > 0)
            {
                double addPool = 0;
                foreach (var p in target.Obj(K.Statuses).Properties().ToList())
                {
                    if (!(p.Value is JObject inst) || Statuses.LiveValue(inst) <= 0) continue;
                    var tv = c.Data.Statuses.Get(p.Name).Obj(K.TaggedVulnerability);
                    if (tv == null || !Js.Items(tv[K.Tags]).Any(t => Js.Includes(attackTags, Js.Str(t)))) continue;
                    if (tv.Str(K.Stacking) == V.Multiplicative) dmg *= tv.Num(K.Mult);
                    else addPool += tv.Num(K.Mult) - 1;
                }
                if (addPool > 0) dmg *= 1 + addPool;
            }
            if (target != null && Js.Truthy(carrier?[K.DamageSchool]))
            {
                var resistance = target.Obj(K.DamageResistanceBySchool)?[school];
                if (Js.IsFinite(resistance)) dmg *= Math.Max(0, 1 - Js.D(resistance) / CombatMath.Percent);
                foreach (var p in target.Obj(K.Statuses).Properties().ToList())
                {
                    if (!(p.Value is JObject inst) || Statuses.LiveValue(inst) <= 0) continue;
                    var vuln = c.Data.Statuses.Get(p.Name).Obj(K.SchoolDamageVulnerability);
                    if (vuln != null && vuln.Str(K.School) == school) dmg *= 1 + inst.Or0(K.Stacks) / CombatMath.Percent;
                }
            }
            dmg = Math.Floor(dmg);
            return dmg < 0 ? 0 : dmg;
        }

        /// <summary>attackTagsFor: the card snapshot's tags, else the card row's stamped tags, else the effect's.</summary>
        public static JArray AttackTagsFor(CombatState c, CombatAction action, JObject effect)
        {
            if (action.Card?[K.Tags] is JArray cardTags) return cardTags;
            var cardId = action.Card?.Str(K.CardId);
            if (!string.IsNullOrEmpty(cardId) && c.Data.Cards.Has(cardId) && c.Data.Cards.Get(cardId)[K.Tags] is JArray stamped && stamped.Count > 0) return stamped;
            return effect[K.Tags] as JArray ?? new JArray();
        }

        public static double ApplyAttackDamage(CombatState c, JObject source, JObject target, double baseAmount, JArray attackTags, JObject carrier)
        {
            if (target == null || !target.Is(K.Alive)) return 0;
            var dmg = ComputeAttackDamage(c, source, target, baseAmount, attackTags, carrier);
            var blocked = Math.Min(target.Num(K.Block), dmg);
            target.Put(K.Block, target.Num(K.Block) - blocked);
            var hpLoss = dmg - blocked;
            if (hpLoss > 0) target.Put(K.Hp, target.Num(K.Hp) - hpLoss);
            var payload = Js.Obj(K.SourceId, source != null ? source[K.Id] : Js.Null(), K.TargetId, target[K.Id], K.Amount, dmg, K.Blocked, blocked);
            if (Js.Truthy(carrier?[K.InstanceId]))
            {
                payload[K.CardInstanceId] = carrier[K.InstanceId].DeepClone();
                if (carrier[K.SourceHand] != null) payload[K.SourceHand] = carrier[K.SourceHand].DeepClone();
                if (carrier[K.GrantedBy] != null) payload[K.GrantedBy] = carrier[K.GrantedBy].DeepClone();
            }
            payload.Put(K.BlockRemaining, target.Num(K.Block));
            payload.Put(K.IsAttack, true);
            c.Emit(E.DamageDealt, payload);
            if (hpLoss > 0)
            {
                if (c.RatingsRules != null) Ratings.ApplyImpact(c, source, target, carrier);
                c.Emit(E.HpLost, Js.Obj(K.TargetId, target[K.Id], K.Amount, hpLoss, K.Cause, V.CauseAttack));
                ApplyArcaneExposure(c, source, target, carrier);
            }
            AfterHpChange(c, target);
            return dmg;
        }

        private static void ApplyArcaneExposure(CombatState c, JObject source, JObject target, JObject carrier)
        {
            if (target == null || target.Str(K.Kind) != V.Enemy || !Js.Truthy(target[K.ArcaneExposure]) || carrier == null) return;
            var schoolMult = c.Data.Balance.Obj(K.ArcaneExposure)?.Obj(K.SchoolBuildupMultipliers) ?? new JObject();
            var school = carrier.Str(K.DamageSchool);
            var perHit = carrier[K.ExposureBuildupPerHit];
            var mapped = school != null && Js.IsFinite(schoolMult[school]) ? schoolMult.Num(school) : 0;
            if (!Js.IsInt(perHit) || Js.D(perHit) <= 0 || mapped <= 0) return;
            var sourceMult = Cards.PassiveMult(c.Data, source?.Arr(K.RelicIds), K.ExposureBuildupMult, Properties.MountsOf(c, source));
            var per = Js.D(perHit);
            AddArcaneExposure(c, source, target, school, per, cfg => Math.Floor(per * mapped * cfg.Num(K.BuildupMultiplier) * sourceMult));
        }

        public static double AddArcaneExposure(CombatState c, JObject source, JObject target, string school, double? attempted, Func<JObject, double> amountFor)
        {
            if (target == null || target.Str(K.Kind) != V.Enemy || !Js.Truthy(target[K.ArcaneExposure])) return 0;
            var cfg = target.Obj(K.ArcaneExposure);
            var sourceId = source != null ? source[K.Id] : Js.Null();
            if (cfg.Str(K.Mode) == V.Immune)
            {
                c.Emit(E.ArcaneExposureRefused, Js.Obj(K.TargetId, target[K.Id], K.SourceId, sourceId, K.Reason, V.Immune, K.School, school, K.Attempted, attempted.HasValue ? (object)attempted.Value : Js.Null()));
                return 0;
            }
            if (cfg.Str(K.Mode) != V.Configured) return 0;
            var onBreak = cfg.Obj(K.OnBreak);
            if (Statuses.Has(target, onBreak.Str(K.Status)))
            {
                c.Emit(E.ArcaneExposureRefused, Js.Obj(K.TargetId, target[K.Id], K.SourceId, sourceId, K.Reason, V.Locked, K.School, school, K.Attempted, attempted.HasValue ? (object)attempted.Value : Js.Null()));
                return 0;
            }
            var amount = Math.Floor(amountFor(cfg));
            if (amount <= 0) return 0;
            cfg.Put(K.Value, cfg.Num(K.Value) + amount);
            c.Emit(E.ArcaneExposureChanged, Js.Obj(K.TargetId, target[K.Id], K.SourceId, sourceId, K.School, school, K.Amount, amount, K.Value, cfg[K.Value], K.Threshold, cfg[K.Threshold]));
            if (cfg.Num(K.Value) < cfg.Num(K.Threshold)) return amount;
            cfg.Put(K.Value, 0);
            c.Emit(E.ArcaneBreak, Js.Obj(K.TargetId, target[K.Id], K.SourceId, sourceId, K.School, school, K.Threshold, cfg[K.Threshold],
                K.Status, onBreak[K.Status], K.Value, onBreak[K.Value], K.Duration, onBreak[K.Duration]));
            Statuses.Apply(c, target, onBreak.Str(K.Status), onBreak.Num(K.Value), source);
            var inst = Statuses.Instance(target, onBreak.Str(K.Status));
            if (inst != null) inst[K.Duration] = onBreak[K.Duration]?.DeepClone();
            return amount;
        }

        public static double ComputeBlockGain(CombatState c, JObject entity, double baseAmount, JObject card)
        {
            var amt = baseAmount + Ratings.CardBonus(c, entity, card, V.BonusBlock, baseAmount) + Statuses.GetAdd(c, entity, K.BlockAdd);
            amt *= Statuses.GetMult(c, entity, K.BlockGainedMult);
            amt = Math.Floor(amt);
            return amt < 0 ? 0 : amt;
        }

        public static double GainBlock(CombatState c, JObject entity, double baseAmount, JObject card)
        {
            if (!entity.Is(K.Alive)) return 0;
            var amt = ComputeBlockGain(c, entity, baseAmount, card);
            var cap = Statuses.GetCap(c, entity, K.BlockCap);
            if (cap != null && entity.Num(K.Block) + amt > cap.Value) amt = Math.Max(0, cap.Value - entity.Num(K.Block));
            entity.Put(K.Block, entity.Num(K.Block) + amt);
            var payload = Js.Obj(K.TargetId, entity[K.Id], K.Amount, amt);
            if (Js.Truthy(card?[K.InstanceId]))
            {
                payload[K.CardInstanceId] = card[K.InstanceId].DeepClone();
                if (card[K.SourceHand] != null) payload[K.SourceHand] = card[K.SourceHand].DeepClone();
                if (card[K.GrantedBy] != null) payload[K.GrantedBy] = card[K.GrantedBy].DeepClone();
            }
            c.Emit(E.BlockGained, payload);
            return amt;
        }

        public static double ApplyLoseHp(CombatState c, JObject target, double amount, string cause)
        {
            if (target == null || !target.Is(K.Alive)) return 0;
            var n = Math.Max(0, Math.Floor(amount));
            if (n == 0) return 0;
            target.Put(K.Hp, target.Num(K.Hp) - n);
            c.Emit(E.HpLost, Js.Obj(K.TargetId, target[K.Id], K.Amount, n, K.Cause, cause));
            AfterHpChange(c, target);
            return n;
        }

        public static double ApplyHeal(CombatState c, JObject target, double amount)
        {
            if (target == null || !target.Is(K.Alive)) return 0;
            var n = Math.Max(0, Math.Floor(amount));
            var gained = Math.Min(n, target.Num(K.MaxHp) - target.Num(K.Hp));
            target.Put(K.Hp, target.Num(K.Hp) + gained);
            c.Emit(E.Healed, Js.Obj(K.TargetId, target[K.Id], K.Amount, gained, K.Requested, n));
            AfterHpChange(c, target);
            return gained;
        }

        private static void AfterHpChange(CombatState c, JObject target)
        {
            if (target.Num(K.Hp) <= 0 && target.Is(K.Alive))
            {
                target.Put(K.Hp, 0);
                target.Put(K.Alive, false);
                if (target.Str(K.Kind) == V.Enemy) c.Emit(E.EnemyDied, Js.Obj(K.TargetId, target[K.Id], K.EnemyId, target[K.EnemyId]));
            }
            Triggers.CheckPhases(c);
        }

        public static void StaggerEnemy(CombatState c, JObject enemy)
        {
            if (enemy == null || enemy.Str(K.Kind) != V.Enemy || !enemy.Is(K.Alive)) return;
            var pending = enemy.Obj(K.PendingMove);
            var cancelled = pending != null ? pending[K.MoveId]?.DeepClone() : Js.Null();
            enemy[K.PendingMove] = Js.Null();
            enemy.Put(K.SkipNextTurn, true);
            enemy[K.Intent] = Js.Obj(K.Kind, V.IntentStaggered, K.MoveId, Js.Null());
            c.Emit(E.EnemyStaggered, Js.Obj(K.TargetId, enemy[K.Id], K.EnemyId, enemy[K.EnemyId], K.CancelledMove, cancelled ?? Js.Null()));
        }

        public static void StaggerPlayer(CombatState c, JObject player)
        {
            var cfg = c.Data.Balance.Obj(K.Stagger)?.Obj(K.Player) ?? new JObject();
            var actionLoss = Js.IsInt(cfg[K.ActionLoss]) ? cfg.Num(K.ActionLoss) : 0;
            var applied = new JObject();
            foreach (var p in (cfg.Obj(K.Statuses) ?? new JObject()).Properties())
            {
                if (!(Js.D(p.Value) > 0)) continue;
                Statuses.Apply(c, player, p.Name, Js.D(p.Value), null);
                applied[p.Name] = p.Value.DeepClone();
            }
            player.Put(K.PendingActionLoss, player.Or0(K.PendingActionLoss) + actionLoss);
            c.Emit(E.PlayerStaggered, Js.Obj(K.TargetId, player[K.Id], K.ActionLoss, actionLoss, K.Statuses, applied));
        }

        public static void DealPoiseDamage(CombatState c, JObject entity, double amount)
        {
            if (c.RatingsRules != null)
            {
                Ratings.ApplyImpact(c, null, entity, Js.Obj(K.DamageSchool, V.Physical), amount);
                return;
            }
            if (entity == null || !entity.Is(K.Alive) || (entity.Str(K.Kind) != V.Enemy && entity.Str(K.Kind) != V.Player)) return;
            var meter = entity.Obj(K.PoiseMeter);
            if (meter == null || !(meter.Num(K.Max) > 0)) return;
            var isEnemy = entity.Str(K.Kind) == V.Enemy;
            var n = Math.Max(0, Math.Floor(amount));
            meter.Put(K.Value, meter.Num(K.Value) + n);
            var cfg = c.Data.Balance.Obj(K.Poise) ?? new JObject();
            var guard = 0;
            while (meter.Num(K.Value) >= meter.Num(K.Max))
            {
                if (++guard > c.Data.Rule(K.Limits, K.FillLoopGuard)) throw new InvalidOperationException(M.PoiseLoop);
                meter.Put(K.Value, meter.Num(K.Value) - meter.Num(K.Max));
                c.Emit(E.MeterFilled, Js.Obj(K.TargetId, entity[K.Id], K.Meter, K.Poise, K.Threshold, meter[K.Max]));
                if (isEnemy)
                {
                    StaggerEnemy(c, entity);
                    foreach (var eff in Js.Items(cfg[K.OnFill]).OfType<JObject>())
                        c.Enqueue(new CombatAction { Effect = eff, Source = entity, Owner = entity, Target = entity, Meta = ActionMeta.Empty() });
                }
                else StaggerPlayer(c, entity);
                var growth = Js.Coalesce(cfg[K.GrowthMult], c.Data.Rule(K.Defaults, K.PoiseGrowthMult));
                if (growth != 1 && !Statuses.AnyCombatantFlag(c, K.MeterMaxGrowthDisabled))
                {
                    meter.Put(K.Max, Math.Ceiling(meter.Num(K.Max) * growth));
                    meter.Put(K.Growths, meter.Or0(K.Growths) + 1);
                    meter.Put(K.GrowthMult, growth);
                }
            }
        }

        // ------------------------------------------------------------------ piles

        public static void DrawCards(CombatState c, double n)
        {
            for (var i = 0; i < n; i++)
            {
                if (c.HandRules != null && c.Piles.Hand.Count >= c.HandMax) return;
                if (c.Piles.Draw.Count == 0)
                {
                    if (c.HandRules != null && c.HandRules[K.Reshuffle] != null && c.HandRules[K.Reshuffle].Type == JTokenType.Boolean && !c.HandRules[K.Reshuffle].Value<bool>()) return;
                    if (c.Piles.Discard.Count == 0) return;
                    ReshuffleDiscardIntoDraw(c);
                }
                var card = c.Piles.Draw[0];
                c.Piles.Draw.RemoveAt(0);
                if (c.Piles.Hand.Count >= c.HandMax)
                {
                    c.Piles.Discard.Add(card);
                    c.Emit(E.CardDiscarded, Js.Obj(K.CardInstanceId, card[K.InstanceId], K.CardId, card[K.CardId], K.Reason, V.ReasonHandFull));
                }
                else
                {
                    c.Piles.Hand.Add(card);
                    c.Emit(E.CardDrawn, Js.Obj(K.CardInstanceId, card[K.InstanceId], K.CardId, card[K.CardId]));
                }
            }
        }

        public static void DiscardFromHand(CombatState c, double n, bool random)
        {
            for (var i = 0; i < n && c.Piles.Hand.Count > 0; i++)
            {
                var idx = random ? (int)Math.Floor(c.Rng.Float(RngStream.Misc) * c.Piles.Hand.Count) : c.Piles.Hand.Count - 1;
                var card = c.Piles.Hand[idx];
                c.Piles.Hand.RemoveAt(idx);
                c.Piles.Discard.Add(card);
                c.Emit(E.CardDiscarded, Js.Obj(K.CardInstanceId, card[K.InstanceId], K.CardId, card[K.CardId], K.Reason, V.ReasonEffect));
            }
        }

        public static void ReshuffleDiscardIntoDraw(CombatState c)
        {
            c.Piles.Draw.AddRange(c.Piles.Discard);
            c.Piles.Discard.Clear();
            c.Piles.Draw = c.Rng.Shuffle(RngStream.Shuffle, c.Piles.Draw);
            c.Emit(E.DeckShuffled, Js.Obj(K.Size, c.Piles.Draw.Count));
        }

        // ------------------------------------------------------------------ targets and amounts

        private static List<JObject> LivingEnemies(CombatState c) => c.Enemies.Where(e => e.Is(K.Alive)).ToList();

        public static List<JObject> ResolveTargets(CombatState c, CombatAction action, string spec)
        {
            var list = new List<JObject>();
            switch (spec)
            {
                case null:
                    if (action.Target != null) list.Add(action.Target);
                    else if (action.Source != null) list.Add(action.Source);
                    return list;
                case V.TargetSelf:
                    if (action.Source != null) list.Add(action.Source);
                    return list;
                case V.RefOwner:
                    if ((action.Owner ?? action.Source) != null) list.Add(action.Owner ?? action.Source);
                    return list;
                case V.RefPlayer:
                    if (c.Player != null) list.Add(c.Player);
                    return list;
                case V.RefEnemy:
                {
                    if (action.Target != null && action.Target.Str(K.Kind) == V.Enemy && action.Target.Is(K.Alive)) return new List<JObject> { action.Target };
                    if (action.Source != null && action.Source.Str(K.Kind) == V.Enemy)
                    {
                        if (c.Player != null) list.Add(c.Player);
                        return list;
                    }
                    var living = LivingEnemies(c);
                    return living.Count > 0 ? new List<JObject> { living[0] } : list;
                }
                case V.TargetAllEnemies:
                    return LivingEnemies(c);
                case V.TargetOtherEnemies:
                {
                    var named = action.Meta?.Event?.Str(K.TargetId);
                    return LivingEnemies(c).Where(e => e != action.Target && e.Str(K.Id) != named).ToList();
                }
                case V.TargetRandomEnemy:
                {
                    var living = LivingEnemies(c);
                    return living.Count > 0 ? new List<JObject> { c.Rng.Pick(RngStream.Misc, living) } : list;
                }
                case V.TargetAlly:
                {
                    var t = action.Target;
                    if (t != null && t.Str(K.Kind) == V.Player && t != action.Source && t.Is(K.Alive)) return new List<JObject> { t };
                    if (action.Source != null) list.Add(action.Source);
                    return list;
                }
                default:
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnknownTarget, spec));
            }
        }

        public static FormulaContext FormulaCtxFor(CombatState c, CombatAction action, JObject primaryTarget)
        {
            var ctx = new FormulaContext();
            ctx.Entities[V.TargetSelf] = action.Source;
            ctx.Entities[V.RefOwner] = action.Owner ?? action.Source;
            ctx.Entities[V.RefTarget] = primaryTarget ?? action.Target;
            ctx.Entities[V.RefEnemy] = primaryTarget ?? action.Target;
            ctx.Entities[V.RefPlayer] = c.Player;
            ctx.AllEnemies = LivingEnemies(c);
            ctx.EnergySpent = action.Meta?.EnergySpent ?? 0;
            ctx.CardsPlayedThisTurn = c.Player != null ? c.Player.Obj(K.Counters).Num(K.CardsPlayedThisTurn) : 0;
            return ctx;
        }

        public static double EvalNum(CombatState c, CombatAction action, JToken value, double dflt, JObject target = null)
        {
            if (value == null) return dflt;
            double v;
            if (Js.IsNum(value)) v = Math.Floor(Js.D(value));
            else if (Formulas.IsFormula(value)) v = Formulas.Evaluate(value, FormulaCtxFor(c, action, target));
            else throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.ExpectedNumber, value));
            var mult = action.Meta?.AmountMult;
            if (mult != null && mult.Value != 1) v = Math.Ceiling(v * mult.Value);
            return v;
        }

        /// <summary>evalRaw: the unfloored amount, for the heal under the run-level heal scale; amountMult applies as in EvalNum.</summary>
        public static double EvalRaw(CombatState c, CombatAction action, JToken value, double dflt, JObject target = null)
        {
            if (value == null) return dflt;
            double v;
            if (Js.IsNum(value)) v = Js.D(value);
            else if (Formulas.IsFormula(value)) v = Formulas.EvaluateRaw(value, FormulaCtxFor(c, action, target));
            else throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.ExpectedNumber, value));
            var mult = action.Meta?.AmountMult;
            if (mult != null && mult.Value != 1) v = Math.Ceiling(v * mult.Value);
            return v;
        }

        // ------------------------------------------------------------------ interpreter

        public static void ExecuteAction(CombatState c, CombatAction action)
        {
            if (c.Result != null) return;
            var eff = action.Effect;
            if (Js.Truthy(eff[K.If]))
            {
                var pctx = new PredicateContext { Owner = action.Owner ?? action.Source, Source = action.Source, Target = action.Target, Card = action.Card, Meta = action.Meta };
                if (!Triggers.EvalPredicate(c, eff.Obj(K.If), pctx)) return;
            }
            var repeat = EvalNum(c, action, eff[K.Repeat], 1);
            for (var r = 0; r < repeat; r++)
            {
                RunOpcode(c, action, eff);
                if (c.Result != null) return;
            }
        }

        internal static void RunOpcode(CombatState c, CombatAction action, JObject eff)
        {
            if (c.RunOpcodes != null && c.RunOpcodes(c, action, eff)) return;
            var op = eff.Str(K.Op);
            var target = eff.Str(K.Target);
            switch (op)
            {
                case Op.Damage:
                {
                    var hits = Math.Max(0, EvalNum(c, action, eff[K.Hits], 1));
                    var attackTags = AttackTagsFor(c, action, eff);
                    for (var h = 0; h < hits; h++)
                    {
                        foreach (var t in ResolveTargets(c, action, target))
                        {
                            if (!t.Is(K.Alive)) continue;
                            var baseAmount = EvalNum(c, action, eff[K.Amount], 0, t);
                            var carrier = action.Card != null ? Js.Spread(action.Card) : new JObject();
                            if (Js.Truthy(eff[K.Attack])) carrier[K.Attack] = eff[K.Attack].DeepClone();
                            Cards.SetOrRemove(carrier, K.DamageSchool, Js.Truthy(eff[K.DamageSchool]) ? eff[K.DamageSchool] : action.Card?[K.DamageSchool]);
                            carrier[K.Tags] = (action.Card?[K.Tags] as JArray ?? attackTags).DeepClone();
                            carrier.Put(K.EnergySpent, action.Meta?.EnergySpent ?? 0);
                            if (c.RatingsRules != null && action.Source?.Str(K.Kind) == V.Enemy)
                            {
                                var moveId = action.Meta?.MoveId ?? action.Source.Obj(K.Intent)?.Str(K.MoveId);
                                var attackType = c.RatingsRules.Obj(K.EnemyAttackType)?.Str(action.Source.Str(K.EnemyId) + V.KeySeparator + moveId);
                                if (!string.IsNullOrEmpty(attackType) && attackType != V.AttackTypeAuto) carrier[K.DamageSchool] = attackType;
                            }
                            var hpBefore = t.Num(K.Hp);
                            ApplyAttackDamage(c, action.Source, t, baseAmount, attackTags, carrier);
                            if (c.RatingsRules == null && t.Str(K.Kind) == V.Player && action.Source?.Str(K.Kind) == V.Enemy && t.Is(K.Alive) && t.Num(K.Hp) < hpBefore)
                            {
                                var perHit = c.Data.Balance.Obj(K.Poise)?[K.PlayerImpactPerHit];
                                if (Js.IsInt(perHit) && Js.D(perHit) > 0)
                                {
                                    DealPoiseDamage(c, t, Js.D(perHit));
                                    var impact = Js.Obj(K.SourceId, action.Source[K.Id], K.TargetId, t[K.Id], K.Amount, Js.D(perHit));
                                    var pm = t.Obj(K.PoiseMeter);
                                    if (pm != null) impact[K.PoiseMeter] = Js.Obj(K.Value, pm[K.Value], K.Max, pm[K.Max]);
                                    c.Emit(E.ImpactDealt, impact);
                                }
                            }
                        }
                    }
                    break;
                }
                case Op.Block:
                    foreach (var t in ResolveTargets(c, action, target)) GainBlock(c, t, EvalNum(c, action, eff[K.Amount], 0, t), action.Card);
                    break;
                case Op.DodgeRoll:
                {
                    var p = c.Player;
                    if (action.Source == null || action.Source.Str(K.Id) != p.Str(K.Id)) break;
                    var roll = c.Rng.Int(RngStream.Misc, 1, (int)c.Data.Mechanics.Obj(K.DodgeRoll).Num(K.Die));
                    var dexterity = c.Attributes != null && Js.Truthy(c.Attributes[K.Dexterity]) ? c.Attributes.Num(K.Dexterity) : c.Data.Rule(K.Defaults, K.Dexterity);
                    var stance = Equipment.PlayerWeightClass(c);
                    var receipt = Equipment.DodgeRoll(c.Data, roll, dexterity, stance.WeightClass);
                    c.Emit(E.DodgeRolled, Js.Obj(K.SourceId, p[K.Id], K.Roll, (double)roll, K.Check, receipt.Check, K.Difficulty, receipt.Difficulty,
                        K.Success, receipt.Success, K.TemporaryGuard, receipt.TemporaryGuard, K.WeightClass, stance.WeightClass[K.Id]));
                    if (receipt.Success && receipt.TemporaryGuard > 0) GainBlock(c, p, receipt.TemporaryGuard, null);
                    break;
                }
                case Op.ApplyStatus:
                    foreach (var t in ResolveTargets(c, action, target))
                    {
                        var stacks = EvalNum(c, action, eff[K.Stacks], 1, t);
                        var statusId = eff.Str(K.Status);
                        Statuses.Apply(c, t, statusId, stacks, action.Source);
                        var inst = Statuses.Instance(t, statusId);
                        if (c.RatingsRules != null && action.Source != null && t.Str(K.Id) == action.Source.Str(K.Id) && action.Card != null && Ratings.IsMagicalAttack(c, action.Card) && inst != null)
                            inst[K.RatingCard] = action.Card.DeepClone();
                    }
                    break;
                case Op.RemoveStatus:
                    foreach (var t in ResolveTargets(c, action, target)) Statuses.Remove(c, t, eff.Str(K.Status), V.ReasonConsumed);
                    break;
                case Op.Draw:
                    DrawCards(c, Math.Max(0, EvalNum(c, action, eff[K.Amount], 1)));
                    break;
                case Op.Discard:
                    DiscardFromHand(c, Math.Max(0, EvalNum(c, action, eff[K.Amount], 1)), eff.Is(K.Random));
                    break;
                case Op.Exhaust:
                {
                    var n = Math.Max(0, EvalNum(c, action, eff[K.Amount], 1));
                    for (var i = 0; i < n && c.Piles.Hand.Count > 0; i++)
                    {
                        var idx = eff.Is(K.Random) ? (int)Math.Floor(c.Rng.Float(RngStream.Misc) * c.Piles.Hand.Count) : c.Piles.Hand.Count - 1;
                        var card = c.Piles.Hand[idx];
                        c.Piles.Hand.RemoveAt(idx);
                        c.Piles.Exhaust.Add(card);
                        c.Emit(E.CardExhausted, Js.Obj(K.CardInstanceId, card[K.InstanceId], K.CardId, card[K.CardId], K.Reason, V.ReasonEffect));
                    }
                    break;
                }
                case Op.AddCard:
                {
                    var cardId = eff.Str(K.Card);
                    c.Data.Cards.Get(cardId);
                    var count = Math.Max(1, EvalNum(c, action, eff[K.Count], 1));
                    var pileName = Js.Truthy(eff[K.Pile]) ? eff.Str(K.Pile) : V.PileDiscard;
                    for (var i = 0; i < count; i++)
                    {
                        var inst = Js.Obj(K.InstanceId, c.NextInstanceId(), K.CardId, cardId, K.Upgraded, false);
                        var pile = PileByName(c, pileName) ?? throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnknownPile, pileName));
                        if (pileName == V.PileHand && pile.Count >= c.HandMax)
                        {
                            c.Piles.Discard.Add(inst);
                            c.Emit(E.CardDiscarded, Js.Obj(K.CardInstanceId, inst[K.InstanceId], K.CardId, inst[K.CardId], K.Reason, V.ReasonHandFull));
                            continue;
                        }
                        var position = Js.Truthy(eff[K.Position]) ? eff.Str(K.Position) : V.PositionRandom;
                        if (position == V.PositionTop) pile.Insert(0, inst);
                        else if (position == V.PositionBottom) pile.Add(inst);
                        else pile.Insert((int)Math.Floor(c.Rng.Float(RngStream.Shuffle) * (pile.Count + 1)), inst);
                    }
                    break;
                }
                case Op.GainEnergy:
                {
                    var n = Math.Max(0, EvalNum(c, action, eff[K.Amount], 1));
                    c.Player.Put(K.Energy, c.Player.Num(K.Energy) + n);
                    c.Emit(E.EnergyGained, Js.Obj(K.Amount, n));
                    break;
                }
                case Op.RestoreStamina:
                    foreach (var t in ResolveTargets(c, action, target))
                    {
                        var amount = Math.Min(t.Num(K.MaxStamina) - t.Num(K.Stamina), Math.Max(0, EvalNum(c, action, eff[K.Amount], 1)));
                        t.Put(K.Stamina, t.Num(K.Stamina) + amount);
                        c.Emit(E.StaminaRecovered, Js.Obj(K.TargetId, t[K.Id], K.Amount, amount, K.Reason, V.ReasonEffect));
                    }
                    break;
                case Op.RestoreMana:
                {
                    var toFloor = eff[K.ToFloorPct] != null;
                    var n = toFloor ? 0 : Math.Max(0, EvalNum(c, action, eff[K.Amount], 1));
                    var pct = toFloor ? Math.Max(0, EvalNum(c, action, eff[K.ToFloorPct], 0)) : 0;
                    foreach (var t in ResolveTargets(c, action, target))
                    {
                        var before = t.Num(K.Mana);
                        if (toFloor)
                        {
                            var floor = Math.Min(t.Num(K.MaxMana), Math.Floor(t.Num(K.MaxMana) * pct / CombatMath.Percent));
                            t.Put(K.Mana, before >= floor ? t.Num(K.MaxMana) : floor);
                        }
                        else t.Put(K.Mana, Math.Min(t.Num(K.MaxMana), t.Num(K.Mana) + n));
                        c.Emit(E.ManaRestored, Js.Obj(K.TargetId, t[K.Id], K.Amount, t.Num(K.Mana) - before));
                    }
                    break;
                }
                case Op.LoseHp:
                    foreach (var t in ResolveTargets(c, action, target))
                        ApplyLoseHp(c, t, EvalNum(c, action, eff[K.Amount], 0, t), Js.Truthy(eff[K.Cause]) ? eff.Str(K.Cause) : V.CauseEffect);
                    break;
                case Op.Heal:
                    foreach (var t in ResolveTargets(c, action, target))
                    {
                        // Under the run-level heal scale the amount is floored ONCE, after the multiplier (shipped #1195).
                        var amount = c.HealMult == 1 ? EvalNum(c, action, eff[K.Amount], 0, t) : Math.Floor(EvalRaw(c, action, eff[K.Amount], 0, t) * c.HealMult);
                        ApplyHeal(c, t, amount + Ratings.CardBonus(c, action.Source, action.Card, V.BonusHeal, amount));
                    }
                    break;
                case Op.ShuffleDiscardIntoDraw:
                    ReshuffleDiscardIntoDraw(c);
                    break;
                case Op.EnterStance:
                {
                    var stanceId = eff.Str(K.Stance);
                    var def = c.Data.Stances.Get(stanceId);
                    if (c.Player.Str(K.StanceId) == stanceId) break;
                    if (Js.Truthy(c.Player[K.StanceId])) c.Emit(E.StanceExited, Js.Obj(K.Stance, c.Player[K.StanceId]));
                    c.Player.Put(K.StanceId, stanceId);
                    c.Emit(E.StanceEntered, Js.Obj(K.Stance, stanceId, K.PlayerId, c.Player[K.Id]));
                    foreach (var onEnter in Js.Items(def[K.OnEnter]).OfType<JObject>())
                        c.Enqueue(new CombatAction { Effect = onEnter, Source = c.Player, Owner = c.Player, Target = action.Target, Meta = action.Meta });
                    break;
                }
                case Op.PoiseDamage:
                    foreach (var t in ResolveTargets(c, action, target)) DealPoiseDamage(c, t, EvalNum(c, action, eff[K.Amount], 0, t));
                    break;
                case Op.Stagger:
                    foreach (var t in ResolveTargets(c, action, target)) StaggerEnemy(c, t);
                    break;
                case Op.ArcaneBuildup:
                {
                    var ev = action.Meta?.Event;
                    var school = Js.Truthy(ev?[K.School]) ? ev.Str(K.School) : V.Arcane;
                    var fired = ev?[K.Threshold];
                    foreach (var t in ResolveTargets(c, action, target))
                    {
                        var captured = t;
                        AddArcaneExposure(c, action.Source, t, school, null, cfg => eff[K.Pct] != null
                            ? Math.Floor((Js.IsFinite(fired) ? Js.D(fired) : cfg.Num(K.Threshold)) * EvalNum(c, action, eff[K.Pct], 0, captured) / CombatMath.Percent)
                            : EvalNum(c, action, eff[K.Amount], 0, captured));
                    }
                    break;
                }
                default:
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnknownOpcode, op));
            }
        }

        private static List<JObject> PileByName(CombatState c, string name)
        {
            switch (name)
            {
                case V.PileDraw: return c.Piles.Draw;
                case V.PileHand: return c.Piles.Hand;
                case V.PileDiscard: return c.Piles.Discard;
                case V.PileExhaust: return c.Piles.Exhaust;
                default: return null;
            }
        }
    }
}
