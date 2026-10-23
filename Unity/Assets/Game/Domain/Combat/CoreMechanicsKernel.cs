using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DK = Ashen.Generated.Core2DamageKeys;
using DM = Ashen.Generated.Core2DamageMessages;
using DV = Ashen.Generated.Core2DamageValues;

namespace Ashen.Domain.Combat
{
    /// <summary>A mechanics-kernel refusal: <c>path: reason</c>, the web's <c>fail(path, message)</c>.</summary>
    public sealed class MechanicsException : Exception
    {
        public MechanicsException(string path, string reason) : base(string.Format(CultureInfo.InvariantCulture, DM.PathReason, path, reason)) { }
    }

    /// <summary>One compiled attachment of a mechanic (web model/mechanicsKernel.js compileBindings output row).</summary>
    public sealed class MechanicBinding
    {
        public string Id;
        public string MechanicId;
        public string Tag;
        public string OwnerId;
        public string SourceInstanceId;
        public string Label;
        public JObject Config;
        public JObject Interface;
        public JObject Service;
    }

    /// <summary>
    /// The mechanics-kernel subset a Core 2 fight reads (web model/mechanicsKernel.js, docs/design/11 section 4): <c>compileBindings</c>, <c>queryBinding</c>
    /// for the parameters query, <c>evaluateModifiers</c> with its conditions, value references and stacking policies, and the closed
    /// parameter-schema validation. Data driven: the mechanics are the folders exported to <c>rules/core2Mechanics.json</c> (manifest, model
    /// schema, interface, service, cases); no mechanic, tag or parameter is named in code. The full runtime interpreter (handlers, ops) is not
    /// part of the subset: Core 2's foundation is the legacy adapter, so behaviour lives in the combat engine; <see cref="CoreMechanicsRuntime"/> runs
    /// the small portable-op set the mechanic cases use.
    /// </summary>
    public static class CoreMechanicsKernel
    {

        private static HashSet<string> Set(params string[] items) => new HashSet<string>(items, StringComparer.Ordinal);

        private static readonly HashSet<string> SchemaTypes = Set(DV.TypeObject, DV.TypeArray, DV.TypeNumber, DV.TypeInteger, DV.TypeString, DV.TypeBoolean);
        private static readonly HashSet<string> SchemaFields = Set(DK.Type, DK.Properties, DK.Required, DK.AdditionalProperties, DK.Items, DK.Default, DK.Minimum, DK.Maximum, DK.Enum, DK.Description, DK.Title);
        private static readonly HashSet<string> BindingFields = Set(DK.Tag, DK.Parameters, DK.UpgradeParameters, DK.Enabled);
        private static readonly HashSet<string> ModifierFields = Set(DK.Id, DK.Phase, DK.Subject, DK.When, DK.Select, DK.Operation, DK.Value, DK.Stacking, DK.Priority, DK.After);
        private static readonly HashSet<string> CalculationPhases = Set(DV.PhaseStatDerived, DV.PhaseCostResource, DV.PhaseEffectAmount, DV.PhaseDamageMitigation, DV.PhaseRollAccuracy, DV.PhaseRollPerception);
        private static readonly HashSet<string> ModifierSubjects = Set(DV.SubjectTarget, DV.SubjectActor, DV.SubjectOwner);
        private static readonly HashSet<string> StackingModes = Set(DV.ModeStrongest, DV.ModeAdditive, DV.ModeMultiplicative, DV.ModeExclusive);
        private static readonly HashSet<string> StackingFields = Set(DK.Group, DK.Mode, DK.Selection);
        private static readonly HashSet<string> StackingSelections = Set(DV.SelectionMinimum, DV.SelectionMaximum);
        private static readonly HashSet<string> DamageKinds = Set(DV.KindPhysical, DV.KindMagical);

        private static MechanicsException Fail(string path, string reason) => new MechanicsException(path, reason);

        private static bool Plain(JToken t) => t is JObject;

        // ------------------------------------------------------------------ parameters

        /// <summary>parameterDefaults(schema): the defaults an object schema declares, recursing into object properties.</summary>
        public static JObject ParameterDefaults(JObject schema)
        {
            var result = new JObject();
            var properties = schema?.Obj(DK.Properties);
            if (properties == null) return result;
            foreach (var p in properties.Properties())
            {
                var row = p.Value as JObject;
                if (row == null) continue;
                if (row[DK.Default] != null) result[p.Name] = row[DK.Default].DeepClone();
                else if (row.Str(DK.Type) == DV.TypeObject) result[p.Name] = ParameterDefaults(row);
            }
            return result;
        }

