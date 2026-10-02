using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Shop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using LegacyDungeons = Ashen.Domain.Loop.LegacyDungeons;
using LK = Ashen.Generated.LoopKeys;
using LoopContext = Ashen.Domain.Loop.LoopContext;
using MK = Ashen.Generated.MapKeys;
using NodeOutcome = Ashen.Domain.Loop.NodeOutcome;
using RK = Ashen.Generated.RunKeys;

namespace Ashen.App.Run
{
    /// <summary>
    /// The node screens' seams on the climb's session (W-09 merchant, W-11 event and dialogue, W-13 legacy dungeon;
    /// D-140), beside the ones the act map's stream built (<see cref="CommitStep{T}"/>, <see cref="LeaveMerchant"/>,
    /// <see cref="ChooseEvent"/>, <see cref="FinishEvent"/>): a merchant action that may be refused (a refusal saves
    /// nothing), and a node step routed like travel — the loop call runs on this run and the door it opens is routed as
    /// <see cref="Travel"/> routes it (a fight begins on the run's RNG, a dungeon shrine's stay opens, a cache stands as a
    /// pending reward, a cleared dungeon left at the summit closes the run out), then the run and the profile cross one save.
    /// </summary>
    public sealed partial class RunSession
    {
        private JArray _buyBack = new JArray();

        /// <summary>What was sold at this merchant visit and can be bought back at its price (D-159); cleared on leave.</summary>
        public IReadOnlyList<JObject> BuyBackList => _buyBack.OfType<JObject>().Select(e => (JObject)e.DeepClone()).ToList();

        /// <summary>One merchant action (Shop.Execute): saved when it lands; a refusal changes and saves nothing.</summary>
        public ShopResult MerchantStep(ShopAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            Require(RunFlowValues.LocationMerchant);
            var snapshot = Snapshot();
            try
            {
                var ctx = Context();
                ShopResult result;
                if (action.Kind == ShopValues.ActionBuyBack)
                {
                    var entry = action.Index >= 0 && action.Index < _buyBack.Count ? (JObject)_buyBack[action.Index] : null;
                    result = BuyBack.Commit(ctx.Data.Shop, _run, entry);
                    if (result.Ok) _buyBack.RemoveAt(action.Index);
                }
                else
                {
                    result = Shop.Execute(ctx.Data.Shop, _run, action, ShopSellOn);
                    var sold = result.Ok ? BuyBack.EntryFor(action, result.Receipt) : null;
                    if (sold != null) _buyBack.Add(sold);
                }
                if (!result.Ok)
                {
                    Restore(snapshot);
                    return result;
                }
                Commit(snapshot);
                return result;
            }
            catch
            {
                Restore(snapshot);
                throw;
            }
        }

        /// <summary>
        /// A node screen's loop step, routed like travel (null routes nothing: the run stays where it stands), then one save;
        /// a failure restores the run and the profile and rethrows. Returns the location after the step.
        /// </summary>
        public string NodeStep(Func<LoopContext, NodeOutcome> step)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));
            var snapshot = Snapshot();
            try
            {
                var ctx = Context();
                var outcome = step(ctx);
                if (outcome != null) Route(ctx, outcome);
                Commit(snapshot);
                return Location;
            }
            catch
            {
                Restore(snapshot);
                throw;
            }
        }

        /// <summary>After a response that swapped the class (the Turncoat Mirror), the saved portrait follows the new class.</summary>
        public void FollowClass()
        {
            var portrait = Content.Portrait(ClassId, _run.Obj(LK.Customization)?.Str(RunFlowKeys.Tint));
            if (portrait == Portrait) return;
            Portrait = portrait;
            Save();
        }

        /// <summary>
        /// Review and test hook (shared tests, PlayMode smoke, captures; never called by the game): the run stands at the
        /// entrance of a legacy dungeon opened at its first start node, as a boss node's door opens it, and is saved there.
        /// </summary>
        internal void ReviewEnterDungeon(JObject def)
        {
            var nodeId = Js.Str(Js.Items(_run.Obj(RK.MapGraph)?[MK.StartIds]).FirstOrDefault());
            _run[MK.MapNodeId] = nodeId;
            _run[MK.Floor] = _run.Obj(RK.MapGraph)?.Obj(MK.Nodes)?.Obj(nodeId)?[MK.Floor]?.DeepClone();
            LegacyDungeons.Begin(Content.Loop, _run, def, nodeId);
            _location = RunFlowValues.LocationDungeon;
            Save();
        }

        /// <summary>Review hook: edit the dungeon state (stand on a node, resolved or cleared; nothing pending, no stay open) and save.</summary>
        internal void ReviewStandAt(string nodeId, bool resolved, bool cleared)
        {
            var s = _run.Obj(RK.LegacyDungeon);
            if (s == null) return;
            s[LK.Previous] = s[RewardsKeys.Current]?.DeepClone();
            s[RewardsKeys.Current] = nodeId;
            if (!Js.Includes(s[LK.Visited], nodeId)) s.Arr(LK.Visited).Add(nodeId);
            if (resolved && !Js.Includes(s[MK.Resolved], nodeId)) s.Arr(MK.Resolved).Add(nodeId);
            s[LK.Cleared] = cleared;
            s[K.Pending] = Js.Null();
            s.Remove(LK.ActiveRest);
            Save();
        }
    }
}
