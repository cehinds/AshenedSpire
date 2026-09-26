using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;

namespace Ashen.Domain.Map
{
    /// <summary>
    /// rules/mapEngine.json, read once: the retry and guard counts and the closed vocabularies the shipped map code
    /// kept as constants (TYPING_RETRIES, the door guard, NODE_TYPES, ANCHOR_KINDS, MAP_SHAPE_KEYS, the node-id
    /// spelling, the boss-label separators and the event-choice history grammar).
    /// </summary>
    public sealed class MapRules
    {
        public MapRules(JObject rules)
        {
            Source = rules ?? throw new ArgumentNullException(nameof(rules));
            TypingRetries = Int(MK.Typing, MK.Retries);
            DoorGuard = Int(MK.Walk, MK.DoorGuard);
            StepMin = Int(MK.Walk, MK.StepMin);
            StepMax = Int(MK.Walk, MK.StepMax);
            ShortestAct = Int(K.Limits, MK.ShortestAct);
            var types = rules.Obj(MK.NodeTypes);
            Monster = types.Str(MK.Monster);
            Event = types.Str(MK.Event);
            Elite = types.Str(MK.Elite);
            Shrine = types.Str(MK.Shrine);
            Merchant = types.Str(MK.Merchant);
            Treasure = types.Str(MK.Treasure);
            Boss = types.Str(MK.Boss);
            NodeTypes = types.Properties().Select(p => Js.Str(p.Value)).ToList();
            var anchors = rules.Obj(MK.AnchorKinds);
            AnchorFirst = anchors.Str(MK.First);
            AnchorLast = anchors.Str(MK.Last);
            AnchorFloor = anchors.Str(MK.Floor);
            AnchorFraction = anchors.Str(MK.Fraction);
            AnchorKinds = anchors.Properties().Select(p => Js.Str(p.Value)).ToList();
            var unknown = rules.Obj(MK.UnknownKinds);
            UnknownEvent = unknown.Str(MK.Event);
            UnknownFight = unknown.Str(MK.Fight);
            RunShapeKeys = Js.Items(rules[MK.RunShapeKeys]).Select(Js.Str).ToList();
            NodeIdPrefix = rules.Obj(K.NodeId).Str(MK.Prefix);
            NodeIdSeparator = rules.Obj(K.NodeId).Str(MK.Separator);
            BossPool = rules.Str(MK.BossPool);
            EnemySeparator = rules.Obj(MK.BossLabel).Str(MK.EnemySeparator);
            PartSeparator = rules.Obj(MK.BossLabel).Str(MK.PartSeparator);
            var history = rules.Obj(MK.History);
            EventChoiceKind = history.Str(MK.EventChoiceKind);
            IdPattern = new Regex(history.Str(MK.IdPattern), RegexOptions.CultureInvariant);
            RequirementGroups = Js.Items(history[MK.RequirementGroups]).Select(Js.Str).ToList();
            SampleSeeds = Int(MK.Sample, MK.Seeds);
        }

        public JObject Source { get; }

        /// <summary>TYPING_RETRIES: typing attempts before the relax path force-places the promised counts.</summary>
        public int TypingRetries { get; }

        /// <summary>The re-roll guard that keeps an unopened door off a column a door already holds.</summary>
        public int DoorGuard { get; }

        /// <summary>The walker's column step, rng.int('map', stepMin, stepMax).</summary>
        public int StepMin { get; }

        public int StepMax { get; }

        /// <summary>The shortest act minViableFloors asks about (the first act length with a rollable floor).</summary>
        public int ShortestAct { get; }

        public string Monster { get; }
        public string Event { get; }
        public string Elite { get; }
        public string Shrine { get; }
        public string Merchant { get; }
        public string Treasure { get; }
        public string Boss { get; }

        /// <summary>NODE_TYPES, the closed set, in the shipped order.</summary>
        public IReadOnlyList<string> NodeTypes { get; }

        public string AnchorFirst { get; }
        public string AnchorLast { get; }
        public string AnchorFloor { get; }
        public string AnchorFraction { get; }

        /// <summary>ANCHOR_KINDS, the closed set.</summary>
        public IReadOnlyList<string> AnchorKinds { get; }

        /// <summary>The Unknown-node kinds the resolver names: an event (then drawn from the pool) and the fallback fight.</summary>
        public string UnknownEvent { get; }
        public string UnknownFight { get; }

        /// <summary>MAP_SHAPE_KEYS: the knobs a run shape may set.</summary>
        public IReadOnlyList<string> RunShapeKeys { get; }

        public string NodeIdPrefix { get; }
        public string NodeIdSeparator { get; }

        /// <summary>The encounter pool that boss destinations are drawn from.</summary>
        public string BossPool { get; }

        public string EnemySeparator { get; }
        public string PartSeparator { get; }

        /// <summary>EVENT_CHOICE_HISTORY_KIND.</summary>
        public string EventChoiceKind { get; }

        /// <summary>The stable-id grammar (quests.js ID_PATTERN). Matched whole, as JS <c>$</c> matches (no trailing newline).</summary>
        public Regex IdPattern { get; }

        /// <summary>The requirement groups (all, any, none), in the shipped order.</summary>
        public IReadOnlyList<string> RequirementGroups { get; }

        /// <summary>sampleActShape's default seed count.</summary>
        public int SampleSeeds { get; }

        /// <summary><c>n${floor}_${col}</c>.</summary>
        public string NodeId(int floor, int col) =>
            NodeIdPrefix + floor.ToString(System.Globalization.CultureInfo.InvariantCulture) + NodeIdSeparator + col.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>quests.js validId: a string matching the id grammar exactly.</summary>
        public bool ValidId(JToken value)
        {
            var s = Js.Str(value);
            if (s == null) return false;
            var m = IdPattern.Match(s);
            return m.Success && m.Index == 0 && m.Length == s.Length;
        }

        private int Int(string section, string key) => (int)Js.D(Source.Obj(section)?[key]);
    }
}