        private static bool TypeIs(JToken value, string type)
        {
            switch (type)
            {
                case DV.TypeString: return Js.IsStr(value);
                case DV.TypeBoolean: return value != null && value.Type == JTokenType.Boolean;
                case DV.TypeNumber: return Js.IsNum(value);
                case DV.TypeObject: return Plain(value);
                default: return false;
            }
        }

        /// <summary>validateParameters(schema, value, path, partial): the supported parameter subset is closed, so a misspelled field is refused.</summary>
        public static JToken ValidateParameters(JObject schema, JToken value, string path = DV.RootParameters, bool partial = false)
        {
            if (schema == null)
            {
                if (value is JObject any && any.Count > 0) throw Fail(path, DM.NoParameters);
                return value;
            }
            var type = schema.Str(DK.Type);
            if (type == DV.TypeObject)
            {
                if (!(value is JObject obj)) throw Fail(path, DM.ExpectedObject);
                var properties = schema.Obj(DK.Properties) ?? new JObject();
                foreach (var p in obj.Properties())
                {
                    if (properties[p.Name] == null) throw Fail(path + DV.PathSeparator + p.Name, DM.UnknownParameter);
                    ValidateParameters((JObject)properties[p.Name], p.Value, path + DV.PathSeparator + p.Name, partial);
                }
                if (!partial)
                    foreach (var required in Js.Items(schema[DK.Required]).Select(Js.Str))
                        if (obj[required] == null) throw Fail(path + DV.PathSeparator + required, DM.RequiredParameter);
            }
            else if (type == DV.TypeArray)
            {
                if (!(value is JArray array)) throw Fail(path, DM.ExpectedArray);
                var index = 0;
                foreach (var item in array)
                    ValidateParameters(schema.Obj(DK.Items), item, string.Format(CultureInfo.InvariantCulture, DM.IndexPath, path, index++), false);
            }
            else if (type == DV.TypeNumber || type == DV.TypeInteger)
            {
                if (!Js.IsFinite(value) || (type == DV.TypeInteger && Math.Floor(Js.D(value)) != Js.D(value)))
                    throw Fail(path, string.Format(CultureInfo.InvariantCulture, DM.ExpectedFinite, type));
                if (schema[DK.Minimum] != null && Js.D(value) < Js.D(schema[DK.Minimum]))
                    throw Fail(path, string.Format(CultureInfo.InvariantCulture, DM.MustBeAtLeast, Js.D(schema[DK.Minimum])));
                if (schema[DK.Maximum] != null && Js.D(value) > Js.D(schema[DK.Maximum]))
                    throw Fail(path, string.Format(CultureInfo.InvariantCulture, DM.MustBeAtMost, Js.D(schema[DK.Maximum])));
            }
            else if (!string.IsNullOrEmpty(type) && !TypeIs(value, type)) throw Fail(path, string.Format(CultureInfo.InvariantCulture, DM.ExpectedType, type));
            if (schema[DK.Enum] is JArray allowed && !allowed.Any(x => JToken.DeepEquals(x, value)))
                throw Fail(path, string.Format(CultureInfo.InvariantCulture, DM.MustBeOneOf, string.Join(DV.ListSeparator, allowed.Select(x => Js.IsStr(x) ? Js.Str(x) : x.ToString(Formatting.None)))));
            return value;
        }

        /// <summary>validateParameterSchema(schema, path): the closed schema subset a parameter declaration may use.</summary>
        public static void ValidateParameterSchema(JObject schema, string path = DV.RootSchema)
        {
            var type = schema?.Str(DK.Type);
            if (!Plain(schema) || !SchemaTypes.Contains(type)) throw Fail(path, DM.UnsupportedSchemaType);
            foreach (var p in schema.Properties())
                if (!SchemaFields.Contains(p.Name)) throw Fail(path + DV.PathSeparator + p.Name, DM.UnsupportedSchemaField);
            var numeric = type == DV.TypeNumber || type == DV.TypeInteger;
            if ((schema[DK.Minimum] != null || schema[DK.Maximum] != null) && !numeric) throw Fail(path, DM.NumericBounds);
            if (schema[DK.Enum] != null && (!(schema[DK.Enum] is JArray list) || list.Count == 0)) throw Fail(path + DV.PathSeparator + DK.Enum, DM.ExpectedNonEmptyArray);
            if (schema[DK.AdditionalProperties] != null && !(schema[DK.AdditionalProperties].Type == JTokenType.Boolean && !schema[DK.AdditionalProperties].Value<bool>()))
                throw Fail(path + DV.PathSeparator + DK.AdditionalProperties, DM.ParametersMustBeClosed);
            if ((schema[DK.Minimum] != null && !Js.IsFinite(schema[DK.Minimum])) || (schema[DK.Maximum] != null && !Js.IsFinite(schema[DK.Maximum]))) throw Fail(path, DM.BoundsFinite);
            if (schema[DK.Minimum] != null && schema[DK.Maximum] != null && Js.D(schema[DK.Minimum]) > Js.D(schema[DK.Maximum])) throw Fail(path, DM.InvertedBounds);
            if (type == DV.TypeObject)
            {
                if (!(schema[DK.Properties] is JObject properties)) throw Fail(path + DV.PathSeparator + DK.Properties, DM.ExpectedObject);
                foreach (var p in properties.Properties())
                    ValidateParameterSchema(p.Value as JObject, path + DV.PathSeparator + DK.Properties + DV.PathSeparator + p.Name);
                foreach (var required in Js.Items(schema[DK.Required]).Select(Js.Str))
                    if (properties[required] == null) throw Fail(path + DV.PathSeparator + DK.Required, string.Format(CultureInfo.InvariantCulture, DM.UnknownParameterNamed, required));
            }
            if (type == DV.TypeArray) ValidateParameterSchema(schema.Obj(DK.Items), path + DV.PathSeparator + DK.Items);
            if (schema[DK.Default] != null) ValidateParameters(schema, schema[DK.Default], path + DV.PathSeparator + DK.Default);
        }

