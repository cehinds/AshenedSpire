using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CCV = Ashen.Generated.Core2CostValues;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DN = Ashen.Generated.Core2DamageNumbers;
using DV = Ashen.Generated.Core2DamageValues;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The Core 2 damage chain as a pure receipt (web engine/gameProfileCombat.js profileDamageReceipt, model/combatRatings.js sourceRatingValue,
    /// magicShareOf, splitContactParts, engine/stanceMitigation.js, engine/concurrentStances.js defensiveCombatStance): the card's damage plus the
    /// attack (or power) rating times the profile's weight, with one source line per rating the attacker carries, the matching-stance
    /// multiplier, Vulnerable, the other status rules, the stance advantage (or Smash against a defensive stance) and the defence mitigation,
    /// floored at each stage; then the hit split into a physical and a magic part, Block and Barrier, and the Poise and Ward protections. The
    /// receipt keeps full precision and shows the running floor on each line, as the web does. It rolls nothing and changes nothing.
    /// </summary>
    public static class CoreDamage
    {
        private static double Whole(double value) => Math.Max(0, Math.Floor(value));

        private static double Num0(JToken t) => Js.IsFinite(t) ? Js.D(t) : 0;

        private static JObject Rules(CombatState c) => c.CoreData.CombatRules;

        private static JObject DamageSection(CombatState c) => c.CoreData.Section(DK.SectionDamage);

        // ------------------------------------------------------------------ ratings

        /// <summary>sourceRatingValue(ctx, entity, id, sourceArmamentId, equipmentScoped): the rating a source contributes (the whole rating when the hit is not scoped to a piece).</summary>
        public static double SourceRatingValue(CombatState c, JObject entity, string id, string sourceArmamentId, bool equipmentScoped)
        {
            if ((string.IsNullOrEmpty(sourceArmamentId) && !equipmentScoped) || !(entity?[K.RatingSources] is JArray sources)) return CorePools.RatingValue(c, entity, id);
            var value = CorePools.StanceRatingBonus(c, entity, id);
            foreach (var source in sources.OfType<JObject>())
            {
                var sourceId = source.Str(K.SourceId);
                if (source.Str(K.Kind) != V.EquipmentSource) value += Num0(source[id]);
                else if ((!string.IsNullOrEmpty(sourceArmamentId) && (sourceId == sourceArmamentId || sourceId == DV.ArmamentPrefix + sourceArmamentId))
                    || (CoreFixCore.Enabled(c) && (source.Str(DK.Slot) == DV.SlotArmor || (sourceId != null && sourceId.StartsWith(DV.ArmorPrefix, StringComparison.Ordinal)))))
                {
                    var effective = source.Obj(K.EffectiveRatings)?[id];
                    value += Num0(!Js.Nullish(effective) ? effective : source[id]);
                }
            }
            if (CoreFixCore.Enabled(c) && id == CK.RatingAr && entity.Obj(K.Statuses)?.Obj(CV.StatusWeak)?.Num(K.Stacks) > 0)
                value = Math.Floor(value * CoreFixCore.Family(c, CK.Statuses, entity).Num(CK.WeakArMultiplier));
            return Math.Max(0, value);
        }

        // ------------------------------------------------------------------ damage types and the magic share

        /// <summary>canonicalDamageType(type): an alias becomes its canonical name; an empty type is null.</summary>
        public static string CanonicalDamageType(CombatState c, string type)
        {
            if (string.IsNullOrEmpty(type)) return null;
            var aliases = DamageSection(c).Obj(DK.DamageTypeAliases);
            return aliases?[type] != null ? Js.Str(aliases[type]) : type;
        }

        private static double? RoutingShare(CombatState c, string type)
        {
            var row = Rules(c).Obj(DK.DamageTypes)?.Obj(type ?? string.Empty);
            if (row == null) return null;
            return Js.IsFinite(row[DK.MagicShare]) ? Js.D(row[DK.MagicShare]) : row[DK.Physical] != null && row[DK.Physical].Type == JTokenType.Boolean && !row[DK.Physical].Value<bool>() ? 1 : 0;
        }

        /// <summary>isMagicDamageType(type): a type that routes any of its damage to Barrier and Ward.</summary>
        public static bool IsMagicDamageType(CombatState c, string type) { var share = RoutingShare(c, type); return share != null && share > 0; }

        /// <summary>isPhysicalDamageType(type): a type that routes all of its damage to Block and Poise.</summary>
        public static bool IsPhysicalDamageType(CombatState c, string type) => RoutingShare(c, type) == 0;

        /// <summary>schoolOf(profile): the magic school a contact carries (its school, else a school:id tag); the physical camp's weapon work is no school.</summary>
        public static string SchoolOf(CombatState c, JObject profile)
        {
            var prefix = DamageSection(c).Str(DK.SchoolTagPrefix);
            var tag = Js.Items(profile?[K.Tags]).Select(Js.Str).FirstOrDefault(t => t != null && t.StartsWith(prefix, StringComparison.Ordinal));
            var school = Js.Truthy(profile?[K.School]) ? Js.Str(profile[K.School]) : tag != null ? tag.Substring(prefix.Length) : null;
            if (string.IsNullOrEmpty(school)) school = null;
            return school != null && school != DamageSection(c).Str(DK.PhysicalSchool) ? school : null;
        }

        private static double SchoolMagicShare(CombatState c, string school)
        {
            var row = Rules(c).Obj(DK.Schools)?.Obj(school);
            return row != null && Js.IsFinite(row[DK.MagicShare]) ? Js.D(row[DK.MagicShare]) : 1;
        }

        private static string PhysicalTypeOf(CombatState c, JObject profile)
        {
            var type = CanonicalDamageType(c, Js.Str(profile?[CK.DamageType]));
            var school = SchoolOf(c, profile);
            var row = school != null ? Rules(c).Obj(DK.Schools)?.Obj(school) : null;
            if (Js.Truthy(profile?[CK.PhysicalDamageType])) return Js.Str(profile[CK.PhysicalDamageType]);
            if (IsPhysicalDamageType(c, type)) return type;
            return Js.Truthy(row?[DK.PhysicalType]) ? Js.Str(row[DK.PhysicalType]) : null;
        }

        /// <summary>isForceHit(profile): the force school, or arcane damage in any of its parts.</summary>
        public static bool IsForceHit(CombatState c, JObject profile)
        {
            if (profile == null) return false;
            bool Arcane(string type) => CanonicalDamageType(c, type) == DV.DamageArcane;
            return SchoolOf(c, profile) == DV.SchoolForce || Arcane(profile.Str(CK.DamageType)) || Js.Items(profile[CK.DamageComponents]).Any(component => Arcane(Js.Str(Js.Get(component, DK.Type))));
        }

        /// <summary>isTechniqueProfile(profile): a physical-camp maneuver that carries a magic school, a magic damage type (or component) or a spell rider.</summary>
        public static bool IsTechniqueProfile(CombatState c, JObject profile)
        {
            if (profile?.Str(CK.Camp) != CV.CampPhysical || !Js.Truthy(profile[CK.Maneuver])) return false;
            var magicComponent = Js.Items(profile[CK.DamageComponents]).OfType<JObject>().Any(component => IsMagicDamageType(c, CanonicalDamageType(c, component.Str(DK.Type))));
            return SchoolOf(c, profile) != null || IsMagicDamageType(c, CanonicalDamageType(c, profile.Str(CK.DamageType))) || magicComponent || Js.Truthy(profile[DK.RiderSpell]);
        }

        private sealed class ComponentParts
        {
            public double MagicShare;
        }

        private static ComponentParts ComponentPartsOf(CombatState c, JObject profile)
        {
            if (!(profile?[CK.DamageComponents] is JArray list) || list.Count < Ashen.Generated.CombatMath.Two) return null;
            double magic = 0, physical = 0;
            foreach (var component in list.OfType<JObject>())
            {
                var weight = Math.Max(0, Num0(component[DK.Weight]));
                var type = CanonicalDamageType(c, component.Str(DK.Type));
                if (type == null) continue;
                if (IsMagicDamageType(c, type)) magic += weight; else physical += weight;
            }
            var total = magic + physical;
            return total > 0 ? new ComponentParts { MagicShare = magic / total } : null;
        }

        /// <summary>magicShareOf(profile): the share of a hit that is magic (Barrier, then Ward) rather than physical (Block, then Poise), by the owner's precedence.</summary>
        public static double MagicShareOf(CombatState c, JObject profile) => MagicShare(c, profile);

        private static double MagicShare(CombatState c, JObject profile)
        {
            if (Js.IsFinite(profile?[CK.MagicShare])) return Math.Min(1, Math.Max(0, Js.D(profile[CK.MagicShare])));
            var components = ComponentPartsOf(c, profile);
            if (components != null) return components.MagicShare;
            var spell = profile?.Str(CK.Camp) == CV.CampSpell;
            var school = SchoolOf(c, profile);
            var type = CanonicalDamageType(c, profile?.Str(CK.DamageType));
            var schoolShare = school != null ? Rules(c).Obj(DK.Schools)?.Obj(school)?[DK.MagicShare] : null;
            if (Js.IsFinite(schoolShare) && Js.D(schoolShare) < 1) return Math.Max(0, Js.D(schoolShare));
            if (spell && school == null && Js.Includes(DamageSection(c)[DK.MeleeReaches], profile?.Str(DK.Reach)) && PhysicalTypeOf(c, profile) != null) return Rules(c).Obj(DK.MagicSplit).Num(DK.MeleeSpell);
            if (IsTechniqueProfile(c, profile)) return Rules(c).Obj(DK.MagicSplit).Num(DK.Technique);
            if (school != null) return SchoolMagicShare(c, school);
            var typed = type != null ? Rules(c).Obj(DK.DamageTypes)?.Obj(type)?[DK.MagicShare] : null;
            if (Js.IsFinite(typed)) return Js.D(typed);
            return spell ? 1 : 0;
        }

        /// <summary>magicDamageTypeOf(profile): the Ward type a hit's magic part deals, its school's, else its damage type when that is magic.</summary>
        public static string MagicDamageTypeOf(CombatState c, JObject profile)
        {
            var school = SchoolOf(c, profile);
            var row = school != null ? Rules(c).Obj(DK.Schools)?.Obj(school) : null;
            if (Js.Truthy(row?[DK.MagicType])) return Js.Str(row[DK.MagicType]);
            var type = CanonicalDamageType(c, profile?.Str(CK.DamageType));
            return IsMagicDamageType(c, type) ? type : null;
        }

        /// <summary>splitContactParts({ amount, base, ratingBonus, ratingPart, share, primary }): one hit split by the card's own damage; the parts always sum to the amount.</summary>
        public static (double Magic, double Physical) SplitContactParts(double amount, double baseAmount, double ratingBonus, string ratingPart, double share, string primary)
        {
            var total = Whole(Num0(new JValue(amount)));
            var card = Whole(baseAmount);
            var rating = Whole(ratingBonus);
            var cardMagic = share >= 1 ? card : share > 0 ? Math.Floor(card * share + Ashen.Generated.CombatMath.Epsilon) : 0;
            double magic = cardMagic, physical = card - cardMagic;
            if (ratingPart == DV.PartMagic) magic += rating;
            else if (ratingPart == DV.PartPhysical) physical += rating;
            var rest = total - card - (ratingPart != null ? rating : 0);
            if (primary == DV.PartMagic) magic += rest; else physical += rest;
            if (magic < 0) { physical += magic; magic = 0; }
            if (physical < 0) { magic += physical; physical = 0; }
            return (magic, physical);
        }

        // ------------------------------------------------------------------ stances

        /// <summary>stanceMitigation(ctx, incoming, defending): the multiplicative stage a defender wins when the directed matchup table says its stance beats the attack.</summary>
        public static JObject StanceMitigation(CombatState c, string incoming, string defending)
        {
            var rules = c.CoreData.Configured(CK.StancesModule);
            string Canonical(string maneuver) => maneuver == CV.StanceCasting || maneuver == DV.StanceCast ? CV.StancePrepared : maneuver == CV.ManeuverRanged ? CV.StanceAttack : maneuver;
            var attack = Canonical(incoming);
            var defense = Canonical(defending);
            var defenseAdvantage = Js.Includes(rules?[CK.Include], attack) && Js.Includes(rules?[CK.Include], defense) && Js.Includes(rules?.Obj(CK.Matchups)?[defense], attack);
            var mitigation = defenseAdvantage ? Js.Coalesce(rules[DK.DefenseMitigation], 0) : 0;
            return Js.Obj(DK.DefenseAdvantage, defenseAdvantage, DK.Mitigation, mitigation, DK.Multiplier, 1 - mitigation);
        }

        /// <summary>activeCombatStances(ctx, entity): the stances an entity holds now (one, or those a Many Forms status allows that have not expired).</summary>
        public static List<JObject> ActiveCombatStances(CombatState c, JObject entity)
        {
            if (!(entity?[CK.CombatStance] is JObject current)) return new List<JObject>();
            var capacity = CoreCosts.StanceCapacity(c, entity);
            if (capacity <= 1) return new List<JObject> { current };
            var unique = new List<KeyValuePair<string, JObject>>();
            foreach (var item in Js.Items(entity[CK.ConcurrentStances]).Concat(new[] { (JToken)current }))
            {
                if (!(item is JObject stance) || !Js.Truthy(stance[CK.Maneuver])) continue;
                if (!(Js.Nullish(stance[CK.ExpiresOnOwnerCycle]) || stance.Num(CK.ExpiresOnOwnerCycle) > entity.Or0(CK.CombatOwnerCycle))) continue;
                var key = stance.Str(CK.Camp) + CCV.StanceKeySeparator + stance.Str(CK.Maneuver);
                var at = unique.FindIndex(pair => pair.Key == key);
                if (at >= 0) unique[at] = new KeyValuePair<string, JObject>(key, stance); else unique.Add(new KeyValuePair<string, JObject>(key, stance));
            }
            return unique.Skip(Math.Max(0, unique.Count - (int)capacity)).Select(pair => pair.Value).ToList();
        }

        private static JObject StancePolicy(CombatState c, JObject entity)
        {
            foreach (var rule in c.CoreData.ProposalStatusRules.Properties())
            {
                if (Js.Or0(entity?.Obj(K.Statuses)?.Obj(rule.Name)?[K.Stacks]) <= 0) continue;
                var behavior = Js.Items((rule.Value as JObject)?[DK.Behaviors]).OfType<JObject>().FirstOrDefault(b => b.Str(K.Kind) == DV.KindStanceCapacity);
                if (behavior != null) return behavior;
            }
            return new JObject();
        }

        /// <summary>defensiveCombatStance(ctx, entity, carrier): the one stance that answers a hostile carrier (the best defence, then the priority order).</summary>
        public static JObject DefensiveCombatStance(CombatState c, JObject entity, JObject carrier)
        {
            var rows = ActiveCombatStances(c, entity);
            if (rows.Count < Ashen.Generated.CombatMath.Two) return entity?.Obj(CK.CombatStance);
            var rawIncoming = carrier?.Obj(CK.CombatProfile)?.Str(CK.Maneuver) ?? carrier?.Str(CK.Maneuver);
            var incoming = rawIncoming == CV.StanceCasting ? CV.StancePrepared : rawIncoming;
            var profile = c.CoreData.Module(CK.StancesModule);
            var priority = Js.Items(StancePolicy(c, entity)[DK.DefensePriority]).Select(Js.Str).ToList();
            if (priority.Count == 0) priority = Js.Items(DamageSection(c)[DK.DefaultDefensePriority]).Select(Js.Str).ToList();
            double Penalty(JObject stance) =>
                profile?.Str(CK.Mode) == DV.ModeConfigured ? (Js.Includes(profile.Obj(CK.Matchups)?[incoming ?? string.Empty], stance.Str(CK.Maneuver)) ? Js.Coalesce(profile[DK.AdvantageMultiplier], DamageSection(c).Num(DK.FallbackAdvantage)) : 1) : 1;
            double Factor(JObject stance) => Penalty(stance) * StanceMitigation(c, incoming, stance.Str(CK.Maneuver)).Num(DK.Multiplier);
            int Order(JObject stance) => priority.IndexOf(stance.Str(CK.Maneuver) == CV.StancePrepared ? CV.StanceCasting : stance.Str(CK.Maneuver));
            return rows.OrderBy(Factor).ThenBy(Order).First();
        }

        // ------------------------------------------------------------------ the receipt

        /// <summary>
        /// proposalDamageType(card): the damage type a card or contact deals as the status rules read it (an explicit type that is not the camp's, else
        /// the profile's, else the magic type, the physical type or the school).
        /// </summary>
        public static string ProposalDamageType(CombatState c, JObject card)
        {
            if (card == null) return null;
            var typed = card.Obj(CK.CombatProfile) ?? new JObject();
            var explicitType = CanonicalDamageType(c, FirstText(card.Obj(CK.Attack)?[CK.DamageType], card[CK.DamageType], typed[CK.DamageType]));
            if (explicitType != null && explicitType != CV.CampMagic && explicitType != CV.CampPhysical) return explicitType;
            var profile = CoreStances.ProfileFor(c, card);
            var merged = Js.Spread(profile, typed);
            merged[K.Tags] = (!Js.Nullish(card[CK.CardTags]) ? card[CK.CardTags] : card[K.Tags])?.DeepClone();
            return CanonicalDamageType(c, FirstText(profile[CK.DamageType], MagicDamageTypeOf(c, merged) is string m ? Js.S(m) : null, card[DK.MagicDamageType], card[CK.PhysicalDamageType], card[CK.DamageSchool]));
        }

        private static string FirstText(params JToken[] tokens)
        {
            foreach (var t in tokens) if (Js.Truthy(t) && Js.IsStr(t)) return Js.Str(t);
            return null;
        }

        private sealed class SourceLines
        {
            public readonly JArray Lines = new JArray();

            public double Transition(string id, string label, double before, double after, string stage = null, double? multiplier = null)
            {
                if (before != after)
                {
                    var line = Js.Obj(DK.Id, id, DK.Label, label, DK.Amount, Whole(after) - Whole(before), DK.Requested, Whole(before), DK.Effective, Whole(after),
                        DK.Stage, stage ?? DV.StageOffense, DK.Operation, multiplier == null ? DV.OperationAdd : DV.OperationMultiply);
                    if (multiplier != null) line[DK.Multiplier] = Js.N(multiplier.Value);
                    Lines.Add(line);
                }
                return after;
            }
        }

        /// <summary>
        /// profileDamageReceipt(ctx, source, target, base, carrier, { roll: null, proposal }): the whole chain. <paramref name="rangeContact"/> is the engagement
        /// contact the hit preview reads (null for a fight with no range state); the engagement planner (<see cref="CoreRange.HitContact"/>) computes it.
        /// </summary>
        public static JObject ProfileDamageReceipt(CombatState c, JObject source, JObject target, double baseAmount, JObject carrier, JObject rangeContact = null, bool proposal = true) =>
            Receipt(c, source, target, baseAmount, carrier ?? new JObject(), rangeContact, proposal);

        /// <summary>
        /// previewDamageHits(ctx, source, target, base, tags, carrier, hits) for a configured fight (web combatDamagePreview.js): every hit is priced in turn on the previewed
        /// target and leaves its effect there (applyReceiptToPreviewTarget: Block, Barrier, Ward, Poise, and the Staggered stance and Vulnerable a Poise break brings), so a
        /// later hit sees what an earlier one spent. <paramref name="target"/> is a detached copy; it is changed. Returns one receipt per hit.
        /// </summary>
        public static List<JObject> PreviewHits(CombatState c, JObject source, JObject target, double baseAmount, JObject carrier, double hits)
        {
            var receipts = new List<JObject>();
            for (var hit = 0; hit < Math.Max(0, Math.Floor(hits)); hit++)
            {
                var receipt = ProfileDamageReceipt(c, source, target, baseAmount, carrier);
                receipts.Add(receipt);
                ApplyReceiptToPreviewTarget(c, target, receipt);
            }
            return receipts;
        }

        /// <summary>applyReceiptToPreviewTarget(ctx, defender, receipt): one resolved hit's effect on the previewed target, carried to its next hit.</summary>
        public static void ApplyReceiptToPreviewTarget(CombatState c, JObject target, JObject receipt)
        {
            target.Put(K.Block, receipt.Num(K.BlockRemaining));
            if (!CoreFixCore.Enabled(c)) return;
            target.Put(CK.Barrier, Math.Max(0, target.Or0(CK.Barrier) - receipt.Num(DK.BarrierMitigated)));
            var ward = target.Obj(CK.PersistentWard);
            if (ward != null) ward.Put(K.Value, Math.Max(0, ward.Num(K.Value) - receipt.Num(DK.WardMitigated)));
            var poise = target.Obj(CK.PersistentPoise);
            if (poise == null) return;
            var before = poise.Num(K.Value);
            poise.Put(K.Value, Math.Max(0, before - receipt.Num(DK.PoiseMitigated) - receipt.Or0(DK.PoiseDrain)));
            if (before > 0 && poise.Num(K.Value) == 0) PreviewPoiseBreak(c, target);
        }

        /// <summary>previewPoiseBreak(ctx, defender): a Poise break staggers the previewed target as live play does (the stance, the Vulnerable), and nothing else.</summary>
        private static void PreviewPoiseBreak(CombatState c, JObject target)
        {
            var rules = CoreFixCore.StaggeredRules(c);
            if (rules.Is(CK.Stance)) target[CK.CombatStance] = CoreImpact.StaggeredStance(target, rules);
            var stacks = rules.Num(DK.VulnerableStacks);
            if (!(stacks > 0)) return;
            var statuses = target.Obj(K.Statuses) ?? (JObject)(target[K.Statuses] = new JObject());
            var vulnerable = statuses.Obj(DV.StatusVulnerable) ?? (JObject)(statuses[DV.StatusVulnerable] = new JObject());
            vulnerable.Put(K.Stacks, vulnerable.Or0(K.Stacks) + stacks);
        }

        /// <summary>damageReceipt(targetId, hits): the receipts of a card's hits as the one damage receipt the card shows, its total the sum of what every hit takes from HP.</summary>
        public static JObject CombinedReceipt(JObject target, IReadOnlyList<JObject> receipts)
        {
            var hits = new JArray(receipts.SelectMany(r => Js.Items(r.Obj(DK.DamageReceipt)?[DK.Hits])).Select(h => h.DeepClone()));
            return new JObject { [DK.Version] = 1, [DK.TargetId] = target?[K.Id]?.DeepClone(), [DK.Hits] = hits, [DK.TotalDamage] = Js.N(receipts.Sum(r => r.Num(DK.HpLoss))) };
        }

        private static JObject Receipt(CombatState c, JObject source, JObject target, double baseAmount, JObject carrier, JObject rangeContact, bool proposal)
        {
            var effects = c.CoreData.Configured(CK.EffectsModule);
            var stances = c.CoreData.Configured(CK.StancesModule);
            var attacking = CoreStances.ConfiguredStance(c, carrier);
            var defending = CoreStances.ConfiguredStance(c, DefensiveCombatStance(c, target, carrier));
            var advantage = Js.Includes(stances?[CK.Include], attacking) && Js.Includes(stances?.Obj(CK.Matchups)?[attacking], defending);
            var flatReturn = Js.Truthy(carrier[DK.CounterReturnAttack]);
            var lines = new SourceLines();
            lines.Lines.Add(Js.Obj(DK.Id, DV.SourceBase, DK.Label, DV.LabelBase, DK.Amount, baseAmount, DK.Stage, DV.StageOffense, DK.Operation, DV.OperationSet));
            var profile = Js.Spread(carrier.Obj(CK.CombatProfile));
            if (Js.IsFinite(carrier[CK.MagicShare])) profile[CK.MagicShare] = carrier[CK.MagicShare].DeepClone();
            var share = MagicShare(c, profile);
            var spells = profile.Str(CK.Camp) == CV.CampSpell || share > 0 || Ratings.IsMagicalAttack(c, carrier);
            var ratingId = source?.Str(K.Kind) != V.Enemy && spells ? CK.RatingPr : CK.RatingAr;
            var ratingLabel = ratingId.ToUpperInvariant();
            var weight = Js.Or0(effects?[DK.AttackRatingWeight]);
            var ar = SourceRatingValue(c, source, ratingId, carrier.Str(K.SourceArmamentId), carrier.Is(DK.EquipmentScoped));
            var ratingBonus = carrier.Is(DK.SkipRatingBonus) ? 0 : ar * weight;
            var raw = baseAmount;
            var scoped = !string.IsNullOrEmpty(carrier.Str(K.SourceArmamentId)) || carrier.Is(DK.EquipmentScoped);
            var applicable = Js.Items(source?[K.RatingSources]).OfType<JObject>().Where(row =>
            {
                var sourceId = row.Str(K.SourceId);
                return row.Str(K.Kind) != V.EquipmentSource || !scoped || row.Str(DK.Slot) == DV.SlotArmor || (sourceId != null && sourceId.StartsWith(DV.ArmorPrefix, StringComparison.Ordinal))
                    || sourceId == carrier.Str(K.SourceArmamentId) || sourceId == DV.ArmamentPrefix + carrier.Str(K.SourceArmamentId);
            }).ToList();
            var sourceAr = applicable.Sum(row => Num0(row[ratingId]));
            if (sourceAr > 0 && ratingBonus > 0)
            {
                for (var index = 0; index < applicable.Count; index++)
                {
                    var row = applicable[index];
                    var contribution = Num0(row[ratingId]) * weight;
                    var rowId = Js.Truthy(row[K.SourceId]) ? row.Str(K.SourceId) : index.ToString(CultureInfo.InvariantCulture);
                    var label = (Js.Truthy(row[K.Name]) ? row.Str(K.Name) : row.Str(K.Kind)) + DV.LabelSeparator + ratingLabel;
                    raw = lines.Transition(ratingId + DV.IdSeparator + rowId, label, raw, raw + contribution);
                }
                raw = lines.Transition(DV.SourceArAdjustments, string.Format(CultureInfo.InvariantCulture, DV.LabelAdjustments, ratingLabel), raw, baseAmount + ratingBonus);
            }
            else raw = lines.Transition(DV.SourceAr, DV.LabelAr, raw, raw + ratingBonus);
            var stanceMultiplier = flatReturn ? 1 : CoreActions.StanceEffectiveness(c, source, carrier);
            raw = lines.Transition(DV.SourceMatchingStance, DV.LabelMatchingStance, raw, raw * stanceMultiplier, DV.StageOffense, stanceMultiplier);
            if (!flatReturn && Js.Or0(target?.Obj(K.Statuses)?.Obj(DV.StatusVulnerable)?[K.Stacks]) > 0)
            {
                var vulnerable = CoreFixCore.Family(c, CK.Statuses, target).Num(DK.VulnerabilityMultiplier);
                raw = lines.Transition(DV.SourceVulnerable, DV.LabelVulnerable, raw, raw * vulnerable, DV.StageOffense, vulnerable);
            }
            if (proposal && target != null && !flatReturn)
                raw = lines.Transition(DV.SourceStatusRules, DV.LabelStatusRules, raw, CoreProposal.HitPlan(c, source, target, raw, carrier, false).Num(DK.Amount));
            var defense = StanceMitigation(c, attacking, defending);
            var smashRule = advantage && attacking == CV.StanceSmash ? CoreFixCore.SmashVsDefensiveRules(c, CoreFixCore.Rules(c, target)) : null;
            var advantageMultiplier = smashRule != null ? smashRule.Num(DK.DamageMultiplier) : advantage && !flatReturn ? Js.Coalesce(stances[DK.AdvantageMultiplier], 1) : 1;
            var amount = smashRule != null
                ? lines.Transition(DV.SourceSmashVsDefensive, DV.LabelSmashVsDefensive, raw, raw * advantageMultiplier, DV.StageStance, advantageMultiplier)
                : lines.Transition(DV.SourceStanceAdvantage, DV.LabelStanceAdvantage, raw, raw * advantageMultiplier, DV.StageStance, advantageMultiplier);
            var defenseMultiplier = flatReturn ? 1 : defense.Num(DK.Multiplier);
            amount = lines.Transition(DV.SourceStanceDefense, DV.LabelStanceDefense, amount, amount * defenseMultiplier, DV.StageStance, defenseMultiplier);
            amount = Whole(amount);
            var hit = CoreHitRoll.Preview(c, source, target, carrier, new HitOptions { RangeContact = rangeContact, DamageType = ProposalDamageType(c, carrier) });
            var hitChance = (Js.Nullish(hit?[DK.HitChance]) ? Ashen.Generated.CombatMath.Percent : hit.Num(DK.HitChance)) / Ashen.Generated.CombatMath.Percent;
            double smashBlockBonus = 0;
            if (smashRule != null && target != null)
            {
                smashBlockBonus = Math.Floor(smashRule.Num(DK.BlockFraction) * Math.Max(0, target.Or0(K.Block)));
                if (smashBlockBonus > 0)
                {
                    lines.Lines.Add(Js.Obj(DK.Id, DV.SourceSmashBlockBonus, DK.Label, DV.LabelSmashBlockBonus, DK.Amount, smashBlockBonus, DK.Requested, amount, DK.Effective, amount + smashBlockBonus,
                        DK.Stage, DV.StageStance, DK.Operation, DV.OperationAdd));
                    amount += smashBlockBonus;
                }
            }
            var incoming = amount;
            var parts = SplitContactParts(incoming, baseAmount, ratingBonus, ratingId == CK.RatingPr || share >= 1 ? DV.PartMagic : DV.PartPhysical, share, share >= 1 ? DV.PartMagic : DV.PartPhysical);
            var physical = parts.Physical;
            var magical = parts.Magic;
            var blocked = Math.Min(Math.Max(0, target.Or0(K.Block)), physical);
            var barrierMitigated = Math.Min(Math.Max(0, target.Or0(CK.Barrier)), magical);
            lines.Transition(DV.SourceBlock, DV.LabelBlock, physical, physical - blocked, DV.StageMitigation);
            lines.Transition(DV.SourceBarrier, DV.LabelBarrier, magical, magical - barrierMitigated, DV.StageMitigation);
            var poiseMeetsMagic = CoreFixCore.WardDisabled(c);
            var poiseIn = physical - blocked + (poiseMeetsMagic ? magical - barrierMitigated : 0);
            JObject poise;
            if (smashRule != null) poise = Js.Obj(DK.Amount, poiseIn, DK.Reduced, 0, DK.Sources, new JArray());
            else poise = CoreFixCore.ProtectionReceipt(c, target, DV.KindPhysical, poiseIn);
            var ward = CoreFixCore.ProtectionReceipt(c, target, DV.KindMagical, poiseMeetsMagic ? 0 : magical - barrierMitigated);
            var poiseDrain = smashRule != null ? Math.Min(Math.Max(0, target.Obj(CK.PersistentPoise).Or0(K.Value)), Math.Floor(smashRule.Num(DK.PoiseFraction) * incoming)) : 0;
            var poiseMitigated = poise.Num(DK.Reduced);
            var wardMitigated = ward.Num(DK.Reduced);
            foreach (var line in (JArray)poise[DK.Sources]) lines.Lines.Add(line.DeepClone());
            foreach (var line in (JArray)ward[DK.Sources]) lines.Lines.Add(line.DeepClone());
            amount = poise.Num(DK.Amount) + ward.Num(DK.Amount);
            var hitEntry = Js.Obj(DK.Incoming, incoming, DK.Physical, physical, DK.Magical, magical, K.Blocked, blocked, DK.BarrierReduced, barrierMitigated, DK.WardReduced, wardMitigated,
                DK.PoiseReduced, poiseMitigated, DK.PoiseDamage, poiseDrain != 0 ? (object)poiseDrain : null, DK.HpLoss, amount, DK.Sources, lines.Lines);
            var receipt = new JObject
            {
                [DK.Raw] = Js.N(raw), [DK.WardMitigated] = Js.N(wardMitigated), [DK.BarrierMitigated] = Js.N(barrierMitigated),
                [DK.DamageReceipt] = new JObject { [DK.Version] = 1, [DK.TargetId] = target?[K.Id]?.DeepClone(), [DK.Hits] = new JArray(hitEntry), [DK.TotalDamage] = Js.N(amount) },
                [DK.HitChance] = Js.N(hitChance), [DK.Uncertain] = hitChance < 1,
                [DK.HpLossBounds] = hitChance < 1 ? new JArray(0, amount) : new JArray(amount, amount),
                [K.Amount] = Js.N(amount + blocked), [DK.HpLoss] = Js.N(amount), [K.Blocked] = Js.N(blocked), [DK.PoiseMitigated] = Js.N(poiseMitigated), [DK.PoiseDrain] = Js.N(poiseDrain),
                [K.BlockRemaining] = Js.N(Math.Max(0, target.Or0(K.Block) - blocked)), [DK.Evaded] = false, [DK.Advantage] = advantage,
            };
            if (smashRule != null) receipt[DK.SmashVsDefensive] = Js.Obj(DK.BlockBonus, smashBlockBonus, DK.PoiseDamage, poiseDrain);
            receipt[DK.DefenseAdvantage] = defense[DK.DefenseAdvantage].DeepClone();
            receipt[DK.StanceMitigation] = defense[DK.Mitigation].DeepClone();
            receipt[DK.AttackStance] = attacking;
            receipt[DK.DefenseStance] = defending;
            receipt[DK.Interrupted] = advantage && stances[DK.StaggerOnAdvantage] != null && stances[DK.StaggerOnAdvantage].Type == JTokenType.Boolean && stances[DK.StaggerOnAdvantage].Value<bool>()
                && Js.Includes(stances[DK.StaggerManeuvers] ?? DamageSection(c)[DK.DefaultStaggerManeuvers], attacking);
            return receipt;
        }
    }
}
