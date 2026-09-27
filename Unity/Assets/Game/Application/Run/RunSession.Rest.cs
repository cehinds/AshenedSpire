using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using LK = Ashen.Generated.LoopKeys;
using LoopContext = Ashen.Domain.Loop.LoopContext;
using LV = Ashen.Generated.LoopValues;
using RestVisit = Ashen.Domain.Loop.RestVisit;
using RK = Ashen.Generated.RunKeys;

namespace Ashen.App.Run
{
    /// <summary>What a rest action did: refused (the visit's reason code, nothing changed) or its receipt, and whether the stay ended.</summary>
    public sealed class RestActionResult
    {
        public string Refusal;
        public JObject Receipt;

        /// <summary>The stay ended (a committed service at a single-use place): the run is back on the map (or in its dungeon).</summary>
        public bool Left;

        public bool Done => Refusal == null;
    }

    /// <summary>
    /// A rest stay as a save holds it (D-124): the place it opened at (null: a legacy-dungeon shrine at the last rest place),
    /// the run's stream counters just before it opened (so a reload re-opens it on the same draws: the arrival top-up and
    /// the smith's roll), and whether a multi-use stay has rested.
    /// </summary>
    internal sealed class RestStay
    {
        public string Place;
        public JObject Counters;
        public bool Rested;

        public RestStay Copy() => new RestStay { Place = Place, Counters = (JObject)Counters?.DeepClone(), Rested = Rested };

        public JObject ToJson() => new JObject
        {
            [RunSaveKeys.RestPlace] = Place,
            [RunSaveKeys.Counters] = Counters?.DeepClone(),
            [RunSaveKeys.Rested] = Rested,
        };

        public static RestStay From(JObject json) => json == null ? null : new RestStay
        {
            Place = (string)json[RunSaveKeys.RestPlace],
            Counters = json[RunSaveKeys.Counters] as JObject,
            Rested = json[RunSaveKeys.Rested]?.Type == JTokenType.Boolean && (bool)json[RunSaveKeys.Rested],
        };
    }

    public sealed partial class RunSession
    {
        private RestStay _stay;
        private RestVisit _visit;

        /// <summary>The open rest stay (re-opened on its saved streams after a reload or a restored step), or null away from a rest place.</summary>
        public RestVisit Visit
        {
            get
            {
                if (_visit == null && !RunOver && _location == RunFlowValues.LocationRest && !IsInCombat && !HasPendingReward) ReopenStay(null);
                return Location == RunFlowValues.LocationRest ? _visit : null;
            }
        }

        /// <summary>A multi-use stay has already rested (the stay refuses a second Rest; the flag survives a reload).</summary>
        public bool StayRested => _stay?.Rested ?? false;

        /// <summary>showRest(null, place): the stay opens (arrival refill, the smith's roll) on the run's streams, remembered for a reload.</summary>
        private void OpenStay(LoopContext ctx, string place)
        {
            _stay = new RestStay { Place = place, Counters = Counters(ctx.Rng) };
            _visit = RestVisit.Open(ctx, place);
            _restAnchor = ctx.RestLocationId;
            _location = RunFlowValues.LocationRest;
        }

        /// <summary>
        /// Re-open a saved stay: the RNG goes back to the counters the stay opened on, so the arrival (a top-up that pours
        /// nothing twice, as the shipped resume notes) and the smith's roll draw exactly what they drew. If the result does not
        /// land on the saved counters (a stay action drew), the stay re-opens on the saved counters instead and warns.
        /// </summary>
        private bool ReopenStay(List<string> warnings)
        {
            var saved = _run.Obj(RK.StreamCounters);
            _rng = new Rng(Seed, ParseCounters(_stay?.Counters ?? saved));
            var ctx = Context();
            _visit = RestVisit.Open(ctx, _stay?.Place);
            _restAnchor = ctx.RestLocationId;
            if (saved == null || SameCounters(_rng, ParseCounters(saved))) return false;
            warnings?.Add(RunFlowMessages.RestResumeDrift);
            _rng = new Rng(Seed, ParseCounters(saved));
            ctx = Context();
            _visit = RestVisit.Open(ctx, _stay?.Place);
            _restAnchor = ctx.RestLocationId;
            return true;
        }

        private static bool SameCounters(Rng rng, Dictionary<RngStream, uint> counters)
        {
            foreach (var kv in rng.Counters())
                if ((counters.TryGetValue(kv.Key, out var v) ? v : 0u) != kv.Value) return false;
            return counters.All(kv => rng.Counter(kv.Key) == kv.Value);
        }

        private RestVisit RequireVisit() => Visit ?? throw new InvalidOperationException(RunFlowMessages.NotAtRest);

        /// <summary>Rest (restAt), refused with the reason when a relic forbids it or a multi-use stay has rested. PF-06 "rest action".</summary>
        public RestActionResult RestHere()
        {
            var visit = RequireVisit();
            if (_stay != null && _stay.Rested && visit.MultiUse) return new RestActionResult { Refusal = LV.RestedRefusal };
            return RestAction(v =>
            {
                var receipt = v.Rest();
                if (receipt[LK.Refused] == null && v.MultiUse && _stay != null) _stay.Rested = true;
                return receipt;
            });
        }

        /// <summary>One flask charge into (step &gt; 0) or out of a kind, the partner picked by the shared pool's plan.</summary>
        public RestActionResult MoveFlask(string kind, double step) => RestAction(v => v.MoveFlask(kind, step));

        /// <summary>Spend banked attribute points ({ attributeId: n }), one applyLevelUp at a time.</summary>
        public RestActionResult AssignPoints(JObject assigned) => RestAction(v => v.AssignPoints(assigned));

        /// <summary>Smith: the item a tier up for its Smithing Stones (every lent card follows).</summary>
        public RestActionResult Smith(string itemRef) => RestAction(v => v.SmithItem(itemRef));

        /// <summary>Extract: the card lifted out of a mount into the deck.</summary>
        public RestActionResult Extract(string itemRef, string mountKey) => RestAction(v => v.Extract(itemRef, mountKey));

        /// <summary>Install: a deck card seated in an open mount.</summary>
        public RestActionResult Install(string itemRef, string mountKey, string instanceId) => RestAction(v => v.Install(itemRef, mountKey, instanceId));

        /// <summary>Leave (the rest screen's onDone): the place's rules unmount; a dungeon shrine resolves its room. Saved at the map.</summary>
        public void LeaveRest()
        {
            RequireVisit();
            var snapshot = Snapshot();
            try
            {
                CloseStay();
                Commit(snapshot);
            }
            catch
            {
                Restore(snapshot);
                throw;
            }
        }

        private void CloseStay()
        {
            _visit.Leave();
            _visit = null;
            _stay = null;
            _location = OnMapOrDungeon();
        }

        /// <summary>
        /// One committed rest action: a refusal changes nothing and is returned; a receipt is saved at once, and when the
        /// action ended a single-use stay the stay closes first (shipped: the service commits, onDone leaves the place).
        /// </summary>
        private RestActionResult RestAction(Func<RestVisit, JObject> action)
        {
            var visit = RequireVisit();
            var snapshot = Snapshot();
            try
            {
                var receipt = action(visit);
                var refused = Js.Str(receipt?[LK.Refused]);
                if (refused != null) return new RestActionResult { Refusal = refused, Receipt = receipt };
                var left = visit.Left;
                if (left) CloseStay();
                Commit(snapshot);
                return new RestActionResult { Receipt = receipt, Left = left };
            }
            catch
            {
                Restore(snapshot);
                throw;
            }
        }
    }
}
