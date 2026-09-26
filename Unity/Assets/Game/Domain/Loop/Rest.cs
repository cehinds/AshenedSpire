using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Events;
using Ashen.Domain.Random;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Ashen.Domain.Shop;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;
using LK = Ashen.Generated.LoopKeys;
using LM = Ashen.Generated.LoopMessages;
using LV = Ashen.Generated.LoopValues;
using RK = Ashen.Generated.RunKeys;
using RV = Ashen.Generated.RunValues;
using SK = Ashen.Generated.ShopKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;

namespace Ashen.Domain.Loop
{
    /// <summary>
    /// Where a run stops, read as a property carrier (shipped model/locations.js): a location's tags are its tagging
    /// rows under the location family, with <c>restMana</c> resolved to the configured mode's tag; its tags say which
    /// services the place offers, and a relic's <c>restDenied</c> passive may forbid its Rest.
    /// </summary>
    public static class Locations
    {
        /// <summary>locationTags(registries, locationId): the tags tagging.csv hands a location, in file order.</summary>
        public static List<string> Tags(LoopData d, string locationId) =>
            d.Tagging.OfType<JObject>()
                .Where(row => row.Str(RK.Family) == d.RuleStr(LK.Locations, RK.Family) && row.Str(LK.ObjectId) == locationId)
                .Select(row => row.Str(LK.TagId)).ToList();

        /// <summary>locationRestTags / resolveRestTags(mode, tags): <c>restMana</c> becomes the mode's tag unless a fixed tag is authored.</summary>
        public static List<string> RestTags(LoopData d, IReadOnlyList<string> tags)
        {
            var mode = d.Balance.Obj(LK.Rest)?.Obj(K.Mana)?.Str(K.Mode);
            var byMode = d.RuleObj(LK.Locations, LK.RestManaTagByMode);
            var fixedTag = mode != null ? byMode.Str(mode) : null;
            if (fixedTag == null) throw new InvalidOperationException(RunJs.Fmt(LM.RestManaModeUnknown, mode));
            var fixedTags = byMode.Properties().Select(p => Js.Str(p.Value)).ToList();
            var restMana = d.RuleStr(LK.Locations, LK.RestManaTag);
            var overridden = tags.Any(fixedTags.Contains);
            var out_ = new List<string>();
            foreach (var tag in tags)
            {
                if (tag == restMana && overridden) continue;
                var resolved = tag == restMana ? fixedTag : tag;
                if (!out_.Contains(resolved)) out_.Add(resolved);
            }
            return out_;
        }

        /// <summary>locationServices(registries, tags): { smith, levelUp, flasks }.</summary>
        public static JObject Services(LoopData d, IReadOnlyList<string> tags)
        {
            var service = d.RuleObj(LK.Locations, LK.ServiceTags);
            return Js.Obj(LK.Smith, tags.Contains(service.Str(LK.Smith)), WK.LevelUp, tags.Contains(service.Str(WK.LevelUp)), K.Flasks, tags.Contains(service.Str(K.Flasks)));
        }

        /// <summary>restDeniedBy(registries, run, tags): the relic whose restDenied passive forbids a Rest here, or null.</summary>
        public static string RestDeniedBy(LoopData d, JToken relicIds, IReadOnlyList<string> tags)
        {
            foreach (var id in Js.Items(relicIds).Select(Js.Str))
            {
                var denied = d.Combat.Relics.Get(id).Obj(K.Passives)?[LK.RestDenied];
                if (denied != null && denied.Type == JTokenType.Boolean && denied.Value<bool>()) return id;
                if (denied is JArray list && list.Any(t => tags.Contains(Js.Str(t)))) return id;
            }
            return null;
        }
    }

    /// <summary>
    /// The grace's flask split (shipped model/gracerefill.js): the Crimson and Azure charge pools are re-split at a grace
    /// one charge at a time with the total held. The refill itself is the run-level door's refillFlasks opcode
    /// (<see cref="RunEffects.GraceRefill"/>, D-100).
    /// </summary>
    public static class GraceRefill
    {
        private static List<string> Kinds(LoopData d) => RunEffects.ChargeKinds(d.Combat);

