using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using RK = Ashen.Generated.Core2RemainingKeys;
using RM = Ashen.Generated.Core2RemainingMessages;
using RV = Ashen.Generated.Core2RemainingValues;
using RngStream = Ashen.Generated.RngStream;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The flask a player drinks in a Core 2 fight (web engine/combat.js doUseFlask) and the scripts a flask effect names (content/scripts.js). A flask is used
    /// by slot or from a charge pool; its effects join the queue as the player's own, and a relic that scales flasks scales each amount (rounded up).
    /// A script is data (<c>rules/core2Combat.json</c> scripts): the port runs the kind a script names, never a script by its id.
    /// </summary>
    public static class CoreFlasks
    {
        /// <summary>doUseFlask(combat, { slot, chargeKind, targetId }).</summary>
        public static void Use(CombatState c, JObject intent)
        {
            if (c.Phase != V.PhasePlayer) throw new InvalidOperationException(M.NotPlayerTurnForFlasks);
            var p = c.Player;
            var kind = intent.Str(K.ChargeKind);
            var chargeId = CombatEngine.ChargeFlaskId(c, kind);
            var currentKey = kind + V.CurrentSuffix;
            var charges = p.Obj(K.FlaskCharges);
            if (chargeId != null && (charges == null || charges.Num(currentKey) <= 0)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.NoFlaskCharges, kind));
            var slot = intent[K.Slot];
            var flasks = p.Arr(K.Flasks);
            string flaskId;
            if (chargeId != null) flaskId = chargeId;
            else
            {
                var held = Js.IsNum(slot) && flasks != null && Js.D(slot) >= 0 && Js.D(slot) < flasks.Count && Js.D(slot) == Math.Floor(Js.D(slot)) ? flasks[(int)Js.D(slot)] as JObject : null;
                flaskId = held?.Str(K.FlaskId) ?? throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.NoFlaskInSlot, Js.IsNum(slot) ? Js.Str(slot) : RV.Undefined));
            }
            var def = c.Data.Flasks.Get(flaskId);
            JObject target = null;
            var targetId = intent.Str(K.TargetId);
            if (targetId != null)
            {
                target = Triggers.FindEntity(c, targetId);
                if (target == null || !target.Is(K.Alive)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.InvalidTarget, targetId));
            }
            else if (def.Is(K.Targeted)) target = c.Enemies.FirstOrDefault(e => e.Is(K.Alive));
            if (chargeId != null) charges.Put(currentKey, charges.Num(currentKey) - 1);
            else flasks.RemoveAt((int)Js.D(slot));
            var used = new JObject { [K.FlaskId] = flaskId, [K.TargetId] = target != null ? target[K.Id] : Js.Null() };
            if (chargeId == null) used[K.Slot] = slot.DeepClone();
            c.Emit(E.FlaskUsed, used);
            var amountMult = Cards.PassiveMult(c.Data, p.Arr(K.RelicIds), K.FlaskPowerMult, Properties.MountsOf(c, p));
            foreach (var eff in Js.Items(def[K.Effects]).OfType<JObject>())
                c.Enqueue(new CombatAction { Effect = eff, Source = p, Owner = p, Target = target, Meta = amountMult != 1 ? new ActionMeta { AmountMult = amountMult } : ActionMeta.Empty() });
            CoreActions.Drain(c);
        }

        /// <summary>
        /// The script an effect names ({ script: 'name' }, web registries.scripts[name](ctx, action)): its row in <c>rules/core2Combat.json</c> says what it does.
        /// A <c>randomRowEffects</c> script draws <c>count</c> distinct rows of a registry (the first from the whole pool, each next from what is left) and
        /// queues their effects as the action's own, with the action's source, owner, target and meta.
        /// </summary>
        public static void RunScript(CombatState c, CombatAction action, string name)
        {
            var def = c.CoreData.Section(RK.Scripts).Obj(name) ?? throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, RM.UnknownScript, name));
            if (def.Str(RK.Kind) != RV.ScriptRandomRowEffects || def.Str(RK.Registry) != RV.RegistryFlasks)
                throw new NotSupportedException(string.Format(CultureInfo.InvariantCulture, RM.ScriptKindUnknown, name, def.Str(RK.Kind)));
            var stream = Ashen.Generated.RngStreamNames.Parse(def.Str(RK.Stream));
            var pool = c.Data.Flasks.All.Where(row => !(def.Is(RK.ExcludeScripted) && UsesScript(row))).ToList();
            if (pool.Count == 0) return;
            var picks = new List<JObject>();
            var count = (int)def.Num(RK.Count);
            while (picks.Count < count && pool.Count > 0)
            {
                var chosen = c.Rng.Pick(stream, pool);
                picks.Add(chosen);
                pool = pool.Where(row => row.Str(K.Id) != chosen.Str(K.Id)).ToList();
            }
            foreach (var row in picks)
                foreach (var eff in Js.Items(row[K.Effects]).OfType<JObject>())
                    c.Enqueue(new CombatAction { Effect = eff, Source = action.Source, Owner = action.Owner, Target = action.Target, Meta = action.Meta ?? ActionMeta.Empty() });
        }

        private static bool UsesScript(JObject row) => Js.Items(row[K.Effects]).Any(e => Js.IsStr(Js.Get(e, Ashen.Generated.Core2CombatKeys.Script)));
    }
}
