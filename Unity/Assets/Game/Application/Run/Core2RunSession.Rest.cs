using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Loop;
using Ashen.Domain.Shop;
using Newtonsoft.Json.Linq;
using RM = Ashen.Generated.Core2RestMessages;
using RR = Ashen.Generated.Core2RestKeys;
using RS = Ashen.Generated.Core2RestValues;
using SK = Ashen.Generated.ShopKeys;
using K = Ashen.Generated.CombatKeys;

namespace Ashen.App.Run
{
    /// <summary>What a rest service did: refused (the web's sentence, nothing changed), or the receipt it gave (the visit, the service, the pools it left).</summary>
    public sealed class Core2RestResult
    {
        /// <summary>The refusal's sentence, or null when the service was taken (or the site left).</summary>
        public string Refusal;

        /// <summary>The service's receipt (<c>visitId</c>, <c>service</c>, <c>hp</c>, <c>mana</c>, <c>stamina</c>), or null.</summary>
        public JObject Receipt;

        public bool Done => Refusal == null;
    }

    public sealed partial class Core2RunSession
    {
        private bool _resting;

        // ------------------------------------------------------------------ the rest site (WP-16)

        /// <summary>The run is at a rest site (the screen is open); a save resumes at the map, where a left site stays enterable.</summary>
        public bool IsResting => _resting;

        /// <summary>The visit the run is in (<c>gameProfileRestVisits</c>), or null.</summary>
        public JObject RestVisit => IsResting && Core2Rest.Visit(_run) is JObject visit ? (JObject)visit.DeepClone() : null;

        /// <summary>The rest site the run stands in as the screen draws it, or null when the run is not at one.</summary>
        public Core2RestViewState RestView(StringTable strings) => IsResting ? Core2RestView.Build(Core.Rewards, _run, strings) : null;

        // The rest node's door (main.js enterNode, then showRest for a configured run): the re-entry record, then the visit — the first arrival heals — and a save.
        private void EnterRest(Core2Door door)
        {
            if (Core.Rewards.Module(_run, Ashen.Generated.Core2ModuleIds.Rest) == null) return;
            Core2Rest.OpenRecord(_run);
            Core2Rest.Begin(Core.Rewards, _run);
            _pendingPort = null;
            _resting = true;
        }

        /// <summary>
        /// Take a service of the rest site (commitGameProfileRest, then recordRestConsumed): the Smith with the item to work on, or Level Up with the attribute. The pools and flasks are full
        /// afterwards. A refusal carries the web's sentence and changes nothing; a service taken is saved at once.
        /// </summary>
        public Core2RestResult TakeRestService(string service, JObject payload)
        {
            if (!IsResting) return new Core2RestResult { Refusal = RM.NoRestOpen };
            try
            {
                var receipt = Core2Rest.Commit(Core.Rewards, _run, service, payload ?? new JObject());
                Core2Rest.RecordConsumed(Core.Rewards, _run, service);
                Save();
                return new Core2RestResult { Receipt = receipt };
            }
            catch (InvalidOperationException e) { return new Core2RestResult { Refusal = e.Message }; }
        }

        /// <summary>The Smith: upgrade <paramref name="itemRef"/> one tier (<c>armament/&lt;id&gt;</c>, <c>armor/&lt;class&gt;/&lt;id&gt;</c> or <c>relic/&lt;id&gt;</c>).</summary>
        public Core2RestResult UpgradeItem(string itemRef) => TakeRestService(RS.ServiceSmith, new JObject { [RR.ItemRef] = itemRef });

        /// <summary>Level Up: spend one earned attribute point on <paramref name="attributeId"/>.</summary>
        public Core2RestResult SpendPoint(string attributeId) => TakeRestService(RS.ServiceLevelUp, new JObject { [RR.AttributeId] = attributeId });

        /// <summary>
        /// The live damage and Block of every deck card the Smith's tier step for <paramref name="itemRef"/> changes (a worn item only), read off two real fights. Slow: asked for the
        /// item under review only.
        /// </summary>
        public IReadOnlyList<JObject> SmithCardRows(string itemRef)
        {
            var plan = Core2Smith.Plan(Core.Rewards, _run, Core2Rest.SmithFree(Core.Rewards, _run));
            var candidate = Js.Items(plan[SK.Candidates]).OfType<JObject>().FirstOrDefault(c => c.Str(K.ItemRef) == itemRef);
            return candidate == null ? new List<JObject>() : Core2Smith.CardRows(Core.Rewards, _run, itemRef, candidate.Num(SK.CurrentLevel), candidate.Num(SK.NextLevel)).OfType<JObject>().ToList();
        }

        /// <summary>The rest screen's Continue (markRestLeft, then the map): the site stays enterable until a new node is chosen. Saved at once.</summary>
        public Core2RestResult LeaveRest()
        {
            if (!IsResting) return new Core2RestResult { Refusal = RM.NoRestOpen };
            // A save that throws puts the site back open, so the screen's Retry leaves again.
            var snapshot = Snapshot();
            try
            {
                Core2Rest.MarkLeft(_run);
                _resting = false;
                _location = Ashen.Generated.RunFlowValues.LocationMap;
                Save();
            }
            catch
            {
                Restore(snapshot);
                throw;
            }
            return new Core2RestResult();
        }
    }
}
