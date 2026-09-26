using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// Combat ratings (shipped engine/combatRatings.js with model/combatRatings.js and model/ratingFormula.js):
    /// AR/DR/PR/Poise/Ward from attributes, worn equipment, relics and mounted properties; the Poise and Ward break
    /// meters; the rating bonus a card adds; the resistance curve damage passes through.
    /// </summary>
    public static class Ratings
    {
        private static JArray Ids(CombatData data) => data.Engine.Obj(K.Ratings)?.Arr(K.Ids) ?? new JArray();

        private static JArray AttributeIds(CombatData data) => data.Engine.Obj(K.Ratings)?.Arr(K.Attributes) ?? new JArray();

        private static string BonusKey(string id) => id + V.BonusSuffix;

        private static string MeterKey(string id) => id + V.MeterSuffix;

        public static double Value(CombatState c, JObject entity, string id)
        {
            var value = Js.Or0(entity?.Obj(K.Ratings)?[id]);
            var statuses = entity?.Obj(K.Statuses);
            if (statuses != null)
                foreach (var p in statuses.Properties())
                    value += Js.Or0(c.RatingsRules?.Obj(K.Bonuses)?.Obj(V.StatusBonusPrefix + V.KeySeparator + p.Name)?[id]) * Js.Or0(Js.Get(p.Value, K.Stacks));
            return Math.Max(0, value);
        }

        public static double SourceValue(CombatState c, JObject entity, string id, string sourceArmamentId, bool equipmentScoped)
        {
            var sources = entity?.Arr(K.RatingSources);
            if ((string.IsNullOrEmpty(sourceArmamentId) && !equipmentScoped) || sources == null) return Value(c, entity, id);
            double value = 0;
            foreach (var source in sources.OfType<JObject>())
            {
                if (source.Str(K.Kind) != V.EquipmentSource) value += Js.Or0(source[id]);
                else if (!string.IsNullOrEmpty(sourceArmamentId) && source.Str(K.SourceId) == sourceArmamentId)
                {
                    var eff = source.Obj(K.EffectiveRatings)?[id];
                    value += Js.Or0(!Js.Nullish(eff) ? eff : source[id]);
                }
            }
            var statuses = entity.Obj(K.Statuses);
            if (statuses != null)
                foreach (var p in statuses.Properties())
                    value += Js.Or0(c.RatingsRules?.Obj(K.Bonuses)?.Obj(V.StatusBonusPrefix + V.KeySeparator + p.Name)?[id]) * Js.Or0(Js.Get(p.Value, K.Stacks));
            return Math.Max(0, value);
        }

        /// <summary>isMagicalAttack(ctx, carrier): the carrier's face (over its registry card) is magical.</summary>
        public static bool IsMagicalAttack(CombatState c, JObject carrier)
        {
            var cardId = carrier?.Str(K.CardId);
            var def = !string.IsNullOrEmpty(cardId) ? c.Data.Cards.Get(cardId) : null;
            JToken Field(string key) => carrier != null && carrier[key] != null ? carrier[key] : def?[key];
            var school = Js.Truthy(carrier?[K.DamageSchool]) ? carrier[K.DamageSchool] : def?[K.DamageSchool];
            var face = new JObject();
            if (Js.Truthy(school)) face[K.DamageSchool] = school.DeepClone();
            var cardTags = Field(K.CardTags);
            if (cardTags != null) face[K.CardTags] = cardTags.DeepClone();
            var tags = Js.Truthy(carrier?[K.Tags]) ? carrier[K.Tags] : def?[K.Tags];
            if (tags != null) face[K.Tags] = tags.DeepClone();
            var mana = Field(K.ManaCost);
            if (mana != null) face[K.ManaCost] = mana.DeepClone();
            return Cards.IsMagical(c.Data, face);
        }

        public static double DamageMultiplier(CombatState c, JObject target, bool magical)
        {
            if (c.RatingsRules == null || !Js.Truthy(target?[K.Ratings])) return 1;
            var r = c.RatingsRules.Obj(K.Resistance);
            var rating = Value(c, target, magical ? K.Ward : K.Poise);
            return 1 - Math.Min(r.Num(K.Maximum), rating / (rating + (magical ? r.Num(K.MagicalK) : r.Num(K.PhysicalK))));
        }

        /// <summary>attackImpact: the Poise/Ward a hit deals (card override, weapon weight, card value, or defaults).</summary>
        public static double AttackImpact(CombatState c, JObject source, JObject carrier)
        {
            var config = c.RatingsRules;
            if (config == null) return 0;
            var cardId = carrier?.Str(K.CardId);
            var explicitImpact = cardId != null ? config.Obj(K.AttackImpact)?[cardId] : null;
            if (Js.IsFinite(explicitImpact) && Js.D(explicitImpact) >= 0) return Js.D(explicitImpact);
            var magical = IsMagicalAttack(c, carrier);
            var impact = config.Obj(K.Impact);
            if (!magical)
            {
                var enemyId = source?.Str(K.EnemyId);
                var enemyOverride = enemyId != null ? config.Obj(K.EnemyImpact)?[enemyId] : null;
                if (Js.IsFinite(enemyOverride) && Js.D(enemyOverride) >= 0) return Js.D(enemyOverride);
                var armamentId = carrier?.Str(K.SourceArmamentId);
                var item = Js.Items(c.Data.Equipment[K.Armaments]).OfType<JObject>().FirstOrDefault(p => p.Str(K.Id) == armamentId && armamentId != null);
                if (item != null)
                {
                    var w = item.Or0(K.Weight);
                    return w <= impact.Num(K.LightMaxWeight) ? impact.Num(K.Light)
                        : w <= impact.Num(K.MediumMaxWeight) ? impact.Num(K.Medium)
                        : w <= impact.Num(K.HeavyMaxWeight) ? impact.Num(K.Heavy) : impact.Num(K.Colossal);
                }
            }
            var def = !string.IsNullOrEmpty(cardId) ? c.Data.Cards.Get(cardId) : null;
            JToken ratingValues = null;
            if (Js.Truthy(carrier?[K.CardRatingValues])) ratingValues = carrier[K.CardRatingValues];
            else if (carrier != null && carrier.Is(K.Upgraded) && Js.Truthy(def?.Obj(K.Upgrade)?[K.CardRatingValues])) ratingValues = def.Obj(K.Upgrade)[K.CardRatingValues];
            else if (Js.Truthy(def?[K.CardRatingValues])) ratingValues = def[K.CardRatingValues];
            var calculated = Js.Get(ratingValues, magical ? K.Ward : K.Poise);
            if (calculated != null && calculated.Type != JTokenType.Undefined)
                return Math.Max(0, Formulas.Evaluate(calculated, new FormulaContext { EnergySpent = Js.Or0(carrier?[K.EnergySpent]) }));
            if (magical) return impact.Num(K.Magic);
            return source?.Str(K.Kind) == V.Enemy ? impact.Num(K.EnemyPhysical) : impact.Num(K.Unarmed);
        }

        // ------------------------------------------------------------------ receipt

        private static double AttributeValue(CombatData data, JObject attributes, JObject config, string id)
        {
            var rule = config.Obj(K.Ratings)?.Obj(id) ?? throw new InvalidOperationException(id);
            double weighted = 0;
            foreach (var attr in AttributeIds(data))
            {
                var a = Js.Str(attr);
                weighted += Math.Floor(Js.Or0(attributes?[a]) * rule.Num(a) + Ashen.Generated.CombatMath.Epsilon);
            }
            var multiplier = Js.Coalesce(config[K.Multiplier], 1);
            return rule.Num(K.Base) + Math.Floor(weighted * multiplier + Ashen.Generated.CombatMath.Epsilon);
        }

        private static bool IsMagicalPiece(CombatData data, JObject piece)
        {
            var profileId = piece.Str(K.AttackProfile);
            var profile = Js.Items(data.Equipment[K.BasicCardProfiles]).OfType<JObject>().FirstOrDefault(r => r.Str(K.Id) == profileId && profileId != null);
            return profile != null && profile.Str(K.DamageSchool) != V.Physical;
        }

        private static string SourceKey(JObject piece) => piece.Str(K.Kind) == V.Armor
            ? string.Join(V.KeySeparator, V.Armor, piece.Str(K.ClassId), piece.Str(K.Id))
            : string.Join(V.KeySeparator, V.ArmamentRefPrefix, piece.Str(K.Id));

        private static double EquipmentBase(JObject piece, string id)
        {
            if (id == K.Ar || id == K.Pr) return piece.Or0(K.AttackRating);
            if (id == K.Dr) return piece.Or0(K.DefenseRating);
            if (id == K.Poise && piece.Str(K.Kind) == V.Armor) return piece.Or0(K.PoiseThreshold);
            if (id == K.Ward) return piece.Or0(K.DefenseRating);
            return 0;
        }

        private static void Add(JObject totals, JArray sources, JArray ids, string name, JObject values, string kind, string sourceId)
        {
            var src = Js.Obj(K.Name, name, K.Kind, kind, K.SourceId, sourceId);
            foreach (var p in values.Properties()) src[p.Name] = p.Value.DeepClone();
            sources.Add(src);
            foreach (var id in ids.Select(Js.Str))
            {
                var n = values[id];
                var add = Js.IsNum(n) ? Js.D(n) : double.NaN;
                totals.Put(id, totals.Num(id) + (double.IsNaN(add) ? 0 : add));
            }
        }

        /// <summary>ratingReceipt(registries, run, config): rating totals and their sources from attributes, worn equipment and relics.</summary>
        public static JObject Receipt(CombatData data, JObject config, JObject attributes, JObject loadout, string classId, JToken relicIds, JObject itemUpgradeLevels, out JArray sources)
        {
            var ids = Ids(data);
            var totals = new JObject();
            foreach (var id in ids) totals.Put(Js.Str(id), 0);
            sources = new JArray();
            var stat = new JObject();
            foreach (var id in ids) stat.Put(Js.Str(id), AttributeValue(data, attributes, config, Js.Str(id)));
            Add(totals, sources, ids, V.AttributesSourceName, stat, V.AttributeSource, null);
            if (loadout != null)
            {
                foreach (var piece in Equipment.EquippedPieces(data, loadout, classId, itemUpgradeLevels ?? new JObject()))
                {
                    var magical = IsMagicalPiece(data, piece);
                    var attack = piece.Or0(K.AttackRating);
                    var values = new JObject();
                    values.Put(K.Ar, magical ? 0 : attack);
                    values.Put(K.Dr, piece.Or0(K.DefenseRating));
                    values.Put(K.Pr, magical ? attack : 0);
                    values.Put(K.Poise, piece.Str(K.Kind) == V.Armor ? piece.Or0(K.PoiseThreshold) : 0);
                    values.Put(K.Ward, 0);
                    var configured = config.Obj(K.ItemRatings)?.Obj(SourceKey(piece)) ?? new JObject();
                    foreach (var id in ids.Select(Js.Str)) if (Js.IsFinite(configured[id])) values[id] = configured[id].DeepClone();
                    var effective = new JObject();
                    foreach (var id in ids.Select(Js.Str)) effective.Put(id, Js.IsFinite(configured[id]) ? configured.Num(id) : EquipmentBase(piece, id));
                    values[K.EffectiveRatings] = effective;
                    Add(totals, sources, ids, piece.Str(K.Name), values, V.EquipmentSource, piece.Str(K.Id));
                }
            }
            foreach (var relicToken in Js.Items(relicIds))
            {
                var relicId = Js.Str(relicToken);
                var itemRef = V.RelicRefPrefix + V.ItemRefSeparator + relicId;
                var relic = Equipment.ResolveUpgradedRelic(data, itemRef, Js.Or0(itemUpgradeLevels?[itemRef]));
                var values = Js.Spread(config.Obj(K.Bonuses)?.Obj(V.RelicBonusPrefix + V.KeySeparator + relicId));
                var passives = relic.Obj(K.Passives);
                values.Put(K.Poise, values.Or0(K.Poise) + Js.Or0(passives?[K.PoiseThresholdAdd]));
                foreach (var id in ids.Select(Js.Str)) values.Put(id, values.Or0(id) + Js.Or0(passives?[BonusKey(id)]));
                Add(totals, sources, ids, relic.Str(K.Name), values, V.RelicKind, relicId);
            }
            return totals;
        }

        /// <summary>refreshCombatRatings(ctx): restamp the player's ratings, their sources and the Poise/Ward meters.</summary>
        public static void Refresh(CombatState c)
        {
            var config = c.RatingsRules;
            if (config == null) return;
            var totals = Receipt(c.Data, config, c.Attributes, c.Loadout, c.Player.Str(K.ClassId), c.Player[K.RelicIds], c.ItemUpgradeLevels, out var sources);
            if (c.PropertyMounts != null && c.PropertyMounts.TryGetValue(Triggers.OwnerKey(c.Player), out var mounts))
            {
                foreach (var entry in mounts.Entries())
                    foreach (var rule in entry.Value.Rules)
                    {
                        var values = Js.Obj(K.Name, rule[K.Tag], K.Kind, V.PropertySource);
                        var any = false;
                        foreach (var id in new[] { K.Ar, K.Dr, K.Pr, K.Poise, K.Ward })
                        {
                            var bonus = Js.Or0(rule.Obj(K.Passives)?[BonusKey(id)]);
                            values.Put(id, bonus);
                            totals.Put(id, totals.Num(id) + bonus);
                            if (bonus != 0) any = true;
                        }
                        if (any) sources.Add(values);
                    }
            }
            c.Player[K.Ratings] = totals;
            c.Player[K.RatingSources] = sources;
            foreach (var id in new[] { K.Poise, K.Ward })
            {
                var key = MeterKey(id);
                var prior = c.Player.Obj(key);
                var max = Math.Max(1, Math.Floor(totals.Num(id)));
                var growths = Js.Or0(prior?[K.Growths]);
                for (var n = 0; n < growths; n++) max = Math.Ceiling(max * config.Obj(K.Breaks).Num(K.ThresholdGrowth));
                var meter = Js.Spread(prior);
                meter.Put(K.Value, Math.Min(Js.Or0(prior?[K.Value]), max - 1));
                meter.Put(K.Max, max);
                meter.Put(K.Growths, growths);
                c.Player[key] = meter;
            }
        }

        /// <summary>applyRatingImpact: fill the target's Poise (physical) or Ward (magical) meter; breaks stagger.</summary>
        public static void ApplyImpact(CombatState c, JObject source, JObject target, JObject carrier, double? explicitAmount = null)
        {
            if (c.RatingsRules == null || target == null || !target.Is(K.Alive)) return;
            var magical = IsMagicalAttack(c, carrier);
            var meterName = magical ? K.Ward : K.Poise;
            var meter = target.Obj(MeterKey(meterName));
            if (meter == null || meter.Num(K.Max) <= 0) return;
            var amount = explicitAmount == null ? AttackImpact(c, source, carrier) : Math.Max(0, Math.Floor(explicitAmount.Value));
            if (amount <= 0) return;
            meter.Put(K.Value, meter.Num(K.Value) + amount);
            var cfg = c.RatingsRules.Obj(K.Breaks);
            var breaks = 0;
            while (meter.Num(K.Value) >= meter.Num(K.Max) && breaks < c.Data.Rule(K.Limits, K.FillLoopGuard))
            {
                meter.Put(K.Value, meter.Num(K.Value) - meter.Num(K.Max));
                breaks++;
                if (target.Str(K.Kind) == V.Player)
                    target.Put(K.PendingActionLoss, target.Or0(K.PendingActionLoss) + cfg.Num(magical ? K.WardActionLoss : K.PoiseActionLoss));
                else
                {
                    target[K.SkipNextTurn] = true;
                    target[K.PendingMove] = Js.Null();
                    target[K.Intent] = Js.Obj(K.Kind, V.IntentStaggered, K.MoveId, Js.Null());
                }
                meter.Put(K.Max, Math.Max(1, Math.Ceiling(meter.Num(K.Max) * cfg.Num(K.ThresholdGrowth))));
                meter.Put(K.Growths, meter.Or0(K.Growths) + 1);
                c.Emit(E.MeterFilled, Js.Obj(K.TargetId, target[K.Id], K.Meter, meterName, K.Threshold, meter[K.Max]));
            }
            c.Emit(E.RatingImpact, Js.Obj(K.TargetId, target[K.Id], K.SourceId, source?[K.Id], K.Meter, meterName, K.Amount, amount,
                K.Value, meter[K.Value], K.Max, meter[K.Max], K.Breaks, breaks, K.Label, magical ? V.DisruptionLabel : V.StaggerLabel));
        }

        public static void RecoverMeters(CombatState c, JObject entity)
        {
            if (c.RatingsRules == null) return;
            foreach (var id in new[] { K.Poise, K.Ward })
            {
                var meter = entity.Obj(MeterKey(id));
                if (meter != null) meter.Put(K.Value, Math.Max(0, meter.Num(K.Value) - c.RatingsRules.Obj(K.Breaks).Num(K.RecoveryPerTurn)));
            }
        }

        /// <summary>cardRatingBonus: the rating a card adds to its damage/block/heal (capped by the card's ratingCap).</summary>
        public static double CardBonus(CombatState c, JObject source, JObject carrier, string op, double baseAmount = 0)
        {
            if (source == null || carrier == null || !Js.Truthy(carrier[K.CardId])) return 0;
            double Capped(double amount) => Js.IsFinite(carrier[K.RatingCap])
                ? Math.Max(0, Math.Min(amount, carrier.Num(K.RatingCap) - baseAmount))
                : amount;
            if (c.RatingsRules == null || !Js.Truthy(source[K.Ratings]))
            {
                var n = Js.IsNum(carrier[K.RatingValue]) ? carrier.Num(K.RatingValue) : double.NaN;
                return Capped(double.IsNaN(n) || n == 0 ? 0 : n);
            }
            var magical = IsMagicalAttack(c, carrier);
            double Val(string id) => Capped(SourceValue(c, source, id, carrier.Str(K.SourceArmamentId), carrier.Is(K.EquipmentRole)));
            var ratingId = carrier.Str(K.RatingId);
            if (Js.Truthy(carrier[K.RatingId]) && (op == V.BonusDamage || op == V.BonusBlock || op == V.BonusHeal)) return Val(ratingId);
            if (op == V.BonusDamage) return Val(magical ? K.Pr : K.Ar);
            if (op == V.BonusBlock && (carrier.Str(K.Type) == V.KindSkill || magical)) return Val(magical ? K.Pr : K.Dr);
            if (op == V.BonusHeal && magical) return Val(K.Pr);
            return 0;
        }
    }
}
