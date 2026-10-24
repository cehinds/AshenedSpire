using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using SK = Ashen.Generated.Core2StatusKeys;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// What a status asks of its owner's turn (web engine/triggers.js fireOwnerHooks): a status that drains Ward every turn (<c>fluxPerTurn</c>, reaction
    /// rules version 2) spends it as the owner's turn opens, then the status and stance hooks declared on the owner's turn start or end are fired.
    /// </summary>
    public static class CoreStatusHooks
    {
        /// <summary>fireOwnerHooks(ctx, entity, hookName).</summary>
        public static void FireOwner(CombatState c, JObject entity, string hook)
        {
            if (!entity.Is(K.Alive)) return;
            if (hook == E.OwnerTurnStart && Js.D(c.Fields[CK.ReactionRulesVersion]) == c.CoreData.ReactionVersion)
                foreach (var id in entity.Obj(K.Statuses).Properties().Select(p => p.Name).ToList())
                {
                    var flux = c.Data.Statuses.Has(id) ? Js.D(c.Data.Statuses.Get(id)[CK.FluxPerTurn]) : double.NaN;
                    if (flux > 0 && entity.Obj(K.Statuses).Obj(id)?.Num(K.Stacks) > 0)
                        CoreStatusControl.SpendWard(c, entity, flux, Js.Obj(K.SourceId, entity[K.Id], K.Cause, Ashen.Generated.Core2DamageKeys.Flux, SK.StatusId, id));
                }
            Triggers.FireOwnerHooks(c, entity, hook);
        }
    }
}
