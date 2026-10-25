using System;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Events;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.App.Run
{
    public sealed partial class Core2RunSession
    {
        // ------------------------------------------------------------------ potions between fights (web RunPotionModel.js)

        /// <summary>
        /// Drinks one charge of a charge flask outside a fight (useRunChargeFlask, applyRunPotion): the flask's effects run on the run with the relics' flask power applied to each amount (rounded
        /// up), then the charge is spent and the run saved. False when the rules shut it (the setting is off, the pool is dry, a fight is on); nothing changes then.
        /// </summary>
        public bool DrinkPotion(string chargeKind)
        {
            if (IsInCombat || RunOver || !PlayerSettings.On(SettingIds.UseRestorativeFlasksOutsideCombat)) return false;
            var def = Core2RunPotions.ChargeFlask(CombatData, chargeKind);
            var charges = _run.Obj(K.FlaskCharges);
            var key = chargeKind + V.CurrentSuffix;
            if (def == null || charges == null || charges.Num(key) <= 0) return false;
            var amountMult = Cards.PassiveMult(CombatData, _run[K.Relics] as JArray ?? new JArray(), K.FlaskPowerMult, null);
            RunEffects.Execute(Core.Events, _run, _rng, def[K.Effects] as JArray ?? new JArray(), amountMult);
            charges[key] = charges.Num(key) - 1;
            Save();
            return true;
        }

        /// <summary>Drops one carried potion of a kind (the first slot holding it) and saves; false when none is carried or a fight is on.</summary>
        public bool DropPotion(string flaskId)
        {
            if (IsInCombat || RunOver || string.IsNullOrEmpty(flaskId)) return false;
            var flasks = _run.Arr(K.Flasks);
            var at = flasks == null ? -1 : flasks.ToList().FindIndex(f => f is JObject o && o.Str(K.FlaskId) == flaskId);
            if (at < 0) return false;
            flasks.RemoveAt(at);
            Save();
            return true;
        }
    }
}
