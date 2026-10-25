using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using S = Ashen.Generated.StringKeys;
using T = Ashen.Generated.CardTextKeys;

namespace Ashen.App.Combat
{
    /// <summary>What the tag rail already says of a card, so its rules text does not (web ui/components/cardTagSymbols.js cardSideTags: representedEffects, representedEntry, representedRules).</summary>
    public sealed class FaceRepresented
    {
        public List<int> Effects = new List<int>();
        public List<string> Entry = new List<string>();
        public List<string> Rules = new List<string>();
    }

    /// <summary>The rules text of one card face at the three levels the web draws it at (web ui/components/card.js renderCard): the hand's (a rest and the extra lines of a selection), a plain face's and the inspector's.</summary>
    public sealed class FaceRules
    {
        /// <summary>Every line the model phrased (null when it refused the card), before the profile's line limit.</summary>
        public List<FaceLine> Lines;

        public FaceRepresented Represented;

        /// <summary>The lines this level shows and, for the hand, which of them wait for the card to be selected.</summary>
        public List<FaceLine> HandLines;
        public List<bool> HandExtra;

        /// <summary>The rules as html (each line a face-line span, or the authored text), compacted.</summary>
        public string Hand;
        public string Face;
        public string Inspect;

        /// <summary>The card's own template with its numbers, none marked (card.js cardTooltip's text: what the inspector reads beside the face).</summary>
        public string Reading;
    }

    /// <summary>
    /// The pieces around cardFaceLines that make a card's rules text: the tag rail's represented sets (cardSideTags, cardBonusTags), the profile's line limits, the
    /// authored text a face the model refuses keeps (card.js fillTemplate), and the compaction of the joined lines (combatSigilView.js compactCardRules).
    /// </summary>
    public static class CardFaceRules
    {
        // ------------------------------------------------------------------ the rail's represented sets

        private sealed class SideTag
        {
            public string Id;
            public List<int> Effects = new List<int>();
            public List<string> Entry = new List<string>();
            public List<string> Rules = new List<string>();
        }

        private sealed class Fact
        {
            public int? Index;
            public string Entry;
        }

        private static bool PositiveOrVariable(FaceEnv env, JToken amount)
        {
            if (amount == null || amount.Type == JTokenType.Undefined) return false;
            var value = CardFaceText.ConstantFormulaValue(env, amount);
            return !value.HasValue || value.Value > 0;
        }

        private static bool CarriesHealthDamage(FaceEnv env, JObject def)
        {
            var visited = new HashSet<JToken>();
            bool Walk(JToken value, int depth)
            {
                if (value == null || !(value is JObject || value is JArray) || depth > env.Text.Json.Value<int>(T.WalkDepth) || !visited.Add(value)) return false;
                if (value is JObject o && o.Str(T.Op) == T.Damage && PositiveOrVariable(env, o[T.Amount])) return true;
                foreach (var child in value is JObject obj ? obj.Properties().Select(p => p.Value) : (IEnumerable<JToken>)(JArray)value)
                    if ((child is JObject || child is JArray) && Walk(child, depth + 1)) return true;
                return false;
            }
            return PositiveOrVariable(env, def.Obj(T.CounterPayload)?[T.Hp]) || env.Text.List(T.DamageWalkKeys).Any(key => Walk(def[key], 0));
        }

