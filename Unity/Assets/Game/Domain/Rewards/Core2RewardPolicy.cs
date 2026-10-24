using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Newtonsoft.Json.Linq;
using RngStream = Ashen.Generated.RngStream;
using RM = Ashen.Generated.Core2RewardsMessages;
using RW = Ashen.Generated.Core2RewardsKeys;

namespace Ashen.Domain.Rewards
{
    /// <summary>
    /// The authored reward policy (web engine/kernel/rewardPolicy.js): nonempty groups must partition the enabled categories exactly once, and a draw
    /// picks a group by weight, then a category of it. Empty groups keep a uniform pick.
    /// </summary>
    public static class Core2RewardPolicy
    {
        /// <summary>JavaScript's <c>value &gt; 0</c> on a JSON value: a number, or a string that reads as one.</summary>
        private static bool PositiveWeight(JToken weight)
        {
            if (Js.IsNum(weight)) return Js.D(weight) > 0;
            return Js.IsStr(weight) && double.TryParse(Js.Str(weight), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0;
        }

        /// <summary>rewardGroupProblems(groups, categories): the refusals of an authored group list, in the web's words.</summary>
        public static List<string> GroupProblems(JToken groups, IReadOnlyList<string> categories)
        {
            if (!(groups is JArray list)) return new List<string> { RM.GroupsNotArray };
            if (list.Count == 0) return new List<string>();
            var problems = new List<string>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < list.Count; index++)
            {
                var group = list[index];
                var at = string.Format(CultureInfo.InvariantCulture, RM.GroupAt, index);
                var id = group is JObject o ? o[RW.Id] : null;
                var idText = Js.IsStr(id) ? Js.Str(id) : null;
                if (!Js.Truthy(group) || idText == null || idText.Length == 0 || ids.Contains(idText)) problems.Add(at + RM.GroupIdProblem);
                if (idText != null) ids.Add(idText);
                var weight = group is JObject w ? w[RW.Weight] : null;
                if (!Js.IsFinite(weight) || Js.D(weight) < 0) problems.Add(at + RM.GroupWeightProblem);
                var cats = group is JObject c ? c[RW.Categories] as JArray : null;
                if (cats == null || cats.Count == 0)
                {
                    problems.Add(at + RM.GroupCategoriesEmpty);
                    continue;
                }
                foreach (var category in cats.Select(Js.Str))
                {
                    if (category == null || !categories.Contains(category) || assigned.Contains(category))
                        problems.Add(at + string.Format(CultureInfo.InvariantCulture, RM.GroupCategoryProblem, category));
                    if (category != null) assigned.Add(category);
                }
            }
            if (!list.Any(g => g is JObject o && PositiveWeight(o[RW.Weight]))) problems.Add(RM.GroupsNeedWeight);
            foreach (var category in categories)
                if (!assigned.Contains(category)) problems.Add(string.Format(CultureInfo.InvariantCulture, RM.GroupsOmit, category));
            return problems;
        }

        /// <summary>
        /// pickRewardCategory(rng, stream, categories, groups): exhausted groups are removed before the draw, renormalizing the remaining positive
        /// weights; a zero-weight group stays disabled even if everything else is empty. Null when nothing can be drawn.
        /// </summary>
        public static string PickCategory(Rng rng, IReadOnlyList<string> categories, JToken groups)
        {
            if (categories.Count == 0) return null;
            var list = (groups as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            if (list.Count == 0) return rng.Pick(RngStream.CardRewards, categories);
            var eligible = list
                .Select(g => new KeyValuePair<double, List<string>>(Js.IsNum(g[RW.Weight]) ? Js.D(g[RW.Weight]) : 0,
                    Js.Items(g[RW.Categories]).Select(Js.Str).Where(kind => categories.Contains(kind)).ToList()))
                .Where(g => g.Key > 0 && g.Value.Count > 0).ToList();
            if (eligible.Count == 0) return null;
            var total = 0d;
            foreach (var g in eligible) total += g.Key;
            var roll = rng.Float(RngStream.CardRewards) * total;
            foreach (var g in eligible)
            {
                roll -= g.Key;
                if (roll < 0) return rng.Pick(RngStream.CardRewards, g.Value);
            }
            return rng.Pick(RngStream.CardRewards, eligible[eligible.Count - 1].Value);
        }
    }
}
