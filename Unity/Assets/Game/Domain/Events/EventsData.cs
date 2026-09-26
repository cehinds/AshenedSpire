using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Ashen.Domain.Shop;
using Newtonsoft.Json.Linq;
using MK = Ashen.Generated.MapKeys;
using EK = Ashen.Generated.EventKeys;

namespace Ashen.Domain.Events
{
    /// <summary>
    /// Everything the event door reads (the shipped registries engine/quests.js, model/quests.js, model/consequence.js,
    /// model/classSwap.js and the run opcodes of engine/actions.js read, plus the content/events.js sidecars the screens
    /// read directly): the merchant's data (its smithing plan backs the upgradeCard op), the event and speaker
    /// registries, catalog/eventMeta.json (the durable choice ids, quest chains, chain speakers and event gates),
    /// catalog/eventChoiceRequirements.json (the per-choice history requirements) and the door's own rules
    /// (rules/eventsEngine.json). Read-only.
    /// </summary>
    public sealed class EventsData
    {
        public EventsData(ShopData shop, Registry events, Registry speakers, JObject eventMeta, JObject choiceRequirements, JObject engine)
        {
            Shop = shop ?? throw new ArgumentNullException(nameof(shop));
            Events = events ?? throw new ArgumentNullException(nameof(events));
            Speakers = speakers ?? throw new ArgumentNullException(nameof(speakers));
            EventMeta = eventMeta ?? new JObject();
            ChoiceRequirements = choiceRequirements ?? new JObject();
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public ShopData Shop { get; }
        public RewardsData Rewards => Shop.Rewards;
        public RunData Run => Shop.Run;
        public CombatData Combat => Shop.Combat;

        /// <summary>registries.events.</summary>
        public Registry Events { get; }

        /// <summary>registries.speakers.</summary>
        public Registry Speakers { get; }

        /// <summary>catalog/eventMeta.json: eventHistoryRequirements, eventChoiceIds, questChains, eventSpeakers.</summary>
        public JObject EventMeta { get; }

        /// <summary>content/events.js eventChoiceHistoryRequirements (per event, one requirement or null per choice).</summary>
        public JObject ChoiceRequirements { get; }

        /// <summary>rules/eventsEngine.json.</summary>
        public JObject Engine { get; }

        public JObject QuestChains => EventMeta.Obj(EK.QuestChains) ?? new JObject();
        public JObject EventSpeakers => EventMeta.Obj(EK.EventSpeakers) ?? new JObject();
        public JObject EventChoiceIds => EventMeta.Obj(EK.EventChoiceIds) ?? new JObject();
        public JObject EventHistoryRequirements => EventMeta.Obj(MK.EventHistoryRequirements) ?? new JObject();

        public JToken Rule(string section, string key) => Engine.Obj(section)?[key];

        public string RuleStr(string section, string key) => Js.Str(Rule(section, key));

        public List<string> RuleList(string section, string key) => Js.Items(Rule(section, key)).Select(Js.Str).ToList();

        public List<string> List(string key) => Js.Items(Engine[key]).Select(Js.Str).ToList();
    }
}
