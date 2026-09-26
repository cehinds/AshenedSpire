using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using SK = Ashen.Generated.ShopKeys;

namespace Ashen.Domain.Shop
{
    /// <summary>
    /// Everything the merchant reads (the shipped registries main.js enterNode 'merchant', ui/screens/shop.js and the
    /// models it calls read): the post-combat data (run data, the tag tree and the Custom Climb ascension order) plus the
    /// merchant's own rules (rules/shopEngine.json — the shelves' rarities, the price mods, the smith's service
    /// vocabulary and the Smithing receipt labels the shipped code kept as constants). The smith's one port (SmithServices,
    /// ItemSmithing, CardExtraction) reads it wherever it is called: the merchant, the events and the rest stop, which
    /// reaches it as <c>LoopData.Shop</c> (D-112u). Read-only.
    /// </summary>
    public sealed class ShopData
    {
        public ShopData(RewardsData rewards, JObject engine)
        {
            Rewards = rewards ?? throw new ArgumentNullException(nameof(rewards));
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public RewardsData Rewards { get; }
        public RunData Run => Rewards.Run;
        public CombatData Combat => Rewards.Combat;
        public JObject Balance => Rewards.Balance;

        /// <summary>balance.shop.</summary>
        public JObject ShopBalance => Balance.Obj(SK.Shop) ?? new JObject();

        /// <summary>rules/shopEngine.json.</summary>
        public JObject Engine { get; }

        public JToken Rule(string section, string key) => Engine.Obj(section)?[key];

        public double RuleNum(string section, string key) => Js.D(Rule(section, key));

        public string RuleStr(string section, string key) => Js.Str(Rule(section, key));

        public JObject RuleObj(string section, string key) => Rule(section, key) as JObject ?? new JObject();

        public List<string> RuleList(string section, string key) => Js.Items(Rule(section, key)).Select(Js.Str).ToList();

        /// <summary><c>(registries.equipment.armaments || []).find((piece) => piece.id === id)</c>.</summary>
        public JObject Armament(string id) => Run.EquipmentRows(K.Armaments).FirstOrDefault(p => p.Str(K.Id) == id);
    }
}