        /// <summary>cardBonusTags(def, preview): the quantified self benefits the rail carries, in the web's order, with what each one stands for.</summary>
        private static List<SideTag> BonusTags(FaceEnv env, JObject def, JObject preview, Func<JObject, int, string, JToken> value)
        {
            var c = env.State;
            var t = env.Text;
            var tags = new List<SideTag>();
            def = CardFaceText.WithChargedEffects(def, preview);
            var core = CoreFixCore.Enabled(c);
            var facts = new List<KeyValuePair<string, List<Fact>>>();
            bool Defined(string status) => c.Data.Statuses.Has(status) || t.In(T.ProtectionKinds, status);
            void Add(string status, Fact fact)
            {
                if (!Defined(status)) return;
                var found = facts.FirstOrDefault(pair => pair.Key == status);
                if (found.Value == null) { found = new KeyValuePair<string, List<Fact>>(status, new List<Fact>()); facts.Add(found); }
                found.Value.Add(fact);
            }
            var effects = Js.Items(def[T.Effects]).OfType<JObject>().ToList();
            for (var index = 0; index < effects.Count; index++)
            {
                var effect = effects[index];
                if (effect.Str(T.Op) != T.ApplyStatus || !t.In(T.BenefitStatuses, effect.Str(T.Status)) || effect.Str(T.Target) != T.Self) continue;
                if (!c.Data.Statuses.Has(effect.Str(T.Status))) continue;
                var amount = value(effect, index, T.Stacks);
                // A tag cannot turn an unknown condition, duration, chance or timing into an unconditional promise.
                var simple = effect.Properties().All(p => t.In(T.SimpleGrantKeys, p.Name));
                double? count = Js.IsInt(amount) && Js.D(amount) >= 0 ? Js.D(amount) : (double?)null;
                var metric = effect[T.Stacks] as JObject;
                if (!count.HasValue && core && effect.Str(T.Status) == T.Evade && metric != null && metric.Str(T.F) == T.Metric && metric.Str(T.MetricKey) == T.EvadeStackGain
                    && metric.Str(T.Of) == T.Self && metric.Properties().All(p => t.In(T.EvadeMetricKeys, p.Name)))
                    count = CoreFixCore.Family(c, T.Accuracy, null, def).Num(T.EvadeStackBase);
                if (!simple || !count.HasValue) continue;
                Add(effect.Str(T.Status), new Fact { Index = index, Entry = core && effect.Str(T.Status) == T.Evade && count.Value > 0 ? T.Evade : null });
            }
            if (def.Str(T.Type) != T.Power && (core || (Js.IsNum(def[T.DefenseRulesVersion]) && def.Num(T.DefenseRulesVersion) == Ashen.Generated.CombatMath.Two)
                || (Js.IsNum(def[T.MinCombatExpansionVersion]) && def.Num(T.MinCombatExpansionVersion) == Ashen.Generated.CombatMath.Two)))
            {
                var entry = CoreStances.EntryBonuses(c, def);
                foreach (var status in core ? t.List(T.CoreEntryStatuses) : t.List(T.EntryOrder))
                {
                    var baseAmount = entry.Num(status);
                    if (!(baseAmount > 0)) continue;
                    // Configured Core suppresses the automatic Evade grant if this card already applied Evade; do not print a second grant.
                    var selfEvade = core && status == T.Evade && effects.Any(e => e.Str(T.Op) == T.ApplyStatus && e.Str(T.Status) == T.Evade && e.Str(T.Target) == T.Self);
                    if (selfEvade)
                    {
                        var existing = facts.FirstOrDefault(pair => pair.Key == T.Evade).Value;
                        if (existing == null || !existing.Any(fact => fact.Entry == T.Evade)) Add(status, new Fact { Entry = status });
                        continue;
                    }
                    Add(status, new Fact { Entry = status });
                }
            }
            foreach (var pair in facts)
            {
                var bodyGrant = core && CardFaceText.EvadeGrantOnlyCard(def);
                var single = !bodyGrant && pair.Value.Count == 1 ? pair.Value[0] : null;
                var side = new SideTag { Id = (t.In(T.ProtectionKinds, pair.Key) ? t.Word(S.CardtextEffectPrefix) : t.Word(S.CardtextStatusPrefix)) + pair.Key };
                if (single != null && single.Index.HasValue) side.Effects.Add(single.Index.Value);
                foreach (var grant in pair.Value) if (grant.Entry != null) side.Entry.Add(grant.Entry);
                tags.Add(side);
            }
            return tags;
        }

