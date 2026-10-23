using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DN = Ashen.Generated.Core2DamageNumbers;
using DM = Ashen.Generated.Core2DamageMessages;
using DV = Ashen.Generated.Core2DamageValues;
using K = Ashen.Generated.CombatKeys;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// A committed card or move as one action (web engine/combatExpansionActions.js): its effects are planned together, every defender gets one
    /// receipt across all its contacts, and a per-hit fight (the reaction rules' contact policy) runs each hit as its own contact with its own
    /// receipt. Planning is committed and seeded: the targets, the contacts and their damage are fixed when the action is accepted and the queue
    /// then plays them. A Counter's reply is one such action (<see cref="CoreCounterReturn"/>); a player's reaction, a card trait or a critical hit is a later package's and is refused by name.
    /// </summary>
    public static class CoreExpansion
    {
        private static bool Present(JObject entity) => entity != null && entity.Is(K.Alive);

        private static double Number0(JToken t) => Js.IsFinite(t) ? Math.Max(0, Js.D(t)) : 0;

        /// <summary>The fight's pending count of committed actions that have not completed (web combat.pendingExpansionActions).</summary>
        public static double Pending(CombatState c) => Js.Or0(c.Fields[DK.PendingExpansionActions]);

        public static void AddPending(CombatState c, double delta) => c.Fields[DK.PendingExpansionActions] = Js.N(Math.Max(0, Pending(c) + delta));

        /// <summary>effectCarrier(carrier, effect): the carrier with the effect's own attack, tags, school and profile laid over it.</summary>
        public static JObject EffectCarrier(CombatState c, JObject carrier, JObject effect)
        {
            var merged = Js.Spread(carrier);
            if (effect.Is(K.Attack)) merged[K.Attack] = effect[K.Attack].DeepClone();
            if (effect.Is(K.Tags)) merged[K.Tags] = effect[K.Tags].DeepClone();
            if (effect.Is(CK.DamageSchool)) merged[CK.DamageSchool] = effect[CK.DamageSchool].DeepClone();
            var typed = CoreStances.ProfileFor(c, merged);
            if (!typed.Is(CK.DamageType) && carrier?.Obj(CK.CombatProfile)?.Is(CK.DamageType) == true) typed.Remove(CK.DamageType);
            var profile = Js.Spread(carrier?.Obj(CK.CombatProfile), typed, effect.Obj(CK.CombatProfile));
            merged[CK.CombatProfile] = profile;
            if (effect.Is(K.Attack) && !typed.Is(CK.DamageComponents)) profile.Remove(CK.DamageComponents);
            var maneuver = Js.Truthy(effect.Obj(CK.CombatProfile)?[CK.Maneuver]) ? effect.Obj(CK.CombatProfile)[CK.Maneuver] : Js.Truthy(carrier?.Obj(CK.CombatProfile)?[CK.Maneuver]) ? carrier.Obj(CK.CombatProfile)[CK.Maneuver] : profile[CK.Maneuver];
            profile[CK.Maneuver] = maneuver?.DeepClone();
            if (carrier?.Is(DK.CounterReturnAttack) == true) profile[DK.CounterReturnAttack] = true;
            return merged;
        }

        private static PredicateContext Ctx(CombatAction a, JObject target) => new PredicateContext { Owner = a.Owner ?? a.Source, Source = a.Source, Target = target, Card = a.Card, Meta = a.Meta };

        private static JObject EntityOf(CombatState c, string id) => id == null ? null : Triggers.FindEntity(c, id);

        /// <summary>
        /// enqueueExpandedAction(ctx, actions, { source, target, carrier, completionMeta }): plans the contacts of a committed action's effects,
        /// queues them (one unit per hit in a per-hit fight) and the completion that settles them. Returns the group.
        /// </summary>
        public static ExpansionGroup Enqueue(CombatState c, List<CombatAction> actions, JObject source, JObject target, JObject carrier, ActionMeta completionMeta = null)
        {
            // An enemy's reaction move is planned as an attack of its own: the group holds a carrier whose maneuver is attack, and its actions carry that carrier.
            if (carrier.Is(DK.CombatReaction) && !carrier.Is(DK.CounterReturnAttack))
            {
                carrier = Js.Spread(carrier);
                var reactionProfile = Js.Spread(carrier.Obj(CK.CombatProfile));
                reactionProfile[CK.Maneuver] = CV.StanceAttack;
                carrier[CK.CombatProfile] = reactionProfile;
                foreach (var reactionAction in actions) reactionAction.Card = carrier;
            }
            if (carrier.Is(DK.ComboHook)) throw new NotSupportedException(DM.ComboNotPorted);
            var serial = CoreSerial.NextGroup(c);
            var group = new ExpansionGroup
            {
                Key = (Js.Truthy(c.Fields[CK.CombatKey]) ? c.Fields.Str(CK.CombatKey) : DV.CombatKeyDefault) + DV.KeySeparator + serial.ToString(CultureInfo.InvariantCulture),
                SourceId = source?.Str(K.Id), Carrier = carrier,
            };
            CoreRange.Accept(c, source, actions, carrier, group.Key);
            var plannedOnce = new HashSet<string>(StringComparer.Ordinal);
            for (var rangeEffectIndex = 0; rangeEffectIndex < actions.Count; rangeEffectIndex++)
            {
                var action = actions[rangeEffectIndex];
                action.Meta = (action.Meta ?? ActionMeta.Empty()).Clone();
                action.Meta.Group = group;
                var eff = action.Effect;
                var op = eff.Str(K.Op);
                var targeted = CoreSets.TargetedSpecs.Contains(eff.Str(K.Target)) || CoreSets.TargetedOps.Contains(op);
                if (!targeted && !CoreSets.PlannedOps.Contains(op)) continue;
                if (eff.Is(CK.OncePerTurn))
                {
                    // The plan reads a detached copy: nothing is marked now, and an effect of the same family and marker planned earlier counts as spent.
                    var owner = action.Owner ?? action.Source ?? c.Player;
                    var index = action.Meta.AbilityEffectIndex != null ? Js.N(action.Meta.AbilityEffectIndex.Value) : eff[K.Op];
                    var skipped = eff.Is(K.If) && !Triggers.EvalPredicate(c, eff.Obj(K.If), Ctx(action, action.Target));
                    if (!skipped) skipped = !CoreRiders.IsFree(owner, eff[CK.OncePerTurn], action.Card, index) || !plannedOnce.Add(owner.Str(K.Id) + CoreRiders.OnceKey(eff[CK.OncePerTurn], action.Card, index));
                    if (skipped)
                    {
                        action.Meta.ExpansionContacts = new List<List<ContactRef>>();
                        action.Meta.ExpansionTargets = new List<List<string>>();
                        continue;
                    }
                }
                var repeat =Math.Max(0, Actions.EvalNum(c, action, eff[CK.Repeat], 1, action.Target));
                var hits = op == Op.Damage ? Math.Max(0, Actions.EvalNum(c, action, eff[K.Hits], 1, action.Target)) : 1;
                action.Meta.ExpansionContacts = new List<List<ContactRef>>();
                action.Meta.ExpansionTargets = new List<List<string>>();
                for (var r = 0; r < repeat; r++)
                    for (var h = 0; h < hits; h++)
                    {
                        var contacts = new List<ContactRef>();
                        var refs = new List<string>();
                        var batchIndex = (int)(r * hits + h);
                        var entities = action.Meta.RangeTargetBatches != null && batchIndex < action.Meta.RangeTargetBatches.Count
                            ? action.Meta.RangeTargetBatches[batchIndex].Select(id => EntityOf(c, id)).Where(e => e != null).ToList()
                            : Actions.ResolveTargets(c, action, eff.Str(K.Target));
                        foreach (var entity in entities)
                        {
                            if (!Present(entity) || (eff.Is(K.If) && !Triggers.EvalPredicate(c, eff.Obj(K.If), Ctx(action, entity)))) continue;
                            refs.Add(entity.Str(K.Id));
                            if (entity == source || (entity.Str(K.Kind) == source?.Str(K.Kind) && op != Op.Damage)) continue;
                            var key = entity.Str(K.Id);
                            if (!group.Targets.TryGetValue(key, out var row)) group.Targets[key] = row = new TargetRow { Id = key };
                            var cardWithCarrier = Js.Spread(carrier, action.Card);
                            var contactCarrier = EffectCarrier(c, cardWithCarrier, eff);
                            var profile = contactCarrier.Obj(CK.CombatProfile);
                            var baseToken = !Js.Nullish(eff[CK.Amount]) ? eff[CK.Amount] : !Js.Nullish(eff[K.Stacks]) ? eff[K.Stacks] : Js.N(1);
                            var baseAmount = Actions.EvalNum(c, action, baseToken, 1, entity);
                            if (op == Op.Damage && source.Obj(CK.CritRules)?.Count > 0 && !carrier.Is(DK.CombatReaction)) throw new NotSupportedException(DM.CritNotPorted);
                            var abilityBase = baseAmount;
                            double amount = 0;
                            JObject split = new JObject();
                            if (op == Op.Damage)
                            {
                                var rangeContact = CoreRange.HitContact(c, source, entity, contactCarrier);
                                amount = CoreDamage.ProfileDamageReceipt(c, source, entity, abilityBase, contactCarrier, rangeContact).Num(DK.Raw);
                                split = Js.Obj(DK.SplitBase, Math.Max(0, Math.Floor(baseAmount)), DK.RatingBonus, contactCarrier.Is(DK.SkipRatingBonus) ? 0 : Math.Max(0, Math.Floor(Ratings.CardBonus(c, source, contactCarrier, Op.Damage, abilityBase))),
                                    DK.RatingPart, RatingPart(c, contactCarrier));
                            }
                            var index = row.Contacts.Count;
                            var contact = Js.Spread(profile);
                            if (carrier.Obj(DK.RangePlan) != null)
                                contact[DK.RangeContactId] = string.Join(DV.ContactSeparator, rangeEffectIndex, r, h, source.Str(K.Kind) == V.Enemy ? DV.PlayerSeat : DV.EnemySeat + entity.Str(K.Id));
                            contact[CK.Amount] = Js.N(amount);
                            foreach (var p in split.Properties()) contact[p.Name] = p.Value.DeepClone();
                            if (Js.IsFinite(contactCarrier[CK.MagicShare])) contact[CK.MagicShare] = contactCarrier[CK.MagicShare].DeepClone();
                            if (Js.Truthy(contactCarrier[CK.PhysicalDamageType])) contact[CK.PhysicalDamageType] = contactCarrier[CK.PhysicalDamageType].DeepClone();
                            if (op != Op.Damage)
                            {
                                contact[DK.Effect] = op == Op.PoiseDamage || op == DV.OpWardDamage ? DV.EffectImpact : DV.EffectStatus;
                                contact[DK.Pressure] = Js.N(baseAmount);
                            }
                            row.Contacts.Add(contact);
                            contacts.Add(new ContactRef { Key = key, Index = index, Id = key });
                        }
                        action.Meta.ExpansionContacts.Add(contacts);
                        action.Meta.ExpansionTargets.Add(refs);
                    }
            }
            AddPending(c, 1);
            foreach (var queued in ContactActions(c, group, actions)) c.Enqueue(queued);
            var completion = (completionMeta ?? ActionMeta.Empty()).Clone();
            completion.Group = group;
            c.Enqueue(new CombatAction { Effect = Js.Obj(K.Op, DV.OpCompleteCombatAction), Source = source, Owner = source, Target = target, Card = carrier, Meta = completion });
            // A reaction offer check reads the planned contacts of the card it tries (enemy reactions).
            c.GroupSink?.Add(group);
            return group;
        }

        private static string RatingPart(CombatState c, JObject carrier)
        {
            var id = carrier.Str(CK.RatingId);
            if (id == CK.RatingPr) return DV.PartMagic;
            if (id == CK.RatingAr) return DV.PartPhysical;
            if (id != null && Js.Truthy(carrier[CK.RatingId])) return null;
            return Ratings.IsMagicalAttack(c, carrier) ? DV.PartMagic : DV.PartPhysical;
        }

        // ------------------------------------------------------------------ per-hit contacts

        private static List<CombatAction> ContactActions(CombatState c, ExpansionGroup group, List<CombatAction> actions)
        {
            var policy = c.Fields.Obj(CK.ReactionRules)?.Str(DK.ContactPolicy);
            if (policy != DV.PolicyPerHit || group.Carrier.Is(DK.CombatReaction)) return actions;
            var contacts = new List<OrderedMap<TargetRow>>();
            var queued = new List<CombatAction>();
            var ancillary = new List<KeyValuePair<CombatAction, int>>();
            for (var effectIndex = 0; effectIndex < actions.Count; effectIndex++)
            {
                var action = actions[effectIndex];
                if (action.Effect.Str(K.Op) != Op.Damage || action.Meta.ExpansionContacts == null || action.Meta.ExpansionContacts.Count == 0)
                {
                    if (action.Meta.ExpansionContacts != null && action.Meta.ExpansionContacts.Count > 0) ancillary.Add(new KeyValuePair<CombatAction, int>(action, Math.Max(0, contacts.Count - 1)));
                    queued.Add(action);
                    continue;
                }
                for (var index = 0; index < action.Meta.ExpansionContacts.Count; index++)
                {
                    var batch = action.Meta.ExpansionContacts[index];
                    var targets = new OrderedMap<TargetRow>();
                    var refs = new List<ContactRef>();
                    foreach (var contact in batch)
                    {
                        var original = group.Targets[contact.Key];
                        if (!targets.TryGetValue(contact.Key, out var row)) targets[contact.Key] = row = new TargetRow { Id = original.Id };
                        var next = contact.Copy();
                        next.Index = row.Contacts.Count;
                        row.Contacts.Add(original.Contacts[contact.Index]);
                        refs.Add(next);
                    }
                    contacts.Add(targets);
                    var contactIndex = contacts.Count - 1;
                    var meta = action.Meta.Clone();
                    meta.ContactIndex = contactIndex;
                    meta.ContactEffectIndex = effectIndex;
                    meta.ContactEffectFirst = index == 0;
                    meta.ExpansionContacts = new List<List<ContactRef>> { refs };
                    meta.ExpansionTargets = new List<List<string>> { action.Meta.ExpansionTargets[index] };
                    meta.ExpansionCursor = 0;
                    meta.RangeSiteBatches = action.Meta.RangeSiteBatches != null ? new JArray { index < action.Meta.RangeSiteBatches.Count ? action.Meta.RangeSiteBatches[index].DeepClone() : new JArray() } : null;
                    var effect = Js.Spread(action.Effect);
                    effect[K.Hits] = 1;
                    effect[CK.Repeat] = 1;
                    var unit = new CombatAction { Effect = effect, Source = action.Source, Owner = action.Owner, Target = action.Target, Card = action.Card, Meta = meta };
                    var completionMeta = meta.Clone();
                    completionMeta.ContactCompletion = true;
                    queued.Add(unit);
                    queued.Add(new CombatAction { Effect = Js.Obj(K.Op, DV.OpCompleteCombatAction), Source = unit.Source, Owner = unit.Owner, Target = unit.Target, Card = unit.Card, Meta = completionMeta });
                }
            }
            if (contacts.Count == 0) return actions;
            foreach (var pair in ancillary)
            {
                var targets = contacts[pair.Value];
                pair.Key.Meta.ExpansionContacts = pair.Key.Meta.ExpansionContacts.Select(batch => batch.Select(contact =>
                {
                    var original = group.Targets[contact.Key];
                    if (!targets.TryGetValue(contact.Key, out var row)) targets[contact.Key] = row = new TargetRow { Id = original.Id };
                    var next = contact.Copy();
                    next.Index = row.Contacts.Count;
                    row.Contacts.Add(original.Contacts[contact.Index]);
                    return next;
                }).ToList()).ToList();
            }
            group.ContactSequence = new ContactSequence { RootKey = group.Key, Index = -1, Contacts = contacts, RootTargets = group.Targets };
            Activate(c, queued.FirstOrDefault(a => a.Meta?.ContactIndex == 0));
            return queued;
        }

        /// <summary>activateCombatContact(ctx, action): the group's live hit becomes the one this unit belongs to.</summary>
        public static void Activate(CombatState c, CombatAction action)
        {
            var group = action?.Meta?.Group;
            var sequence = group?.ContactSequence;
            if (sequence == null) return;
            action.Meta.ProposalTargetDecisions = sequence.ProposalTargetDecisions ??= action.Meta.ProposalTargetDecisions ?? new JObject();
            var index = action.Meta.ContactIndex;
            if (index == null || sequence.Index == index || action.Meta.ContactCompletion) return;
            sequence.Index = index.Value;
            group.Key = sequence.RootKey + DV.HitSeparator + index.Value.ToString(CultureInfo.InvariantCulture);
            group.Targets = sequence.Contacts[index.Value];
            group.Begun = false;
            group.PayloadStarted = false;
            group.ReactionAnswered = null;
            group.ReactionNoticed = null;
            group.ManualCounterSerials = null;
            group.EnemyWindows = null;
            group.ContactTelegraphed = false;
        }

        // ------------------------------------------------------------------ begin and complete

        /// <summary>beginExpandedAction(ctx, group, source): each defender's receipt is written once, before the first of the action's effects runs.</summary>
        public static void Begin(CombatState c, ExpansionGroup group, JObject source)
        {
            if (group == null || group.Begun) return;
            group.Begun = true;
            foreach (var pair in group.Targets.Entries())
            {
                var row = pair.Value;
                var target = EntityOf(c, row.Id);
                if (!Present(target)) continue;
                var profile = row.Contacts.Count > 0 ? row.Contacts[0] : group.Carrier.Obj(CK.CombatProfile) ?? new JObject();
                var carrier = Js.Spread(group.Carrier);
                carrier[CK.CombatProfile] = Js.Spread(group.Carrier.Obj(CK.CombatProfile), profile);
                if (Js.Truthy(profile[DK.RangeContactId])) carrier[DK.RangeContactId] = profile[DK.RangeContactId].DeepClone();
                carrier[DK.AdditionalTargets] = Math.Max(0, group.Targets.Count - 1);
                SetManualSerial(group, carrier, row);
                row.Receipt = CoreTactical.Begin(c, source, target, carrier, row.Contacts);
                row.Interactions = Js.Spread(profile);
                row.Interactions[DK.ContainsFire] = row.Contacts.Any(contact => contact.Num(CK.Amount) > 0 && (contact.Str(CK.School) == DV.SchoolFire || contact.Str(CK.DamageType) == DV.SchoolFire));
                row.Interactions[DK.ContainsBlunt] = row.Contacts.Any(contact => contact.Num(CK.Amount) > 0 && contact.Str(CK.DamageType) == DV.DamageBlunt);
                row.Interactions[DK.ContainsReveal] = row.Contacts.Any(contact => contact.Num(CK.Amount) > 0 && (contact.Str(CK.Maneuver) == CV.StanceSweep || CoreSets.RevealTypes.Contains(contact.Str(CK.DamageType))));
                CoreControl.BeforeStatusAction(c, target, row.Interactions, row.Receipt.Is(DK.Connected));
            }
        }

        /// <summary>The Counter serial a seat paid a reaction for on this root rides the carrier its hits are met with (web group.manualCounterSerials).</summary>
        public static void SetManualSerial(ExpansionGroup group, JObject carrier, TargetRow row)
        {
            if (group.ManualCounterSerials != null && group.ManualCounterSerials.TryGetValue(V.Player, out var serial) && serial > 0) carrier[Ashen.Generated.Core2MatchupKeys.ManualCounterSerial] = Js.N(serial);
        }

        private static TargetRow RootRow(ExpansionGroup group, string key) => group.ContactSequence.RootTargets[key];

        private static void CompleteContact(CombatState c, CombatAction action, ExpansionGroup group)
        {
            var start = c.Queue.Count;
            foreach (var pair in group.Targets.Entries().ToList())
            {
                var key = pair.Key;
                var row = pair.Value;
                var target = EntityOf(c, row.Id);
                var receipt = row.Receipt;
                if (target == null || receipt == null || !Present(target)) { CoreCounterReturn.DiscardReservation(group, target, row); continue; }
                var incoming = Js.Spread(group.Carrier);
                if (Js.Truthy(row.Contacts.FirstOrDefault()?[DK.RangeContactId])) incoming[DK.RangeContactId] = row.Contacts[0][DK.RangeContactId].DeepClone();
                SetManualSerial(group, incoming, row);
                CoreTactical.Complete(c, action.Source, target, incoming, receipt);
                CoreCounterReturn.DiscardReservation(group, target, row);
                var root = RootRow(group, key);
                root.Receipt ??= new JObject { [DK.Connected] = false, [DK.StatusAccepted] = 0, [CK.Amount] = 0 };
                root.Receipt[DK.Connected] = root.Receipt.Is(DK.Connected) || receipt.Is(DK.Connected);
                root.Receipt[DK.StatusAccepted] = Js.N(root.Receipt.Num(DK.StatusAccepted) + receipt.Or0(DK.StatusAccepted));
                root.Receipt[CK.Amount] = Js.N(root.Receipt.Num(CK.Amount) + receipt.Or0(CK.Amount));
                root.Interactions ??= row.Interactions;
            }
            // A reply and its bounded response finish before the next incoming contact.
            var generated = c.Queue.Skip(start).ToList();
            foreach (var queued in generated) c.Queue.Remove(queued);
            for (var i = generated.Count - 1; i >= 0; i--) c.Queue.AddFirst(generated[i]);
        }

        private static OrderedMap<TargetRow> CompletedRootTargets(ExpansionGroup group)
        {
            var sequence = group.ContactSequence;
            var roots = sequence.RootTargets;
            foreach (var pair in roots.Entries())
            {
                var root = pair.Value;
                var receipts = sequence.Contacts.Select(contact => contact.TryGetValue(pair.Key, out var row) ? row.Receipt : null).Where(r => r != null).ToList();
                root.Receipt = new JObject
                {
                    [DK.Connected] = receipts.Any(r => r.Is(DK.Connected)),
                    [DK.StatusAccepted] = Js.N(receipts.Sum(r => r.Or0(DK.StatusAccepted))),
                    [CK.Amount] = Js.N(receipts.Sum(r => r.Or0(CK.Amount))),
                    [DK.Profile] = root.Contacts.Count > 0 ? root.Contacts[0] : group.Carrier.Obj(CK.CombatProfile),
                };
                var profile = root.Receipt.Obj(DK.Profile);
                root.Interactions = Js.Spread(profile);
                root.Interactions[DK.ContainsFire] = root.Contacts.Any(contact => contact.Num(CK.Amount) > 0 && (contact.Str(CK.School) == DV.SchoolFire || contact.Str(CK.DamageType) == DV.SchoolFire));
                root.Interactions[DK.ContainsBlunt] = root.Contacts.Any(contact => contact.Num(CK.Amount) > 0 && contact.Str(CK.DamageType) == DV.DamageBlunt);
                root.Interactions[DK.ContainsReveal] = root.Contacts.Any(contact => contact.Num(CK.Amount) > 0 && (contact.Str(CK.Maneuver) == CV.StanceSweep || CoreSets.RevealTypes.Contains(contact.Str(CK.DamageType))));
            }
            return roots;
        }

        /// <summary>completeExpandedAction(ctx, action): settles the action, or one hit of it: the defenders' status finalization, then the action stops counting as pending.</summary>
        public static void Complete(CombatState c, CombatAction action)
        {
            var group = action.Meta?.Group;
            if (group == null) return;
            if (action.Meta.ContactCompletion)
            {
                if (!group.Cancelled) CompleteContact(c, action, group);
                return;
            }
            if (group.Cancelled) { AddPending(c, -1); return; }
            Begin(c, group, action.Source);
            if (group.ContactSequence != null) group.Targets = CompletedRootTargets(group);
            foreach (var pair in group.Targets.Entries().ToList())
            {
                var row = pair.Value;
                var target = EntityOf(c, row.Id);
                var receipt = row.Receipt;
                if (target == null || receipt == null) continue;
                // A Smash that killed the target still broke its Guard; a dead target takes no bonus Poise.
                if (!target.Is(K.Alive)) { CoreMatchups.CompleteGuardBreak(c, action.Source, target, group.Carrier, receipt); continue; }
                if (group.ContactSequence == null)
                {
                    var settled = Js.Spread(group.Carrier);
                    SetManualSerial(group, settled, row);
                    CoreTactical.Complete(c, action.Source, target, settled, receipt);
                }
                CoreControl.CompleteStatusAction(c, target, row.Interactions ?? receipt.Obj(DK.Profile) ?? new JObject(), receipt.Is(DK.Connected), group.Key, receipt.Num(CK.Amount) > 0);
            }
            foreach (var pair in group.Targets.Entries()) CoreCounterReturn.DiscardReservation(group, EntityOf(c, pair.Value.Id), pair.Value);
            CoreCardTraits.ApplyImpact(c, action, group);
            AddPending(c, -1);
        }
    }
}
