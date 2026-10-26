using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using S = Ashen.Generated.StringKeys;
using T = Ashen.Generated.CardTextKeys;

namespace Ashen.App.Combat
{
    /// <summary>The two parts of a hit that is part magic and part physical (web model/playingCard.js splitOf): their amounts and their damage types.</summary>
    public sealed class FaceSplit
    {
        public double Magic;
        public double Physical;
        public string MagicType;
        public string PhysicalType;
    }

    /// <summary>
    /// What the card face reads beyond the card: the fight (or, outside one, a fight-shaped state holding only the profile's data: <see cref="Bare"/>), the words, the tag labels.
    /// The web passes its registries and the run's rules; every rule the face asks of them is asked of the one state here.
    /// </summary>
    public sealed class FaceEnv
    {
        public readonly CombatState State;
        public readonly CardTextData Text;
        private readonly IReadOnlyDictionary<string, string> _tagLabels;

        public FaceEnv(CombatState state, CardTextData text)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            Text = text ?? throw new ArgumentNullException(nameof(text));
            _tagLabels = state.CoreData?.TagLabels ?? new Dictionary<string, string>();
        }

        /// <summary>A state with the profile's data and nothing in it: what a reward, shop or deck face reads (the web prices those with the registries and the run's rules alone).</summary>
        public static CombatState Bare(CoreCombatData data)
        {
            var state = new CombatState { Data = data.Combat, CoreData = data, Player = new JObject() };
            state.Fields[Core2CombatKeys.CombatExpansionVersion] = Js.N(data.ExpandedVersion);
            state.Fields[Core2CombatKeys.CombatExpansionRules] = data.Run.CombatExpansionRules.DeepClone();
            return state;
        }

        /// <summary>wardReadsAsPoise: the profile has Core rules with Ward off, so a Ward damage effect reads as Poise.</summary>
        public bool WardReadsAsPoise => CoreFixCore.WardDisabled(State);

        public string TagLabel(string id) => _tagLabels.TryGetValue(id, out var label) ? label : null;

        /// <summary>The ids of every tag the profile's registry holds (a tag outside it is dropped from a card's face, as the web's tag service drops it).</summary>
        public IEnumerable<string> KnownTags => _tagLabels.Keys;

        // ------------------------------------------------------------------ damage words and splits