        /// <summary>cardSideTags(model, def, registries, preview, rules): the sets of effects, stance entries and rules the first five tags of the rail stand for.</summary>
        public static FaceRepresented RepresentedBy(FaceEnv env, JObject def, JObject preview, IReadOnlyList<string> modelTags, Func<JObject, int, string, JToken> value)
        {
            var c = env.State;
            var t = env.Text;
            var tags = modelTags.ToList();
            var selected = new List<SideTag>();
            var camp = tags.FirstOrDefault(id => t.In(T.CampTags, id));
            var type = def.Str(T.Type);
            var category = type == T.Power ? T.Power : type == T.Skill && camp != t.Word(S.CardtextCampSpell) ? T.Skill : null;
            var healthDamage = CarriesHealthDamage(env, def);
            if (category != null) selected.Add(new SideTag { Id = t.Say(S.CardtextCategoryTag, category) });
            else if (camp != null && (camp != t.Word(S.CardtextCampPhysical) || healthDamage || string.IsNullOrEmpty(type))) selected.Add(new SideTag { Id = camp });
            var damage = healthDamage ? tags.Where(id => id.StartsWith(T.DamageTagPrefix, StringComparison.Ordinal)).ToList() : new List<string>();
            var element = tags.FirstOrDefault(id => id.StartsWith(t.Word(S.CardtextElementPrefix), StringComparison.Ordinal) && IsArtElement(t, id));
            var school = tags.FirstOrDefault(id => id.StartsWith(t.Word(S.CardtextSchoolPrefix), StringComparison.Ordinal));
            var evasion = Js.Truthy(def[T.Evade]) || Js.Items(def[T.Effects]).Any(e => Js.Str(Js.Get(e, T.Op)) == T.DodgeRoll) || tags.Any(id => t.In(T.EvadeTags, id));
            var identity = damage.Count > 0 ? damage : evasion ? new List<string> { t.Word(S.CardtextEvadeEffectTag) } : new[] { element ?? school }.Where(id => id != null).ToList();
            foreach (var id in identity) if (!selected.Any(row => row.Id == id) && selected.Count < t.Json.Value<int>(T.RailMax)) selected.Add(new SideTag { Id = id });

            var statuses = new List<string>();
            void AddStatus(string id) { if (c.Data.Statuses.Has(id) && !statuses.Contains(id)) statuses.Add(id); }
            foreach (var id in tags) if (id.StartsWith(t.Word(S.CardtextStatusPrefix), StringComparison.Ordinal)) AddStatus(id.Substring(t.Word(S.CardtextStatusPrefix).Length));
            var visited = new HashSet<JToken>();
            void Walk(JToken token, int depth)
            {
                if (token == null || !(token is JObject || token is JArray) || depth > t.Json.Value<int>(T.WalkDepth) || !visited.Add(token)) return;
                if (token is JObject o && (o.Str(T.Op) == T.ApplyStatus || o.Str(T.Op) == T.Buildup) && Js.IsStr(o[T.Status])) AddStatus(o.Str(T.Status));
                foreach (var child in token is JObject obj ? obj.Properties().Select(p => p.Value).ToList() : ((JArray)token).ToList()) Walk(child, depth + 1);
            }
            foreach (var effect in Js.Items(def[T.Effects])) Walk(effect, 0);
            foreach (var key in t.List(T.StatusWalkKeys)) Walk(def[key], 0);

            var benefits = BonusTags(env, def, preview, value);
            if (!string.IsNullOrEmpty(env.SmashClause(def))) benefits.Add(new SideTag { Id = t.Word(S.CardtextSmashTag), Rules = new List<string> { T.SmashRule } });
            foreach (var benefit in benefits)
            {
                var existing = selected.FindIndex(tag => tag.Id == t.Word(S.CardtextEvadeEffectTag) && benefit.Id == t.Say(S.CardtextStatusTag, T.Evade));
                if (existing >= 0) selected.RemoveAt(existing);
                if (benefit.Id.StartsWith(t.Word(S.CardtextStatusPrefix), StringComparison.Ordinal)) statuses.Remove(benefit.Id.Substring(t.Word(S.CardtextStatusPrefix).Length));
            }
            var max = t.Json.Value<int>(T.RailMax);
            var keep = Math.Max(0, max - benefits.Count);
            if (selected.Count > keep) selected.RemoveRange(keep, selected.Count - keep);
            selected.AddRange(benefits);
            var traitIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in tags)
            {
                if (!id.StartsWith(t.Word(S.CardtextTraitPrefix), StringComparison.Ordinal) || !t.In(T.SymbolTraits, id.Substring(t.Word(S.CardtextTraitPrefix).Length)) || traitIds.Contains(id)) continue;
                if (selected.Count >= max) break;
                traitIds.Add(id);
                selected.Add(new SideTag { Id = id });
            }
            foreach (var id in statuses.Take(t.Json.Value<int>(T.StatusMax)))
            {
                if (selected.Count >= max) break;
                selected.Add(new SideTag { Id = t.Say(S.CardtextStatusTag, id) });
            }
            var result = new FaceRepresented();
            foreach (var tag in selected.Take(max))
            {
                result.Effects.AddRange(tag.Effects);
                result.Entry.AddRange(tag.Entry);
                result.Rules.AddRange(tag.Rules);
                if (tag.Id.StartsWith(t.Word(S.CardtextStatusPrefix), StringComparison.Ordinal))
                {
                    var status = tag.Id.Substring(t.Word(S.CardtextStatusPrefix).Length);
                    if (c.Data.Statuses.Has(status) && !string.IsNullOrEmpty(c.Data.Statuses.Get(status).Str(T.Tooltip))) result.Rules.Add(tag.Id);
                }
            }
            return result;
        }

