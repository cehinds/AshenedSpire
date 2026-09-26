using System;
using Ashen.Domain.Loop;
using Ashen.Domain.Shop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using SK = Ashen.Generated.ShopKeys;
using SV = Ashen.Generated.ShopValues;

namespace Ashen.App.Nodes
{
    /// <summary>
    /// A merchant visit as the application plays it (W-09; US-9.1–9.3, AF-08), over the ported merchant
    /// (<see cref="Shop"/>): <see cref="Start"/> rolls the stock onto the run when the run holds none (the 'shop' and
    /// 'smith' streams; a resumed visit re-reads the saved stock) and saves at the merchant; <see cref="Execute"/> takes
    /// one purchase, burn, smith service or sale through <see cref="Shop.Execute"/> and saves after every one that lands
    /// (a refusal changes nothing and saves nothing); <see cref="Leave"/> clears the stock, saves at the act map and
    /// returns the map exit. The sell shelf follows the host's shopSell setting. Engine-free.
    /// </summary>
    public sealed class MerchantSession
    {
        private MerchantSession(LoopContext ctx, INodeHost host, NodeEntry entry)
        {
            Context = ctx;
            Host = host;
            Entry = entry;
        }

        public LoopContext Context { get; }
        public INodeHost Host { get; }
        public NodeEntry Entry { get; }
        public ShopData Data => Context.Data.Shop;
        public JObject Run => Context.Run;

        /// <summary>The stock was rolled by this visit's <see cref="Start"/> (false on a resume).</summary>
        public bool Rolled { get; private set; }

        /// <summary>The visit is over (the stock left with the player).</summary>
        public bool Left { get; private set; }

        /// <summary>settingOn(settings, 'shopSell'), the shipped default on without a host.</summary>
        public bool SellOn => Host?.SellOn ?? true;

        /// <summary>The run's stock (run.shopStock), or null once left.</summary>
        public JObject Stock => Run[SK.ShopStock] as JObject;

        /// <summary>Open the merchant for a run standing on a merchant node: roll the stock unless it is already saved, then save here.</summary>
        public static MerchantSession Start(LoopContext ctx, INodeHost host, NodeEntry entry = null)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            var session = new MerchantSession(ctx, host, entry ?? new NodeEntry { Screen = ScreenIds.Merchant, Kind = MapKeys.Merchant });
            var step = new NodeStep(ctx);
            if (!(ctx.Run[SK.ShopStock] is JObject))
            {
                Shop.Open(ctx.Data.Shop, ctx.Run, ctx.Rng);
                session.Rolled = true;
            }
            step.Commit(host, session.Entry);
            return session;
        }

        /// <summary>The domain's view of every offer (Shop.View), or null once left.</summary>
        public JObject DomainView() => Stock == null ? null : Shop.View(Data, Run, SellOn);

        /// <summary>One action; a landed one is saved at once (PF-06), a refused one changes nothing.</summary>
        public ShopResult Execute(ShopAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (action.Kind == SV.ActionLeave) throw new ArgumentException(NodeMessages.LeaveThroughLeave);
            var step = new NodeStep(Context);
            var result = Shop.Execute(Data, Run, action, SellOn);
            if (!result.Ok) return result;
            step.Commit(Host, Entry);
            return result;
        }

        /// <summary>Leave: the stock is cleared (US-9.1) and the run saved at the act map.</summary>
        public NodeExit Leave()
        {
            var step = new NodeStep(Context);
            var result = Shop.Execute(Data, Run, new ShopAction { Kind = SV.ActionLeave }, SellOn);
            if (!result.Ok) return NodeExit.To(NodeValues.ExitMap);
            step.Commit(Host, null);
            Left = true;
            return NodeExit.To(NodeValues.ExitMap);
        }
    }
}
