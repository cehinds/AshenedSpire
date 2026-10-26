using System;
using Ashen.Domain.Combat;
using Ashen.Domain.Loop;
using Ashen.Domain.Shop;
using Newtonsoft.Json.Linq;
using SW = Ashen.Generated.Core2ShopKeys;
using SM = Ashen.Generated.Core2ShopMessages;

namespace Ashen.App.Run
{
    /// <summary>What a purchase did: refused (the web's sentence, nothing changed), or the item bought and the price paid.</summary>
    public sealed class Core2ShopResult
    {
        /// <summary>The refusal's sentence, or null when the item was bought (or the shop left).</summary>
        public string Refusal;

        /// <summary>The item bought (<c>kind</c>, <c>id</c>, <c>price</c>), or null.</summary>
        public JObject Item;

        public bool Done => Refusal == null;
    }

    public sealed partial class Core2RunSession
    {
        // ------------------------------------------------------------------ the shop (WP-17)

        /// <summary>A merchant's shop is open (<c>gameProfileOpenShop</c> on the run); a save resumes at it (main.js, the load door).</summary>
        public bool HasOpenShop => Core2Shop.Open(_run) != null;

        /// <summary>The open shop's id (<c>&lt;act&gt;:&lt;node&gt;</c>), or null.</summary>
        public string OpenShopId => Core2Shop.Open(_run);

        /// <summary>The open shop's stock (id, options, taken, claimed, price), or null when no shop is open.</summary>
        public JObject ShopStock
        {
            get
            {
                var shop = Core2Shop.Open(_run);
                return shop != null && _run.Obj(Ashen.Generated.Core2RewardsKeys.GameProfileRewardOffers)?[Core2Shop.OfferId(shop)] is JObject stock ? (JObject)stock.DeepClone() : null;
            }
        }

        /// <summary>The cinders the run carries.</summary>
        public double Cinders => _run.Num(SW.Cinders);

        internal bool CanBuy(JObject stock, int index) => Core2Shop.Available(Core.Rewards, _run, stock, index);

        // The merchant node's door (main.js enterNode, then showShop): a profile without shops goes straight back to the map; otherwise the stock is drawn on the first visit.
        private void EnterShop(Core2Door door)
        {
            _pendingPort = null;
            Core2Shop.Enter(Core.Rewards, _run, _rng, door.NodeId);
        }

        /// <summary>
        /// Buy the item at <paramref name="index"/> (purchaseGameProfileShopOffer): the price comes off the cinders and the item is claimed as a reward is (a card lands in the
        /// sideboard). A refusal carries the web's sentence and changes nothing. Saved at once.
        /// </summary>
        public Core2ShopResult BuyFromShop(int index)
        {
            var shop = Core2Shop.Open(_run);
            if (shop == null) return new Core2ShopResult { Refusal = SM.NoShopOpen };
            var snapshot = Snapshot();
            try
            {
                var item = Core2Shop.Purchase(Core.Rewards, _run, shop, index);
                SaveOrRestore(snapshot);
                return new Core2ShopResult { Item = item };
            }
            catch (InvalidOperationException e) { return new Core2ShopResult { Refusal = e.Message }; }
        }

        /// <summary>The shop screen's Leave: the open marker goes (the stock keeps what was taken), the climb is back at the map. Saved at once.</summary>
        public Core2ShopResult LeaveShop()
        {
            if (!HasOpenShop) return new Core2ShopResult { Refusal = SM.NoShopOpen };
            // A save that throws puts the shop back open, so the screen's Retry leaves again.
            var snapshot = Snapshot();
            try
            {
                Core2Shop.Leave(_run);
                _location = Ashen.Generated.RunFlowValues.LocationMap;
                Save();
            }
            catch
            {
                Restore(snapshot);
                throw;
            }
            return new Core2ShopResult();
        }
    }
}