        private static bool IsArtElement(CardTextData t, string value)
        {
            var key = value.ToLowerInvariant();
            key = key.Substring(key.LastIndexOf(t.Word(S.CardtextColon)[0]) + 1);
            return t.In(T.ArtElements, key) || t.Map(T.ArtAliases, key) != null;
        }

        // ------------------------------------------------------------------ the model's tags

        /// <summary>The tag ids of the card the face reads: its tags the registry knows, then the ones its combat profile adds (playingCardModel's tags and combatProfileTags).</summary>
        public static List<string> ModelTags(FaceEnv env, JObject def)
        {
            var c = env.State;
            var t = env.Text;
            var known = new HashSet<string>(env.KnownTags, StringComparer.Ordinal);
            var own = (def[T.CardTags] as JArray ?? def[T.Tags] as JArray)?.Select(x => Js.IsStr(x) ? Js.Str(x) : Js.Str(Js.Get(x, T.Id))).ToList() ?? new List<string>();
            var ids = own.Where(known.Contains).ToList();
            var profile = CoreStances.ProfileFor(c, def);
            var fromProfile = new List<string>();
            void Push(string prefix, string value) { if (!string.IsNullOrEmpty(value)) fromProfile.Add(prefix + value); }
            Push(t.Word(S.CardtextCampPrefix), profile.Str(T.Camp));
            Push(t.Word(S.CardtextManeuverPrefix), profile.Str(T.Maneuver));
            Push(t.Word(S.CardtextSchoolPrefix), profile.Str(T.School));
            Push(T.DamageTagPrefix, profile.Str(T.DamageType));
            Push(t.Word(S.CardtextCounterPrefix), profile.Str(T.CounterMode));
            Push(t.Word(S.CardtextRangePrefix), profile.Str(T.Range));
            Push(t.Word(S.CardtextTargetingPrefix), profile.Str(T.Targeting));
            foreach (var trait in Js.Items(profile[T.Traits])) Push(t.Word(S.CardtextTraitPrefix), Js.Str(trait));
            foreach (var id in fromProfile.Where(known.Contains)) if (!ids.Contains(id)) ids.Add(id);
            return ids;
        }

        // ------------------------------------------------------------------ the authored text (fillTemplate)

        private static readonly Regex TokenPattern = new Regex(Ashen.Generated.CombatPatterns.TextToken, RegexOptions.CultureInvariant);

