using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using SK = Ashen.Generated.ShopKeys;

namespace Ashen.Domain.Shop
{
    /// <summary>
    /// Why an action was not taken: a string key (strings/shop.en.json, or a ui.* id of strings/en.json for the
    /// screen's own availability words) and the arguments its template's {0}.. slots take. The domain never returns
    /// English (the presentation resolves the key).
    /// </summary>
    public sealed class Refusal
    {
        public Refusal(string key, params string[] args)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            Args = args ?? new string[0];
        }

        public string Key { get; }
        public IReadOnlyList<string> Args { get; }

        public JObject ToJson() => Js.Obj(SK.Key, Key, K.Args, new JArray(Args.Cast<object>().ToArray()));
    }

    /// <summary>
    /// A shipped model refusal (a plan's reason, or a commit's revalidation) raised through a commit: the caller that
    /// executes a player action turns it into a <see cref="Refusal"/>. Every other exception is a content or state
    /// defect and propagates.
    /// </summary>
    public sealed class RefusalException : InvalidOperationException
    {
        public RefusalException(string key, params string[] args) : base(key)
        {
            Refusal = new Refusal(key, args);
        }

        public Refusal Refusal { get; }
    }
}
