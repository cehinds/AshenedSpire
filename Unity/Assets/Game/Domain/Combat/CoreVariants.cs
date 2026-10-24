using System.Linq;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using DK = Ashen.Generated.Core2DamageKeys;
using SK = Ashen.Generated.Core2StatusKeys;

namespace Ashen.Domain.Combat
{
    /// <summary>A card's effect variants (web engine/cardBehavior.js selectCardVariant): the first row whose condition holds replaces the card's effects for this play.</summary>
    public static class CoreVariants
    {
        /// <summary>
        /// selectCardVariant(ctx, def, paid, target): the card with the effects of the first variant whose <c>if</c> holds (it reads the Stamina and Mana the
        /// play will pay), else the card as it is.
        /// </summary>
        public static JObject Select(CombatState c, JObject def, JObject paid, JObject target)
        {
            var variants = def.Arr(DK.EffectVariants);
            if (variants == null || variants.Count == 0) return def;
            var meta = new ActionMeta { EnergySpent = paid.Num(K.Energy), ManaSpent = paid.Num(K.Mana), StaminaSpent = paid.Num(K.Stamina) };
            var index = -1;
            for (var i = 0; i < variants.Count; i++)
            {
                var row = variants[i] as JObject;
                if (Triggers.EvalPredicate(c, row?.Obj(K.If), new PredicateContext { Source = c.Player, Owner = c.Player, Target = target, Card = def, Meta = meta })) { index = i; break; }
            }
            if (index < 0) return def;
            var variant = (JObject)variants[index];
            var result = Js.Spread(def);
            result[K.Effects] = variant[K.Effects]?.DeepClone() ?? new JArray();
            if (variant[K.TextTemplate] != null) result[K.TextTemplate] = variant[K.TextTemplate].DeepClone();
            result[SK.ActiveEffectVariant] = index;
            return result;
        }
    }
}