        /// <summary>merge(a, b): a deep copy of a with b laid over it, objects merged key by key.</summary>
        private static JObject Merge(JObject a, JObject b)
        {
            var result = (JObject)a.DeepClone();
            foreach (var p in b.Properties())
                result[p.Name] = p.Value is JObject incoming && result[p.Name] is JObject existing ? Merge(existing, incoming) : p.Value.DeepClone();
            return result;
        }

        // ------------------------------------------------------------------ bindings

        private static JObject Manifest(JObject mechanic) => mechanic.Obj(DK.Mechanic) ?? new JObject();

        /// <summary>compileBindings({ mechanics, attachments, moduleParameters, enabledMechanics }): one binding per active attached behaviour tag, sorted by id.</summary>
        public static List<MechanicBinding> CompileBindings(JObject mechanics, IEnumerable<JObject> attachments, JObject moduleParameters = null, ISet<string> enabledMechanics = null)
        {
            if (mechanics == null) throw Fail(DV.RootMechanics, DM.ExpectedRegistered);
            foreach (var m in mechanics.Properties())
            {
                var schema = Manifest((JObject)m.Value).Obj(DK.Parameters);
                if (schema != null) ValidateParameterSchema(schema, string.Format(CultureInfo.InvariantCulture, DM.MechanicParametersPath, m.Name));
            }
            if (moduleParameters != null)
                foreach (var p in moduleParameters.Properties())
                {
                    if (mechanics[p.Name] == null) throw Fail(DV.ModuleParametersPrefix + p.Name, DM.UnknownMechanic);
                    ValidateParameters(Manifest((JObject)mechanics[p.Name]).Obj(DK.Parameters), p.Value, DV.ModuleParametersPrefix + p.Name, true);
                }
            var tags = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var m in mechanics.Properties())
            {
                var manifest = Manifest((JObject)m.Value);
                var tag = manifest.Str(DK.Tag);
                if (string.IsNullOrEmpty(tag) || manifest.Str(DK.Portability) == DV.PortabilityData) continue;
                if (tags.ContainsKey(tag)) throw Fail(string.Format(CultureInfo.InvariantCulture, DM.MechanicTagPath, m.Name), string.Format(CultureInfo.InvariantCulture, DM.DuplicateTag, tag));
                tags[tag] = (JObject)m.Value;
            }
            var knownTags = new HashSet<string>(mechanics.Properties().Select(m => Manifest((JObject)m.Value).Str(DK.Tag)).Where(t => t != null), StringComparer.Ordinal);
            var families = new HashSet<string>(tags.Keys.Select(t => t.Split(new[] { DV.TagSeparator }, StringSplitOptions.None)[0]), StringComparer.Ordinal);
            var result = new List<MechanicBinding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = -1;
            foreach (var source in attachments ?? Enumerable.Empty<JObject>())
            {
                index++;
                var path = string.Format(CultureInfo.InvariantCulture, DM.AttachmentPath, index);
                foreach (var key in new[] { DK.OwnerId, DK.SourceInstanceId, DK.Kind })
                    if (!Js.IsStr(source[key]) || string.IsNullOrEmpty(Js.Str(source[key]))) throw Fail(path + DV.PathSeparator + key, DM.RequiredString);
                foreach (var tag in Js.Items(source[DK.Tags]).Select(Js.Str))
                    if (families.Contains(tag.Split(new[] { DV.TagSeparator }, StringSplitOptions.None)[0]) && !knownTags.Contains(tag)) throw Fail(path + DV.PathSeparator + DK.Tags, string.Format(CultureInfo.InvariantCulture, DM.UnknownTag, tag));
                foreach (var tag in Js.Items(source[DK.MechanicTags]).Select(Js.Str))
                    if (!tags.ContainsKey(tag)) throw Fail(path + DV.PathSeparator + DK.MechanicTags, string.Format(CultureInfo.InvariantCulture, DM.UnknownTag, tag));
                var explicitRows = new Dictionary<string, JObject>(StringComparer.Ordinal);
                var explicitOrder = new List<string>();
                var rowIndex = -1;
                foreach (var item in Js.Items(source[DK.MechanicBindings]))
                {
                    rowIndex++;
                    var row = (JObject)item;
                    var at = string.Format(CultureInfo.InvariantCulture, DM.BindingPath, path, rowIndex);
                    foreach (var p in row.Properties())
                        if (!BindingFields.Contains(p.Name)) throw Fail(at + DV.PathSeparator + p.Name, DM.UnknownBindingField);
                    var rowTag = row.Str(DK.Tag);
                    if (rowTag == null || !tags.ContainsKey(rowTag)) throw Fail(at + DV.PathSeparator + DK.Tag, string.Format(CultureInfo.InvariantCulture, DM.UnknownTag, rowTag));
                    if (explicitRows.ContainsKey(rowTag)) throw Fail(at + DV.PathSeparator + DK.Tag, string.Format(CultureInfo.InvariantCulture, DM.DuplicateBinding, rowTag));
                    if (row[DK.Enabled] != null && row[DK.Enabled].Type != JTokenType.Boolean) throw Fail(at + DV.PathSeparator + DK.Enabled, DM.ExpectedBoolean);
                    explicitRows[rowTag] = row;
                    explicitOrder.Add(rowTag);
                }
                var active = new List<string>();
                void Add(string tag) { if (!active.Contains(tag)) active.Add(tag); }
                foreach (var tag in Js.Items(source[DK.Tags]).Select(Js.Str).Where(tags.ContainsKey)) Add(tag);
                foreach (var tag in Js.Items(source[DK.MechanicTags]).Select(Js.Str)) Add(tag);
                foreach (var tag in explicitOrder) Add(tag);
                foreach (var tag in active)
                {
                    var mechanic = tags[tag];
                    var manifest = Manifest(mechanic);
                    explicitRows.TryGetValue(tag, out var row);
                    row = row ?? new JObject();
                    if (!Js.Includes(manifest[DK.Attaches], source.Str(DK.Kind))) throw Fail(path + DV.PathSeparator + tag, string.Format(CultureInfo.InvariantCulture, DM.CannotAttach, source.Str(DK.Kind)));
                    var iface = mechanic.Obj(DK.Interface);
                    foreach (var required in Js.Items(iface?[DK.Requires]).Select(Js.Str))
                    {
                        if (mechanics[required] == null) throw Fail(path + DV.PathSeparator + tag, string.Format(CultureInfo.InvariantCulture, DM.RequiresUnknown, required));
                        if (enabledMechanics != null && !enabledMechanics.Contains(required)) throw Fail(path + DV.PathSeparator + tag, string.Format(CultureInfo.InvariantCulture, DM.RequiresDisabled, required));
                    }
                    var schema = manifest.Obj(DK.Parameters);
                    var config = ParameterDefaults(schema);
                    ValidateParameters(schema, config, path + DV.PathSeparator + tag + DV.PathSeparator + DK.Defaults, true);
                    var moduleValues = moduleParameters?.Obj(manifest.Str(DK.Id)) ?? new JObject();
                    foreach (var layer in new[] { (DK.Module, moduleValues), (DK.Parameters, row.Obj(DK.Parameters) ?? new JObject()), (DK.UpgradeParameters, row.Obj(DK.UpgradeParameters) ?? new JObject()) })
                    {
                        ValidateParameters(schema, layer.Item2, path + DV.PathSeparator + tag + DV.PathSeparator + layer.Item1, true);
                        config = Merge(config, layer.Item2);
                    }
                    ValidateParameters(schema, config, path + DV.PathSeparator + tag + DV.PathSeparator + DK.Config);
                    var inactive = (source[DK.Active] != null && source[DK.Active].Type == JTokenType.Boolean && !source[DK.Active].Value<bool>())
                        || (row[DK.Enabled] != null && row[DK.Enabled].Type == JTokenType.Boolean && !row[DK.Enabled].Value<bool>())
                        || (enabledMechanics != null && !enabledMechanics.Contains(manifest.Str(DK.Id)));
                    if (inactive) continue;
                    var id = new JArray(source.Str(DK.OwnerId), source.Str(DK.SourceInstanceId), manifest.Str(DK.Id)).ToString(Formatting.None);
                    if (!seen.Add(id)) throw Fail(path, string.Format(CultureInfo.InvariantCulture, DM.DuplicateAttachment, id));
                    var service = mechanic.Obj(DK.Service) ?? new JObject();
                    ValidateModifiers(Js.Items(service[DK.Modifiers]).OfType<JObject>().Where(x => x[DK.Phase] != null).ToList(), path + DV.PathSeparator + tag + DV.PathSeparator + DK.Modifiers);
                    var behaviors = Js.Items(service[DK.Handlers]).Count() + Js.Items(service[DK.Modifiers]).Count() + (iface?.Obj(DK.Queries)?.Count ?? 0);
                    if (behaviors == 0) throw Fail(path + DV.PathSeparator + tag, DM.NoBehavior);
                    result.Add(new MechanicBinding
                    {
                        Id = id, MechanicId = manifest.Str(DK.Id), Tag = tag, OwnerId = source.Str(DK.OwnerId), SourceInstanceId = source.Str(DK.SourceInstanceId),
                        Label = source.Str(DK.Label) ?? manifest.Str(DK.Label), Config = config, Interface = (JObject)(iface ?? new JObject()).DeepClone(), Service = (JObject)service.DeepClone(),
                    });
                }
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return result;
        }

