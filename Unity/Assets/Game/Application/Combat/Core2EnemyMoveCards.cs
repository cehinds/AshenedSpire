using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using J = Ashen.Generated.InspectorKeys;
using N = Ashen.Generated.InspectorNumbers;
using S = Ashen.Generated.StringKeys;
using X = Ashen.Generated.InspectorValues;

namespace Ashen.App.Combat
{
    /// <summary>One read-only action card of an enemy (web model/enemyMoveCards.js): its name, what it does, and the line of state and tags under it.</summary>
    public sealed class EnemyMoveCard
    {
        public string MoveId;
        public string Name;
        public string Detail;
        public string Meta;
        public bool Active;
        public bool Locked;
        public JObject Profile;
    }

    /// <summary>
    /// The enemy's move cards as the combatant door reads them (web <c>src/model/enemyMoveCards.js</c> enemyMoveCards and enemyActionEffectText, with combatCardProfile.js combatProfileTags): no randomness, no mutation,
    /// no selection; the live numbers come from the move's own preview (<see cref="CoreEnemyPreview"/>).
    /// </summary>
    public static class Core2EnemyMoveCards
    {
        private static string Say(StringTable strings, string key, params string[] pairs)
        {
            var args = new StringArgs();
            for (var i = 0; i + 1 < pairs.Length; i += (int)N.Two) args.Add(pairs[i], pairs[i + 1]);
            return strings.Format(key, args);
        }

