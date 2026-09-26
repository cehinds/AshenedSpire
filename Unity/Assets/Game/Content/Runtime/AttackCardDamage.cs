using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CardDamageKeys;

namespace Ashen.Content
{
    /// <summary>
    /// Port of the shipped model/attackCardDamage.js <c>projectAttackCardDamageBundle</c> (with
    /// <c>materializeCardValueBonuses</c>): the configured cost-derived value formulas are applied to every card's
    /// damage, Block and poise damage before the registries freeze. Arithmetic is IEEE double, in the shipped order.
    /// </summary>
    internal static class AttackCardDamage
    {
        private static readonly string[] ValueConfigs = { K.AttackCards, K.DefenseCards, K.PotencyCards, K.PoiseCards, K.WardCards };

        private static readonly HashSet<string> MagicWords = new HashSet<string>(StringComparer.Ordinal)
        {
            K.MagicWordMagic, K.MagicWordMagical, K.MagicWordArcane, K.MagicWordHoly, K.MagicWordFire, K.MagicWordSpell,
        };

        /// <summary>The bundle with balance.damage materialized and every card projected (the input is not changed).</summary>
        public static JObject ProjectBundle(JObject source)
        {
            var materialized = MaterializeCardValueBonuses(source);
            var configs = JsValues.Get(JsValues.Get(materialized, RegistryKeys.Balance), K.Damage) as JObject;
            foreach (var name in ValueConfigs)
                if (!JsValues.Truthy(configs?[name]))
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, RegistryMessages.MissingDamageConfig, name));

