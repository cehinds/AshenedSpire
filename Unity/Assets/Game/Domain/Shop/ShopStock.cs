using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using RngStream = Ashen.Generated.RngStream;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using WK = Ashen.Generated.RewardsKeys;
using WV = Ashen.Generated.RewardsValues;
using SK = Ashen.Generated.ShopKeys;

namespace Ashen.Domain.Shop
{
    /// <summary>
    /// The merchant's shelves (shipped engine/encounters.js buildShopStock and rollShopCards, main.js shopPriceMult):
    /// cards, relics, flasks, armaments and weapon arts rolled on the 'shop' stream in the shipped draw order, each
    /// priced from balance.shop, and the price of a card removal.
    /// </summary>
    public static class ShopStock
    {
        private static int Lo(JToken range) => (int)Js.D((range as JArray)?[0]);

        private static int Hi(JToken range) => (int)Js.D((range as JArray)?[1]);

        /// <summary>rollShopCards: the class pool plus the neutral colorless cards (weapon arts excluded), drawn without repeats.</summary>
        private static List<string> RollShopCards(ShopData d, Rng rng, string classId, double count)
        {
            var arts = new HashSet<string>(ArmamentTrading.EligibleWeaponArts(d), StringComparer.Ordinal);
            var rarities = d.RuleList(SK.Stock, SK.CardRarities);
            var colorlessClass = d.RuleStr(SK.Stock, SK.ColorlessClass);
            var colorless = d.Run.Cards.All.Where(c => c.Str(RK.Class) == colorlessClass && rarities.Contains(c.Str(RK.Rarity)) && !arts.Contains(c.Str(K.Id))).Select(c => c.Str(K.Id));
            var pool = RunJs.Strs(d.Run.Classes.Get(classId)[WK.CardPool]).Concat(colorless).ToList();
            var out_ = new List<string>();
            while (out_.Count < count && pool.Count > 0)
            {
                var id = rng.Pick(RngStream.Shop, pool);
                pool.RemoveAt(pool.IndexOf(id));
                out_.Add(id);
            }
            return out_;
        }

        private static JObject Offer(string id, int cost) => Js.Obj(K.Id, id, K.Cost, (double)cost);

        /// <summary>
        /// buildShopStock(registries, rng, run) → { cards, relics, flasks, armaments, weaponArts, removeCost }: each
        /// shelf [{ id, cost }], deterministic on stream 'shop'.
        /// </summary>
        public static JObject Build(ShopData d, Rng rng, JObject run)
        {
            var bal = d.ShopBalance;
            var classId = run.Str(RK.Class);

            var cards = new JArray();
            foreach (var id in RollShopCards(d, rng, classId, bal.Num(SK.CardStock)))
            {
                var range = bal.Obj(SK.CardCost)?[RunJs.Key(d.Run.Cards.Get(id)[RK.Rarity])];
                cards.Add(Offer(id, rng.Int(RngStream.Shop, Lo(range), Hi(range))));
            }

            var relicRarities = d.RuleList(SK.Stock, WK.RelicRarities);
            var relicPool = d.Run.Relics.All
                .Where(r => (Js.Truthy(r[WK.Pool]) ? RunJs.Key(r[WK.Pool]) : WV.RewardPool) == WV.RewardPool && relicRarities.Contains(r.Str(RK.Rarity)) && !Js.Includes(run[K.Relics], r.Str(K.Id)))
                .Select(r => r.Str(K.Id)).ToList();
            var relics = new JArray();
            for (var i = 0; i < bal.Num(SK.RelicStock) && relicPool.Count > 0; i++)
            {
                var id = rng.Pick(RngStream.Shop, relicPool);
                relicPool.RemoveAt(relicPool.IndexOf(id));
                var range = bal.Obj(SK.RelicCost)?[RunJs.Key(d.Run.Relics.Get(id)[RK.Rarity])];
                relics.Add(Offer(id, rng.Int(RngStream.Shop, Lo(range), Hi(range))));
            }

            var flaskIds = RewardRolls.UtilityFlaskIds(d.Rewards);
            var flasks = new JArray();
            for (var i = 0; i < bal.Num(SK.FlaskStock) && flaskIds.Count > 0; i++)
            {
                var id = rng.Pick(RngStream.Shop, flaskIds);
                flasks.Add(Offer(id, rng.Int(RngStream.Shop, Lo(bal[SK.FlaskCost]), Hi(bal[SK.FlaskCost]))));
            }

            var removeCost = bal.Num(SK.RemoveBase) + bal.Num(SK.RemoveStep) * run.Or0(SK.RemovesPurchased);
            var owned = new HashSet<string>(RewardRolls.CarriedIds(run.Obj(K.Loadout)).Where(x => x != null), StringComparer.Ordinal);
            var armamentCost = bal.Obj(SK.ArmamentCost);
            var armamentPool = d.Run.EquipmentRows(K.Armaments)
                .Where(p => !owned.Contains(p.Str(K.Id)) && Js.Truthy(armamentCost?[RunJs.Key(p[RK.Rarity])])).ToList();
            var armaments = new JArray();
            for (var i = 0; i < bal.Or0(SK.ArmamentStock) && armamentPool.Count > 0; i++)
            {
                var piece = rng.Pick(RngStream.Shop, armamentPool);
                armamentPool.RemoveAt(armamentPool.IndexOf(piece));
                var range = armamentCost[RunJs.Key(piece[RK.Rarity])];
                armaments.Add(Offer(piece.Str(K.Id), rng.Int(RngStream.Shop, Lo(range), Hi(range))));
            }

            var artPool = ArmamentTrading.EligibleWeaponArts(d);
            var weaponArts = new JArray();
            for (var i = 0; i < bal.Or0(SK.WeaponArtStock) && artPool.Count > 0; i++)
            {
                var id = rng.Pick(RngStream.Shop, artPool);
                artPool.RemoveAt(artPool.IndexOf(id));
                weaponArts.Add(Offer(id, rng.Int(RngStream.Shop, Lo(bal[SK.WeaponArtCost]), Hi(bal[SK.WeaponArtCost]))));
            }
            return Js.Obj(K.Cards, cards, K.Relics, relics, K.Flasks, flasks, K.Armaments, armaments, SK.WeaponArts, weaponArts, SK.RemoveCost, removeCost);
        }

        /// <summary>
        /// shopPriceMult(): the product of the active Custom Climb price mods' multipliers (Greedy Merchants, Hoarder),
        /// 1 when none is on.
        /// </summary>
        public static double PriceMult(ShopData d, JObject run)
        {
            double m = 1;
            var customMods = d.Balance.Obj(SK.CustomMods) ?? new JObject();
            foreach (var row in Js.Items(d.Engine[SK.PriceMods]).OfType<JObject>())
                if (Js.Truthy(run[RK.Custom]) && CombatEnd.ModOn(d.Rewards, run, row.Str(SK.Mod))) m *= customMods.Num(row.Str(K.Mult));
            return m;
        }
    }
}