        private static double Count(JObject charges, string kind)
        {
            var value = charges[kind];
            if (!Js.IsInt(value) || Js.D(value) < 0) throw new InvalidOperationException(RunJs.Fmt(LM.ChargeNotACount, kind));
            return Js.D(value);
        }

        /// <summary>flaskChargePlan(registries, charges): per kind, the count and whether one charge can come in (from the richest other) or go out (to the poorest other).</summary>
        public static List<(string Kind, double Count, string Donor, string Receiver, bool CanAdd, bool CanSub)> Plan(LoopData d, JObject charges)
        {
            if (charges == null || !Js.IsInt(charges[K.Capacity]) || charges.Num(K.Capacity) <= 0) throw new InvalidOperationException(LM.ChargePlanNeedsCapacity);
            var kinds = Kinds(d);
            string Pick(string self, Func<double, double, bool> better)
            {
                string best = null;
                foreach (var kind in kinds)
                {
                    if (kind == self) continue;
                    if (best == null || better(Count(charges, kind), Count(charges, best))) best = kind;
                }
                return best;
            }
            foreach (var kind in kinds) Count(charges, kind);
            return kinds.Select(kind =>
            {
                var donor = Pick(kind, (a, b) => a > b);
                var receiver = Pick(kind, (a, b) => a < b);
                var held = Count(charges, kind);
                return (kind, held, donor, receiver, donor != null && Count(charges, donor) > 0, receiver != null && held > 0);
            }).ToList();
        }

