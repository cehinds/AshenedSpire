using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DM = Ashen.Generated.Core2DamageMessages;
using DV = Ashen.Generated.Core2DamageValues;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using Op = Ashen.Generated.CombatOps;
using RngStream = Ashen.Generated.RngStream;
using V = Ashen.Generated.CombatValues;
using SV = Ashen.Generated.Core2StatusValues;
using SM = Ashen.Generated.Core2StatusMessages;
using SK = Ashen.Generated.Core2StatusKeys;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The effect ops of a Core 2 fight beyond damage, Block, Barrier and the status ops of the committed-action machinery (web engine/actions.js
    /// runOpcode, resolveTargets): Health lost and healed, Stamina and cards gained, the cards discarded, exhausted, added and moved, build-up
    /// pressure, Ward and Poise damage, the next-roll modes, the card charges, the linked spell, the revealed intent, a stagger, the cinders a
    /// fight pays and the stance a card enters by a maneuver. Every op is a member of <see cref="Ashen.Generated.EffectOp"/>; none is named by an
    /// id: the cards and statuses that use them are content.
    /// </summary>
    public static class CoreOps
    {
        private static bool Expanded(CombatState c) => Js.D(c.Fields[CK.CombatExpansionVersion]) == c.CoreData.ExpandedVersion;

        private static double Eval(CombatState c, CombatAction action, JToken value, double dflt, JObject target = null) => Actions.EvalNum(c, action, value, dflt, target);

        // ------------------------------------------------------------------ targets

        private static List<JObject> Living(CombatState c) => c.Enemies.Where(e => e.Is(K.Alive)).ToList();

        /// <summary>resolveRawTargets(ctx, action, spec): the named creatures, before the committed action's plan filters them.</summary>
        private static List<JObject> RawTargets(CombatState c, CombatAction action, string spec)
        {
            var list = new List<JObject>();
            switch (spec)
            {
                case null:
                    if (action.Target != null) list.Add(action.Target); else if (action.Source != null) list.Add(action.Source);
                    return list;
                case V.TargetSelf:
                    if (action.Source != null) list.Add(action.Source);
                    return list;
                case V.RefOwner:
                    if ((action.Owner ?? action.Source) != null) list.Add(action.Owner ?? action.Source);
                    return list;
                case V.RefPlayer:
                    if (c.Player != null) list.Add(c.Player);
                    return list;
                case V.RefEnemy:
                {
                    if (action.Target != null && action.Target.Str(K.Kind) == V.Enemy && action.Target.Is(K.Alive)) return new List<JObject> { action.Target };
                    if (action.Source != null && action.Source.Str(K.Kind) == V.Enemy)
                    {
                        if (c.Player != null) list.Add(c.Player);
                        return list;
                    }
                    var living = Living(c);
                    return living.Count > 0 ? new List<JObject> { living[0] } : list;
                }
                case DV.TargetAllEnemies:
                    return Living(c);
                case DV.TargetOtherEnemies:
                {
                    var named = action.Meta?.Event?.Str(K.TargetId);
                    return Living(c).Where(e => e != action.Target && e.Str(K.Id) != named).ToList();
                }
                case V.TargetRandomEnemy:
                {
                    var living = Living(c);
                    return living.Count > 0 ? new List<JObject> { c.Rng.Pick(RngStream.Misc, living) } : list;
                }
                case V.TargetAlly:
                {
                    var t = action.Target;
                    if (t != null && t.Str(K.Kind) == V.Player && t != action.Source && t.Is(K.Alive)) return new List<JObject> { t };
                    if (action.Source != null) list.Add(action.Source);
                    return list;
                }
                default:
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M_UnknownTarget, spec));
            }
        }

        private static readonly string M_UnknownTarget = Ashen.Generated.CombatMessages.UnknownTarget;

        /// <summary>
        /// resolveTargets(ctx, action, spec): once a committed action has begun, an effect that is not one of its planned hits acts on the creatures its
        /// plan reached for this repeat; creatures that avoided the action are left out; <c>allEnemies</c> honours the card's target limit.
        /// </summary>
        public static List<JObject> Targets(CombatState c, CombatAction action, string spec)
        {
            var group = action.Meta?.Group;
            var op = action.Effect?.Str(K.Op);
            var generic = op != Op.Damage && op != Op.ApplyStatus && op != CV.OpBuildup;
            List<JObject> targets;
            var repeat = action.Meta?.ExpansionExecutionRepeat ?? 0;
            if (group != null && group.Begun && generic && action.Meta.ExpansionTargets != null && repeat < action.Meta.ExpansionTargets.Count)
                targets = action.Meta.ExpansionTargets[repeat].Select(id => Triggers.FindEntity(c, id)).Where(e => e != null && e.Is(K.Alive)).ToList();
            else targets = RawTargets(c, action, spec);
            var eligible = targets.Where(e => !(group != null && group.Targets.TryGetValue(e.Str(K.Id), out var row) && row.Receipt?.Is(DK.Avoided) == true)).ToList();
            if (spec == DV.TargetAllEnemies && action.Card != null && action.Card[DK.TargetLimit] != null)
                return eligible.Take((int)Math.Max(0, Eval(c, action, action.Card[DK.TargetLimit], eligible.Count))).ToList();
            return eligible;
        }

        private static TargetRow RowOf(CombatAction action, JObject entity) =>
            action.Meta?.Group != null && action.Meta.Group.Targets.TryGetValue(entity.Str(K.Id), out var row) ? row : null;

        // ------------------------------------------------------------------ dispatcher

        /// <summary>The op of an action, when it is one of this class's; false for an op the committed-action machinery or no package owns.</summary>
        public static bool Run(CombatState c, CombatAction action, JObject eff)
        {
            switch (eff.Str(K.Op))
            {
                case DV.OpGrantRollMode: GrantRollMode(c, action, eff); return true;
                case K.Retain: Retain(c, action, eff); return true;
                case DV.OpGainPoise:
                case SV.GainWard: GainGuard(c, action, eff); return true;
                case CV.OpBuildup: Buildup(c, action, eff); return true;
                case K.DodgeRoll: DodgeRoll(c, action); return true;
                case DV.OpGrantCardCharge: GrantCardCharge(c, action, eff); return true;
                case SV.CastLinkedSpell: CastLinkedSpell(c, action, eff); return true;
                case SV.RevealIntent: RevealIntent(c, action, eff); return true;
                case SV.ModifyCards:
                case SV.MoveCards: MoveCards(c, action, eff); return true;
                case Op.Discard: Discard(c, action, eff); return true;
                case K.Exhaust: Exhaust(c, action, eff); return true;
                case Op.AddCard: AddCard(c, action, eff); return true;
                case Op.GainEnergy: GainEnergy(c, action, eff); return true;
                case Op.LoseHp: LoseHp(c, action, eff); return true;
                case Op.Heal: Heal(c, action, eff); return true;
                case Op.ShuffleDiscardIntoDraw: Actions.ReshuffleDiscardIntoDraw(c); return true;
                case Op.EnterStance: EnterStance(c, action, eff); return true;
                case K.Stagger: Stagger(c, action, eff); return true;
                case Op.AddCinders: AddCinders(c, action, eff); return true;
                case SV.AddBurden: AddBurden(c, action, eff); return true;
                default: return false;
            }
        }

        // ------------------------------------------------------------------ rolls, guards, retained cards

        private static void GrantRollMode(CombatState c, CombatAction action, JObject eff)
        {
            if (!Expanded(c) || eff.Str(K.Roll) != CV.StatusEvade) throw new InvalidOperationException(SM.UnsupportedRollMode);
            foreach (var t in Targets(c, action, eff.Str(K.Target)))
                t[Ashen.Generated.Core2HitKeys.NextEvadeMode] = new JObject { [DK.Advantage] = eff.Is(DK.Advantage), [Ashen.Generated.Core2HitKeys.Disadvantage] = eff.Is(Ashen.Generated.Core2HitKeys.Disadvantage) };
        }

        private static void Retain(CombatState c, CombatAction action, JObject eff)
        {
            var owner = action.Owner ?? action.Source;
            var retained = owner.Arr(CK.CombatRetainedCards) ?? (JArray)(owner[CK.CombatRetainedCards] = new JArray());
            var amount = (int)Eval(c, action, eff[K.Amount], 0);
            var candidates = c.Piles.Hand.Where(card => !Js.Includes(retained, card.Str(K.InstanceId))).ToList();
            var count = amount < 0 ? Math.Max(0, candidates.Count + amount) : Math.Min(amount, candidates.Count);
            foreach (var card in candidates.Take(count)) retained.Add(card[K.InstanceId].DeepClone());
        }

        /// <summary>gainPoise / gainWard in a configured fight: a Poise guard is a running total on the creature; a Ward guard gives nothing.</summary>
        private static void GainGuard(CombatState c, CombatAction action, JObject eff)
        {
            if (eff.Str(K.Op) != DV.OpGainPoise || !Js.Includes(c.CoreData.Configured(CK.DefensesModule)?[CK.Include], K.Poise)) return;
            foreach (var t in Targets(c, action, eff.Str(K.Target)))
            {
                t.Put(SK.GameProfilePoise, t.Or0(SK.GameProfilePoise) + Math.Max(0, Eval(c, action, eff[K.Amount], 0, t)));
                c.Emit(SV.MeterGuardGained, Js.Obj(K.TargetId, t[K.Id], K.Meter, K.Poise, K.Amount, t[SK.GameProfilePoise], K.Total, t[SK.GameProfilePoise]));
            }
        }

        private static void DodgeRoll(CombatState c, CombatAction action)
        {
            if (Js.Includes(c.CoreData.Configured(CK.StancesModule)?[CK.Include], CV.StatusEvade))
                action.Source[CK.CombatStance] = new JObject { [CK.Maneuver] = CV.StatusEvade, [CK.Camp] = CV.CampPhysical };
        }

        // ------------------------------------------------------------------ pressure

        private static void TrackOwnPressure(CombatState c, CombatAction action, JObject target, string meter, double amount) =>
            CoreCardTraits.TrackOwnPressure(c, action, target, meter, amount);

        private static double SpellStatusBonus(CombatState c, CombatAction action, double authored)
        {
            if (action.Card == null || action.Meta?.Group == null || action.Meta.StanceEntry || !(authored > 0)) return 0;
            var stance = action.Source?.Obj(CK.CombatStance);
            var eligible = CoreSets.CastingManeuvers.Contains(stance?.Str(CK.Maneuver)) || stance?.Str(CK.Maneuver) == CV.StanceCounter && stance.Str(CK.Camp) == CV.CampSpell;
            return eligible ? CoreStances.Rules(c).Num(DK.SpellStatusStacks) : 0;
        }

        private static List<JObject> PlannedTargets(CombatState c, CombatAction action, JObject eff)
        {
            var meta = action.Meta;
            if (meta?.ExpansionTargets == null || meta.StatusTargetCursor >= meta.ExpansionTargets.Count) return Targets(c, action, eff.Str(K.Target));
            var refs = meta.ExpansionTargets[meta.StatusTargetCursor++];
            return refs.Select(id => Triggers.FindEntity(c, id)).Where(e => e != null && e.Is(K.Alive)
                && !(meta.Group != null && meta.Group.Targets.TryGetValue(e.Str(K.Id), out var row) && row.Receipt?.Is(DK.Avoided) == true)).ToList();
        }

        private static void Buildup(CombatState c, CombatAction action, JObject eff)
        {
            foreach (var t in PlannedTargets(c, action, eff))
            {
                if (eff[DK.Chance] != null && !CoreStatusControl.RollChance(c, eff.Num(DK.Chance), eff.Is(DK.Advantage), eff.Is(Ashen.Generated.Core2HitKeys.Disadvantage), Ashen.Generated.Core2RngStream.StatusPressure).Is(K.Success)) continue;
                var authored = Eval(c, action, eff[K.Amount], 0, t);
                // Ashen Blight's spell buildup is no part of a Core 2 run: a creature with a milestone is refused by name in the turn start.
                var charged = action.Meta?.Charge?.Or0(CV.OpBuildup) ?? 0;
                var camp = Js.Truthy(eff[CK.Camp]) ? eff[CK.Camp] : action.Card?.Obj(CK.CombatProfile)?[CK.Camp];
                var options = Js.Obj(CK.Camp, camp, SK.ActionKey, action.Meta?.Group?.Key ?? c.Turn.ToString(CultureInfo.InvariantCulture) + V.KeySeparator + (Js.Truthy(action.Card?[K.InstanceId]) ? action.Card.Str(K.InstanceId) : V.CauseEffect),
                    SK.RecoveryProfile, eff[SK.RecoveryProfile], CK.Traits, eff[CK.Traits]);
                var pressure = CoreStatusControl.ApplyPressure(c, t, eff.Str(K.Status), authored + SpellStatusBonus(c, action, authored) + charged, action.Source, options);
                var row = RowOf(action, t);
                if (row?.Receipt != null)
                {
                    row.Receipt[DK.StatusAccepted] = Js.N(row.Receipt.Or0(DK.StatusAccepted) + pressure.Num(SK.AcceptedBuildup) + pressure.Num(SK.ActiveAdded));
                    row.Receipt[DK.StatusResisted] = Js.N(row.Receipt.Or0(DK.StatusResisted) + pressure.Num(SK.Resisted) + pressure.Num(SK.WardSpent));
                }
            }
        }

        private static double EdgeMultiplier(CombatAction action) =>
            !(action.Card?.Is(SK.CombatWardEdgeApplied) == true) && action.Card?.Obj(CK.CombatProfile)?.Str(CK.Camp) == CV.CampSpell && action.Card.Obj(CK.CombatProfile).Str(CK.DamageType) == SV.Piercing
                ? c_WardEdge : 1;

        private const double c_WardEdge = 1.25;

        // ------------------------------------------------------------------ riders and linked spells

        private static void GrantCardCharge(CombatState c, CombatAction action, JObject eff)
        {
            foreach (var target in Targets(c, action, eff.Str(K.Target)))
            {
                var charge = (JObject)eff.DeepClone();
                foreach (var field in new[] { K.Damage, SV.ManaDiscount, K.Block, Op.Heal, SV.Break, CV.OpBuildup })
                    if (eff[field] != null) charge[field] = Js.N(Math.Max(0, Eval(c, action, eff[field], 0, target)));
                CoreRiders.Grant(target, charge);
            }
        }

        private static void CastLinkedSpell(CombatState c, CombatAction action, JObject eff)
        {
            var depth = action.Meta?.LinkedDepth ?? 0;
            if (depth >= 8) throw new InvalidOperationException(SM.LinkedDepth);
            var linked = c.Data.Cards.Get(eff.Str(K.Card));
            foreach (var effect in Js.Items(linked[K.Effects]).OfType<JObject>())
            {
                var card = Js.Spread(action.Card, linked);
                card[K.CardId] = linked[K.Id]?.DeepClone();
                var meta = (action.Meta ?? new ActionMeta()).Clone();
                meta.LinkedDepth = depth + 1;
                c.Enqueue(new CombatAction { Effect = effect, Source = action.Source, Owner = action.Owner, Target = action.Target, Card = card, Meta = meta });
            }
        }

        private static void RevealIntent(CombatState c, CombatAction action, JObject eff)
        {
            foreach (var enemy in Targets(c, action, eff.Str(K.Target)))
            {
                if (enemy.Str(K.Kind) != V.Enemy) continue;
                var until = c.Turn + Math.Max(1, Math.Min(10, Eval(c, action, eff[K.Amount], 1)));
                var owner = action.Owner ?? action.Source;
                var ownerId = owner?.Str(K.Id);
                if (ownerId == null) throw new InvalidOperationException(SM.RevealNeedsOwner);
                var map = enemy.Obj(SK.ProposalIntentRevealedUntilByOwner) ?? (JObject)(enemy[SK.ProposalIntentRevealedUntilByOwner] = new JObject());
                map[ownerId] = Js.N(until);
                c.Emit(CK.IntentRevealed, Js.Obj(K.TargetId, enemy[K.Id], K.OwnerId, ownerId, SK.Until, until));
            }
        }

        // ------------------------------------------------------------------ cards

        private static void MoveCards(CombatState c, CombatAction action, JObject eff)
        {
            var resolved = action;
            var changes = eff.Obj(Ashen.Generated.ShopKeys.Changes);
            if (changes != null && changes[SK.HideTurns] is JObject)
            {
                var effect = (JObject)eff.DeepClone();
                effect.Obj(Ashen.Generated.ShopKeys.Changes)[SK.HideTurns] = Js.N(Math.Min(10, Math.Max(1, Eval(c, action, changes[SK.HideTurns], 1))));
                resolved = new CombatAction { Effect = effect, Source = action.Source, Owner = action.Owner, Target = action.Target, Card = action.Card, Meta = action.Meta };
            }
            CoreCardZones.Move(c, resolved, Eval(c, action, eff[K.Amount], 1));
        }

        private static void Discard(CombatState c, CombatAction action, JObject eff)
        {
            var n = Math.Max(0, Eval(c, action, eff[K.Amount], 1));
            if (eff.Is(DK.Choose)) { CoreCardZones.RequestDiscard(c, n); return; }
            var random = eff.Is(K.Random);
            for (var i = 0; i < n && c.Piles.Hand.Count > 0; i++)
            {
                var index = random ? (int)Math.Floor(c.Rng.Float(RngStream.Misc) * c.Piles.Hand.Count) : c.Piles.Hand.Count - 1;
                var card = c.Piles.Hand[index];
                c.Piles.Hand.RemoveAt(index);
                c.Piles.Discard.Add(card);
                c.Emit(E.CardDiscarded, Js.Obj(K.CardInstanceId, card[K.InstanceId], K.CardId, card[K.CardId], K.Reason, random ? K.Random : V.ReasonEffect, SK.Explicit, true, K.SourceId, c.Player[K.Id]));
            }
        }

        private static void Exhaust(CombatState c, CombatAction action, JObject eff)
        {
            var n = Math.Max(0, Eval(c, action, eff[K.Amount], 1));
            for (var i = 0; i < n && c.Piles.Hand.Count > 0; i++)
            {
                var index = eff.Is(K.Random) ? (int)Math.Floor(c.Rng.Float(RngStream.Misc) * c.Piles.Hand.Count) : c.Piles.Hand.Count - 1;
                var card = c.Piles.Hand[index];
                c.Piles.Hand.RemoveAt(index);
                c.Piles.Exhaust.Add(card);
                c.Emit(E.CardExhausted, Js.Obj(K.CardInstanceId, card[K.InstanceId], K.CardId, card[K.CardId], K.Reason, V.ReasonEffect));
            }
        }

        private static void AddCard(CombatState c, CombatAction action, JObject eff)
        {
            c.Data.Cards.Get(eff.Str(K.Card));
            var count = (int)Math.Max(1, Eval(c, action, eff[K.Count], 1));
            var pileName = Js.Truthy(eff[K.Pile]) ? eff.Str(K.Pile) : V.PileDiscard;
            for (var i = 0; i < count; i++)
            {
                var inst = new JObject { [K.InstanceId] = c.NextInstanceId(), [K.CardId] = eff[K.Card].DeepClone(), [K.Upgraded] = false };
                List<JObject> pile;
                switch (pileName)
                {
                    case V.PileDraw: pile = c.Piles.Draw; break;
                    case V.PileHand: pile = c.Piles.Hand; break;
                    case V.PileDiscard: pile = c.Piles.Discard; break;
                    case V.PileExhaust: pile = c.Piles.Exhaust; break;
                    default: throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, SM.UnknownPile, pileName));
                }
                if (pileName == V.PileHand && pile.Count >= c.HandMax)
                {
                    c.Piles.Discard.Add(inst);
                    c.Emit(E.CardDiscarded, Js.Obj(K.CardInstanceId, inst[K.InstanceId], K.CardId, inst[K.CardId], K.Reason, V.ReasonHandFull));
                    continue;
                }
                var position = Js.Truthy(eff[K.Position]) ? eff.Str(K.Position) : K.Random;
                if (position == V.PositionTop) pile.Insert(0, inst);
                else if (position == V.PositionBottom) pile.Add(inst);
                else pile.Insert((int)Math.Floor(c.Rng.Float(RngStream.Shuffle) * (pile.Count + 1)), inst);
            }
        }

        private static void GainEnergy(CombatState c, CombatAction action, JObject eff)
        {
            var n = Math.Max(0, Eval(c, action, eff[K.Amount], 1) - (Js.IsNum(eff[CK.Funded]) ? Js.D(eff[CK.Funded]) : 0));
            c.Player.Put(K.Stamina, c.Player.Num(K.Stamina) + n);
            c.Emit(E.EnergyGained, Js.Obj(K.Amount, n));
        }

        // ------------------------------------------------------------------ Health

        private static void LoseHp(CombatState c, CombatAction action, JObject eff)
        {
            foreach (var t in Targets(c, action, eff.Str(K.Target)))
            {
                var amount = Math.Max(0, Eval(c, action, eff[K.Amount], 0, t));
                if (eff.Is(DK.Nonlethal) && amount >= t.Num(K.Hp)) throw new InvalidOperationException(SM.NonlethalOffering);
                var death = action.Meta?.KillReceipt ?? Js.Obj(SK.TargetStatusesBefore, t.Obj(K.Statuses)?.DeepClone() ?? new JObject(), K.SourceId, action.Source?[K.Id]);
                LoseHealth(c, t, amount, Js.Truthy(eff[K.Cause]) ? eff.Str(K.Cause) : V.CauseEffect, death);
                if (eff.Is(SK.Offering) && amount > 0)
                {
                    CoreRiders.RecordOffering(t, amount);
                    c.Emit(SV.HpOfferingPaid, Js.Obj(K.Amount, amount, K.Cost, amount, K.SourceId, t[K.Id], K.TargetId, t[K.Id]));
                }
            }
        }

        /// <summary>applyLoseHp(ctx, target, amount, cause, death): direct Health loss ignores every attack modifier and all Block.</summary>
        public static double LoseHealth(CombatState c, JObject target, double amount, string cause, JObject death = null)
        {
            if (target == null || !target.Is(K.Alive)) return 0;
            var n = Math.Max(0, Math.Floor(amount));
            if (n == 0) return 0;
            target.Put(K.Hp, target.Num(K.Hp) - n);
            c.Emit(E.HpLost, Js.Obj(K.TargetId, target[K.Id], K.Amount, n, K.Cause, cause));
            CoreImpact.AfterHpChange(c, target, death ?? new JObject());
            return n;
        }

        private static void Heal(CombatState c, CombatAction action, JObject eff)
        {
            foreach (var t in Targets(c, action, eff.Str(K.Target)))
            {
                var nominal = Actions.EvalRaw(c, action, Js.Nullish(eff[SK.AshenBlightBaseAmount]) ? eff[K.Amount] : eff[SK.AshenBlightBaseAmount], 0, t);
                var percentage = Expanded(c) && nominal > 0 ? CoreStatusControl.RestorationPercent(c, t) + Js.Or0(eff[SK.AshenBlightBonusPct]) : 0;
                var amount = Math.Max(0, Math.Floor(nominal * c.HealMult * (1 + percentage / Ashen.Generated.CombatMath.Percent)));
                var rated = Math.Floor((amount + Ratings.CardBonus(c, action.Source, action.Card, V.BonusHeal, amount) + (action.Meta?.Charge?.Or0(V.BonusHeal) ?? 0)) * CoreActions.StanceEffectiveness(c, action.Source, action.Card));
                var healing = CoreProposal.HealingPlan(c, t, rated, Js.IsStr(eff[CK.DamageType]) ? CoreDamage.CanonicalDamageType(c, eff.Str(CK.DamageType)) : CoreDamage.ProposalDamageType(c, action.Card ?? new JObject()));
                if (healing.Is(K.Damage)) LoseHealth(c, t, healing.Num(K.Damage), SV.HealingInversion);
                CoreImpact.Heal(c, t, healing.Num(DK.Healing));
            }
        }

        // ------------------------------------------------------------------ stance, stagger, cinders

        private static void EnterStance(CombatState c, CombatAction action, JObject eff)
        {
            var choose = eff[DK.Choose];
            var stanceId = Js.Truthy(choose) ? action.Meta?.Choice : eff[CK.Stance];
            var byManeuver = Js.Truthy(eff[CK.Maneuver]) || Js.Str(choose) == CK.Maneuver;
            if (!byManeuver)
            {
                if (Js.Truthy(choose) && Js.Nullish(stanceId)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, SM.ChooseNeedsPlay, Js.Str(choose)));
                throw new NotSupportedException(SM.LegacyStanceNotPorted);
            }
            var maneuver = Js.Truthy(eff[CK.Maneuver]) ? eff.Str(CK.Maneuver) : Js.Str(stanceId);
            var maneuvers = new[] { K.Attack, CV.StanceSmash, CV.StanceDefend, CV.StanceSweep, CK.Counter, CV.ManeuverCasting, CK.Evade };
            if (!maneuvers.Contains(maneuver)) throw new InvalidOperationException(SM.BadManeuver);
            foreach (var entity in Targets(c, action, Js.Truthy(eff[K.Target]) ? eff.Str(K.Target) : V.TargetSelf))
            {
                var carrier = Js.Spread(action.Card);
                var profile = Js.Spread(action.Card?.Obj(CK.CombatProfile));
                profile[CK.Maneuver] = maneuver;
                profile[CK.StanceTrigger] = true;
                profile[CK.Range] = Js.Truthy(action.Card?.Obj(CK.CombatProfile)?[CK.Range]) ? action.Card.Obj(CK.CombatProfile)[CK.Range].DeepClone() : Js.S(CV.RangeMelee);
                profile[CK.Camp] = Js.Truthy(action.Card?.Obj(CK.CombatProfile)?[CK.Camp]) ? action.Card.Obj(CK.CombatProfile)[CK.Camp].DeepClone() : Js.S(CV.CampPhysical);
                carrier[CK.CombatProfile] = profile;
                CoreStances.SetProfileStance(c, entity, carrier);
            }
        }

        /// <summary>trimCombatStances(ctx, entity): the stances an entity holds beside its main one are cut back to its capacity.</summary>
        public static void TrimStances(CombatState c, JObject entity)
        {
            if (CoreCosts.StanceCapacity(c, entity) <= 1) entity.Remove(CK.ConcurrentStances);
            else entity[CK.ConcurrentStances] = new JArray(CoreDamage.ActiveCombatStances(c, entity).Select(row => row.DeepClone()));
        }

        private static void Stagger(CombatState c, CombatAction action, JObject eff)
        {
            foreach (var t in Targets(c, action, eff.Str(K.Target))) CoreImpact.StaggerEnemy(c, t);
        }

        private static void AddBurden(CombatState c, CombatAction action, JObject eff)
        {
            foreach (var enemy in Targets(c, action, eff.Str(K.Target)))
            {
                if (enemy.Str(K.Kind) != V.Enemy) throw new InvalidOperationException(SM.BurdenEnemiesOnly);
                CoreBurdens.Add(c, enemy, eff.Str(K.Card), Eval(c, action, eff[K.Count], 1), Js.Truthy(eff[K.Pile]) ? eff.Str(K.Pile) : K.Draw);
            }
        }

        private static void AddCinders(CombatState c, CombatAction action, JObject eff)
        {
            var amount = Math.Max(0, Eval(c, action, eff[K.Amount], 0));
            var rewards = c.Fields.Obj(SK.ProposalRewards) ?? (JObject)(c.Fields[SK.ProposalRewards] = new JObject { [Ashen.Generated.NodePlaceholders.Cinders] = 0 });
            rewards.Put(Ashen.Generated.NodePlaceholders.Cinders, rewards.Num(Ashen.Generated.NodePlaceholders.Cinders) + amount);
            c.Emit(Ashen.Generated.EventValues.CindersChanged, Js.Obj(K.Amount, amount, K.Total, rewards[Ashen.Generated.NodePlaceholders.Cinders], SK.CombatReward, true));
        }

        // ------------------------------------------------------------------ Block and Barrier in the committed-action machinery

        /// <summary>
        /// gainBlockAmount(ctx, entity, amount, card, wardProvenance): Block already settled to a whole number (a protection a status rule lays down,
        /// a stance's entry), capped by the entity's blockCap.
        /// </summary>
        public static double GainBlockAmount(CombatState c, JObject entity, double amount, JObject card)
        {
            var amt = amount;
            var cap = CoreActions.StatusCap(c, entity, K.BlockCap);
            if (cap != null && entity.Or0(K.Block) + amt > cap.Value) amt = Math.Max(0, cap.Value - entity.Or0(K.Block));
            entity.Put(K.Block, entity.Or0(K.Block) + amt);
            var payload = Js.Obj(K.TargetId, entity[K.Id], K.Amount, amt);
            if (card != null && Js.Truthy(card[DK.LogSource])) payload[CK.Source] = card[DK.LogSource].DeepClone();
            if (card != null && Js.Truthy(card[K.InstanceId]))
            {
                payload[DK.CardInstanceId] = card[K.InstanceId].DeepClone();
                if (card[K.SourceHand] != null) payload[K.SourceHand] = card[K.SourceHand].DeepClone();
                if (card[K.GrantedBy] != null) payload[K.GrantedBy] = card[K.GrantedBy].DeepClone();
                if (card.Has(DK.UsedArmamentId)) payload[DK.UsedArmamentId] = card[DK.UsedArmamentId].DeepClone();
            }
            c.Emit(E.BlockGained, payload);
            return amt;
        }

        /// <summary>gainBarrierAmount(ctx, entity, amount, card, spell: false): Barrier added to the entity as it is.</summary>
        public static double GainBarrierAmount(CombatState c, JObject entity, double amount, JObject card)
        {
            entity.Put(CK.Barrier, entity.Or0(CK.Barrier) + amount);
            var payload = Js.Obj(K.TargetId, entity[K.Id], K.Amount, amount, K.Total, entity[CK.Barrier]);
            if (card != null && Js.Truthy(card[DK.LogSource])) payload[CK.Source] = card[DK.LogSource].DeepClone();
            c.Emit(DK.BarrierGained, payload);
            return amount;
        }

        /// <summary>The block / gainBarrier ops: a protection floor, a stance entry or restore is whole and unmodified; everything else is a card's Block with the charge on it.</summary>
        public static void Protection(CombatState c, CombatAction action, JObject eff)
        {
            var barrier = eff.Str(K.Op) == CV.OpGainBarrier;
            var floor = action.Card?.Is(SK.ProposalProtectionFloor) == true || (Expanded(c) && (action.Meta?.StanceEntry == true || action.Meta?.StanceRestore == true));
            foreach (var t in Targets(c, action, eff.Str(K.Target)))
            {
                var baseAmount = Eval(c, action, eff[K.Amount], 0, t);
                if (floor)
                {
                    var whole = Math.Max(0, Math.Floor(baseAmount));
                    if (barrier) GainBarrierAmount(c, t, whole, action.Card);
                    else
                    {
                        var card = Js.Spread(action.Card);
                        card[K.CardId] = Js.Null();
                        card[CK.SyntheticDefense] = true;
                        GainBlockAmount(c, t, whole, card);
                    }
                }
                else CoreActions.GainBlock(c, t, baseAmount + (action.Meta?.Charge?.Or0(V.BonusBlock) ?? 0), action.Card);
            }
        }
    }
}