        /// <summary>card.js fillTemplate: the card's own template with its numbers, each a val span (up or down when the preview moved it), Bleed and the keywords marked.</summary>
        public static string FillTemplate(FaceEnv env, JObject def, JObject tokens, JObject baseTokens, JArray sequences, JObject preview)
        {
            var t = env.Text;
            var template = def.Str(T.TextTemplate) ?? string.Empty;
            // A Core Smash adds the shared rule sentence once; an authored template that has it is left alone.
            var smash = env.SmashClause(def);
            if (!string.IsNullOrEmpty(smash) && !template.Contains(smash)) template = t.Say(S.CardtextSpaced, template.Trim(), smash);
            var liveTokens = (JObject)tokens.DeepClone();
            var compared = baseTokens == null ? null : (JObject)baseTokens.DeepClone();
            // Complex cards retain authored clauses; their defensive amount still reads the same allocation as the compact face and combat execution.
            template = Regex.Replace(template, t.Word(S.CardtextDefensePattern), m =>
            {
                var token = m.Groups[1].Value;
                if (token.StartsWith(T.Block, StringComparison.Ordinal) && !(Js.IsNum(def[T.DefenseRulesVersion]) && def.Num(T.DefenseRulesVersion) == Ashen.Generated.CombatMath.Two)
                    && !(Js.IsNum(def[T.MinCombatExpansionVersion]) && def.Num(T.MinCombatExpansionVersion) == Ashen.Generated.CombatMath.Two)) return m.Value;
                var liveBlock = liveTokens[token + T.BlockSuffix];
                var liveWard = liveTokens[token + T.WardSuffix];
                var split = Js.IsFinite(liveBlock) && Js.IsFinite(liveWard) ? new JObject { [T.Block] = liveBlock, [T.Ward] = liveWard } : env.SpellDefenseSplit(def, liveTokens[token]);
                if (split == null) return m.Value;
                liveTokens[token + T.BlockSuffix] = split[T.Block].DeepClone();
                liveTokens[token + T.WardSuffix] = split[T.Ward].DeepClone();
                var baseSplit = baseTokens != null ? env.SpellDefenseSplit(def, baseTokens[token]) : null;
                if (baseSplit != null)
                {
                    compared[token + T.BlockSuffix] = baseSplit[T.Block].DeepClone();
                    compared[token + T.WardSuffix] = baseSplit[T.Ward].DeepClone();
                }
                return t.Say(S.CardtextDefenseReplacement, token);
            });
            foreach (var sequence in Js.Items(sequences).OfType<JObject>())
            {
                if (!Js.IsStr(sequence[T.AmountToken]) || !Js.IsStr(sequence[T.HitsToken]) || !(sequence[T.HitDamages] is JArray damages) || damages.Count < Ashen.Generated.CombatMath.Two
                    || damages.All(x => Js.D(x) == Js.D(damages[0]))) continue;
                var pattern = t.Word(S.CardtextSequencePattern).Replace(t.Word(S.CardtextAmountMark), Regex.Escape(sequence.Str(T.AmountToken))).Replace(t.Word(S.CardtextHitsMark), Regex.Escape(sequence.Str(T.HitsToken)));
                var replacement = t.Word(S.CardtextSequenceReplacement).Replace(t.Word(S.CardtextAmountMark), sequence.Str(T.AmountToken))
                    .Replace(t.Word(S.CardtextTotalMark), CardTextData.Show(sequence[T.TotalDamage])).Replace(t.Word(S.CardtextCountMark), damages.Count.ToString(CultureInfo.InvariantCulture));
                template = Regex.Replace(template, pattern, replacement);
                liveTokens[sequence.Str(T.AmountToken)] = Js.S(string.Join(t.Word(S.CardtextPlusJoin), damages.Select(x => CardTextData.Show(x))));
            }
            var html = t.EscBasic(template);
            html = TokenPattern.Replace(html, m =>
            {
                var name = m.Groups[1].Value;
                var v = liveTokens[name];
                if (Js.Nullish(v)) return m.Value;
                var cls = t.Word(S.CardtextClassVal);
                if (compared != null && Js.IsNum(v) && Js.IsNum(compared[name]))
                {
                    if (Js.D(v) > Js.D(compared[name])) cls += t.Word(S.CardtextClassUp);
                    else if (Js.D(v) < Js.D(compared[name])) cls += t.Word(S.CardtextClassDown);
                }
                return t.Say(S.CardtextVal, cls, t.EscBasic(CardTextData.Show(v)));
            });
            foreach (var mark in Js.Items(t.Json[T.Marks]).OfType<JArray>()) html = Regex.Replace(html, mark[0].Value<string>(), mark[1].Value<string>());
            _ = preview;
            return html;
        }

        // ------------------------------------------------------------------ the three levels