        /// <summary>moveFlaskCharge(registries, charges, { from, to }): one charge moved, the whole split rewritten (and refilled) through reallocate.</summary>
        public static JObject Move(LoopData d, JObject charges, string from, string to)
        {
            Plan(d, charges);
            var kinds = Kinds(d);
            if (from == to) throw new InvalidOperationException(LM.MoveSameKind);
            foreach (var kind in new[] { from, to }) if (!kinds.Contains(kind)) throw new InvalidOperationException(RunJs.Fmt(LM.NotAChargeKind, kind));
            if (!(charges.Num(from) > 0)) throw new InvalidOperationException(RunJs.Fmt(LM.NoChargeToMove, from));
            var next = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var kind in kinds) next[kind] = charges.Num(kind);
            next[from] -= 1;
            next[to] += 1;
            if (next.Values.Any(v => v < 0) || next.Values.Sum() != charges.Num(K.Capacity))
                throw new InvalidOperationException(RunJs.Fmt(LM.AllocationBreaksCapacity, RunJs.NumStr(charges.Num(K.Capacity))));
            foreach (var kind in kinds) charges.Put(kind, next[kind]);
            foreach (var kind in kinds) charges.Put(kind + V.CurrentSuffix, next[kind]);
            return charges;
        }
    }

    /// <summary>
    /// The character level spent at a rest place (shipped model/levelup.js levelUpPlan/applyLevelUp): each earned point
    /// raises one attribute, is recorded on the ledgers the load door checks (levelUps, levelPoints) and re-derives the
    /// pools from the run's own snapshot.
    /// </summary>
    public static class LevelPoints
    {
        /// <summary>levelUpPlan(registries, run).points: the points waiting (0 when none).</summary>
        public static double Waiting(JObject run)
        {
            var row = run.Obj(K.Level);
            var points = row?[RK.UnspentPoints];
            return Js.IsInt(points) && Js.D(points) > 0 ? Js.D(points) : 0;
        }

        /// <summary>applyLevelUp(registries, run, attributeId): one point spent on one attribute; throws by name when none waits.</summary>
        public static void Apply(LoopData d, JObject run, string attributeId)
        {
            var ids = CreationStats.OrderedAttributes(d.Run).Select(a => a.Str(K.Id)).ToList();
            if (!ids.Contains(attributeId)) throw new InvalidOperationException(RunJs.Fmt(LM.NotAnAttribute, attributeId, string.Join(RV.ListJoiner, ids)));
            if (Waiting(run) <= 0) throw new InvalidOperationException(LM.NoPointWaiting);
            if (!run.Is(K.DerivedStatRuleSnapshot) || !run.Obj(K.DerivedStatRuleSnapshot).Is(RK.Rules)) throw new InvalidOperationException(LM.NoDerivedSnapshot);
            var attributes = run.Obj(K.Attributes);
            attributes.Put(attributeId, attributes.Num(attributeId) + 1);
            var level = run.Obj(K.Level);
            level.Put(RK.UnspentPoints, level.Num(RK.UnspentPoints) - 1);
            run.Put(RK.LevelUps, (Js.IsInt(run[RK.LevelUps]) ? run.Num(RK.LevelUps) : 0) + 1);
            run.Put(RK.LevelPoints, (Js.IsInt(run[RK.LevelPoints]) ? run.Num(RK.LevelPoints) : 0) + 1);
            LevelUp.RederivePools(d.Rewards, run);
        }
    }

    /// <summary>
    /// One stay at a rest place (shipped main.js showRest over engine/locations.js createLocationVisit/arriveAt/restAt/
    /// previewRest/leaveLocation, and the rest screen's actions): the place's rules are mounted from arrival to departure;
    /// arriving fires <c>arrived</c> (the flask refill) once per stay, Rest fires <c>rested</c>. Rest, Smith and the card
    /// services end a single-use stay; flask moves and assigned points do not.
    /// </summary>
    public sealed class RestVisit
    {
        private readonly LoopContext _ctx;
        private readonly RunEffectContext _effects;
        private readonly double _healMult;
        private bool _rested;

        public string LocationId { get; }
        public List<string> TagIds { get; }

        /// <summary>{ smith, levelUp, flasks }: the services the place's tags offer.</summary>
        public JObject Services { get; }

        /// <summary>The relic forbidding the Rest here (re-read after arrival and before a Rest), or null.</summary>
        public string RestDenied { get; private set; }

        /// <summary>The arrival's refill receipt, or null (a place that refills nothing, or a stay whose arrival already ran).</summary>
        public JObject Refill { get; private set; }

        /// <summary>smithServicesAt(registries, 'shrine', rng) where the place carries the smith tag (the merchant's <see cref="SmithServices"/>), else null.</summary>
        public JObject Smith { get; private set; }

        /// <summary>Whether an action re-opens the place (the multi-use setting) rather than ending the stay.</summary>
        public bool MultiUse { get; }

        /// <summary>Whether the stay has ended (an action of a single-use stay, or Leave).</summary>
        public bool Left { get; private set; }

        private RestVisit(LoopContext ctx, string locationId, double healMult)
        {
            _ctx = ctx;
            var d = ctx.Data;
            LocationId = locationId;
            _healMult = healMult;
            var authored = Locations.Tags(d, locationId);
            if (authored.Count == 0) throw new InvalidOperationException(RunJs.Fmt(LM.LocationHasNoTags, locationId));
            TagIds = Locations.RestTags(d, authored);
            Services = Locations.Services(d, TagIds);
            var mult = healMult * Cards.PassiveMult(d.Combat, ctx.Run[K.Relics] as JArray ?? new JArray(), LK.RestHealMult, null);
            _effects = new RunEffectContext(d.Events, ctx.Run, ctx.Rng, mult, Js.Obj(LK.Counts, ctx.Settings.RefillCounts?.DeepClone() ?? new JObject()));
            Properties.MountCarrier(_effects.State, V.Player, LV.LocationKind, locationId, locationId, TagIds);
            RestDenied = Locations.RestDeniedBy(d, ctx.Run[K.Relics], TagIds);
            MultiUse = !ctx.Run.Is(RK.Journey) && ctx.Settings.MultiUse;
        }

        /// <summary>
        /// showRest(null, locationId): the stay opens at the place (a null place stands where the last one stood); its
        /// arrival fires unless a legacy-dungeon rest already refilled, and the smith services are resolved on the smith's
        /// stream where the place carries the smith tag.
        /// </summary>
        public static RestVisit Open(LoopContext ctx, string locationId)
        {
            var run = ctx.Run;
            if (run.Is(RK.Journey)) throw new NotSupportedException(LM.JourneyDeferred);
            if (!string.IsNullOrEmpty(locationId)) ctx.RestLocationId = locationId;
            var healMult = ctx.ModOn(LV.LessHealing) ? ctx.Data.Balance.Obj(LK.CustomMods).Num(LK.LessHealingMult) : 1;
            var restState = run.Obj(RK.LegacyDungeon)?.Obj(LK.ActiveRest);
            var visit = new RestVisit(ctx, ctx.RestLocationId, healMult);
            if (!(restState?.Is(LK.Refilled) ?? false))
            {
                visit.Arrive();
                if (restState != null) restState.Put(LK.Refilled, true);
            }
            if (visit.Services.Is(LK.Smith)) visit.Smith = SmithServices.At(ctx.Data.Shop, ctx.Data.RuleStr(LK.Locations, LK.SmithNodeKind), ctx.Rng);
            return visit;
        }

        private void Arrive()
        {
            _effects.Refill = null;
            _effects.EmitAndDrain(LV.Arrived, Js.Obj(LK.LocationId, LocationId));
            Refill = _effects.Refill;
            RestDenied = Locations.RestDeniedBy(_ctx.Data, _ctx.Run[K.Relics], TagIds);
        }

        /// <summary>The stay as the screen opens it: { location, tags, services, restDenied, refill, smith, multiUse }.</summary>
        public JObject ToJson() => Js.Obj(LK.Location, LocationId, K.Tags, new JArray(TagIds), LK.Services, Services.DeepClone(), LK.RestDenied, Js.S(RestDenied),
            LK.Refill, (JToken)Refill?.DeepClone() ?? Js.Null(), LK.Smith, (JToken)Smith?.DeepClone() ?? Js.Null(), LK.MultiUse, MultiUse);

        private JObject RestReceipt(RunEffectContext effects, JObject before)
        {
            var run = effects.Run;
            var events = effects.EmitAndDrain(LV.Rested, Js.Obj(LK.LocationId, LocationId));
            return Js.Obj(LK.Heal, run.Num(K.Hp) - before.Num(K.Hp), K.Mana, run.Num(K.Mana) - before.Num(K.Mana), K.Hp, run[K.Hp]?.DeepClone(),
                K.MaxHp, run[K.MaxHp]?.DeepClone(), LK.ManaAfter, run[K.Mana]?.DeepClone(), MK.Events, events);
        }

        /// <summary>
        /// previewRest(visit): what Rest would restore, on a copy of the run and of the streams, the arrival's gates carried
        /// and the denying relics set aside — the same rules, no write. Null when a relic denies the Rest.
        /// </summary>
        public JObject Preview()
        {
            if (RestDenied != null) return null;
            var d = _ctx.Data;
            var clone = (JObject)_ctx.Run.DeepClone();
            var dryCtx = new LoopContext(d, clone, _ctx.Rng.Clone(), _ctx.Profile, _ctx.Settings);
            var dry = new RestVisit(dryCtx, LocationId, _healMult);
            foreach (var gate in _effects.State.TriggerState.Entries())
                dry._effects.State.TriggerState[gate.Key] = new TriggerGate { Fires = gate.Value.Fires, Turn = gate.Value.Turn, TurnFires = gate.Value.TurnFires };
            clone[K.Relics] = new JArray(Js.Items(clone[K.Relics]).Where(id => Locations.RestDeniedBy(d, new JArray(id.DeepClone()), dry.TagIds) == null).Select(t => t.DeepClone()));
            var receipt = dry.RestReceipt(dry._effects, Js.Obj(K.Hp, clone[K.Hp]?.DeepClone(), K.Mana, clone[K.Mana]?.DeepClone()));
            Properties.UnmountCarrier(dry._effects.State, V.Player, LV.LocationKind, LocationId);
            return receipt;
        }

        /// <summary>
        /// Rest (restAt): the place's <c>rested</c> rules fire. Refused — with the preview beside it — when a relic denies
        /// it, or when a multi-use stay has rested already.
        /// </summary>
        public JObject Rest()
        {
            var relicNoRest = RestDenied != null;
            var preview = relicNoRest ? null : Preview();
            if (relicNoRest || (MultiUse && _rested))
                return Js.Obj(K.Op, LV.RestAction, LK.Refused, relicNoRest ? LV.RelicRefusal : LV.RestedRefusal, LK.Preview, (JToken)preview ?? Js.Null());
            RestDenied = Locations.RestDeniedBy(_ctx.Data, _ctx.Run[K.Relics], TagIds);
            if (RestDenied != null) throw new InvalidOperationException(RunJs.Fmt(LM.RestDenied, LocationId, RestDenied));
            var r = RestReceipt(_effects, Js.Obj(K.Hp, _ctx.Run[K.Hp]?.DeepClone(), K.Mana, _ctx.Run[K.Mana]?.DeepClone()));
            if (MultiUse) _rested = true; else Left = true;
            return Js.Obj(K.Op, LV.RestAction, LK.Preview, (JToken)preview ?? Js.Null(), LK.Heal, r[LK.Heal], K.Mana, r[K.Mana], K.Hp, r[K.Hp],
                K.MaxHp, r[K.MaxHp], LK.ManaAfter, r[LK.ManaAfter]);
        }

        /// <summary>One flask charge moved into (step &gt; 0) or out of <paramref name="kind"/>; the model picks the partner.</summary>
        public JObject MoveFlask(string kind, double step)
        {
            if (!Services.Is(K.Flasks)) return Js.Obj(K.Op, LV.FlaskAction, LK.Refused, LV.ServiceRefusal);
            var charges = _ctx.Run.Obj(K.FlaskCharges);
            var row = GraceRefill.Plan(_ctx.Data, charges).First(r => r.Kind == kind);
            var allowed = step > 0 ? row.CanAdd : row.CanSub;
            if (!allowed) return Js.Obj(K.Op, LV.FlaskAction, LK.Refused, LV.EdgeRefusal);
            var partner = step > 0 ? row.Donor : row.Receiver;
            if (step > 0) GraceRefill.Move(_ctx.Data, charges, partner, kind);
            else GraceRefill.Move(_ctx.Data, charges, kind, partner);
            return Js.Obj(K.Op, LV.FlaskAction, LK.Charges, charges.DeepClone());
        }

        /// <summary>
        /// The level card's Assign: <paramref name="assigned"/> points per attribute (at most the points waiting), committed
        /// one applyLevelUp at a time in the attribute table's order.
        /// </summary>
        public JObject AssignPoints(JObject assigned)
        {
            var d = _ctx.Data;
            if (!(LevelPoints.Waiting(_ctx.Run) > 0 && Services.Is(WK.LevelUp))) return Js.Obj(K.Op, LV.LevelAction, LK.Refused, LV.OfferRefusal);
            var pending = new JObject();
            foreach (var attr in CreationStats.OrderedAttributes(d.Run)) pending[attr.Str(K.Id)] = Js.N(Js.Or0(assigned?[attr.Str(K.Id)]));
            foreach (var p in pending.Properties().ToList())
                for (var i = 0; i < Js.D(p.Value); i++) LevelPoints.Apply(d, _ctx.Run, p.Name);
            return Js.Obj(K.Op, LV.LevelAction, LK.Assigned, pending, WK.Points, _ctx.Run.Obj(K.Level)[RK.UnspentPoints]?.DeepClone());
        }

        /// <summary>The smith services this stay offers (the table's, else the upgrade alone), or none without the smith tag.</summary>
        public List<string> OfferedServices() =>
            !Services.Is(LK.Smith) ? new List<string>()
                : Smith?[LK.Services] is JArray list ? list.Select(Js.Str).ToList() : new List<string> { LV.UpgradeService };

        /// <summary>Why no Smith upgrade can be committed here ('none' — not offered or nothing to promote; 'stones'), or null.</summary>
        public string SmithRefusal()
        {
            if (!OfferedServices().Contains(LV.UpgradeService)) return LV.NoneRefusal;
            var candidates = ItemSmithing.Plan(_ctx.Data.Shop, _ctx.Run).Arr(SK.Candidates);
            if (candidates.Count == 0) return LV.NoneRefusal;
            return candidates.OfType<JObject>().Any(c => c.Is(SK.Affordable)) ? null : LV.StonesRefusal;
        }

        /// <summary>Smith: one item promoted a tier (commitSmithing), ending a single-use stay.</summary>
        public JObject SmithItem(string itemRef)
        {
            var refusal = SmithRefusal();
            if (refusal != null) return Js.Obj(K.Op, LV.SmithAction, LK.Refused, refusal);
            var receipt = ItemSmithing.Commit(_ctx.Data.Shop, _ctx.Run, itemRef);
            if (!MultiUse) Left = true;
            return Js.Obj(K.Op, LV.SmithAction, K.ItemRef, receipt[K.ItemRef], LK.AfterLevel, receipt[LK.AfterLevel], LK.Spent, receipt[LK.Spent]);
        }

        /// <summary>Why no card service can be committed here ('none'), or null.</summary>
        public string CardServiceRefusal(string service)
        {
            if (!OfferedServices().Contains(service)) return LV.NoneRefusal;
            var plan = service == LV.ExtractService ? CardExtraction.ExtractionPlan(_ctx.Data.Shop, _ctx.Run) : CardExtraction.InstallPlan(_ctx.Data.Shop, _ctx.Run);
            return plan.Arr(SK.Candidates).Count == 0 ? LV.NoneRefusal : null;
        }

        /// <summary>Extract: the card lifted out of one mount into the run's deck, ending a single-use stay.</summary>
        public JObject Extract(string itemRef, string mountKey)
        {
            var refusal = CardServiceRefusal(LV.ExtractService);
            if (refusal != null) return Js.Obj(K.Op, LV.ExtractService, LK.Refused, refusal);
            var receipt = CardExtraction.CommitExtraction(_ctx.Data.Shop, _ctx.Run, itemRef, mountKey);
            if (!MultiUse) Left = true;
            return MountReceipt(LV.ExtractService, receipt);
        }

        /// <summary>Install: a run-owned deck card seated in an open mount, ending a single-use stay.</summary>
        public JObject Install(string itemRef, string mountKey, string instanceId)
        {
            var refusal = CardServiceRefusal(LV.InstallService);
            if (refusal != null) return Js.Obj(K.Op, LV.InstallService, LK.Refused, refusal);
            var receipt = CardExtraction.CommitInstall(_ctx.Data.Shop, _ctx.Run, itemRef, mountKey, instanceId);
            if (!MultiUse) Left = true;
            return MountReceipt(LV.InstallService, receipt);
        }

        private static JObject MountReceipt(string op, JObject receipt) =>
            Js.Obj(K.Op, op, K.ItemRef, receipt[K.ItemRef], LK.MountKey, receipt[LK.MountKey], K.CardId, receipt[K.CardId], K.InstanceId, receipt[K.InstanceId]);

        /// <summary>
        /// Leave (the screen's onDone): the place's rules unmount; a legacy-dungeon shrine resolves its node. The caller
        /// returns to the map.
        /// </summary>
        public void Leave()
        {
            Properties.UnmountCarrier(_effects.State, V.Player, LV.LocationKind, LocationId);
            Left = true;
            if (_ctx.Run.Obj(RK.LegacyDungeon)?.Is(LK.ActiveRest) ?? false) LegacyDungeons.ResolveNode(_ctx.Data, _ctx.Run);
        }
    }
}
