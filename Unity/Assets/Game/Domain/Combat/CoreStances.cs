using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// What a card or an enemy move is in combat terms, and the stance it puts its owner in (web model/combatCardProfile.js
    /// combatProfileFor, model/cardClassification.js cardPreparationCategory, model/stanceRules.js stanceOf, sameStance and
    /// stanceEntryBonuses, model/profileStance.js configuredStance). The profile and the stance are read from a carrier's tags: the
    /// card names and damage types never choose a camp (D-165 of the web's contract). Enemy moves are the carriers WP-4 needs; the
    /// player's cards arrive with WP-5 through the same functions.
    /// </summary>
    public static class CoreStances
    {
        // ------------------------------------------------------------------ tags

        /// <summary>The tag ids of a carrier's <c>cardTags ?? tags</c> list (a tag is its id or a row with one); null when it has none.</summary>
        public static List<string> TagIds(JObject def)
        {
            var tags = !Js.Nullish(def?[CK.CardTags]) ? def[CK.CardTags] : def?[K.Tags];
            if (!(tags is JArray array)) return null;
            return array.Select(t => Js.IsStr(t) ? Js.Str(t) : Js.Str(Js.Get(t, K.Id))).ToList();
        }

        private static string Identity(List<string> tags, string prefix, IReadOnlyList<string> allowed, string name)
        {
            var values = tags.Where(t => t != null && t.StartsWith(prefix, StringComparison.Ordinal)).Select(t => t.Substring(prefix.Length)).Distinct().ToList();
            if (values.Count > 1) throw new InvalidOperationException(string.Format(System.Globalization.CultureInfo.InvariantCulture, CM.ConflictingTags, name, prefix));
            if (values.Count > 0 && allowed != null && !allowed.Contains(values[0]))
                throw new InvalidOperationException(string.Format(System.Globalization.CultureInfo.InvariantCulture, CM.UnknownTag, name, prefix, values[0]));
            return values.Count > 0 && values[0].Length > 0 ? values[0] : null;
        }

        private static List<string> Vocabulary(CombatState c, string key) => Js.Items(c.CoreData.Section(CK.Vocabulary)[key]).Select(Js.Str).ToList();

        // ------------------------------------------------------------------ combat profile

        /// <summary>combatProfileFor(carrier): camp, maneuver, school, damage type and counter mode from the tags, then the authored payload fields.</summary>
        public static JObject ProfileFor(CombatState c, JObject carrier)
        {
            carrier ??= new JObject();
            var id = Js.Truthy(carrier[K.MoveId]) ? carrier.Str(K.MoveId) : Js.Truthy(carrier[K.CardId]) ? carrier.Str(K.CardId) : carrier.Str(K.Id) ?? string.Empty;
            var tags = TagIds(carrier) ?? new List<string>();
            var camp = Identity(tags, CV.TagCamp, Vocabulary(c, CK.Camps), id);
            var maneuver = Identity(tags, CV.TagManeuver, Vocabulary(c, CK.Maneuvers), id);
            var school = Identity(tags, CV.TagSchool, Vocabulary(c, CK.Schools), id);
            var counterMode = Identity(tags, CV.TagCounter, Vocabulary(c, CK.CounterModes), id);
            var attack = carrier.Obj(K.Attack);
            var components = attack?[CK.Components] as JArray;
            var types = (components != null
                    ? components.Select(x => Js.Str(Js.Get(x, K.Type)))
                    : Js.Truthy(attack?[CK.DamageType]) ? new[] { attack.Str(CK.DamageType) }
                    : tags.Where(t => t != null && t.StartsWith(CV.TagDamage, StringComparison.Ordinal)).Select(t => t.Substring(CV.TagDamage.Length)))
                .Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
            var profile = new JObject
            {
                [CK.Camp] = Js.S(camp),
                [CK.Maneuver] = Js.S(maneuver),
                [CK.School] = Js.S(school),
                [CK.DamageType] = types.Count == 1 ? Js.S(types[0]) : Js.Null(),
                [CK.CounterMode] = Js.S(counterMode),
            };
            if (components != null && types.Count > 1)
            {
                profile[CK.DamageType] = Js.S(DominantDamageType(components));
                profile[CK.DamageComponents] = new JArray(components.Select(x => Js.Obj(K.Type, Js.Get(x, K.Type), CK.Weight, Js.Get(x, CK.Weight))));
            }
            var rangeTags = tags.Select(t => t != null && t.StartsWith(CV.TagReach, StringComparison.Ordinal) ? CV.TagRange + NormalizeRange(c, t.Substring(CV.TagReach.Length)) : t).ToList();
            var range = Identity(rangeTags, CV.TagRange, Vocabulary(c, CK.Ranges), id);
            var targeting = Identity(tags, CV.TagTargeting, Vocabulary(c, CK.Targetings), id);
            if (range != null) profile[CK.Range] = range;
            if (targeting != null) profile[CK.Targeting] = targeting;
            var traits = tags.Where(t => t != null && t.StartsWith(CV.TagTrait, StringComparison.Ordinal)).Select(t => t.Substring(CV.TagTrait.Length)).Distinct().ToList();
            if (traits.Count > 0) profile[CK.Traits] = new JArray(traits);
            if (Js.IsStr(carrier[CK.Reach]) && carrier[CK.Range] == null && Vocabulary(c, CK.Ranges).Contains(NormalizeRange(c, carrier.Str(CK.Reach))))
                profile[CK.Range] = NormalizeRange(c, carrier.Str(CK.Reach));
            foreach (var key in Js.Items(c.CoreData.Section(CK.Vocabulary)[CK.ProfileFields]).Select(Js.Str))
                if (carrier[key] != null) profile[key] = carrier[key].DeepClone();
            return profile;
        }

        private static string NormalizeRange(CombatState c, string value)
        {
            var legacy = c.CoreData.Section(CK.Vocabulary).Obj(CK.LegacyRanges);
            return legacy != null && legacy[value] != null ? legacy.Str(value) : value;
        }

        private static string DominantDamageType(JArray components)
        {
            JToken best = null;
            foreach (var x in components) if (best == null || Js.D(Js.Get(x, CK.Weight)) > Js.D(Js.Get(best, CK.Weight))) best = x;
            return Js.Str(Js.Get(best, K.Type));
        }

        /// <summary>isSpellCard: the spell camp, nothing else.</summary>
        public static bool IsSpellCard(JObject card)
        {
            if (card == null) return false;
            var tags = !Js.Nullish(card[K.Tags]) ? card[K.Tags] : card[CK.CardTags];
            return card.Obj(CK.CombatProfile)?.Str(CK.Camp) == CV.CampSpell || (tags is JArray && Js.Includes(tags, CV.TagCampSpell));
        }

        // ------------------------------------------------------------------ preparation (a spell or a skill prepares rather than strikes)

        private static JObject ProfileOf(CombatState c, JObject def)
        {
            if (def.Obj(CK.CombatProfile) != null && Js.Truthy(def[CK.CombatProfile])) return def.Obj(CK.CombatProfile);
            if (Js.Truthy(def[CK.Camp])) return def;
            var ids = TagIds(def);
            if (ids == null) return ProfileFor(c, def);
            var copy = (JObject)def.DeepClone();
            copy[CK.CardTags] = new JArray(ids);
            return ProfileFor(c, copy);
        }

        private static string MartialStanceType(CombatState c, JObject def, JObject profile)
        {
            if (!Js.Nullish(def[CK.Evade])) return CV.StanceEvade;
            var maneuver = profile.Str(CK.Maneuver);
            var mapped = maneuver != null ? c.CoreData.Section(CK.Vocabulary).Obj(CK.MartialStances)?.Str(maneuver) : null;
            if (mapped != null) return mapped;
            return def.Str(K.Type) == CV.TypeAttack
                || ((maneuver == CV.ManeuverCasting || maneuver == CV.ManeuverRanged)
                    && Js.Items(def[K.Effects]).Any(e => Js.Str(Js.Get(e, K.Op)) == CV.OpDamage)) ? CV.StanceAttack : null;
        }

        /// <summary>cardPreparationCategory(def): 'power', 'spell', 'skill' or null (a martial card or an enemy move that strikes).</summary>
        public static string PreparationCategory(CombatState c, JObject def)
        {
            var type = def.Str(K.Type);
            var cfg = c.CoreData.Section(CK.Classification);
            if (Js.Includes(cfg[CK.NonPreparingTypes], type) || Js.Includes(cfg[CK.NonPreparingKinds], def.Str(K.Kind))) return null;
            var profile = ProfileOf(c, def);
            var tags = TagIds(def) ?? new List<string>();
            var kinds = Js.Items(def[CK.KindIds]).Select(Js.Str).Concat(tags).ToList();
            if (type == CV.TypePower || kinds.Contains(CV.KindPower)) return CV.TypePower;
            var explicitSpell = kinds.Contains(CV.KindSpell) || tags.Contains(CV.TagSourceSpell) || def.Str(CK.AbilityKind) == CV.TypeSpell || type == CV.TypeSpell;
            var cardKind = Js.Includes(cfg[CK.CardKindTypes], type) || kinds.Any(kind => Js.Includes(cfg[CK.CardKindClassifications], kind));
            if (explicitSpell || (profile.Str(CK.Camp) == CV.CampSpell && cardKind)) return CV.TypeSpell;
            var skill = type == CV.TypeSkill || kinds.Contains(CV.KindSkill);
            if (skill && (profile.Str(CK.Maneuver) == CV.ManeuverCasting || MartialStanceType(c, def, profile) == null)) return CV.TypeSkill;
            return null;
        }

        // ------------------------------------------------------------------ stance

        /// <summary>mirroredManeuver(carrier): the basic card a Spell mirrors, or null.</summary>
        public static string MirroredManeuver(CombatState c, JObject carrier)
        {
            var mirrors = Js.Str(carrier?.Obj(CK.CombatProfile)?[CK.Mirrors] ?? carrier?[CK.Mirrors]);
            var camp = carrier?.Obj(CK.CombatProfile)?[CK.Camp] ?? CV.CampSpell;
            return mirrors != null && Vocabulary(c, CK.MirroredTypes).Contains(mirrors) && Js.Str(camp) == CV.CampSpell ? mirrors : null;
        }

        /// <summary>configuredStance(carrier): the stance id a carrier plays under a configured profile ('prepared' for a cast).</summary>
        public static string ConfiguredStance(CombatState c, JObject carrier)
        {
            var mirrored = MirroredManeuver(c, carrier);
            if (mirrored != null) return mirrored;
            if (PreparationCategory(c, carrier) != null) return CV.StancePrepared;
            var tagIds = (TagIds(carrier) ?? new List<string>()).FirstOrDefault(t => t != null && t.StartsWith(CV.TagManeuver, StringComparison.Ordinal));
            var raw = Js.Str(Js.Get(carrier[CK.StanceTrigger], CK.Stance)) ?? Js.Str(carrier.Obj(CK.CombatProfile)?[CK.Maneuver]) ?? Js.Str(carrier[CK.Maneuver])
                ?? (tagIds != null ? tagIds.Substring(CV.TagManeuver.Length) : null);
            var aliases = c.CoreData.Section(CK.Vocabulary).Obj(CK.ManeuverAliases);
            if (raw != null && aliases != null && aliases[raw] != null) return aliases.Str(raw);
            return raw ?? CV.StanceNeutral;
        }

        /// <summary>stanceOf(carrier, entity): the stance a carrier (a card, a move or a stance record) puts its owner in, or null.</summary>
        public static JObject StanceOf(CombatState c, JObject carrier, JObject entity = null)
        {
            if (carrier == null) return null;
            var profile = carrier.Obj(CK.CombatProfile) != null && Js.Truthy(carrier[CK.CombatProfile]) ? carrier.Obj(CK.CombatProfile)
                : Js.Truthy(carrier[CK.Camp]) ? carrier : ProfileFor(c, Normalized(carrier));
            if (profile[CK.StanceTrigger] != null && profile[CK.StanceTrigger].Type == JTokenType.Boolean && !profile[CK.StanceTrigger].Value<bool>()) return null;
            var preparation = PreparationCategory(c, carrier);
            if (preparation == null && !Js.Truthy(profile[CK.Maneuver])) return null;
            var camp = Vocabulary(c, CK.SpellCamps).Contains(profile.Str(CK.Camp)) ? CV.CampSpell : CV.CampPhysical;
            var mirrorTypes = Vocabulary(c, CK.MirroredTypes);
            var mirrored = camp == CV.CampSpell && ((profile.Str(CK.Mirrors) != null && mirrorTypes.Contains(profile.Str(CK.Mirrors)))
                || (profile[CK.Mirrored] != null && profile[CK.Mirrored].Type == JTokenType.Boolean && profile[CK.Mirrored].Value<bool>() && mirrorTypes.Contains(profile.Str(CK.Maneuver) ?? string.Empty)));
            string maneuver;
            if (mirrored) maneuver = Js.Truthy(profile[CK.Mirrors]) ? profile.Str(CK.Mirrors) : profile.Str(CK.Maneuver);
            else if (preparation != null) maneuver = CV.StanceCasting;
            else maneuver = Js.Str(Js.Get(carrier[CK.StanceTrigger], CK.Stance)) is string s1 && s1.Length > 0 ? s1
                : Js.Str(Js.Get(profile[CK.StanceTrigger], CK.Stance)) is string s2 && s2.Length > 0 ? s2 : profile.Str(CK.Maneuver);
            if (maneuver == CV.StanceDefense || maneuver == CV.StanceBlock) maneuver = CV.StanceDefend;
            if (maneuver == CV.StanceSpell || maneuver == CV.StanceCast || maneuver == CV.StancePrepared) maneuver = CV.StanceCasting;
            var evadeRow = profile.Obj(CK.Evade);
            var eligible = profile[CK.EvadeEligible];
            var evade = evadeRow != null && !(eligible != null && eligible.Type == JTokenType.Boolean && !eligible.Value<bool>())
                && (!Js.Truthy(evadeRow[CK.WhileStatus]) || (eligible != null && eligible.Type == JTokenType.Boolean && eligible.Value<bool>())
                    || entity?.Obj(K.Statuses)?.Obj(evadeRow.Str(CK.WhileStatus) ?? string.Empty)?.Num(K.Stacks) > 0);
            if (preparation == null && ((evade && (maneuver == CV.StanceDefend || maneuver == CV.StanceCounter)) || maneuver == CV.StanceEvade)) maneuver = CV.StanceEvade;
            if (camp == CV.CampSpell && !mirrored && maneuver != CV.StanceDefend && maneuver != CV.StanceCounter && maneuver != CV.StanceEvade
                && (Js.Truthy(profile[CK.Range]) ? profile.Str(CK.Range) : (Js.Truthy(profile[CK.Projectile]) || maneuver == CV.StanceRanged ? CV.RangeRanged : CV.RangeMelee)) != CV.RangeMelee)
                maneuver = CV.StanceCasting;
            if (maneuver == CV.StanceRanged) maneuver = CV.StanceAttack;
            var counterStance = maneuver == CV.StanceEvade && (profile.Str(CK.Maneuver) == CV.StanceCounter
                || (profile[CK.CounterStance] != null && profile[CK.CounterStance].Type == JTokenType.Boolean && profile[CK.CounterStance].Value<bool>()));
            var result = new JObject { [CK.Maneuver] = Js.S(maneuver), [CK.Camp] = camp };
            if (mirrored) result[CK.Mirrored] = true;
            if (counterStance) result[CK.CounterStance] = true;
            result[CK.Range] = Js.Truthy(profile[CK.Range]) ? profile[CK.Range].DeepClone() : Js.S(CV.RangeMelee);
            if (Js.Truthy(profile[CK.School])) result[CK.School] = profile[CK.School].DeepClone();
            return result;
        }

        /// <summary>The carrier with its tags normalised to ids (<c>cardTags</c>), as stanceOf hands it to combatProfileFor.</summary>
        private static JObject Normalized(JObject carrier)
        {
            var ids = TagIds(carrier);
            if (ids == null) return carrier;
            var copy = (JObject)carrier.DeepClone();
            copy[CK.CardTags] = new JArray(ids);
            return copy;
        }

        /// <summary>sameStance(entity, carrier): the carrier would leave the entity in a stance it already holds.</summary>
        public static bool SameStance(CombatState c, JObject entity, JObject carrier)
        {
            var active = StanceOf(c, entity?.Obj(CK.CombatStance));
            var next = StanceOf(c, carrier, entity);
            if (next == null) return false;
            var rows = new List<JObject>();
            if (entity?[CK.ConcurrentStances] is JArray concurrent) rows.AddRange(concurrent.OfType<JObject>());
            rows.Add(active);
            return rows.Any(row => row != null && row.Str(CK.Maneuver) == next.Str(CK.Maneuver) && row.Str(CK.Camp) == next.Str(CK.Camp));
        }

        /// <summary>stanceRulesFor(ctx): the defaults of rules/stanceRules.json over the balance's own overrides (the Core 2 balance has none).</summary>
        public static JObject Rules(CombatState c)
        {
            var defaults = c.CoreData.StanceRules;
            var overrides = c.CoreData.Combat.Balance.Obj(CK.StanceRules);
            return Js.Spread(defaults, overrides);
        }

        /// <summary>stanceEntryBonuses(carrier): the Precision, Evade, Block and Barrier a stance entry grants.</summary>
        public static JObject EntryBonuses(CombatState c, JObject carrier)
        {
            var stance = StanceOf(c, carrier);
            if (stance == null) return Js.Obj(CK.Precision, 0, CK.Evade, 0, K.Block, 0, CK.Barrier, 0);
            var cfg = Rules(c);
            var maneuver = stance.Str(CK.Maneuver);
            var offensive = stance[CK.CounterStance] != null || Vocabulary(c, CK.OffensiveManeuvers).Contains(maneuver);
            var defensive = Vocabulary(c, CK.DefensiveManeuvers).Contains(maneuver);
            var amount = defensive ? cfg.Num(CK.EntryProtection) : 0;
            var share = Js.IsNum(cfg.Obj(CK.SchoolBlockShares)?[stance.Str(CK.School) ?? string.Empty]) ? cfg.Obj(CK.SchoolBlockShares).Num(stance.Str(CK.School)) : cfg.Num(CK.SpellBlockShare);
            double block;
            if (stance.Str(CK.Camp) == CV.CampSpell)
                block = CoreFixCore.Enabled(c) ? Math.Floor(amount * share) : Math.Floor(amount * share + Ashen.Generated.CombatMath.Half);
            else block = amount;
            var profile = carrier.Obj(CK.CombatProfile) ?? carrier;
            var eligible = profile[CK.EvadeEligible];
            var auxiliaryEvade = profile.Obj(CK.Evade) != null && !(eligible != null && eligible.Type == JTokenType.Boolean && !eligible.Value<bool>())
                && (!Js.Truthy(profile.Obj(CK.Evade)[CK.WhileStatus]) || (eligible != null && eligible.Type == JTokenType.Boolean && eligible.Value<bool>()));
            return Js.Obj(CK.Precision, offensive ? cfg.Num(CK.OffensivePrecision) : 0, CK.Evade, maneuver == CV.StanceEvade || auxiliaryEvade ? cfg.Num(CK.EvadeStacks) : 0,
                K.Block, block, CK.Barrier, amount - block);
        }

        /// <summary>precisionStatMix(ctx, entity, carrier): the stat mix a stance entry adds to its Precision grant (FixCore).</summary>
        public static double PrecisionStatMix(CombatState c, JObject entity, JObject carrier)
        {
            var weights = CoreFixCore.Family(c, CK.Accuracy, entity).Obj(CK.PrecisionWeights);
            var attributes = entity.Obj(K.Attributes) ?? new JObject();
            double Attr(string id) => Js.Or0(attributes[id]);
            var definition = entity.Str(K.Kind) == V.Enemy && entity.Str(K.EnemyId) != null && c.Data.Enemies.Has(entity.Str(K.EnemyId)) ? c.Data.Enemies.Get(entity.Str(K.EnemyId)) : null;
            var level = entity.Str(K.Kind) == V.Enemy && !Js.Truthy(definition?[CK.LevelProfile]) ? 0
                : Js.IsFinite(entity.Obj(K.Level)?[K.Level]) ? entity.Obj(K.Level).Num(K.Level) : Js.IsFinite(entity[K.Level]) ? entity.Num(K.Level) : 0;
            return Math.Floor(weights.Num(CK.WeightDex) * Attr(CK.AttrDexterity) + weights.Num(CK.WeightStr) * Attr(CK.AttrStrength)
                + weights.Num(CK.WeightWis) * Attr(CK.AttrWisdom) + weights.Num(CK.WeightInt) * Attr(CK.AttrIntelligence) + weights.Num(CK.WeightLevel) * level);
        }

        /// <summary>precisionEntryGrant(ctx, entity, base, carrier): the Precision a stance entry grants under FixCore, base plus the stat mix.</summary>
        public static double PrecisionEntryGrant(CombatState c, JObject entity, double baseGrant, JObject carrier) =>
            baseGrant > 0 ? baseGrant + PrecisionStatMix(c, entity, carrier) : 0;

        /// <summary>
        /// setProfileStance(ctx, entity, carrier): the stance an accepted card or a telegraphed enemy move puts its owner in. A new
        /// stance grants its Precision and Evade; the stance in force is remembered nowhere else (one stance at a time, capacity 1).
        /// </summary>
        public static void SetProfileStance(CombatState c, JObject entity, JObject carrier)
        {
            var fixCore = CoreFixCore.Enabled(c);
            if (fixCore && carrier != null && carrier[CK.FixCoreSameStance] == null) carrier[CK.FixCoreSameStance] = SameStance(c, entity, carrier);
            if (fixCore) entity[CK.MatchingStanceCardMultiplier] = carrier != null && carrier.Is(CK.FixCoreSameStance)
                ? Js.N(CoreFixCore.Family(c, CK.Stance, entity).Num(CK.EffectivenessMultiplier) - 1) : Js.N(0);
            var grants = fixCore && !SameStance(c, entity, carrier) ? EntryBonuses(c, carrier) : null;
            var rule = c.CoreData.Configured(CK.StancesModule);
            var stance = ConfiguredStance(c, carrier);
            var profile = carrier?.Obj(CK.CombatProfile);
            if (rule != null && Js.Includes(rule[CK.Include], stance))
            {
                var next = new JObject
                {
                    [CK.Maneuver] = stance,
                    [CK.Camp] = Js.Truthy(profile?[CK.Camp]) ? profile[CK.Camp].DeepClone() : Js.S(CV.CampPhysical),
                    [CK.Range] = Js.Truthy(profile?[CK.Range]) ? profile[CK.Range].DeepClone() : Js.S(CV.RangeMelee),
                };
                if (MirroredManeuver(c, carrier) != null) { next[CK.Mirrored] = true; next[CK.Camp] = CV.CampSpell; }
                entity[CK.CombatStance] = next;
            }
            else entity[CK.CombatStance] = new JObject { [CK.Maneuver] = CV.StanceNeutral, [CK.Camp] = CV.CampPhysical, [CK.Range] = CV.RangeMelee };
            if (grants != null && grants.Num(CK.Precision) > 0)
                CoreStatuses.Apply(c, entity, CV.StatusPrecision, PrecisionEntryGrant(c, entity, grants.Num(CK.Precision), carrier), entity, TagIds(carrier) ?? new List<string>());
            if (grants != null && grants.Num(CK.Evade) > 0 && !Js.Includes(carrier?[CK.AppliedStatuses], CV.StatusEvade))
                CoreStatuses.Apply(c, entity, CV.StatusEvade, grants.Num(CK.Evade), entity);
            // rememberCombatStance at capacity 1 keeps no concurrent stance.
            entity.Remove(CK.ConcurrentStances);
            c.Emit(CK.CombatStanceChanged, Js.Obj(K.TargetId, entity[K.Id], CK.Maneuver, entity.Obj(CK.CombatStance)[CK.Maneuver]));
        }

        /// <summary>stanceStaminaCost(base, entity, carrier): an enemy move in its owner's current stance costs the matching-stance discount less.</summary>
        public static double StaminaCost(CombatState c, double baseCost, JObject entity, JObject carrier, double manaBase = 0)
        {
            if (Js.D(c.Fields[CK.CombatExpansionVersion]) != c.CoreData.ExpandedVersion || !SameStance(c, entity, carrier)) return baseCost;
            if (CoreFixCore.Enabled(c))
            {
                if (entity?.Str(K.Kind) == V.Enemy)
                {
                    if (!(baseCost > 0) || manaBase > 0) return baseCost;
                    var cfg = CoreFixCore.Family(c, CK.Stance);
                    return Math.Max(cfg.Num(CK.MinimumCost), baseCost - cfg.Num(CK.Discount));
                }
            }
            throw new NotSupportedException(CM.PlayerStaminaCostNotPorted);
        }
    }
}