        // ------------------------------------------------------------------ values and conditions

        /// <summary>resolveValue(node, scope): a <c>$path</c> reads the scope, arrays and objects resolve member by member, an object with an <c>f</c> is a scalar formula.</summary>
        public static JToken ResolveValue(JToken node, IDictionary<string, JToken> scope)
        {
            if (Js.IsStr(node) && Js.Str(node).StartsWith(DV.ReferencePrefix, StringComparison.Ordinal))
            {
                JToken value = null;
                var first = true;
                foreach (var key in Js.Str(node).Substring(DV.ReferencePrefix.Length).Split(new[] { DV.ReferenceSeparator }, StringSplitOptions.None))
                {
                    if (first)
                    {
                        first = false;
                        if (!scope.TryGetValue(key, out value) || value == null) throw Fail(Js.Str(node), DM.UnresolvedReference);
                        continue;
                    }
                    if (!(value is JObject holder) || holder[key] == null) throw Fail(Js.Str(node), DM.UnresolvedReference);
                    value = holder[key];
                }
                return value;
            }
            if (node is JArray array) return new JArray(array.Select(x => ResolveValue(x, scope)));
            if (node is JObject obj)
            {
                var resolved = new JObject();
                foreach (var p in obj.Properties()) resolved[p.Name] = ResolveValue(p.Value, scope);
                if (Js.IsStr(resolved[DK.F]))
                {
                    var scalar = Formulas.EvaluateRaw(resolved, new FormulaContext());
                    if (double.IsNaN(scalar) || double.IsInfinity(scalar)) throw Fail(DV.FormulaPath, DM.NonfiniteScalar);
                    return Js.N(scalar);
                }
                return resolved;
            }
            return node;
        }

