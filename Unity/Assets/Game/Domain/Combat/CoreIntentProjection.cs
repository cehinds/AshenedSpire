using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using DK = Ashen.Generated.Core2DamageKeys;
using HK = Ashen.Generated.Core2HitKeys;
using IK = Ashen.Generated.IntentKeys;
using K = Ashen.Generated.CombatKeys;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// What the player is told an enemy's revealed move will do to them (P-1, D-1320..): the web's <c>previewIntent</c> (engine/combat.js, the move priced against the player
    /// hit by hit) and <c>projectCombatIntent</c> (ui/models/CombatIntentProjectionModel.js), which adds the HP the move can take from the player's current defenses as a
    /// range: every reachable state of the player's Block, pools, stance and statuses is walked hit by hit (a miss keeps the defenses for the next hit, a landed hit
    /// spends them) and the lowest and highest HP lost are the range; the same walk with the player in no stance is the baseline that says whether the stance helped.
    /// Everything is read on a detached copy of the fight; no draw is made. Only a move the player has read may be passed in (the caller holds the observer's gate).
    /// </summary>
    public static class CoreIntentProjection
    {
        private const double Epsilon = 1e-9;

        /// <summary>combatIntentStance(intent, profile).</summary>
        public static string Stance(JObject intent, JObject profile)
        {
            if (Js.Truthy(intent[IK.Stance])) return intent.Str(IK.Stance);
            var kind = intent.Str(K.Kind);
            if (kind == V.IntentStaggered) return IK.StanceStaggered;
            var maneuver = profile?.Str(CK.Maneuver);
            var camp = profile?.Str(CK.Camp);
            if (maneuver == IK.ManeuverCounter) return IK.StanceCountering;
            if (maneuver == IK.ManeuverDefend) return IK.StanceDefending;
            if (maneuver == IK.ManeuverSweep) return IK.StanceSweeping;
            if (camp == IK.CampSpell || camp == IK.CampSpells || camp == IK.CampMagic) return IK.StanceCasting;
            if (maneuver == IK.ManeuverRanged) return IK.StanceRanged;
            if (maneuver == IK.ManeuverSmash) return IK.StanceSmashing;
            if (kind == IK.KindBlock || kind == IK.KindDefend) return IK.StanceDefending;
            if (maneuver == IK.ManeuverAttack || kind == IK.KindAttack || kind == IK.KindAttackDebuff) return IK.StanceAttacking;
            return IK.StancePreparing;
        }

        /// <summary>The carrier of an enemy's attack as the intent previews price it: enemyMoveCarrier with the damage school the ratings rules or the damage effect name.</summary>
        private static JObject AttackCarrier(CombatState c, JObject enemy, JObject authored, JObject expanded, string moveId)
        {
            var carrier = CoreIntents.Carrier(c, enemy, authored ?? new JObject(), moveId);
            var type = c.RatingsRules?.Obj(DK.EnemyAttackType)?.Str(enemy.Str(K.EnemyId) + V.KeySeparator + moveId);
            var effect = Js.Items(expanded?[K.Effects]).OfType<JObject>().FirstOrDefault(e => e.Str(K.Op) == Op.Damage);
            JToken school = !string.IsNullOrEmpty(type) && type != V.AttackTypeAuto ? type : Js.Truthy(effect?[CK.DamageSchool]) ? effect[CK.DamageSchool] : expanded?[CK.DamageSchool];
            if (Js.Truthy(school)) carrier[CK.DamageSchool] = school.DeepClone(); else carrier.Remove(CK.DamageSchool);
            return carrier;
        }

        private static JObject EnemyOf(CombatState c, JObject enemy) => c.Enemies.First(e => e.Str(K.Id) == enemy.Str(K.Id));

        private static string Compact(JToken value) => value.ToString(Formatting.None);

        // ------------------------------------------------------------------ previewIntent

        /// <summary>
        /// previewIntent(combat, enemyInstanceId) for a move the player has read: the intent with the move's public payload, its stance, a Counter's base return and, for
        /// an attack, previewDamageHits against the player (damage, hits, hitDamages, totalDamage, hpLoss, hitChance, uncertain, hpLossBounds, damageReceipt).
        /// </summary>
        public static JObject Preview(CombatState c, JObject enemy)
        {
            var intent = enemy.Obj(K.Intent) ?? Js.Obj(K.Kind, V.IntentUnknown, K.MoveId, Js.Null());
            var profile = intent.Obj(CK.CombatProfile) ?? new JObject();
            var moveId = intent.Str(K.MoveId);
            var result = Js.Spread(intent);
            var action = string.IsNullOrEmpty(moveId) ? null : CoreEnemyPreview.Preview(c, enemy, moveId);
            if (action != null) foreach (var p in action.Properties()) result[p.Name] = p.Value.DeepClone();
            result[IK.Profile] = profile.DeepClone();
            result[IK.Stance] = Stance(intent, profile);
            result[IK.Revealed] = true;
            result[IK.Hidden] = false;
            if (result.Str(IK.Stance) == IK.StanceCountering)
            {
                var counter = enemy.Obj(CK.CombatCounter);
                var payload = counter?.Obj(IK.Payload);
                result[IK.CounterDamage] = !Js.Nullish(payload?[K.Hp]) ? payload[K.Hp].DeepClone() : !Js.Nullish(counter?[K.Damage]) ? counter[K.Damage].DeepClone() : Js.N(0);
                result[IK.CounterPoiseDamage] = !Js.Nullish(payload?[CK.Poise]) ? payload[CK.Poise].DeepClone() : !Js.Nullish(counter?[IK.PoiseDamage]) ? counter[IK.PoiseDamage].DeepClone() : Js.N(0);
            }
            if (!Js.Nullish(intent[K.Damage]) && moveId != null)
                foreach (var p in DamageHits(c, enemy, intent, moveId).Properties()) result[p.Name] = p.Value;
            result[K.Pending] = Js.Truthy(enemy[K.PendingMove]);
            if (c.Fields.Obj(CK.EnemyKnowledge) != null)
            {
                var knowledge = enemy.Obj(CK.KnowledgeAction);
                result[IK.ActionSerial] = Js.Truthy(knowledge?[CK.Serial]) ? knowledge[CK.Serial].DeepClone() : Js.N(0);
                result[IK.KnowledgeRead] = IK.ReadExact;
                result[CK.Label] = intent.Str(K.Kind) == V.IntentStaggered ? IK.LabelStaggered : knowledge?[CK.Category]?.DeepClone();
            }
            return result;
        }

        /// <summary>previewDamageHits(combat, enemy, combat.player, intent.damage, tags, carrier, intent.hits ?? 1) for a configured fight.</summary>
        private static JObject DamageHits(CombatState c, JObject enemy, JObject intent, string moveId)
        {
            var d = c.Detach();
            var source = EnemyOf(d, enemy);
            var authored = c.Data.Enemies.Get(enemy.Str(K.EnemyId)).Obj(K.Moves)?.Obj(moveId);
            var expanded = CoreIntents.Expanded(d, source, authored ?? new JObject(), moveId);
            var carrier = AttackCarrier(d, source, authored, expanded, moveId);
            var receipts = CoreDamage.PreviewHits(d, source, d.Player, intent.Num(K.Damage), carrier, Js.Coalesce(intent[K.Hits], 1));
            var losses = receipts.Select(r => r.Num(DK.HpLoss)).ToList();
            var bounds = receipts.Select(r => Js.Items(r[DK.HpLossBounds]).Select(Js.D).ToList()).ToList();
            return new JObject
            {
                [IK.DamageReceipt] = CoreDamage.CombinedReceipt(c.Player, receipts),
                [K.Damage] = Js.N(losses.Count > 0 ? losses[0] : 0),
                [K.Hits] = Js.N(receipts.Count),
                [IK.HitDamages] = new JArray(losses.Select(Js.N)),
                [DK.TotalDamage] = Js.N(losses.Sum()),
                [DK.HpLoss] = Js.N(losses.Sum()),
                [DK.HitChance] = receipts.Count > 0 && !Js.Nullish(receipts[0][DK.HitChance]) ? receipts[0][DK.HitChance].DeepClone() : Js.N(1),
                [DK.Uncertain] = receipts.Any(r => Js.Truthy(r[DK.Uncertain])),
                [DK.HpLossBounds] = new JArray(Js.N(bounds.Sum(b => b.Count > 0 ? b[0] : 0)), Js.N(bounds.Sum(b => b.Count > 1 ? b[1] : 0))),
            };
        }

        // ------------------------------------------------------------------ projectCombatIntent

        private static JObject Unknown(string reason) => new JObject
        {
            [IK.Basis] = IK.BasisHp, [IK.Min] = Js.Null(), [IK.Max] = Js.Null(), [IK.Label] = IK.UnknownMark, [IK.Tone] = IK.ToneRed, [IK.StanceReduced] = false,
            [DK.Uncertain] = true, [IK.Reason] = reason,
        };

        private sealed class Bounds
        {
            public double Min;
            public double Max;
            public double HitChance;
            public double Expected;
        }

        private sealed class State
        {
            public JObject Target;
            public string Key;
            public double Loss;
            public double Probability;
        }

        private static string TargetKey(JObject row) => Compact(new JArray(row[K.Block], row[CK.Barrier], row[CK.PersistentWard], row[CK.PersistentPoise], row[CK.CombatStance], row[K.Statuses]));

        /// <summary>damageBounds(combat, enemy, move, base, hits, neutral) for a configured fight (the web's other branches are not Core 2).</summary>
        private static Bounds DamageBounds(CombatState c, JObject enemy, JObject authored, double baseAmount, int hits, bool neutral)
        {
            var d = c.Detach();
            var source = EnemyOf(d, enemy);
            var player = d.Player;
            if (neutral)
            {
                player.Remove(CK.CombatStance);
                player.Remove(CK.ConcurrentStances);
            }
            var moveId = enemy.Obj(K.Intent).Str(K.MoveId);
            var expanded = CoreIntents.Expanded(d, source, authored ?? new JObject(), moveId);
            var carrier = AttackCarrier(d, source, authored, expanded, moveId);
            var proposal = CoreProposal.TargetPlan(d, source, player, carrier, CoreDamage.ProposalDamageType(d, carrier));
            // Dynamic perception/charge consumption and reactive damage are not a deterministic HP promise; the attack-only projection reports a range.
            var refused = proposal[HK.Allowed] != null && proposal[HK.Allowed].Type == JTokenType.Boolean && !proposal.Is(HK.Allowed);
            var proposalChance = (refused ? 0 : 1)
                * (Js.Nullish(proposal[HK.PerceptionChance]) ? 1 : proposal.Num(HK.PerceptionChance) / 100)
                * (1 - proposal.Or0(HK.EvadeChance) / 100);
            // A miss preserves the target's defenses for the next hit. The reachable target states are merged rather than all-landed damage multiplied by the hit chance;
            // a landed hit carries into the next one exactly as previewDamageHits does, so a Poise break ends a Smash's doubling.
            var states = new List<State> { new State { Target = player, Key = TargetKey(player), Loss = 0, Probability = 1 } };
            for (var hit = 0; hit < hits; hit++)
            {
                var next = new List<State>();
                var index = new Dictionary<string, State>();
                void Add(State row)
                {
                    if (!(row.Probability > 0)) return;
                    var key = row.Key + ":" + Number(row.Loss);
                    if (index.TryGetValue(key, out var previous)) previous.Probability += row.Probability;
                    else { index[key] = row; next.Add(row); }
                }
                foreach (var state in states)
                {
                    var receipt = CoreDamage.ProfileDamageReceipt(d, source, state.Target, baseAmount, carrier);
                    var landed = (JObject)state.Target.DeepClone();
                    CoreDamage.ApplyReceiptToPreviewTarget(d, landed, receipt);
                    var chance = receipt.Num(DK.HitChance);
                    Add(new State { Target = landed, Key = TargetKey(landed), Loss = state.Loss + receipt.Num(DK.HpLoss), Probability = state.Probability * chance });
                    Add(new State { Target = state.Target, Key = state.Key, Loss = state.Loss, Probability = state.Probability * (1 - chance) });
                }
                states = next;
            }
            var losses = states.Select(s => s.Loss).ToList();
            var min = losses.Min();
            var max = losses.Max();
            return new Bounds
            {
                Min = proposalChance < 1 ? 0 : min,
                Max = proposalChance == 0 ? 0 : max,
                HitChance = states.Where(s => s.Loss > 0).Sum(s => s.Probability) * proposalChance,
                Expected = states.Sum(s => s.Loss * s.Probability) * proposalChance,
            };
        }

        /// <summary>String(number): a whole number without a fraction, otherwise the shortest text that reads back the same.</summary>
        public static string Number(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// projectCombatIntent(combat, enemyInstanceId) for a move the player has read: <see cref="Preview"/> and, for an attack with one damage effect, its
        /// <c>projectedDamage</c> { basis, min, max, hitChance, expected, label ('16' or '0–16'), tone ('green' when the stance lowered the range shown), stanceReduced,
        /// displayedReduction, uncertain, baseline { min, max, expected }, scope }, or the unknown mark with the reason the web gives.
        /// </summary>
        public static JObject Project(CombatState c, JObject enemy)
        {
            var intent = Preview(c, enemy);
            if (intent.Str(K.Kind) == V.IntentStaggered) return intent;
            if (intent.Obj(IK.Profile)?.Str(CK.Maneuver) == IK.ManeuverEvade)
            {
                var evade = enemy.Obj(CK.CombatEvade);
                var stacks = enemy.Obj(K.Statuses)?.Obj(CK.Evade)?[K.Stacks];
                intent[IK.DodgeCharges] = !Js.Nullish(evade?[CK.Charges]) ? evade[CK.Charges].DeepClone() : !Js.Nullish(stacks) ? stacks.DeepClone() : Js.N(0);
            }
            if (Js.Nullish(intent[K.Damage])) return intent;
            var live = enemy.Obj(K.Intent);
            var moveId = live.Str(K.MoveId);
            var authored = c.Data.Enemies.Get(enemy.Str(K.EnemyId)).Obj(K.Moves)?.Obj(moveId ?? string.Empty);
            var move = CoreIntents.Expanded(c, enemy, authored ?? new JObject(), moveId);
            if (Js.Items(move[K.Effects]).Count(e => Js.Str(Js.Get(e, K.Op)) == Op.Damage) > 1) { intent[IK.ProjectedDamage] = Unknown(IK.ReasonMultiple); return intent; }
            var baseAmount = Js.IsNum(live[K.Damage]) ? live.Num(K.Damage) : double.NaN;
            var hits = Math.Max(0, Math.Floor(Js.Coalesce(live[K.Hits], 1)));
            if (double.IsNaN(baseAmount) || double.IsInfinity(baseAmount) || double.IsNaN(hits) || double.IsInfinity(hits) || hits > 100) { intent[IK.ProjectedDamage] = Unknown(IK.ReasonUnresolved); return intent; }
            var actual = DamageBounds(c, enemy, authored, baseAmount, (int)hits, false);
            var baseline = DamageBounds(c, enemy, authored, baseAmount, (int)hits, true);
            var stanceReduced = actual.Expected < baseline.Expected - Epsilon;
            // Green describes the displayed HP range, not an undisplayed average: a wider failed-dodge upper bound stays red even if its expectation improves.
            var displayedReduction = stanceReduced && actual.Min <= baseline.Min && actual.Max < baseline.Max;
            intent[IK.ProjectedDamage] = new JObject
            {
                [IK.Basis] = IK.BasisHp,
                [IK.Min] = Js.N(actual.Min), [IK.Max] = Js.N(actual.Max), [DK.HitChance] = Js.N(actual.HitChance), [IK.Expected] = Js.N(actual.Expected),
                [IK.Label] = actual.Min == actual.Max ? Number(actual.Max) : Number(actual.Min) + IK.RangeDash + Number(actual.Max),
                [IK.Tone] = displayedReduction ? IK.ToneGreen : IK.ToneRed,
                [IK.StanceReduced] = stanceReduced, [IK.DisplayedReduction] = displayedReduction, [DK.Uncertain] = actual.Min != actual.Max,
                [IK.Baseline] = new JObject { [IK.Min] = Js.N(baseline.Min), [IK.Max] = Js.N(baseline.Max), [IK.Expected] = Js.N(baseline.Expected) },
                [IK.Scope] = IK.ScopeDirect,
            };
            return intent;
        }

        // ------------------------------------------------------------------ intentDamageRead

        /// <summary>What intentDamageRead says of a revealed intent: the badge's number, and the amount and basis of the line under it.</summary>
        public sealed class DamageRead
        {
            /// <summary>The number the badge shows ('9', '5×3', '4+6', '3–9×4', or the projected '0–16').</summary>
            public string Label;

            /// <summary>The amount the summary names ('0–16', '12').</summary>
            public string Amount;

            /// <summary>The summary names HP lost (true) or the damage before Block.</summary>
            public bool Hp;

            /// <summary>The summary is a contact's: 1-based index and count (0 when it is the total).</summary>
            public int ContactIndex;
            public int ContactCount;
        }

        /// <summary>intentDamageRead(intent): null for a move that is hidden, unread or does no damage.</summary>
        public static DamageRead Read(JObject intent)
        {
            var unrevealed = intent?[IK.Revealed] != null && intent[IK.Revealed].Type == JTokenType.Boolean && !intent.Is(IK.Revealed);
            if (intent == null || intent.Is(IK.Hidden) || unrevealed || Js.Nullish(intent[K.Damage])) return null;
            var projected = intent.Obj(IK.ProjectedDamage);
            var listed = Js.Items(intent[IK.HitDamages]).Select(Js.D).ToList();
            var hits = listed.Count > 0 ? listed : Enumerable.Repeat(intent.Num(K.Damage), (int)Math.Max(1, Js.Or0(intent[K.Hits]))).ToList();
            var same = hits.All(n => n == hits[0]);
            string label;
            if (hits.Count == 1 && projected != null) label = projected.Str(IK.Label);
            else if (same) label = Number(hits[0]) + (hits.Count > 1 ? IK.TimesMark + hits.Count : string.Empty);
            else if (hits.Count <= 3) label = string.Join(IK.PlusMark, hits.Select(Number));
            else label = Number(hits.Min()) + IK.RangeDash + Number(hits.Max()) + IK.TimesMark + hits.Count;
            string amount;
            if (Js.Truthy(projected?[IK.Label])) amount = projected.Str(IK.Label);
            else if (intent[DK.HpLossBounds] is JArray b && b.Count >= 2) amount = Js.D(b[0]) == Js.D(b[1]) ? Number(Js.D(b[1])) : Number(Js.D(b[0])) + IK.RangeDash + Number(Js.D(b[1]));
            else amount = !Js.Nullish(intent[DK.TotalDamage]) ? Number(intent.Num(DK.TotalDamage)) : Number(hits.Sum());
            var hp = projected?.Str(IK.Basis) == IK.BasisHp || intent.Str(IK.DamageBasis) == IK.BasisHp || intent[DK.HpLossBounds] != null;
            var read = new DamageRead { Label = label, Amount = amount, Hp = hp };
            if (intent.Str(IK.DamageScope) == IK.ScopeContact)
            {
                read.ContactIndex = (int)intent.Or0(IK.ContactIndex) + 1;
                read.ContactCount = Js.Truthy(intent[IK.ContactCount]) ? (int)intent.Num(IK.ContactCount) : 1;
            }
            return read;
        }
    }
}