        /// <summary>words(value) of the card model: the first letter capitalised, camel case and the dashes and underscores spaced.</summary>
        public static string Words(string value)
        {
            var text = Regex.Replace(Regex.Replace(value ?? string.Empty, X.WordsCamel, X.WordsCamelTo), X.WordsSeparator, X.Space);
            return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        private static string Value(JToken input) => Js.IsNum(input) || Js.IsStr(input) ? (Js.IsNum(input) ? CombatText.Number(Js.D(input)) : Js.Str(input)) : null;

        private static string Plural(StringTable strings, double count) => count == 1 ? string.Empty : strings.Get(S.CombatantPluralS);

        /// <summary>enemyActionEffectText(effect, registries): one opcode of an enemy action in words.</summary>
        public static string EffectText(CombatState c, StringTable strings, JObject effect)
        {
            if (Js.Truthy(effect[J.ConditionUncertain]))
            {
                var plain = (JObject)effect.DeepClone();
                plain[J.ConditionUncertain] = false;
                return Say(strings, S.CombatantMoveUncertain, X.ArgText, EffectText(c, strings, plain));
            }
            var op = effect.Str(J.Op);
            string Val(JToken input) => Value(input) ?? strings.Get(S.CombatantMoveVariable);
            var targetId = effect.Str(J.Target);
            var target = targetId == X.TargetSelf ? strings.Get(S.CombatantMoveSelf)
                : targetId == X.TargetPlayer ? strings.Get(S.CombatantMovePlayer)
                : Js.Truthy(effect[J.Target]) ? Words(targetId) : strings.Get(S.CombatantMoveDefaultTarget);
            string StatusName(JToken id) => c.Data.Statuses.Has(Js.Str(id) ?? string.Empty) && Js.Truthy(c.Data.Statuses.Get(Js.Str(id)).Str(J.Name)) ? c.Data.Statuses.Get(Js.Str(id)).Str(J.Name) : Words(Js.Str(id));
            if (op == X.OpApplyStatus)
            {
                var stacks = !Js.Nullish(effect[J.Stacks]) ? effect[J.Stacks] : !Js.Nullish(effect[J.Value]) ? effect[J.Value] : !Js.Nullish(effect[J.Amount]) ? effect[J.Amount] : Js.N(1);
                var text = Say(strings, S.CombatantMoveApply, X.ArgValue, Val(stacks), X.ArgName, StatusName(effect[J.Status]), X.ArgTarget, target);
                return Js.Nullish(effect[J.Duration]) ? text : text + Say(strings, S.CombatantMoveForTurns, X.ArgValue, Val(effect[J.Duration]));
            }
            if (op == X.OpHeal) return Say(strings, S.CombatantMoveHeal, X.ArgTarget, target, X.ArgValue, Val(effect[J.Amount]));
            if (op == X.OpBlock) return Say(strings, S.CombatantMoveBlock, X.ArgTarget, target, X.ArgValue, Val(effect[J.Amount]));
            if (op == X.OpPoiseDamage) return Say(strings, S.CombatantMovePoise, X.ArgValue, Val(effect[J.Amount]), X.ArgTarget, target);
            if (op == X.OpAddCard)
            {
                var card = effect.Str(J.Card);
                var name = card != null && c.Data.Cards.Has(card) && Js.Truthy(c.Data.Cards.Get(card).Str(J.Name)) ? c.Data.Cards.Get(card).Str(J.Name) : Words(card);
                var text = Say(strings, S.CombatantMoveAddCard, X.ArgName, name, X.ArgPile, Words(effect.Str(J.Pile)));
                return Js.Truthy(effect[J.Position]) ? text + Say(strings, S.CombatantMovePosition, X.ArgValue, effect.Str(J.Position)) : text;
            }
            if (op == X.OpDamage)
            {
                var text = Say(strings, S.CombatantMoveDamage, X.ArgValue, Val(effect[J.Amount]), X.ArgTarget, target);
                return Js.D(effect[J.Hits]) > 1 ? Say(strings, S.CombatantMoveDamageHits, X.ArgValue, Val(effect[J.Amount]), X.ArgHits, CombatText.Number(Js.D(effect[J.Hits])), X.ArgTarget, target) : text;
            }
            if (op == X.OpMoveEngagement)
            {
                var bands = Js.D(effect[J.Bands]);
                var count = Math.Abs(bands);
                return Say(strings, S.CombatantMoveBands, X.ArgName, strings.Get(bands < 0 ? S.CombatantMoveClose : S.CombatantMoveRetreat), X.ArgValue, CombatText.Number(count), X.ArgPlural, Plural(strings, count));
            }
            if (op == X.OpWardDamage) return Say(strings, S.CombatantMoveWard, X.ArgValue, Val(effect[J.Amount]), X.ArgTarget, target);
            if (op == X.OpBuildup) return Say(strings, S.CombatantMoveBuildup, X.ArgValue, Val(effect[J.Amount]), X.ArgName, StatusName(effect[J.Status]), X.ArgTarget, target);
            if (op == X.OpRemoveStatus) return Say(strings, S.CombatantMoveRemove, X.ArgName, StatusName(effect[J.Status]), X.ArgTarget, target);
            if (op == X.OpRestoreMana || op == X.OpRestoreStamina)
                return Say(strings, S.CombatantMoveRestore, X.ArgValue, Val(effect[J.Amount]), X.ArgName, strings.Get(op == X.OpRestoreMana ? S.CombatantMoveMp : S.CombatantMoveSp), X.ArgTarget, target);
            var tail = Js.Nullish(effect[J.Amount]) ? string.Empty : Say(strings, S.CombatantMoveAmount, X.ArgValue, Val(effect[J.Amount]));
            return Say(strings, S.CombatantMoveFuture, X.ArgName, Words(op), X.ArgAmount, tail, X.ArgTarget, target);
        }

        // ------------------------------------------------------------------ tags

        /// <summary>combatProfileTags(profile, registries): the tags of a move's combat profile as the registry words them (label and blurb), the Core Smash help swapped in for maneuver:smash.</summary>
        public static List<KeyValuePair<string, string>> ProfileTags(CombatState c, FaceEnv env, JObject profileIn)
        {
            var profile = Js.Truthy(profileIn[J.Camp]) ? profileIn : CoreStances.ProfileFor(c, profileIn);
            var ids = new List<string>();
            void Push(string prefix, JToken value) { if (Js.Truthy(value)) ids.Add(prefix + Js.Str(value)); }
            Push(X.PrefixCamp, profile[J.Camp]);
            Push(X.PrefixManeuver, profile[J.Maneuver]);
            Push(X.PrefixSchool, profile[J.School]);
            Push(X.PrefixDamage, profile[J.DamageType]);
            Push(X.PrefixCounter, profile[J.CounterMode]);
            Push(X.PrefixRange, profile[J.Range]);
            Push(X.PrefixTargeting, profile[J.Targeting]);
            foreach (var trait in Js.Items(profile[J.Traits])) Push(X.PrefixTrait, trait);
            var rows = new List<KeyValuePair<string, string>>();
            foreach (var id in ids)
            {
                var label = env.TagLabel(id);
                if (label == null) continue;
                var blurb = env.TagBlurb(id) ?? string.Empty;
                if (id == X.TagManeuverSmash) blurb = env.SmashTagBlurb() ?? blurb;
                rows.Add(new KeyValuePair<string, string>(label, blurb));
            }
            return rows;
        }

        // ------------------------------------------------------------------ the poise a move spends

        /// <summary>enemySmashFallbackPoise(move, profile, ctx): the Poise a Smash that authors none deals (expansion 2 only).</summary>
        private static JToken SmashFallbackPoise(CombatState c, JObject move, JObject profile)
        {
            var version = c.Fields[Ashen.Generated.Core2CombatKeys.CombatExpansionVersion];
            if (!(Js.IsNum(version) && Js.D(version) == N.Two) || profile?.Str(J.Maneuver) != X.ManeuverSmash) return null;
            var effects = Js.Items(move[J.Effects]).ToList();
            if (!(!Js.Nullish(move[J.Damage]) || effects.Any(e => Js.Str(Js.Get(e, J.Op)) == X.OpDamage)) || effects.Any(e => Js.Str(Js.Get(e, J.Op)) == X.OpPoiseDamage)) return null;
            return CoreStances.Rules(c)[J.FallbackPoise]?.DeepClone();
        }

        private static double CounterPoise(CombatState c, JObject move, JObject profile)
        {
            var version = c.Fields[Ashen.Generated.Core2CombatKeys.CombatExpansionVersion];
            if (!(Js.IsNum(version) && Js.D(version) == N.Two)) return Js.Or0(move[J.CounterPoiseDamage]);
            var token = profile?.Obj(J.CounterPayload)?[J.Poise] ?? move[J.CounterPoiseDamage];
            var authored = Js.Nullish(token) ? 0 : Js.D(token);
            var hasPoise = Js.Items(move[J.Effects]).Any(e => Js.Str(Js.Get(e, J.Op)) == X.OpPoiseDamage);
            return authored > 0 || hasPoise ? authored : Js.D(CoreStances.Rules(c)[J.FallbackPoise]);
        }

        private static List<string> ChargingPoise(CombatState c, StringTable strings, JObject move, JObject profile)
        {
            var pieces = new List<string>();
            var version = c.Fields[Ashen.Generated.Core2CombatKeys.CombatExpansionVersion];
            if (!(Js.IsNum(version) && Js.D(version) == N.Two)) return pieces;
            var stage = 1;
            for (var charging = move.Obj(J.Delay)?.Obj(J.WhileCharging); charging != null; charging = charging.Obj(J.Delay)?.Obj(J.WhileCharging), stage++)
            {
                var phase = stage == 1 ? strings.Get(S.CombatantMoveWhileCharging) : Say(strings, S.CombatantMoveChargingStage, X.ArgValue, CombatText.Number(stage));
                foreach (var effect in Js.Items(charging[J.Effects]).OfType<JObject>().Where(e => e.Str(J.Op) == X.OpPoiseDamage))
                    pieces.Add(Say(strings, S.CombatantMovePhased, X.ArgText, EffectText(c, strings, effect), X.ArgPhase, phase));
                var fallback = SmashFallbackPoise(c, charging, profile);
                if (fallback != null) pieces.Add(Say(strings, S.CombatantMoveFallbackPhased, X.ArgValue, CombatText.Number(Js.D(fallback)), X.ArgPhase, phase));
            }
            return pieces;
        }

        // ------------------------------------------------------------------ the cards

        /// <summary>The variable-hit text of an intent (model/intentDamage.js variableIntentDamage): the hits joined, and their total, or null when the hits are alike or unknown.</summary>
        public static KeyValuePair<string, double>? VariableDamage(JObject intent)
        {
            if (intent == null || Js.Truthy(intent[J.Hidden]) || intent.Str(J.Kind) == X.KindUnknown || intent[J.MoveId]?.Type == JTokenType.Null) return null;
            if (!(intent[J.HitDamages] is JArray hits) || hits.Count < N.Two) return null;
            if (hits.Any(a => !Js.IsFinite(a) || Js.D(a) < 0) || hits.All(a => Js.D(a) == Js.D(hits[0]))) return null;
            return new KeyValuePair<string, double>(string.Join(X.PlusJoin, hits.Select(a => CombatText.Number(Js.D(a)))), hits.Sum(a => Js.D(a)));
        }

        /// <summary>
        /// enemyMoveCards(def, { enemy, preview, actionPreviews }): the card of each move of the enemy, in the order of its definition. <paramref name="preview"/> is the enemy's intent as the player reads it; only the
        /// moves named in <paramref name="only"/> are built when it is given (the door reads the intent's card and the deck's).
        /// </summary>
        public static List<EnemyMoveCard> Build(CombatState c, FaceEnv env, StringTable strings, JObject enemy, JObject preview, ISet<string> only = null)
        {
            var def = c.Data.Enemies.Get(enemy.Str(J.EnemyId));
            var cards = new List<EnemyMoveCard>();
            var moves = def.Obj(J.Moves) ?? new JObject();
            foreach (var entry in moves.Properties())
            {
                if (only != null && !only.Contains(entry.Name)) continue;
                var moveId = entry.Name;
                var move = (JObject)entry.Value;
                var actionPreview = CoreEnemyPreview.Preview(c, enemy, moveId);
                var probe = Js.Spread(move);
                probe[J.EnemyId] = def.Str(J.Id);
                probe[J.MoveId] = moveId;
                var profile = CoreStances.ProfileFor(c, probe);
                var combatTags = ProfileTags(c, env, profile);
                var active = !(preview != null && Js.Truthy(preview[J.Hidden])) && preview != null && preview.Str(J.MoveId) == moveId;
                var liveDamage = active && !Js.Nullish(preview[J.Damage]);
                var projected = Js.Items(actionPreview?[J.ActionEffects]).OfType<JObject>().FirstOrDefault(e => e.Str(J.Op) == X.OpDamage);
                var damage = liveDamage ? preview[J.Damage] : !Js.Nullish(projected?[J.Amount]) ? projected[J.Amount] : CoreIntents.MoveDamage(enemy, move);
                var hits = liveDamage ? (!Js.Nullish(preview[J.Hits]) ? Js.D(preview[J.Hits]) : 1) : !Js.Nullish(projected?[J.Hits]) ? Js.D(projected[J.Hits]) : !Js.Nullish(move[J.Hits]) ? Js.D(move[J.Hits]) : 1;
                var locked = Js.Truthy(move[J.Locked]) && !Js.Items(enemy[J.UnlockedMoves]).Select(Js.Str).Contains(moveId);
                var pieces = new List<string>();
                if (profile.Str(J.Maneuver) == X.ManeuverCounter)
                {
                    var rules = c.Fields.Obj(Ashen.Generated.Core2CombatKeys.CombatMatchupRules)?.Obj(X.ManeuverCounter) ?? new JObject();
                    var counterMove = Js.Spread(move);
                    counterMove[J.Damage] = !Js.Nullish(move[J.CounterDamage]) ? move[J.CounterDamage] : !Js.Nullish(move[J.Damage]) ? move[J.Damage] : rules[J.DefaultDamage];
                    var basis = CoreIntents.MoveDamage(enemy, counterMove);
                    pieces.Add(Say(strings, S.CombatantMoveCounterBase, X.ArgValue, Value(basis) ?? string.Empty));
                    pieces.Add(Say(strings, S.CombatantMoveCounterRule, X.ArgIncoming, Value(rules[J.IncomingMultiplier]) ?? string.Empty, X.ArgRetaliation, Value(rules[J.RetaliationMultiplier]) ?? string.Empty,
                        X.ArgFlat, Value(rules[J.RetaliationFlat]) ?? string.Empty));
                    var poise = CounterPoise(c, move, profile);
                    var version = Js.D(c.Fields[Ashen.Generated.Core2CombatKeys.CombatExpansionVersion]);
                    if (poise > 0)
                        pieces.Add(Say(strings, version == N.Two ? S.CombatantMoveCounterPoise : S.CombatantMoveCounterPoiseScaled, X.ArgValue, CombatText.Number(poise), X.ArgMult, Value(rules[J.PoiseMultiplier]) ?? string.Empty));
                }
                var sequence = liveDamage ? VariableDamage(preview) : null;
                if (!Js.Nullish(damage) && (actionPreview == null || liveDamage))
                {
                    var amount = sequence.HasValue ? Say(strings, S.CombatantMoveSequence, X.ArgText, sequence.Value.Key, X.ArgValue, CombatText.Number(sequence.Value.Value))
                        : hits > 1 ? Say(strings, S.CombatantMoveTimes, X.ArgValue, Value(damage) ?? string.Empty, X.ArgHits, CombatText.Number(hits)) : Value(damage) ?? string.Empty;
                    pieces.Add(Say(strings, liveDamage ? S.CombatantMoveProjected : S.CombatantMoveBaseDamage, X.ArgText, amount));
                }
                if (actionPreview == null && !Js.Nullish(move[J.Block])) pieces.Add(Say(strings, S.CombatantMoveBaseBlock, X.ArgValue, Value(move[J.Block]) ?? string.Empty));
                var effects = actionPreview != null ? Js.Items(actionPreview[J.ActionEffects]).OfType<JObject>().ToList() : Js.Items(move[J.Effects]).OfType<JObject>().ToList();
                for (var i = 0; i < effects.Count; i++)
                {
                    var effect = effects[i];
                    if (actionPreview != null && liveDamage && !Js.Nullish(move[J.Damage]) && i == 0 && effect.Str(J.Op) == X.OpDamage) continue;
                    pieces.Add(EffectText(c, strings, effect) + (effect[J.ConditionMet] != null && effect[J.ConditionMet].Type == JTokenType.Boolean && !effect.Is(J.ConditionMet) ? strings.Get(S.CombatantMoveConditionNotMet) : string.Empty));
                }
                var fallback = SmashFallbackPoise(c, move, profile);
                if (actionPreview == null && fallback != null) pieces.Add(Say(strings, S.CombatantMoveFallbackPlayer, X.ArgValue, CombatText.Number(Js.D(fallback))));
                if (Js.Truthy(move[J.Delay]))
                {
                    var delay = move.Obj(J.Delay);
                    var turns = Js.D(delay[J.Turns]);
                    pieces.Add(Say(strings, S.CombatantMoveDelayed, X.ArgValue, CombatText.Number(turns), X.ArgPlural, Plural(strings, turns)));
                    if (actionPreview == null && !Js.Nullish(delay.Obj(J.WhileCharging)?[J.Block])) pieces.Add(Say(strings, S.CombatantMoveChargingBlock, X.ArgValue, Value(delay.Obj(J.WhileCharging)[J.Block]) ?? string.Empty));
                    if (actionPreview != null)
                        pieces.AddRange(Js.Items(actionPreview[J.ChargingEffects]).OfType<JObject>().Select(e => Say(strings, S.CombatantMovePhased, X.ArgText, EffectText(c, strings, e), X.ArgPhase, strings.Get(S.CombatantMoveWhileCharging))));
                    else pieces.AddRange(ChargingPoise(c, strings, move, profile));
                }
                var phaseRequirements = Js.Items(def[J.Phases]).OfType<JObject>().Where(p => Js.Items(p[J.UnlockMoves]).Select(Js.Str).Contains(moveId))
                    .Select(p => p.Str(J.On) == X.OnHpBelowPct ? Say(strings, S.CombatantMoveUnlocksHp, X.ArgValue, Value(p[J.Pct]) ?? string.Empty) : Say(strings, S.CombatantMoveUnlocksOn, X.ArgName, Words(p.Str(J.On)))).ToList();
                var tags = move[J.Tags] is JArray tagArray ? tagArray.Select(Js.Str).ToList() : new List<string>();
                var state = active ? strings.Get(Js.Truthy(preview[J.Pending]) ? S.CombatantMoveCharging : S.CombatantMoveCurrent) : strings.Get(locked ? S.CombatantMoveLocked : S.CombatantMoveSet);
                var meta = new List<string> { state, Words(move.Str(J.Intent)) };
                meta.AddRange(phaseRequirements);
                if (!Js.Nullish(move[J.MaxConsecutive])) meta.Add(Say(strings, S.CombatantMoveMaxConsecutive, X.ArgValue, Value(move[J.MaxConsecutive]) ?? string.Empty));
                if (tags.Count > 0) meta.Add(Say(strings, S.CombatantMoveTags, X.ArgValue, string.Join(strings.Get(S.CombatantTagJoin), tags.Select(Words))));
                if (combatTags.Count > 0) meta.Add(string.Join(strings.Get(S.CombatantJoin), combatTags.Select(t => t.Key)));
                pieces.AddRange(combatTags.Where(t => !string.IsNullOrEmpty(t.Value)).Select(t => Say(strings, S.CombatantMoveTagBlurb, X.ArgName, t.Key, X.ArgText, t.Value)));
                cards.Add(new EnemyMoveCard
                {
                    MoveId = moveId,
                    Name = Js.Truthy(move[J.Name]) ? move.Str(J.Name) : Words(moveId),
                    Active = active,
                    Locked = locked,
                    Profile = profile,
                    Detail = pieces.Count > 0 ? string.Join(strings.Get(S.CombatantJoin), pieces) : Words(move.Str(J.Intent)),
                    Meta = string.Join(strings.Get(S.CombatantJoin), meta),
                });
            }
            return cards;
        }
    }
}
