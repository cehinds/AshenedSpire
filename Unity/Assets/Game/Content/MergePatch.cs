using Newtonsoft.Json.Linq;

namespace Ashen.Content
{
    /// <summary>RFC 7386 JSON merge-patch (docs/design/08 §8): objects merge, null deletes, anything else replaces.</summary>
    public static class MergePatch
    {
        public static JToken Apply(JToken target, JToken patch)
        {
            if (!(patch is JObject patchObj)) return patch?.DeepClone();
            var result = target is JObject t ? (JObject)t.DeepClone() : new JObject();
            foreach (var p in patchObj.Properties())
            {
                if (p.Value.Type == JTokenType.Null) result.Remove(p.Name);
                else result[p.Name] = Apply(result[p.Name], p.Value);
            }
            return result;
        }
    }
}
