using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using Op = Ashen.Generated.CombatOps;
using RngStream = Ashen.Generated.RngStream;
using SK = Ashen.Generated.Core2StatusKeys;
using SM = Ashen.Generated.Core2StatusMessages;
using SV = Ashen.Generated.Core2StatusValues;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// Enemy burdens (web engine/enemyBurdens.js): cards a player lays into an enemy's own piles. An enemy keeps its authored move plans; the burdens it
    /// holds on its turn tax what its moves cost, can make it skip its actions, and can put a status on it while held. A burden is data on the card row
    /// (<c>burden</c>), never a card id.
    /// </summary>
    public static class CoreBurdens
    {
        private static readonly string[] Zones = { K.Draw, K.Hand, K.Discard, K.Exhaust };

        /// <summary>addEnemyBurden(ctx, enemy, cardId, count, pile): the generated card goes into the enemy's draw pile or hand.</summary>
        public static void Add(CombatState c, JObject enemy, string cardId, double count, string pile)
        {
            if (pile != K.Draw && pile != K.Hand) throw new InvalidOperationException(SM.BurdenPile);
            var def = c.Data.Cards.Get(cardId);
            if (!Js.Truthy(def[SK.Burden])) throw new InvalidOperationException(SM.BurdenBehavior);
            var piles = enemy.Obj(CK.BurdenPiles);
            if (piles == null)
            {
                piles = new JObject();
                foreach (var zone in Zones) piles[zone] = new JArray();
                enemy[CK.BurdenPiles] = piles;
            }
            var n = (int)Math.Min(20, Math.Max(0, Math.Floor(count)));
            for (var i = 0; i < n; i++) piles.Arr(pile).Add(Js.Obj(K.InstanceId, c.NextInstanceId(), K.CardId, cardId, K.Upgraded, false));
            c.Emit(SV.EnemyBurdenAdded, Js.Obj(K.TargetId, enemy[K.Id], K.CardId, cardId, K.Count, count));
        }

        /// <summary>beginEnemyBurdens(ctx, enemy, pool): at the enemy's turn start it draws one burden, and every burden it holds taxes, skips or marks it.</summary>
        public static void Begin(CombatState c, JObject enemy, double pool)
        {
            var piles = enemy.Obj(CK.BurdenPiles);
            if (piles == null) return;
            if (piles.Arr(K.Draw).Count == 0 && piles.Arr(K.Discard).Count > 0)
            {
                piles[K.Draw] = new JArray(c.Rng.Shuffle(RngStream.Shuffle, piles.Arr(K.Discard).OfType<JObject>().ToList()));
                piles[K.Discard] = new JArray();
            }
            var draw = piles.Arr(K.Draw);
            JToken card = null;
            if (draw.Count > 0) { card = draw[0]; draw.RemoveAt(0); }
            enemy.Put(SK.BurdenBudget, Math.Max(0, pool));
            enemy.Put(SK.BurdenTax, 0);
            if (card != null) piles.Arr(K.Hand).Add(card);
            foreach (var held in piles.Arr(K.Hand).OfType<JObject>().ToList())
            {
                var rule = c.Data.Cards.Get(held.Str(K.CardId)).Obj(SK.Burden);
                if (rule.Is(SK.SkipActions)) enemy.Put(K.SkipNextTurn, true);
                enemy.Put(SK.BurdenTax, enemy.Num(SK.BurdenTax) + Math.Max(0, rule.Or0(Ashen.Generated.Core2CostKeys.StaminaTax)) + Math.Max(0, rule.Or0(Ashen.Generated.Core2CostKeys.ManaTax)));
                if (Js.Truthy(rule[K.Status]))
                    c.Enqueue(new CombatAction { Effect = Js.Obj(K.Op, Op.ApplyStatus, K.Target, V.TargetSelf, K.Status, rule[K.Status], K.Stacks, 1), Source = enemy, Owner = enemy, Target = enemy, Meta = new ActionMeta() });
                c.Emit(SV.EnemyBurdenDrawn, Js.Obj(K.TargetId, enemy[K.Id], K.CardId, held[K.CardId], K.CardInstanceId, held[K.InstanceId], SK.Skip, rule.Is(SK.SkipActions), SK.Tax, enemy[SK.BurdenTax]));
            }
        }

        /// <summary>allowBurdenMove(ctx, enemy, baseCost, move): a move the burdens' tax puts over the turn's budget is blocked.</summary>
        public static bool AllowMove(CombatState c, JObject enemy, double baseCost, JObject move)
        {
            var piles = enemy.Obj(CK.BurdenPiles);
            if (piles == null || !Js.Truthy(enemy[SK.BurdenTax])) return true;
            var profile = move.Obj(CK.CombatProfile) ?? new JObject();
            var range = Js.Truthy(move[CK.Range]) ? move.Str(CK.Range) : Js.Truthy(profile[CK.Range]) ? profile.Str(CK.Range) : CV.RangeMelee;
            var type = Js.Truthy(move[K.Type]) ? move.Str(K.Type) : move.Str(K.Intent) == K.Attack ? K.Attack : K.Skill;
            double tax = 0;
            foreach (var card in piles.Arr(K.Hand).OfType<JObject>())
            {
                var rule = c.Data.Cards.Get(card.Str(K.CardId)).Obj(SK.Burden);
                var rangeOk = !(rule[CK.Ranges] is JArray ranges && ranges.Count > 0) || Js.Includes(rule[CK.Ranges], range);
                var typeOk = !(rule[Ashen.Generated.Core2CostKeys.CardTypes] is JArray types && types.Count > 0) || Js.Includes(rule[Ashen.Generated.Core2CostKeys.CardTypes], type);
                if (rangeOk && typeOk) tax += rule.Or0(Ashen.Generated.Core2CostKeys.StaminaTax) + rule.Or0(Ashen.Generated.Core2CostKeys.ManaTax);
            }
            if (tax == 0) return true;
            var cost = Math.Max(0, baseCost) + tax;
            if (cost > enemy.Or0(SK.BurdenBudget))
            {
                c.Emit(SV.EnemyBurdenBlocked, Js.Obj(K.TargetId, enemy[K.Id], K.Cost, cost, SK.Remaining, enemy.Or0(SK.BurdenBudget)));
                return false;
            }
            enemy.Put(SK.BurdenBudget, enemy.Num(SK.BurdenBudget) - cost);
            return true;
        }

        /// <summary>endEnemyBurdens(ctx, enemy): the burdens it held go to its discard (an Ethereal or Exhaust one to its exhaust) and their statuses come off.</summary>
        public static void End(CombatState c, JObject enemy)
        {
            var piles = enemy.Obj(CK.BurdenPiles);
            if (piles == null) return;
            foreach (var card in piles.Arr(K.Hand).OfType<JObject>().ToList())
            {
                var def = c.Data.Cards.Get(card.Str(K.CardId));
                var to = Js.Includes(def[K.Keywords], CV.KeywordEthereal) || Js.Includes(def[K.Keywords], K.Exhaust) ? K.Exhaust : K.Discard;
                piles.Arr(to).Add(card);
                var status = def.Obj(SK.Burden)[K.Status];
                if (Js.Truthy(status))
                    c.Enqueue(new CombatAction { Effect = Js.Obj(K.Op, Op.RemoveStatus, K.Target, V.TargetSelf, K.Status, status, K.Amount, 1), Source = enemy, Owner = enemy, Target = enemy, Meta = new ActionMeta() });
            }
            piles[K.Hand] = new JArray();
            enemy.Remove(SK.BurdenTax);
            enemy.Remove(SK.BurdenBudget);
        }
    }
}