        /// <summary>evaluateCondition(node, scope): <c>all</c>, <c>any</c>, <c>not</c>, <c>cmp</c>, <c>hasTag</c> and <c>hasStatus</c>.</summary>
        public static bool EvaluateCondition(JToken node, IDictionary<string, JToken> scope, Func<JToken, JToken> resolve = null)
        {
            resolve = resolve ?? (value => ResolveValue(value, scope));
            if (!(node is JObject cond) || cond.Count != 1) throw Fail(DV.WhenPath, DM.ExpectedOneOperator);
            var p = cond.Properties().First();
            switch (p.Name)
            {
                case DV.CondAll: return Js.Items(p.Value).All(c => EvaluateCondition(c, scope, resolve));
                case DV.CondAny: return Js.Items(p.Value).Any(c => EvaluateCondition(c, scope, resolve));
                case DV.CondNot: return !EvaluateCondition(p.Value, scope, resolve);
                case DV.CondCmp:
                {
                    var parts = ((JArray)p.Value).ToList();
                    var op = Js.Str(parts[0]);
                    var x = resolve(parts[1]);
                    var y = resolve(parts.Last());
                    var equality = op == DV.CmpEq || op == DV.CmpNe;
                    if (!equality && !(Js.IsFinite(x) && Js.IsFinite(y))) throw Fail(DV.CmpPath, DM.CmpNeedsNumbers);
                    switch (op)
                    {
                        case DV.CmpGt: return Js.D(x) > Js.D(y);
                        case DV.CmpGe: return Js.D(x) >= Js.D(y);
                        case DV.CmpLt: return Js.D(x) < Js.D(y);
                        case DV.CmpLe: return Js.D(x) <= Js.D(y);
                        case DV.CmpEq: return JToken.DeepEquals(x, y);
                        case DV.CmpNe: return !JToken.DeepEquals(x, y);
                        default: throw Fail(DV.CmpPath, string.Format(CultureInfo.InvariantCulture, DM.UnknownComparator, op));
                    }
                }
                case DV.CondHasTag:
                case DV.CondHasStatus:
                {
                    var parts = ((JArray)p.Value).ToList();
                    var target = resolve(parts[0]);
                    var id = Js.Str(resolve(parts[1]));
                    var entity = Js.IsStr(target) ? (scope.TryGetValue(DK.Entities, out var all) ? (all as JObject)?.Obj(Js.Str(target)) : null) : target as JObject;
                    if (entity == null) throw Fail(DV.WhenPrefix + p.Name, DM.UnknownEntityReference);
                    if (p.Name == DV.CondHasTag) return Js.Includes(entity[DK.Tags], id);
                    var held = entity.Obj(DK.Statuses)?[id];
                    var stacks = held == null ? 0 : Js.IsNum(held) ? Js.D(held) : Js.Or0((held as JObject)?[DK.Stacks]);
                    return stacks > 0;
                }
                default: throw Fail(DV.WhenPath, DM.UnknownCondition);
            }
        }

