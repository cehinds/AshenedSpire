using System;
using Ashen.App.Run;
using Ashen.Domain.Shop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using SK = Ashen.Generated.ShopKeys;

namespace Ashen.App.Nodes
{
    /// <summary>
    /// A merchant visit (W-09; US-9.1–9.3, AF-08) on the climb's session: travel rolled the stock onto the run (the 'shop'
    /// and 'smith' streams) and saved it, so a reload reuses it; <see cref="Execute"/> takes one purchase, burn, smith
    /// service or sale through <see cref="RunSession.MerchantStep"/> (saved when it lands; a refusal changes and saves
    /// nothing); <see cref="Leave"/> is <see cref="RunSession.LeaveMerchant"/> (the stock leaves the run; saved at the map).
    /// The sell shelf follows the session's shopSell setting. Engine-free.
    /// </summary>
    public sealed class MerchantSession
    {
        private MerchantSession(RunSession owner) => Owner = owner;

        public RunSession Owner { get; }
        public ShopData Data => Owner.Content.Loop.Shop;

        /// <summary>settingOn(settings, 'shopSell') (D-081).</summary>
        public bool SellOn => Owner.ShopSellOn;

        /// <summary>The run document (a copy; views read one per redraw).</summary>
        public JObject Run => Owner.Run;

        public static MerchantSession Start(RunSession owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (owner.Location != RunFlowValues.LocationMerchant) throw new InvalidOperationException(string.Format(System.Globalization.CultureInfo.InvariantCulture, RunFlowMessages.NotAtNode, RunFlowValues.LocationMerchant));
            return new MerchantSession(owner);
        }

        /// <summary>The stock on a run copy (null once left).</summary>
        public static JObject Stock(JObject run) => run?[SK.ShopStock] as JObject;

        /// <summary>The domain's view of every offer (Shop.View) on a run copy, or null once left.</summary>
        public JObject DomainView(JObject run) => Stock(run) == null ? null : Shop.View(Data, run, SellOn);

        public ShopResult Execute(ShopAction action) => Owner.MerchantStep(action);

        public void Leave() => Owner.LeaveMerchant();
    }
}