        /// <summary>
        /// The rules a face draws for a card: the shared model's lines (cut to the profile's face limit on a plain face and in the hand, whole in the inspector) or, when the
        /// model refuses the card, its authored text; a Counter's refusal keeps the damage label in front of it.
        /// </summary>
        public static FaceRules Render(FaceEnv env, JObject def, JObject preview)
        {
            var t = env.Text;
            var c = env.State;
            var result = new FaceRules();
            var modelTags = ModelTags(env, def);
            // The numbers a rail count or a body line reads, from the same preview.
            var summaryTokens = CardFaceText.StaticTokens(env, def);
            var baseTokens = summaryTokens;
            var tokens = preview?.Obj(T.Tokens) is JObject live ? Js.Spread(summaryTokens, live) : (JObject)summaryTokens.DeepClone();
            var represented = RepresentedBy(env, def, preview, modelTags, CardFaceText.Reader(env, def, preview));
            result.Represented = represented;
            var options = new FaceOptions { StanceEntry = false, RepresentedEffects = represented.Effects, RepresentedEntry = represented.Entry, RepresentedRules = represented.Rules };
            var baseHidden = t.In(T.BaseCardIds, def.Str(T.Id));
            var lines = baseHidden ? null : CardFaceText.Lines(env, def, preview, options);
            result.Lines = lines;
            var cardsModule = c.CoreData.Module(T.CardsModule);
            var configured = c.CoreData.Profile.Obj(T.Foundation)?.Str(T.Mode) == T.Configured;
            var limit = Math.Min(cardsModule != null && Js.IsNum(cardsModule[T.FaceMaxEffects]) ? cardsModule.Num(T.FaceMaxEffects) : double.PositiveInfinity,
                cardsModule != null && Js.IsNum(cardsModule[T.FaceMaxLines]) ? cardsModule.Num(T.FaceMaxLines) : double.PositiveInfinity);
            var sliced = lines == null ? null : configured ? lines.Take((int)Math.Min(limit, lines.Count)).ToList() : lines;
            var priced = Js.Truthy(def[T.WeightClassPriced]);
            var note = new FaceLine { Kind = T.Detail, Html = t.Word(S.CardtextWeightNote) };
            var faceLines = sliced != null && priced ? sliced.Concat(new[] { note }).ToList() : sliced;
            var everyLine = lines != null && priced ? lines.Concat(new[] { note }).ToList() : lines;
            result.HandLines = faceLines;
            var handLimit = t.Json.Value<int>(T.HandLines);
            result.HandExtra = faceLines?.Select((line, i) => i >= handLimit || (line.Kind == T.Detail && i > 0)).ToList();

            string Authored()
            {
                var expanded = (preview != null && Js.IsNum(preview[T.CombatExpansionVersion]) && preview.Num(T.CombatExpansionVersion) == Ashen.Generated.CombatMath.Two)
                    || (Js.IsNum(def[T.DefenseRulesVersion]) && def.Num(T.DefenseRulesVersion) == Ashen.Generated.CombatMath.Two)
                    || (Js.IsNum(def[T.MinCombatExpansionVersion]) && def.Num(T.MinCombatExpansionVersion) == Ashen.Generated.CombatMath.Two);
                var label = expanded && (Js.Items(def[T.Effects]).Any(e => Js.Str(Js.Get(e, T.Op)) == T.Damage) || Js.Truthy(def.Obj(T.CounterPayload)?[T.Hp])) ? env.DamageLabel(def, preview, null) : string.Empty;
                var sequences = preview?[T.DamageSequences] as JArray;
                var text = FillTemplate(env, def, tokens, baseTokens, sequences, preview);
                if (priced) text = t.Say(S.CardtextWeightAuthored, Regex.Replace(text, t.Word(S.CardtextLightPattern), string.Empty));
                return t.Compact((!string.IsNullOrEmpty(label) ? t.Say(S.CardtextLabelled, t.EscBasic(label), text) : text));
            }
            string Join(List<FaceLine> shown, bool hand)
            {
                if (shown == null) return Authored();
                return t.Compact(string.Concat(shown.Select((line, i) => t.Say(S.CardtextFaceLine, line.Kind, hand && result.HandExtra[i] ? t.Word(S.CardtextExtraAttribute) : string.Empty, line.Html))));
            }
            result.Hand = Join(faceLines, true);
            result.Face = Join(faceLines, false);
            result.Inspect = Join(everyLine, false);
            result.Reading = FillTemplate(env, def, tokens, null, preview?[T.DamageSequences] as JArray, preview);
            return result;
        }

        private static string TokenFor(FaceEnv env, JObject def, int index, string field)
        {
            var bindings = CombatPreview.TokenBindings(env.State.Data, def[T.Effects]);
            return bindings.TryGetValue(index.ToString(CultureInfo.InvariantCulture) + Ashen.Generated.CombatValues.KeySeparator + field, out var token) ? token : null;
        }
    }
}