        /// <summary>queryBinding(binding, query, params): the interface query's value resolved over the binding's config (a copy; the config is never exposed).</summary>
        public static JToken QueryBinding(MechanicBinding binding, string query = DV.QueryParameters, JObject parameters = null)
        {
            var definition = binding.Interface.Obj(DK.Queries)?.Obj(query);
            if (definition == null) throw Fail(binding.MechanicId + DV.PathSeparator + query, DM.UnknownBindingQuery);
            foreach (var p in (parameters ?? new JObject()).Properties())
                if (definition.Obj(DK.Params)?[p.Name] == null) throw Fail(binding.MechanicId + DV.PathSeparator + query + DV.PathSeparator + p.Name, DM.UnknownQueryParameter);
            var scope = new Dictionary<string, JToken>(StringComparer.Ordinal);
            if (parameters != null) foreach (var p in parameters.Properties()) scope[p.Name] = p.Value;
            scope[DK.Config] = binding.Config;
            return ResolveValue(definition[DK.Value], scope).DeepClone();
        }

        // ------------------------------------------------------------------ modifiers

        /// <summary>validateModifiers(modifiers, path): the closed modifier row shape, unique ids, supported stacking and no dependency cycle.</summary>
        public static void ValidateModifiers(IReadOnlyList<JObject> modifiers, string path = DV.RootModifiers)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < modifiers.Count; i++)
            {
                var row = modifiers[i];
                var at = string.Format(CultureInfo.InvariantCulture, DM.IndexPath, path, i);
                foreach (var p in row.Properties())
                    if (!ModifierFields.Contains(p.Name)) throw Fail(at + DV.PathSeparator + p.Name, DM.UnknownModifierField);
                foreach (var key in new[] { DK.Id, DK.Phase })
                    if (!Js.IsStr(row[key]) || string.IsNullOrEmpty(Js.Str(row[key]))) throw Fail(at + DV.PathSeparator + key, DM.RequiredString);
                if (!CalculationPhases.Contains(row.Str(DK.Phase))) throw Fail(at + DV.PathSeparator + DK.Phase, string.Format(CultureInfo.InvariantCulture, DM.UnknownPhase, row.Str(DK.Phase)));
                if (!ids.Add(row.Str(DK.Id))) throw Fail(at + DV.PathSeparator + DK.Id, string.Format(CultureInfo.InvariantCulture, DM.DuplicateModifier, row.Str(DK.Id)));
                if (!ModifierSubjects.Contains(row.Str(DK.Subject))) throw Fail(at + DV.PathSeparator + DK.Subject, DM.ExpectedSubject);
                var operation = row.Str(DK.Operation);
                if (operation != DV.OperationMultiply && operation != DV.OperationAdd) throw Fail(at + DV.PathSeparator + DK.Operation, string.Format(CultureInfo.InvariantCulture, DM.UnknownOperation, operation));
                var value = row[DK.Value];
                var referenceOrFormula = (Js.IsStr(value) && Js.Str(value).StartsWith(DV.ReferencePrefix, StringComparison.Ordinal)) || (value is JObject f && Js.IsStr(f[DK.F]));
                if (value == null || !(Js.IsFinite(value) || referenceOrFormula)) throw Fail(at + DV.PathSeparator + DK.Value, DM.ExpectedScalarOrReference);
                if (row[DK.Priority] != null && !Js.IsFinite(row[DK.Priority])) throw Fail(at + DV.PathSeparator + DK.Priority, DM.ExpectedFiniteNumber);
                var stacking = row.Obj(DK.Stacking);
                if (stacking == null || !Js.IsStr(stacking[DK.Group]) || string.IsNullOrEmpty(Js.Str(stacking[DK.Group])) || !StackingModes.Contains(stacking.Str(DK.Mode)))
                    throw Fail(at + DV.PathSeparator + DK.Stacking, DM.StackingRequired);
                foreach (var p in stacking.Properties())
                    if (!StackingFields.Contains(p.Name)) throw Fail(at + DV.PathSeparator + DK.Stacking + DV.PathSeparator + p.Name, DM.UnknownStackingField);
                if (stacking[DK.Selection] != null && !StackingSelections.Contains(stacking.Str(DK.Selection)))
                    throw Fail(at + DV.PathSeparator + DK.Stacking + DV.PathSeparator + DK.Selection, DM.ExpectedSelection);
                var mode = stacking.Str(DK.Mode);
                if ((mode == DV.ModeAdditive && operation != DV.OperationAdd) || (mode == DV.ModeMultiplicative && operation != DV.OperationMultiply))
                    throw Fail(at + DV.PathSeparator + DK.Stacking, DM.StackingMismatch);
                if (row[DK.Select] != null)
                {
                    var select = row[DK.Select] as JObject;
                    if (select == null || select.Properties().Any(p => p.Name != DK.DamageKind) || !DamageKinds.Contains(select.Str(DK.DamageKind)))
                        throw Fail(at + DV.PathSeparator + DK.Select, DM.UnknownSelector);
                }
                if (row[DK.After] != null && !(row[DK.After] is JArray after && after.All(Js.IsStr))) throw Fail(at + DV.PathSeparator + DK.After, DM.ExpectedIdArray);
            }
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string id)
            {
                if (visiting.Contains(id)) throw Fail(path, string.Format(CultureInfo.InvariantCulture, DM.DependencyCycleAt, id));
                if (visited.Contains(id)) return;
                visiting.Add(id);
                foreach (var dep in Js.Items(modifiers.FirstOrDefault(x => x.Str(DK.Id) == id)?[DK.After]).Select(Js.Str))
                {
                    if (!ids.Contains(dep)) throw Fail(path, string.Format(CultureInfo.InvariantCulture, DM.UnknownDependency, dep));
                    Visit(dep);
                }
                visiting.Remove(id);
                visited.Add(id);
            }
            foreach (var id in ids) Visit(id);
        }

        private sealed class Candidate
        {
            public JObject Row;
            public double Value;
            public MechanicBinding Binding;
            public string Group => Row.Obj(DK.Stacking).Str(DK.Group);
            public string Mode => Row.Obj(DK.Stacking).Str(DK.Mode);
            public string Selection => Row.Obj(DK.Stacking).Str(DK.Selection);
            public string Operation => Row.Str(DK.Operation);
            public string RowId => Row.Str(DK.Id);
        }

        /// <summary>
        /// evaluateModifiers({ bindings, phase, subjectId, actorId, context, amount, quantity }): the modifiers of the phase that apply, grouped by their
        /// stacking policy, ordered by priority and dependency, and folded over the amount. A quantity keeps full precision and shows the running floor
        /// on each source line. Returns <c>{ requested, amount, unrounded, rounding, sources }</c>.
        /// </summary>
        public static JObject EvaluateModifiers(IReadOnlyList<MechanicBinding> bindings, string phase, string subjectId, string actorId, IDictionary<string, JToken> context, double amount, bool quantity = false)
        {
            if (double.IsNaN(amount) || double.IsInfinity(amount)) throw Fail(DV.AmountPath, DM.ExpectedFiniteNumber);
            context = context ?? new Dictionary<string, JToken>(StringComparer.Ordinal);
            JToken Ctx(string key) => context.TryGetValue(key, out var v) ? v : null;
            var candidates = new List<Candidate>();
            foreach (var binding in bindings)
            {
                var modifiers = Js.Items(binding.Service[DK.Modifiers]).OfType<JObject>().ToList();
                ValidateModifiers(modifiers);
                var scope = new Dictionary<string, JToken>(StringComparer.Ordinal);
                foreach (var p in context) scope[p.Key] = p.Value;
                scope[DK.Config] = binding.Config;
                scope[DK.Owner] = (Ctx(DK.Entities) as JObject)?[binding.OwnerId] ?? Ctx(DK.Owner);
                foreach (var row in modifiers)
                {
                    var subject = row.Str(DK.Subject);
                    if (row.Str(DK.Phase) != phase) continue;
                    if (subject == DV.SubjectTarget && binding.OwnerId != subjectId) continue;
                    if (subject == DV.SubjectActor && binding.OwnerId != actorId) continue;
                    if (subject == DV.SubjectOwner && binding.OwnerId != (Js.Str(Ctx(DK.OwnerId)) ?? subjectId)) continue;
                    if (row.Obj(DK.Select) != null && row.Obj(DK.Select).Properties().Any(p => !JToken.DeepEquals(Ctx(p.Name), p.Value))) continue;
                    if (row[DK.When] != null && !EvaluateCondition(row[DK.When], scope)) continue;
                    var value = ResolveValue(row[DK.Value], scope);
                    if (!Js.IsFinite(value)) throw Fail(binding.Id + DV.PathSeparator + row.Str(DK.Id), DM.ExpectedFiniteScalar);
                    candidates.Add(new Candidate { Row = row, Value = Js.D(value), Binding = binding });
                }
            }
            var groups = new List<(string Group, List<Candidate> Rows)>();
            List<Candidate> GroupRows(string group) { foreach (var g in groups) if (g.Group == group) return g.Rows; return null; }
            foreach (var row in candidates.OrderBy(c => c.Binding.Id, StringComparer.Ordinal).ThenBy(c => c.RowId, StringComparer.Ordinal).ToList())
            {
                var previous = GroupRows(row.Group);
                if (previous != null && (previous[0].Mode != row.Mode || previous[0].Operation != row.Operation || previous[0].Selection != row.Selection))
                    throw Fail(row.Group, DM.ConflictingPolicy);
                if (previous == null) groups.Add((row.Group, new List<Candidate> { row }));
                else if (row.Mode == DV.ModeExclusive) throw Fail(row.Group, DM.ConflictingExclusive);
                else if (row.Mode == DV.ModeStrongest)
                {
                    var minimum = row.Selection != null ? row.Selection == DV.SelectionMinimum : row.Operation == DV.OperationMultiply;
                    if (minimum ? row.Value < previous[0].Value : row.Value > previous[0].Value)
                    {
                        previous.Clear();
                        previous.Add(row);
                    }
                }
                else previous.Add(row);
            }
            var pending = groups.SelectMany(g => g.Rows).ToList();
            pending = pending.OrderBy(c => c.Row[DK.Priority] != null ? Js.D(c.Row[DK.Priority]) : 0).ThenBy(c => c.Group, StringComparer.Ordinal)
                .ThenBy(c => c.Binding.Id, StringComparer.Ordinal).ThenBy(c => c.RowId, StringComparer.Ordinal).ToList();
            var ordered = new List<Candidate>();
            var done = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                var found = pending.FindIndex(row => Js.Items(row.Row[DK.After]).Select(Js.Str).All(dep =>
                    done.Contains(row.Binding.Id + DV.DoneSeparator + dep) || !pending.Any(x => x.Binding.Id == row.Binding.Id && x.RowId == dep)));
                if (found < 0) throw Fail(DV.RootModifiers, DM.DependencyCycle);
                var next = pending[found];
                pending.RemoveAt(found);
                ordered.Add(next);
                done.Add(next.Binding.Id + DV.DoneSeparator + next.RowId);
            }
            var running = amount;
            double Shown(double number) => quantity ? Math.Max(0, Math.Floor(number)) : number;
            var sources = new JArray();
            foreach (var row in ordered)
            {
                var before = running;
                running = row.Operation == DV.OperationMultiply ? running * row.Value : running + row.Value;
                if (double.IsNaN(running) || double.IsInfinity(running)) throw Fail(row.RowId, DM.NonfiniteResult);
                var requested = Shown(before);
                var effective = Shown(running);
                sources.Add(Js.Obj(
                    DK.Id, row.Binding.Id, DK.ModifierId, row.RowId, DK.MechanicId, row.Binding.MechanicId, DK.Tag, row.Binding.Tag,
                    DK.OwnerId, row.Binding.OwnerId, DK.SourceInstanceId, row.Binding.SourceInstanceId, DK.Label, row.Binding.Label,
                    DK.Group, row.Group, DK.Stage, phase, DK.Operation, row.Operation,
                    DK.Multiplier, row.Operation == DV.OperationMultiply ? (object)row.Value : null,
                    DK.Value, row.Value, DK.Requested, requested, DK.Effective, effective, DK.Amount, effective - requested));
            }
            var final = Shown(running);
            return Js.Obj(DK.Requested, amount, DK.Amount, final, DK.Unrounded, running, DK.Rounding, quantity ? 0 : final - running, DK.Sources, sources);
        }
    }
}
