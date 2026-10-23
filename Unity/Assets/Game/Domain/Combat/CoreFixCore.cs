using System;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using K = Ashen.Generated.CombatKeys;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The FixCore switch and rule families a Core 2 fight reads (web model/fixCoreMechanics.js fixCoreEnabled, fixCoreRules,
    /// fixCoreWardDisabled). The family a fight reads is the snapshot it carries (<c>rules/fixCoreSnapshot.json</c>); the default
    /// profile attaches no mechanic tags or bindings to any entity, status, gear or card, so no attachment replaces a family. The
    /// mechanics kernel that resolves those attachments (<c>compileBindings</c>, <c>queryBinding</c>) is WP-7; until then an
    /// entity carrying any is refused by <see cref="Rules"/> rather than read wrongly.
    /// </summary>
    public static class CoreFixCore
    {
        /// <summary>fixCoreEnabled(ctx): an expanded fight whose rules (or profile) say FixCore version 1 and are enabled.</summary>
        public static bool Enabled(CombatState c)
        {
            if (!c.IsCore) return false;
            if (Js.D(c.Fields[CK.CombatExpansionVersion]) != c.CoreData.ExpandedVersion) return false;
            var version = c.CoreData.FixCoreVersion;
            var snapshot = c.Fields.Obj(CK.FixCoreRules);
            var effects = c.CoreData.Module(CK.EffectsModule);
            var on = (snapshot != null && snapshot.Num(CK.Version) == version) || (effects != null && effects.Num(CK.FixCoreVersion) == version);
            return on && Raw(c).Is(CK.Enabled);
        }

        /// <summary>rawRules(ctx): the fight's snapshot, else the profile's pinned rules.</summary>
        public static JObject Raw(CombatState c) => c.Fields.Obj(CK.FixCoreRules) ?? c.CoreData.FixCore;

        /// <summary>fixCoreRules(ctx, entity): the rules as an entity reads them (see the class note).</summary>
        public static JObject Rules(CombatState c, JObject entity = null)
        {
            if (entity != null && (Js.Items(entity[CK.MechanicTags]).GetEnumerator().MoveNext() || Js.Items(entity[CK.MechanicBindings]).GetEnumerator().MoveNext()))
                throw new NotSupportedException(Ashen.Generated.Core2CombatMessages.MechanicsKernelNotPorted);
            return Raw(c);
        }

        /// <summary>One family of the rules (<c>stance</c>, <c>accuracy</c>, <c>perception</c>, <c>statuses</c>, ...).</summary>
        public static JObject Family(CombatState c, string family, JObject entity = null) => Rules(c, entity).Obj(family) ?? new JObject();

        /// <summary>fixCoreWardDisabled(ctx): FixCore is on and the data toggle has switched Ward off.</summary>
        public static bool WardDisabled(CombatState c) => Enabled(c) && Raw(c).Obj(CK.Ward)?[CK.Enabled] is JToken t && t.Type == JTokenType.Boolean && !t.Value<bool>();

        /// <summary>fixCoreStatusDefinition(ctx, id, base): a status row with the FixCore override for that id laid over it.</summary>
        public static JObject StatusDefinition(CombatState c, string id, JObject baseDefinition)
        {
            if (!Enabled(c)) return baseDefinition;
            var overrides = Rules(c).Obj(CK.Statuses)?.Obj(CK.Overrides)?.Obj(id);
            return overrides != null ? Js.Spread(baseDefinition, overrides) : Js.Spread(baseDefinition);
        }
    }
}
