using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Run
{
    /// <summary>rules/runFlow.json: the F1 default character, the first-fight rule and the autosave policy (D-063f).</summary>
    public sealed class RunFlowRules
    {
        public int SaveFormat;
        public string DefaultClass;
        public string DefaultTint;
        public string PortraitTemplate;
        public string Journey;
        public string NameKey;
        public string FirstFightSeat;
        public string FirstFightPool;
        public int FirstFightOrdinal;
        public string AfterCommand;
        public IReadOnlyCollection<string> CheckpointOn = Array.Empty<string>();

        public static RunFlowRules From(JObject json)
        {
            var creation = (JObject)json[RunFlowKeys.Creation];
            var fight = (JObject)json[RunFlowKeys.FirstFight];
            var autosave = (JObject)json[RunFlowKeys.Autosave];
            return new RunFlowRules
            {
                SaveFormat = (int)json[RunFlowKeys.SaveFormat],
                DefaultClass = (string)creation[RunFlowKeys.DefaultClass],
                DefaultTint = (string)creation[RunFlowKeys.DefaultTint],
                PortraitTemplate = (string)creation[RunFlowKeys.Portrait],
                Journey = (string)creation[RunFlowKeys.Journey],
                NameKey = (string)creation[RunFlowKeys.NameKey],
                FirstFightSeat = (string)fight[RunFlowKeys.Seat],
                FirstFightPool = (string)fight[RunFlowKeys.Pool],
                FirstFightOrdinal = (int)fight[RunFlowKeys.Ordinal],
                AfterCommand = (string)autosave[RunFlowKeys.AfterCommand],
                CheckpointOn = new HashSet<string>((autosave[RunFlowKeys.CheckpointOn] as JArray ?? new JArray()).Select(t => (string)t), StringComparer.Ordinal),
            };
        }
    }
}
