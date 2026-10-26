using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DN = Ashen.Generated.Core2DamageNumbers;
using DM = Ashen.Generated.Core2DamageMessages;
using DV = Ashen.Generated.Core2DamageValues;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The tactical receipt of a committed action against one defender (web engine/combatMatchups.js previewTacticalAction, beginTacticalAction,
    /// completeTacticalAction, normalizedProfile, preparedCounter), for the configured Core 2 profile: the contacts' damage is already planned,
    /// the action's one d20 gates every contact, and the damage provider (<see cref="CoreDamage"/>) owns stance, Block and Poise. A prepared
    /// Counter, the Guard break of a Smash and the Counter return are <see cref="CoreMatchups"/> and <see cref="CoreCounterReturn"/> (WP-8).
    /// </summary>
    public static class CoreTactical
    {
        private static double Number0(JToken t) => Js.IsFinite(t) ? Math.Max(0, Js.D(t)) : 0;

        private static bool IsSpell(JObject profile) => profile != null && CoreSets.SpellCamps.Contains(profile.Str(CK.Camp));

        internal static bool HasTrait(JObject profile, string name) =>
            Js.Includes(profile?[CK.Traits], name) || profile?.Obj(CK.CombatTraits)?[name]?.Type == JTokenType.Boolean && profile.Obj(CK.CombatTraits).Is(name)
            || Js.Includes(profile?[CK.CombatTraits], name) || Js.Includes(profile?[K.Tags], DV.TraitTagPrefix + name) || profile?[name]?.Type == JTokenType.Boolean && profile.Is(name);

        /// <summary>normalizedProfile(carrier, contact): the carrier's profile with the contact's own fields, its camp, range and targeting settled.</summary>
        public static JObject NormalizedProfile(JObject carrier, JObject contact = null)
        {
            var baseProfile = carrier?.Obj(CK.CombatProfile);
            if (baseProfile == null || !CoreSets.CampsWithProfile.Contains(baseProfile.Str(CK.Camp))) baseProfile = null;
            var profile = Js.Spread(baseProfile, contact);
            if (IsSpell(profile)) profile[CK.Camp] = CV.CampSpell;
            if (!profile.Is(CK.Range)) profile[CK.Range] = profile.Str(DK.Delivery) == DV.DeliveryProjectile || profile.Str(CK.Maneuver) == CV.StanceRanged ? CV.RangeReach : CV.RangeMelee;
            if (!profile.Is(CK.Targeting)) profile[CK.Targeting] = profile.Str(CK.Maneuver) == CV.StanceSweep ? CV.TargetingArea : CV.TargetingSingle;
            if (carrier?.Is(DK.CounterReturnAttack) == true) profile[DK.CounterReturnAttack] = true;
            if (profile[DK.Projectile] == null || profile[DK.Projectile].Type == JTokenType.Null)
                profile[DK.Projectile] = carrier?.Obj(K.Attack)?.Str(DK.Delivery) == DV.DeliveryProjectile || HasTrait(profile, DV.TraitProjectile);
            if ((profile[DK.Dodgeable] == null || profile[DK.Dodgeable].Type == JTokenType.Null) && carrier?.Obj(K.Attack)?[DK.Dodgeable] != null) profile[DK.Dodgeable] = carrier.Obj(K.Attack)[DK.Dodgeable].DeepClone();
            return profile;
        }

        /// <summary>previewTacticalAction for a configured fight: the planned amounts, nothing resolved.</summary>
        public static JObject Preview(CombatState c, JObject source, JObject target, JObject carrier, IReadOnlyList<JObject> contacts)
        {
            var profiles = contacts.Select(contact => NormalizedProfile(carrier, contact)).ToList();
            var cover = CoreMatchups.CoverContacts(c, target, carrier, profiles, contacts);
            var amounts = contacts.Select(contact => contact.Is(DK.Effect) ? 0 : Number0(contact[CK.Amount])).ToList();
            return new JObject
            {
                [CK.Version] = DN.ReceiptVersion, [DK.TargetKey] = target?[K.Id]?.DeepClone(), [DK.Profile] = NormalizedProfile(carrier),
                [DK.Profiles] = new JArray(profiles), [CK.Amount] = Js.N(amounts.Sum()), [DK.Amounts] = new JArray(amounts.Select(a => Js.N(a))),
                [DK.CounterEligible] = cover.Eligible, [CK.Counter] = (JToken)cover.Counter ?? Js.Null(), [DK.Covered] = new JArray(cover.Covered.Select(Js.B)),
                [DK.CounterDisabledAtStart] = cover.DisabledAtStart, [DK.Avoidance] = new JObject { [DK.Chance] = 0 },
                [DK.HpLoss] = 0, [K.Blocked] = 0, [DK.WardResisted] = 0, [DK.StatusAccepted] = 0, [DK.StatusResisted] = 0,
                [DK.Avoided] = false, [DK.Connected] = true, [DK.Completed] = false,
            };
        }

        /// <summary>beginTacticalAction(ctx, source, target, carrier, contacts): the receipt, then the action's one d20 (it gates every contact).</summary>
        public static JObject Begin(CombatState c, JObject source, JObject target, JObject carrier, IReadOnlyList<JObject> contacts)
        {
            var receipt = Preview(c, source, target, carrier, contacts);
            receipt[DK.Begun] = true;
            if (CoreFixCore.Enabled(c))
            {
                var hit = CoreHitRoll.Resolve(c, source, target, carrier, new HitOptions { RangeContact = CoreRange.HitContact(c, source, target, carrier), DamageType = CoreDamage.ProposalDamageType(c, carrier) });
                receipt[DK.CounterAttack] = hit;
                receipt[DK.Avoided] = hit != null && !hit.Is(DK.Hit);
                receipt[DK.Connected] = !receipt.Is(DK.Avoided);
            }
            CoreMatchups.ConsumeCounter(c, source, target, receipt);
            return receipt;
        }

        /// <summary>completeTacticalAction(ctx, source, target, carrier, receipt): the root finalizes: a Smash that broke the last Guard says so, and a Counter the action met answers it.</summary>
        public static void Complete(CombatState c, JObject source, JObject target, JObject carrier, JObject receipt)
        {
            if (receipt == null || receipt.Is(DK.Completed)) return;
            receipt[DK.Completed] = true;
            CoreCardTraits.CompleteTraitGuard(c, source, target, receipt);
            CoreMatchups.CompleteGuardBreak(c, source, target, carrier, receipt);
            CoreCounterReturn.Complete(c, source, target, carrier, receipt);
        }

        /// <summary>matchupPoiseMultiplier(ctx, source, target, carrier): a plain physical attack on a target that is preparing a Smash or a spell does more Poise.</summary>
        public static double MatchupPoiseMultiplier(CombatState c, JObject source, JObject target, JObject carrier)
        {
            var profile = carrier?.Obj(CK.CombatProfile);
            if (profile == null || !CoreSets.CampsWithProfile.Contains(profile.Str(CK.Camp)) || carrier.Is(DK.CombatReaction) || profile.Str(CK.Camp) != CV.CampPhysical || profile.Str(CK.Maneuver) != CV.StanceAttack) return 1;
            var maneuver = target?.Obj(CK.CombatStance)?.Str(CK.Maneuver);
            return maneuver == CV.StanceSmash || maneuver == CV.StanceCasting ? c.Fields.Obj(CK.CombatExpansionRules).Obj(CK.Matchups).Obj(CK.Attack).Num(DK.PreparedPoiseMultiplier) : 1;
        }

        /// <summary>stancePressureMultiplier(ctx, source, target, carrier, hit): one matchup matrix for HP, Poise and Ward.</summary>
        public static double StancePressureMultiplier(CombatState c, JObject source, JObject target, JObject carrier, JObject hit = null)
        {
            // attackHitPreview answers nothing for a creature acting on its own side before it looks at the engagement.
            var opposed = source != null && target != null && !ReferenceEquals(source, target) && source.Str(K.Kind) != target.Str(K.Kind);
            if (hit == null && opposed) hit = CoreHitRoll.Preview(c, source, target, carrier, new HitOptions { RangeContact = CoreRange.HitContact(c, source, target, carrier), DamageType = CoreDamage.ProposalDamageType(c, carrier), EvadeDisabled = CoreControl.Locked(c, target) });
            var profile = NormalizedProfile(carrier);
            var stance = CoreStances.StanceOf(c, CoreDamage.DefensiveCombatStance(c, target, new JObject { [CK.CombatProfile] = profile }));
            var incoming = profile.Str(CK.Maneuver) == CV.StanceCounter ? new JObject { [CK.Maneuver] = CV.StanceCounter } : CoreStances.StanceOf(c, new JObject { [CK.CombatProfile] = profile });
            if (incoming == null) return 1;
            var cfg = CoreStances.Rules(c);
            var defending = stance?.Str(CK.Maneuver);
            var attempted = hit?.Is(DK.EvadeAttempted) == true;
            var offense = incoming.Str(CK.Maneuver) == CV.StanceSmash && (defending == CV.StanceDefend || defending == CV.StanceCasting || attempted) ? cfg.Num(DK.SmashMultiplier)
                : incoming.Str(CK.Maneuver) == CV.StanceCounter && (defending == CV.StanceAttack || defending == CV.StanceSmash || attempted) ? cfg.Num(DK.CounterMultiplier)
                : incoming.Str(CK.Maneuver) == CV.StanceSweep && defending == CV.StanceCounter ? cfg.Num(DK.SweepCounterMultiplier) : 1;
            return offense * CoreDamage.StanceMitigation(c, incoming.Str(CK.Maneuver), defending).Num(DK.Multiplier);
        }
    }
}