        /// <summary>combatCardDamageLabel(def, preview, registries, index): the damage types of the card's hits ("Fire", "Blunt/Piercing"); the whole card when no index is given.</summary>
        public string DamageLabel(JObject def, JObject preview, int? index)
        {
            var c = State;
            var effects = Js.Items(def[T.Effects]).ToList();
            var contacts = new List<KeyValuePair<JObject, JObject>>();
            JObject Live(int at) => (preview?[T.Values] as JArray)?.ElementAtOrDefault(at) as JObject;
            if (index == null)
                for (var at = 0; at < effects.Count; at++)
                {
                    if (Js.Str(Js.Get(effects[at], T.Op)) == T.Damage) contacts.Add(new KeyValuePair<JObject, JObject>(effects[at] as JObject, Live(at)));
                }
            else contacts.Add(new KeyValuePair<JObject, JObject>(effects.ElementAtOrDefault(index.Value) as JObject, Live(index.Value)));
            if (contacts.Count == 0)
                contacts = Js.Items(preview?[T.Values]).OfType<JObject>().Where(row => row.Str(T.Op) == T.Damage).Select(row => new KeyValuePair<JObject, JObject>(null, row)).ToList();
            if (contacts.Count == 0) contacts.Add(new KeyValuePair<JObject, JObject>(null, null));
            var profile = CoreStances.ProfileFor(c, def);
            List<string> authored = def[T.CardTags] is JArray cardTags ? cardTags.Select(Js.Str).ToList()
                : def[T.Tags] is JArray tags ? tags.Select(Js.Str).ToList()
                : Js.Truthy(profile[T.DamageType]) ? new List<string> { T.DamageTagPrefix + profile.Str(T.DamageType) } : new List<string>();
            var ids = new List<string>();
            foreach (var contact in contacts)
            {
                var live = contact.Value;
                IEnumerable<string> found;
                if (live != null && live.Str(T.Op) == T.Damage && live[T.Tags] is JArray liveTags) found = liveTags.Select(Js.Str);
                else
                {
                    var attack = contact.Key?.Obj(T.Attack) ?? def.Obj(T.Attack);
                    found = attack?[T.Components] is JArray components ? components.Select(x => T.DamageTagPrefix + Js.Str(Js.Get(x, T.Type)))
                        : Js.Truthy(attack?[T.DamageType]) ? new[] { T.DamageTagPrefix + attack.Str(T.DamageType) } : authored;
                }
                foreach (var id in found) if (id != null && id.StartsWith(T.DamageTagPrefix, StringComparison.Ordinal) && !ids.Contains(id)) ids.Add(id);
            }
            string Resolve(IEnumerable<string> list) => string.Join(Text.Word(S.CardtextSlash), list.Select(TagLabel).Where(label => label != null));
            // A hit that is all magic but carries a physical damage tag (lightning with piercing) names both: its magic type, then the physical.
            var carrier = Js.Spread(profile);
            carrier[T.MagicShare] = def[T.MagicShare]?.DeepClone();
            carrier[T.PhysicalDamageType] = def[T.PhysicalDamageType]?.DeepClone();
            var magicType = CoreDamage.MagicDamageTypeOf(c, carrier);
            string TypeOf(string id) => CoreDamage.CanonicalDamageType(c, id.Substring(T.DamageTagPrefix.Length));
            var physical = ids.Where(id => CoreDamage.IsPhysicalDamageType(c, TypeOf(id))).ToList();
            if (magicType != null && physical.Count > 0 && !ids.Any(id => CoreDamage.IsMagicDamageType(c, TypeOf(id))) && CoreDamage.MagicShareOf(c, carrier) >= 1)
                return Text.Say(S.CardtextMagicPhysical, Resolve(new[] { T.DamageTagPrefix + magicType }), Resolve(physical));
            return Resolve(ids);
        }

        /// <summary>splitOf(def, profile, amount, ratings, live): the parts of a hit of this amount from this card, or null when the card is whole magic or whole physical.</summary>
        public FaceSplit SplitOf(JObject def, JObject profile, JToken amount, JObject live)
        {
            if (!Js.IsFinite(amount)) return null;
            var c = State;
            var carrier = Js.Spread(profile);
            carrier[T.MagicShare] = def[T.MagicShare]?.DeepClone();
            carrier[T.PhysicalDamageType] = !Js.Nullish(def[T.PhysicalDamageType]) ? def[T.PhysicalDamageType].DeepClone() : live?[T.PhysicalDamageType]?.DeepClone();
            var share = CoreDamage.MagicShareOf(c, carrier);
            if (!(share > 0 && share < 1)) return null;
            var parts = CoreDamage.SplitContactParts(Js.D(amount), !Js.Nullish(live?[T.SplitBase]) ? Js.D(live[T.SplitBase]) : Js.D(amount), Js.Or0(live?[T.RatingBonus]),
                Js.Str(live?[T.RatingPart]), share, profile.Str(T.Camp) == T.Spell ? Ashen.Generated.Core2DamageValues.PartMagic : Ashen.Generated.Core2DamageValues.PartPhysical);
            var types = CoreDamage.SplitDamageTypesOf(c, carrier);
            return new FaceSplit { Magic = parts.Magic, Physical = parts.Physical, MagicType = types.Magic, PhysicalType = types.Physical };
        }

