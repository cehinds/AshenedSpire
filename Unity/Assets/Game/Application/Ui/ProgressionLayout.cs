using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>One row of the Character Sheet's stat grid (web ProgressionInspectionModel.progressionStats): which stat, where its number comes from, and the string keys of its face and label.</summary>
    public sealed class ProgressionRowDef
    {
        public string Id;
        public string Source;
        public string LabelKey;
        public string FaceKey;
    }

    /// <summary>The benefit an attribute's card puts on its face (web creationBrief.js MAIN_BENEFITS): the stat and the string key of its words.</summary>
    public sealed class MainBenefitDef
    {
        public string Stat;
        public string LabelKey;
    }

    /// <summary>
    /// ui/progression.json: the rows of the Character Sheet's stat grid in the web's order, the rating rows an attribute card names as scaling, the three hand rows and the benefit each attribute leads with. Engine-free.
    /// </summary>
    public sealed class ProgressionLayout
    {
        public IReadOnlyList<ProgressionRowDef> Rows = Array.Empty<ProgressionRowDef>();
        public IReadOnlyList<string> RatingRows = Array.Empty<string>();
        public IReadOnlyDictionary<string, string> RatingLabels = new Dictionary<string, string>();
        public IReadOnlyList<string> HandRows = Array.Empty<string>();
        public IReadOnlyDictionary<string, IReadOnlyList<MainBenefitDef>> MainBenefits = new Dictionary<string, IReadOnlyList<MainBenefitDef>>();

        public static ProgressionLayout From(JObject json)
        {
            return new ProgressionLayout
            {
                Rows = ((JArray)json[ProgressionKeys.Rows]).OfType<JObject>().Select(r => new ProgressionRowDef
                {
                    Id = r.Value<string>(CardLayoutKeys.Id),
                    Source = r.Value<string>(ProgressionKeys.Source),
                    LabelKey = r.Value<string>(ProgressionKeys.LabelKey),
                    FaceKey = r.Value<string>(ProgressionKeys.FaceKey),
                }).ToList(),
                RatingRows = ((JArray)json[ProgressionKeys.RatingRows]).Select(v => (string)v).ToList(),
                RatingLabels = ((JObject)json[ProgressionKeys.RatingLabels]).Properties().ToDictionary(p => p.Name, p => (string)p.Value),
                HandRows = ((JArray)json[ProgressionKeys.HandRows]).Select(v => (string)v).ToList(),
                MainBenefits = ((JObject)json[ProgressionKeys.MainBenefits]).Properties().ToDictionary(p => p.Name,
                    p => (IReadOnlyList<MainBenefitDef>)((JArray)p.Value).OfType<JObject>().Select(b => new MainBenefitDef
                    {
                        Stat = b.Value<string>(ProgressionKeys.Stat),
                        LabelKey = b.Value<string>(ProgressionKeys.LabelKey),
                    }).ToList()),
            };
        }
    }
}
