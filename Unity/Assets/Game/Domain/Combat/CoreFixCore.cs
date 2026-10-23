using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using DK = Ashen.Generated.Core2DamageKeys;
using DM = Ashen.Generated.Core2DamageMessages;
using DV = Ashen.Generated.Core2DamageValues;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The FixCore switch and rule families a Core 2 fight reads (web model/fixCoreMechanics.js fixCoreEnabled, fixCoreRules, fixCoreAttachments,
    /// protectionBindings, protectionReceipt, fixCoreWardDisabled). The family a fight reads is the snapshot it carries (<c>rules/fixCoreSnapshot.json</c>)
    /// resolved through the mechanics kernel (<see cref="CoreMechanicsKernel"/>): the run attaches every family at once, and an entity, a mount, a
    /// status or a card may attach one of its own (<c>mechanicTags</c>, <c>mechanicBindings</c>), which replaces that family for the reads of that
    /// entity or card. The default profile attaches none, so the result is the snapshot with the mechanics' schema defaults under it.
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

        private static JObject Section(CombatState c) => c.CoreData.Section(DK.SectionMechanics);

        /// <summary>The mechanics a fight registers (the combat families and the protections), by slug.</summary>
        public static JObject Registered(CombatState c)
        {
            var data = c.CoreData;
            if (data.RegisteredMechanics != null) return data.RegisteredMechanics;
            var registered = new JObject();
            foreach (var slug in Js.Items(Section(c)[DK.Registered]).Select(Js.Str))
                if (data.Mechanics[slug] != null) registered[slug] = data.Mechanics[slug];
            return data.RegisteredMechanics = registered;
        }

        /// <summary>fixCoreModuleParameters(rules): the protection parameters and each combat family's rules as the module parameters of its mechanic.</summary>
        public static JObject ModuleParameters(CombatState c)
        {
            var raw = Raw(c);
            var section = Section(c);
            var result = new JObject();
            if (raw.Obj(DK.ProtectionParameters) != null) foreach (var p in raw.Obj(DK.ProtectionParameters).Properties()) result[p.Name] = p.Value.DeepClone();
            foreach (var family in Js.Items(section[DK.Families]).Select(Js.Str))
            {
                var value = family == section.Str(DK.ActionCostsFamily)
                    ? new JObject { [section.Str(DK.ActionCostsKey)] = raw[section.Str(DK.ActionCostsKey)]?.DeepClone() }
                    : raw[family]?.DeepClone();
                result[section.Str(DK.FamilyPrefix) + family] = value;
            }
            return result;
        }

        private static JArray DefaultTags(CombatState c)
        {
            var raw = Raw(c);
            var tags = raw.Arr(DK.DefaultTags) ?? new JArray();
            return WardEnabled(raw) ? tags : new JArray(tags.Where(t => Js.Str(t) != Section(c).Str(DK.WardTag)));
        }

        /// <summary>fixCoreWardEnabled(rules): Ward is on unless the rules switch it off.</summary>
        public static bool WardEnabled(JObject rules) => !(rules.Obj(CK.Ward)?[CK.Enabled] is JToken t && t.Type == JTokenType.Boolean && !t.Value<bool>());

        /// <summary>
        /// fixCoreAttachments(ctx, entity, carrier): the active sources of an entity: its own row, the worn mounts, the current statuses and the
        /// card in play. Inventory is never consulted.
        /// </summary>
        public static List<JObject> Attachments(CombatState c, JObject entity, JObject carrier = null)
        {
            var rows = new List<JObject>();
            if (entity == null) return rows;
            var ownerId = entity.Str(K.Id);
            var definition = entity.Str(K.Kind) == V.Enemy ? c.Data.Enemies.Get(entity.Str(K.EnemyId)) : null;
            var tags = entity[DK.MechanicTags] ?? definition?[DK.MechanicTags] ?? DefaultTags(c);
            var bindings = entity[DK.MechanicBindings] ?? definition?[DK.MechanicBindings] ?? new JArray();
            rows.Add(new JObject { [DK.OwnerId] = ownerId, [DK.SourceInstanceId] = ownerId, [DK.Kind] = DK.KindEntity, [DK.Tags] = tags.DeepClone(), [DK.MechanicBindings] = bindings.DeepClone() });
            var mounts = Properties.MountsOf(c, entity);
            if (mounts != null)
                foreach (var mount in mounts.Entries())
                {
                    var kinds = Section(c).Obj(DK.MountKinds);
                    rows.Add(new JObject
                    {
                        [DK.OwnerId] = ownerId, [DK.SourceInstanceId] = mount.Key,
                        [DK.Kind] = kinds?[mount.Value.Kind] != null ? kinds[mount.Value.Kind].DeepClone() : Js.S(mount.Value.Kind),
                        [DK.Tags] = new JArray(mount.Value.ScopeTags), [DK.MechanicBindings] = new JArray(),
                    });
                }
            var statuses = entity.Obj(K.Statuses);
            if (statuses != null)
                foreach (var s in statuses.Properties())
                {
                    var instance = s.Value as JObject;
                    if (!(Js.Or0(instance?[K.Stacks]) > 0 || Js.Or0(instance?.Obj(DK.Meter)?[K.Value]) > 0)) continue;
                    var def = c.Data.Statuses.Get(s.Name);
                    if (Js.Truthy(def?[DK.MechanicTags]) || Js.Truthy(def?[DK.MechanicBindings]))
                        rows.Add(new JObject { [DK.OwnerId] = ownerId, [DK.SourceInstanceId] = DK.StatusPrefix + s.Name, [DK.Kind] = DK.KindStatus,
                            [DK.Tags] = def[DK.MechanicTags]?.DeepClone() ?? new JArray(), [DK.MechanicBindings] = def[DK.MechanicBindings]?.DeepClone() ?? new JArray() });
                }
            if (carrier != null && (carrier[DK.MechanicTags] != null || carrier[DK.MechanicBindings] != null))
            {
                var id = Js.Truthy(carrier[K.InstanceId]) ? carrier[K.InstanceId] : Js.Truthy(carrier[K.CardId]) ? carrier[K.CardId] : Js.Truthy(carrier[K.MoveId]) ? carrier[K.MoveId] : Js.S(DK.ActiveCard);
                rows.Add(new JObject { [DK.OwnerId] = ownerId, [DK.SourceInstanceId] = DK.CardPrefix + Js.Str(id), [DK.Kind] = DK.KindCard,
                    [DK.Tags] = carrier[DK.MechanicTags]?.DeepClone() ?? new JArray(), [DK.MechanicBindings] = carrier[DK.MechanicBindings]?.DeepClone() ?? new JArray() });
            }
            return rows;
        }

        private static string CacheKey(JObject moduleParameters, IEnumerable<JObject> attachments) =>
            moduleParameters.ToString(Formatting.None) + DK.CacheSeparator + new JArray(attachments).ToString(Formatting.None);

        private static List<MechanicBinding> Compile(CombatState c, IEnumerable<JObject> attachments)
        {
            var list = attachments.ToList();
            var moduleParameters = ModuleParameters(c);
            var key = CacheKey(moduleParameters, list);
            if (c.CoreData.BindingCache.TryGetValue(key, out var cached)) return cached;
            var compiled = CoreMechanicsKernel.CompileBindings(Registered(c), list, moduleParameters);
            return c.CoreData.BindingCache[key] = compiled;
        }

        /// <summary>The rule row the run attaches for every combat family at once (owner <c>rules</c>).</summary>
        private static JObject RulesAttachment(CombatState c)
        {
            var section = Section(c);
            return new JObject
            {
                [DK.OwnerId] = section.Str(DK.RulesOwner), [DK.SourceInstanceId] = section.Str(DK.RulesOwner), [DK.Kind] = DK.KindRun,
                [DK.Tags] = new JArray(Js.Items(section[DK.Families]).Select(f => section.Str(DK.FamilyTagPrefix) + Js.Str(f))),
            };
        }

        /// <summary>
        /// fixCoreRules(ctx, entity, carrier): the rules as an entity reads them. Each family is queried from the one binding the entity or card
        /// attaches (two are refused), else from the run's.
        /// </summary>
        public static JObject Rules(CombatState c, JObject entity = null, JObject carrier = null)
        {
            var raw = Raw(c);
            var section = Section(c);
            var attachments = new List<JObject> { RulesAttachment(c) };
            attachments.AddRange(Attachments(c, entity, carrier));
            var bindings = Compile(c, attachments);
            var result = (JObject)raw.DeepClone();
            foreach (var family in Js.Items(section[DK.Families]).Select(Js.Str))
            {
                var mechanicId = section.Str(DK.FamilyPrefix) + family;
                var candidates = bindings.Where(b => b.MechanicId == mechanicId).ToList();
                var attached = candidates.Where(b => b.OwnerId != section.Str(DK.RulesOwner)).ToList();
                if (attached.Count > 1)
                    throw new MechanicsException(string.Format(System.Globalization.CultureInfo.InvariantCulture, DM.ConflictingAttachments, family), string.Join(DV.ListSeparator, attached.Select(b => b.SourceInstanceId)));
                var value = (JObject)CoreMechanicsKernel.QueryBinding(attached.FirstOrDefault() ?? candidates[0], DV.QueryParameters);
                if (family == section.Str(DK.ActionCostsFamily)) result[section.Str(DK.ActionCostsKey)] = value[section.Str(DK.ActionCostsKey)]?.DeepClone();
                else result[family] = value;
            }
            return result;
        }

        /// <summary>One family of the rules (<c>stance</c>, <c>accuracy</c>, <c>perception</c>, <c>statuses</c>, ...).</summary>
        public static JObject Family(CombatState c, string family, JObject entity = null, JObject carrier = null) => Rules(c, entity, carrier).Obj(family) ?? new JObject();

        /// <summary>fixCoreWardDisabled(ctx): FixCore is on and the data toggle has switched Ward off.</summary>
        public static bool WardDisabled(CombatState c) => Enabled(c) && !WardEnabled(Raw(c));

        /// <summary>fixCoreStatusDefinition(ctx, id, base): a status row with the FixCore override for that id laid over it.</summary>
        public static JObject StatusDefinition(CombatState c, string id, JObject baseDefinition)
        {
            if (!Enabled(c)) return baseDefinition;
            var overrides = Rules(c).Obj(CK.Statuses)?.Obj(CK.Overrides)?.Obj(id);
            return overrides != null ? Js.Spread(baseDefinition, overrides) : Js.Spread(baseDefinition);
        }

        // ------------------------------------------------------------------ the Staggered, Smash and Poise rules

        private static JObject WithDefaults(JObject defaults, JObject over) => Js.Spread(defaults, over);

        /// <summary>fixCoreStaggeredRules(rules): the Staggered stance's costs, the shipped defaults under the snapshot's block.</summary>
        public static JObject StaggeredRules(CombatState c) => WithDefaults(c.CoreData.Section(DK.SectionDamage).Obj(DK.Staggered), Raw(c).Obj(DK.Staggered));

        /// <summary>fixCoreSmashVsDefensiveRules(rules): the Smash-against-a-defensive-stance numbers, the shipped defaults under the given block.</summary>
        public static JObject SmashVsDefensiveRules(CombatState c, JObject rules) => WithDefaults(c.CoreData.Section(DK.SectionDamage).Obj(DK.SmashVsDefensive), rules.Obj(DK.SmashVsDefensive));

        // ------------------------------------------------------------------ protection

        /// <summary>protectionBindings(ctx, entity): the protections the entity attaches (Poise, Ward) compiled with the module parameters.</summary>
        public static List<MechanicBinding> ProtectionBindings(CombatState c, JObject entity, JObject carrier = null) => Compile(c, Attachments(c, entity, carrier));

        /// <summary>
        /// protectionReceipt(ctx, entity, kind, amount): the share of a hit the entity's protection takes off. Poise cannot absorb more than it holds
        /// (the rest of the hit gets through). Returns <c>{ requested, amount, unrounded, rounding, sources, reduced }</c>.
        /// </summary>
        public static JObject ProtectionReceipt(CombatState c, JObject entity, string kind, double amount)
        {
            // Detached preview targets may omit an inactive pool: the read model gets an empty one; a live entity is read as it is.
            var owner = entity;
            if (entity != null && (entity[CK.PersistentWard] == null || entity[CK.PersistentPoise] == null))
            {
                owner = Js.Spread(entity);
                if (owner[CK.PersistentWard] == null) owner[CK.PersistentWard] = Js.Obj(K.Value, 0, CK.Max, 0);
                if (owner[CK.PersistentPoise] == null) owner[CK.PersistentPoise] = Js.Obj(K.Value, 0, CK.Max, 0);
            }
            var context = new Dictionary<string, JToken> { [DK.Owner] = owner, [DK.DamageKind] = new JValue(kind) };
            var bindings = Enabled(c) && amount > 0 ? ProtectionBindings(c, entity) : new List<MechanicBinding>();
            var receipt = CoreMechanicsKernel.EvaluateModifiers(bindings, DV.PhaseDamageMitigation, entity?.Str(K.Id), null, context, amount, true);
            var sources = (JArray)receipt[DK.Sources];
            if (Js.Truthy(receipt[DK.Rounding]))
                sources.Add(Js.Obj(DK.Id, DV.SourceRounding, DK.Label, DV.LabelRounding, DK.Stage, DV.StageRounding,
                    DK.Operation, DV.OperationAdd, DK.Amount, receipt[DK.Rounding]));
            var held = kind == DV.KindPhysical && owner?.Obj(CK.PersistentPoise) != null ? Math.Max(0, owner.Obj(CK.PersistentPoise).Num(K.Value)) : double.PositiveInfinity;
            var reduced = Math.Max(0, amount - receipt.Num(DK.Amount));
            var taken = receipt.Num(DK.Amount);
            if (reduced > held)
            {
                sources.Add(Js.Obj(DK.Id, DV.SourcePoiseCap, DK.Label, DV.LabelPoiseCap, DK.Stage, DV.StageMitigation,
                    DK.Operation, DV.OperationAdd, DK.Amount, reduced - held));
                taken += reduced - held;
                reduced = held;
            }
            receipt[DK.Amount] = Js.N(taken);
            receipt[DK.Reduced] = Js.N(reduced);
            return receipt;
        }
    }
}