        /// <summary>spellDefenseSplit(card, amount): the Block and the Ward a Spell's protection splits into, or null for a card this does not apply to.</summary>
        public JObject SpellDefenseSplit(JObject card, JToken amount)
        {
            var profile = card.Obj(T.CombatProfile) ?? CoreStances.ProfileFor(State, card);
            var maneuver = profile.Str(T.Maneuver);
            if (profile.Str(T.Camp) != T.Spell || !((maneuver == T.Defend || maneuver == T.Counter) || (string.IsNullOrEmpty(maneuver) && card.Str(T.Type) == T.Skill))
                || Js.Truthy(card[T.Evade]) || Js.Truthy(profile[T.Evade]) || !Js.IsFinite(amount)) return null;
            var rules = State.CoreData.Section(T.SpellDefenseSection);
            var school = profile.Str(T.School);
            var wardShare = school != null && Js.IsNum(rules.Obj(T.Schools)?[school]) ? rules.Obj(T.Schools).Num(school) : rules.Num(T.DefaultWardShare);
            var total = Math.Max(0, Math.Floor(Js.D(amount)));
            var block = Math.Floor(total * (1 - wardShare) + Ashen.Generated.CombatMath.Half);
            return new JObject { [T.Block] = Js.N(block), [T.Ward] = Js.N(total - block) };
        }

        // ------------------------------------------------------------------ Smash

        private List<string> SmashBeats()
        {
            var stances = State.CoreData.Configured(T.StancesModule);
            if (!Js.Includes(stances?[T.Include], T.Smash)) return new List<string>();
            return Js.Items(stances.Obj(T.Matchups)?[T.Smash]).Select(Js.Str).Distinct().ToList();
        }

        /// <summary>The web's num(): Math.round(n * 1000) / 1000, halves up.</summary>
        private static double Rounded(double value) => Math.Round(value * Ashen.Generated.CardTextMath.Thousand, MidpointRounding.AwayFromZero) / Ashen.Generated.CardTextMath.Thousand;

        private string Fraction(double value)
        {
            var words = Text.Table(T.Fractions);
            var key = Text.Say(S.CardtextFractionKey, CardTextData.Num(value));
            return words[key] != null ? words.Str(key) : Text.Say(S.CardtextPercentWord, CardTextData.Num(Rounded(value * Ashen.Generated.CombatMath.Percent)));
        }

        private string Fill(string key)
        {
            var rules = CoreFixCore.SmashVsDefensiveRules(State, CoreFixCore.Rules(State));
            var multiplier = rules.Num(T.DamageMultiplier);
            var words = new Dictionary<string, string>
            {
                [T.Mult] = multiplier == Ashen.Generated.CombatMath.Two ? Text.Word(S.CardtextDouble) : Text.Say(S.CardtextMultiplier, CardTextData.Num(Rounded(multiplier))),
                [T.Block] = Fraction(rules.Num(T.BlockFraction)),
                [T.Poise] = Fraction(rules.Num(T.PoiseFraction)),
                [T.Stances] = ListWords(SmashBeats().Select(id => Text.Strings.Has(Text.Say(S.CardtextStanceNameKey, id)) ? Text.Word(Text.Say(S.CardtextStanceNameKey, id)) : char.ToUpperInvariant(id[0]) + id.Substring(1)).ToList()),
            };
            var template = Text.Word(key);
            return System.Text.RegularExpressions.Regex.Replace(template, Text.Word(S.CardtextPlaceholderPattern), m => words.TryGetValue(m.Groups[1].Value, out var word) ? word : m.Value);
        }

        private string ListWords(List<string> names) =>
            names.Count < Ashen.Generated.CombatMath.Two ? string.Join(string.Empty, names) : string.Join(Text.Word(S.CardtextListJoin), names.Take(names.Count - 1)) + Text.Word(S.CardtextOrWord) + names[names.Count - 1];

        /// <summary>coreSmashClause(def): the sentence a Smash says under a Core profile, else empty.</summary>
        public string SmashClause(JObject def)
        {
            if (def == null || CoreStances.ConfiguredStance(State, def) != T.Smash) return string.Empty;
            if (!Js.Items(def[T.Effects]).Any(effect => Js.Str(Js.Get(effect, T.Op)) == T.Damage)) return string.Empty;
            if (!CoreFixCore.Enabled(State) || SmashBeats().Count == 0) return string.Empty;
            return Fill(S.CardtextSmashClause);
        }
    }
}
