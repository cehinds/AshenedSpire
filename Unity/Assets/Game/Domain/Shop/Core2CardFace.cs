using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using DK = Ashen.Generated.Core2DamageKeys;
using K = Ashen.Generated.CombatKeys;

namespace Ashen.Domain.Shop
{
    /// <summary>
    /// The figures a card face prints for a card in a fight, as the web's <c>previewCard(combat, instanceId)</c> states them with no target aimed: the damage a damaging effect
    /// would do before any defence or matchup (<c>faceValue</c>, whole, rounded down) and the Block a defending effect grants (<c>computeBlockGain</c>). The Smith follows these
    /// figures from one tier to the next (model/smithingCore.js liveCardValues). A Counter's reply is priced against the incoming attack, not from the weapon's rating, so its damage
    /// is not a face figure here. The preview is priced on a detached copy of the fight as the player sees it that has entered the card's stance, as the combat screen's card does.
    /// </summary>
    internal static class Core2CardFace
    {
        public static void Read(CombatState c, JObject inst, JObject printed, IReadOnlyList<string> ops, Dictionary<string, double?> values, Action<string> setName)
        {
            var id = inst.Str(K.InstanceId);
            var costs = CoreCosts.PreviewPlayCosts(c, id);
            var def = CoreVariants.Select(c, printed, costs, null);
            if (def.Str(K.Name) != null) setName(def.Str(K.Name));
            var authored = Js.Items(def[K.Effects]).OfType<JObject>().ToList();
            var priced = CoreVisibility.Project(CoreTransaction.DetachPaused(c));
            var carrier = CoreCardPlay.Carrier(priced, inst, def);
            CoreStances.SetProfileStance(priced, priced.Player, carrier);
            var needsTarget = CoreTargets.NeedsEnemyTarget(c, def);
            var action = new CombatAction
            {
                Source = priced.Player,
                Owner = priced.Player,
                Target = needsTarget ? priced.Enemies.FirstOrDefault(e => e.Is(K.Alive)) : null,
                Card = carrier,
                Meta = Meta(costs),
            };
            var counterCard = carrier.Obj(CK.CombatProfile)?.Str(CK.Maneuver) == CK.Counter;
            double? Price(JToken value, JObject on)
            {
                if (Js.IsNum(value)) return Math.Floor(Js.D(value));
                return CombatPreview.EvalPreview(priced, action, value, on);
            }
            foreach (var effect in authored)
            {
                var op = effect.Str(K.Op);
                if (!ops.Contains(op) || values.ContainsKey(op)) continue;
                action.Effect = effect;
                action.Meta = Meta(costs);
                var primary = CombatPreview.FirstResolvedTarget(priced, action, effect);
                if (op == Ashen.Generated.CombatOps.Damage)
                {
                    // The un-aimed face of a damage effect: the amount before any defense or matchup, whole, rounded down. A Counter's reply has no face figure.
                    if (counterCard) continue;
                    var printedAmount = Price(effect[K.Amount], null);
                    values[op] = Math.Floor(CoreDamage.ProfileDamageReceipt(priced, priced.Player, null, printedAmount.Value, carrier).Num(DK.Raw));
                }
                else if (op == Ashen.Generated.CombatOps.Block)
                {
                    var amount = Price(effect[K.Amount], primary);
                    values[op] = CoreActions.ComputeBlockGain(priced, priced.Player, amount.Value, carrier);
                }
            }
        }

        private static ActionMeta Meta(JObject costs) => new ActionMeta { EnergySpent = costs.Num(K.Stamina), ManaSpent = costs.Num(K.Mana), StaminaSpent = costs.Num(K.Stamina) };
    }
}