            var schools = ProfileSchools(materialized);
            var tagsByCard = AuthoredCardTags(materialized);
            var cards = new JArray();
            foreach (var cardToken in JsValues.Items(materialized[RegistryKeys.Cards]))
            {
                var card = (JObject)cardToken;
                var tags = TagsOf(tagsByCard, card);
                cards.Add(ProjectCard(card, configs, tags, AlternateSchool(card, tags, schools)));
            }
            var result = JsValues.Spread(materialized);
            result[RegistryKeys.Cards] = cards;
            return result;
        }

        // ---------------------------------------------------------------- materializeCardValueBonuses

        public static JObject MaterializeCardValueBonuses(JObject source)
        {
            var balance = JsValues.Get(source, RegistryKeys.Balance) as JObject;
            var damage = JsValues.Get(balance, K.Damage);
            if (!JsValues.Truthy(damage)) return source;
            var damageObj = damage as JObject ?? new JObject();
            var schools = ProfileSchools(source);
            var tagsByCard = AuthoredCardTags(source);
            var materializedDamage = new JObject();
            foreach (var configName in ValueConfigs)
            {
                var config = JsValues.Truthy(damageObj[configName]) ? damageObj[configName] as JObject ?? new JObject() : new JObject();
                var defaults = new JObject();
                foreach (var row in EligibleCards(source, configName, schools, tagsByCard))
                    defaults[JsValues.Text(row[RegistryKeys.Id])] = JsValues.Number(DefaultCardBonus(row, configName, config));
                var statusDefaults = new JObject();
                foreach (var status in JsValues.Items(source[RegistryKeys.Statuses]))
                    statusDefaults[JsValues.Text(JsValues.Get(status, RegistryKeys.Id))] = new JValue(0L);
                var merged = JsValues.Spread(config);
                merged[K.StatusMultipliers] = JsValues.Spread(statusDefaults, config[K.StatusMultipliers] as JObject);
                merged[K.CardBonuses] = JsValues.Spread(defaults, config[K.CardBonuses] as JObject);
                materializedDamage[configName] = merged;
            }
            var result = JsValues.Spread(source);
            var newBalance = JsValues.Spread(balance);
            newBalance[K.Damage] = JsValues.Spread(damageObj, materializedDamage);
            result[RegistryKeys.Balance] = newBalance;
            return result;
        }

        private static List<JObject> EligibleCards(JObject bundle, string configName, Dictionary<string, List<string>> schools,
            Dictionary<string, JArray> tagsByCard)
        {
            var eligible = new List<JObject>();
            foreach (var token in JsValues.Items(bundle[RegistryKeys.Cards]))
            {
                if (!(token is JObject card)) continue;
                var tags = TagsOf(tagsByCard, card);
                var authored = CardIsMagical(With(card, tags, null));
                var other = AlternateSchool(card, tags, schools);
                var sides = other != null ? new[] { authored, !authored } : new[] { authored };
                var attack = JsValues.Str(JsValues.Get(card, K.Type)) == K.TypeAttack;
                foreach (var magical in sides)
                {
                    var fits = (configName == K.AttackCards && attack && !magical && HasNumeric(card, K.OpDamage))
                        || (configName == K.PotencyCards && magical && (HasNumeric(card, K.OpDamage) || HasNumeric(card, K.OpBlock)))
                        || (configName == K.DefenseCards && !magical && HasNumeric(card, K.OpBlock))
                        || (configName == K.PoiseCards && attack && !magical)
                        || (configName == K.WardCards && attack && magical);
                    if (!fits) continue;
                    eligible.Add(card);
                    break;
                }
            }
            return eligible;
        }

        /// <summary>A NUMERIC amount on the base or upgraded face, because that is all projectOperation moves.</summary>
        private static bool HasNumeric(JObject card, string op) =>
            new[] { card[K.Effects], JsValues.Get(card[K.Upgrade], K.Effects) }
                .Any(effects => JsValues.Items(effects).Any(e => JsValues.Str(JsValues.Get(e, K.Op)) == op && JsValues.IsNumber(JsValues.Get(e, K.Amount))));

        private static double DefaultCardBonus(JObject row, string configName, JObject config)
        {
            var op = ReferenceOperation(row, configName);
            var baseFace = JsValues.Spread(row);
            baseFace[K.Effects] = JsValues.Truthy(row[K.Effects]) ? row[K.Effects].DeepClone() : new JArray();
            JObject upgradedFace = null;
            if (JsValues.Truthy(row[K.Upgrade]))
            {
                upgradedFace = JsValues.Spread(row, row[K.Upgrade] as JObject);
                upgradedFace[K.Effects] = FirstNonNullish(JsValues.Get(row[K.Upgrade], K.Effects), row[K.Effects]) ?? new JArray();
            }
            var face = NumericBaseline(baseFace[K.Effects], op) == null ? upgradedFace : baseFace;
            var authored = NumericBaseline(face?[K.Effects], op);
            if (authored == null) return 0;
            return authored.Value - CostDerivedValue(face, config);
        }

        // ---------------------------------------------------------------- the formula

        private static double CostOf(JObject face, string key)
        {
            var value = JsValues.Get(face, key);
            if (key == K.Cost && JsValues.IsString(value) && (string)value == K.CostX) return 1;
            return JsValues.IsFinite(value) ? JsValues.ToDouble(value) : 0;
        }

        private static List<string> StatusIds(JToken effects) =>
            JsValues.Items(effects)
                .Where(e => JsValues.Str(JsValues.Get(e, K.Op)) == K.OpApplyStatus && JsValues.IsString(JsValues.Get(e, K.Status)))
                .Select(e => (string)e[K.Status])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

        private static double CostDerivedValue(JObject face, JObject config)
        {
            var action = CostOf(face, K.Cost);
            var mana = CostOf(face, K.ManaCost);
            var stamina = CostOf(face, K.StaminaCost);
            var multipliers = config[K.StatusMultipliers];
            double sum = 0;
            foreach (var id in StatusIds(JsValues.Get(face, K.Effects)))
            {
                var m = JsValues.Get(multipliers, id);
                sum = sum + (JsValues.IsNullish(m) ? 0 : JsValues.NumberOrNaN(m));
            }
            var reduction = JsValues.NumberOrNaN(config[K.StatusEffectReductionMultiplier]) * sum;
            var costs = action * JsValues.NumberOrNaN(config[K.ActionCostMultiplier])
                + mana * JsValues.NumberOrNaN(config[K.ManaCostMultiplier])
                + stamina * JsValues.NumberOrNaN(config[K.StaminaCostMultiplier]);
            return Math.Floor(JsValues.NumberOrNaN(config[K.GlobalMultiplier]) * costs - reduction);
        }

        /// <summary>The deterministic base value a card's three resource costs contribute, plus its card bonus.</summary>
        private static double BaseValue(JObject face, JObject config)
        {
            var bonus = JsValues.Get(config[K.CardBonuses], JsValues.Text(JsValues.Get(face, RegistryKeys.Id)));
            var cardBonus = JsValues.IsNullish(bonus) ? 0 : JsValues.NumberOrNaN(bonus);
            return Math.Max(0, CostDerivedValue(face, config) + cardBonus);
        }

        public static bool CardIsMagical(JObject face)
        {
            var school = JsValues.Get(face, K.DamageSchool);
            if (JsValues.Truthy(school)) return JsValues.Text(school) != K.SchoolPhysical;
            var cardTags = JsValues.Get(face, K.CardTags);
            var tags = JsValues.Truthy(cardTags) ? cardTags : JsValues.Get(face, K.Tags);
            foreach (var tag in JsValues.Items(tags))
            {
                var text = JsValues.IsString(tag) ? (string)tag : JsValues.Text(JsValues.Get(tag, K.TagIdField));
                var parts = text.Split(K.TagSeparator[0]);
                if (MagicWords.Contains(parts[parts.Length - 1])) return true;
            }
            var mana = JsValues.Get(face, K.ManaCost);
            return JsValues.Truthy(mana) && JsValues.NumberOrNaN(mana) > 0;
        }

        // ---------------------------------------------------------------- projection

        private static JToken FirstNonNullish(params JToken[] tokens) => tokens.FirstOrDefault(t => !JsValues.IsNullish(t));

        private static double? NumericBaseline(JToken effects, string op)
        {
            var numeric = JsValues.Items(effects).Where(e => JsValues.Str(JsValues.Get(e, K.Op)) == op && JsValues.IsNumber(JsValues.Get(e, K.Amount))).ToList();
            var primary = numeric.FirstOrDefault(e => !JsValues.Truthy(e[K.If]));
            if (primary != null) return JsValues.ToDouble(primary[K.Amount]);
            return numeric.Count > 0 ? numeric.Min(e => JsValues.ToDouble(e[K.Amount])) : (double?)null;
        }

        private static string ReferenceOperation(JObject card, string configName)
        {
            if (configName == K.AttackCards) return K.OpDamage;
            if (configName == K.DefenseCards) return K.OpBlock;
            if (configName == K.PotencyCards)
                return new[] { card[K.Effects], JsValues.Get(card[K.Upgrade], K.Effects) }
                    .Any(effects => JsValues.Items(effects).Any(e => JsValues.Str(JsValues.Get(e, K.Op)) == K.OpDamage)) ? K.OpDamage : K.OpBlock;
            return K.OpPoiseDamage;
        }

        /// <summary><c>addToAmount</c> over a numeric amount (the only kind the projection moves): no bonus leaves it as is.</summary>
        private static double AddToAmount(double amount, double bonus) =>
            bonus == 0 || double.IsNaN(bonus) ? amount : Math.Max(0, amount + bonus);

        private static void ProjectOperation(JObject face, JArray effects, string op, double baseValue, double? authoredReference)
        {
            var matching = effects.OfType<JObject>().Where(e => JsValues.Str(e[K.Op]) == op && JsValues.IsNumber(e[K.Amount])).ToList();
            var primary = matching.FirstOrDefault(e => !JsValues.Truthy(e[K.If]));
            var currentBase = NumericBaseline(face[K.Effects], op);
            var shift = currentBase != null && authoredReference != null ? currentBase.Value - authoredReference.Value : 0;
            var adjustedBase = AddToAmount(baseValue, shift);

            if (primary != null)
            {
                primary[K.Amount] = JsValues.Number(adjustedBase);
                return;
            }
            if (matching.Count == 0) return; // formula-valued amounts stay exactly as authored
            var authoredBase = matching.Min(e => JsValues.ToDouble(e[K.Amount]));
            foreach (var effect in matching)
                effect[K.Amount] = JsValues.Number(AddToAmount(adjustedBase, JsValues.ToDouble(effect[K.Amount]) - authoredBase));
        }

        private static JArray ProjectEffects(JObject face, JObject configs, Dictionary<string, double?> references)
        {
            var effects = new JArray(JsValues.Items(face[K.Effects]).Select(e => e is JObject o ? JsValues.Spread(o) : e?.DeepClone()));
            var magical = CardIsMagical(face);
            var attack = JsValues.Str(JsValues.Get(face, K.Type)) == K.TypeAttack;
            if (attack)
            {
                var name = magical ? K.PotencyCards : K.AttackCards;
                ProjectOperation(face, effects, K.OpDamage, BaseValue(face, (JObject)configs[name]), references[name]);
            }
            if (effects.Any(e => JsValues.Str(JsValues.Get(e, K.Op)) == K.OpBlock))
            {
                var name = magical ? K.PotencyCards : K.DefenseCards;
                ProjectOperation(face, effects, K.OpBlock, BaseValue(face, (JObject)configs[name]), references[name]);
            }
            if (attack && effects.Any(e => JsValues.Str(JsValues.Get(e, K.Op)) == K.OpPoiseDamage))
            {
                var name = magical ? K.WardCards : K.PoiseCards;
                ProjectOperation(face, effects, K.OpPoiseDamage, BaseValue(face, (JObject)configs[name]), references[name]);
            }
            return effects;
        }

        private static JObject RatingValues(JObject face, JObject configs)
        {
            if (JsValues.Str(JsValues.Get(face, K.Type)) != K.TypeAttack) return null;
            // A card that authors its own poise damage already carries its impact in that effect.
            if (JsValues.Items(face[K.Effects]).Any(e => JsValues.Str(JsValues.Get(e, K.Op)) == K.OpPoiseDamage)) return null;
            var magical = CardIsMagical(face);
            return new JObject
            {
                [magical ? K.Ward : K.Poise] = JsValues.Number(BaseValue(face, (JObject)configs[magical ? K.WardCards : K.PoiseCards])),
            };
        }

        private sealed class Faces
        {
            public JObject Base;
            public JObject Upgrade;
        }

        private static JObject WithSchool(JObject face, string school)
        {
            if (!string.IsNullOrEmpty(school)) face[K.DamageSchool] = school;
            return face;
        }

        private static Faces ProjectFaces(JObject card, JObject configs, JArray tags, string school)
        {
            var baseFace = JsValues.Spread(card);
            baseFace[K.Tags] = tags.DeepClone();
            baseFace[K.Effects] = JsValues.Truthy(card[K.Effects]) ? card[K.Effects].DeepClone() : new JArray();
            WithSchool(baseFace, school);
            JObject upgradedFace = null;
            if (JsValues.Truthy(card[K.Upgrade]))
            {
                upgradedFace = JsValues.Spread(card, card[K.Upgrade] as JObject);
                upgradedFace[K.Tags] = tags.DeepClone();
                upgradedFace[K.Effects] = FirstNonNullish(JsValues.Get(card[K.Upgrade], K.Effects), card[K.Effects])?.DeepClone() ?? new JArray();
                WithSchool(upgradedFace, school);
            }
            var references = new Dictionary<string, double?>(StringComparer.Ordinal);
            foreach (var name in ValueConfigs)
            {
                var op = ReferenceOperation(baseFace, name);
                references[name] = NumericBaseline(baseFace[K.Effects], op) ?? NumericBaseline(upgradedFace?[K.Effects], op);
            }
            JObject Face(JObject source)
            {
                var face = new JObject { [K.Effects] = ProjectEffects(source, configs, references) };
                var values = RatingValues(source, configs);
                if (values != null) face[K.CardRatingValues] = values;
                return face;
            }
            return new Faces { Base = Face(baseFace), Upgrade = upgradedFace != null ? Face(upgradedFace) : null };
        }

        private static JObject ProjectCard(JObject card, JObject configs, JArray tags, string otherSchool)
        {
            var faces = ProjectFaces(card, configs, tags, null);
            var projected = JsValues.Spread(card, faces.Base);
            if (JsValues.Truthy(card[K.Upgrade])) projected[K.Upgrade] = JsValues.Spread(card[K.Upgrade] as JObject, faces.Upgrade);
            // A profile that moves this card across the physical/magical line resolves it through the OTHER formulas.
            if (otherSchool != null)
            {
                var other = ProjectFaces(card, configs, tags, otherSchool);
                var variant = new JObject { [K.Magical] = CardIsMagical(With(card, tags, otherSchool)) };
                variant = JsValues.Spread(variant, other.Base);
                if (other.Upgrade != null) variant[K.Upgrade] = other.Upgrade;
                projected[K.SchoolVariant] = variant;
            }
            return projected;
        }

        // ---------------------------------------------------------------- schools and tags

        /// <summary><c>{ ...card, tags, damageSchool? }</c> — the face cardIsMagical is asked about.</summary>
        private static JObject With(JObject card, JArray tags, string school)
        {
            var face = JsValues.Spread(card);
            face[K.Tags] = tags.DeepClone();
            if (school != null) face[K.DamageSchool] = school;
            return face;
        }

        /// <summary>The schools a basic-card profile (or a weapon package dealing a card under one) can put a card in.</summary>
        private static Dictionary<string, List<string>> ProfileSchools(JObject bundle)
        {
            var schools = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var equipment = JsValues.Get(bundle, RegistryKeys.Equipment);
            var profiles = JsValues.Items(JsValues.Get(equipment, RegistryKeys.BasicCardProfiles)).ToList();
            void Add(JToken cardId, JToken profile)
            {
                var school = JsValues.Get(profile, K.DamageSchool);
                if (!JsValues.Truthy(cardId) || !JsValues.IsString(school)) return;
                var id = JsValues.Text(cardId);
                if (!schools.TryGetValue(id, out var list)) schools[id] = list = new List<string>();
                list.Add((string)school);
            }
            foreach (var profile in profiles) Add(JsValues.Get(profile, K.BaseCardId), profile);
            var byId = new Dictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var profile in profiles) byId[JsValues.Text(JsValues.Get(profile, RegistryKeys.Id))] = profile;
            foreach (var piece in JsValues.Items(JsValues.Get(equipment, RegistryKeys.Armaments)))
            {
                var pack = JsValues.Get(piece, K.WeaponCardPackage) as JObject;
                if (pack == null || !(pack[K.PriorityAttackRefs] is JArray refs)) continue;
                var filler = pack[K.FillerAttackProfileId];
                foreach (var raw in refs)
                {
                    var cardId = JsValues.IsString(raw) ? raw : JsValues.Get(raw, K.RefCardId);
                    var profileRef = JsValues.IsString(raw) ? null : JsValues.Get(raw, K.RefProfileId);
                    var profileId = JsValues.Truthy(profileRef) ? profileRef : filler;
                    byId.TryGetValue(JsValues.Text(profileId), out var profile);
                    Add(cardId, JsValues.IsNullish(profileId) ? null : profile);
                }
            }
            return schools;
        }

        private static string AlternateSchool(JObject card, JArray tags, Dictionary<string, List<string>> schools)
        {
            var authoredMagical = CardIsMagical(With(card, tags, null));
            if (!schools.TryGetValue(JsValues.Text(card[RegistryKeys.Id]), out var list)) return null;
            var found = list.FirstOrDefault(school => CardIsMagical(With(card, tags, school)) != authoredMagical);
            return string.IsNullOrEmpty(found) ? null : found;
        }

        /// <summary>Every card's authored tag ids from the tagging junction (all domains, authoring order).</summary>
        private static Dictionary<string, JArray> AuthoredCardTags(JObject bundle)
        {
            var byCard = new Dictionary<string, JArray>(StringComparer.Ordinal);
            foreach (var row in JsValues.Items(JsValues.Get(bundle, RegistryKeys.Tagging)))
            {
                if (JsValues.Str(JsValues.Get(row, TagKeys.Family)) != TagKeys.CardFamily || !JsValues.IsString(row[TagKeys.ObjectId])) continue;
                var id = (string)row[TagKeys.ObjectId];
                if (!byCard.TryGetValue(id, out var list)) byCard[id] = list = new JArray();
                list.Add(row[TagKeys.TagId]?.DeepClone() ?? JValue.CreateNull());
            }
            return byCard;
        }

        private static JArray TagsOf(Dictionary<string, JArray> tagsByCard, JObject card) =>
            JsValues.IsString(card[RegistryKeys.Id]) && tagsByCard.TryGetValue((string)card[RegistryKeys.Id], out var tags) ? tags : new JArray();
    }
}
