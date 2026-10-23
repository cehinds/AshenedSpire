using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using MK = Ashen.Generated.MapKeys;

namespace Ashen.Domain.Map
{
    /// <summary>
    /// What a Core 2 profile says about its routes (web gameContent(registries, 'routes') and gameModule(registries, 'routes')):
    /// the route content as shipped (profile/content/routes.json), the module (profile/core.json modules.routes, whose
    /// hideUnknownNodes is the fog switch) and the profile hash a map is stamped with. The content stays the JSON it came as,
    /// because the builder validates it knob by knob, in the web's order and with the web's refusals.
    /// </summary>
    public sealed class CoreRoutesConfig
    {
        public CoreRoutesConfig(JObject content, JObject module, string profileHash)
        {
            Content = content;
            Module = module ?? new JObject();
            ProfileHash = profileHash;
        }

        public JObject Content { get; }

        public JObject Module { get; }

        public string ProfileHash { get; }

        /// <summary>modules.routes.hideUnknownNodes: hidden node types are shown as events (the fog).</summary>
        public bool HideUnknownNodes => Js.Truthy(Module[MK.HideUnknownNodes]);

        /// <summary>The route types the profile hides, in authored order (a missing list is empty).</summary>
        public List<string> HiddenTypes => Strings(MK.HiddenTypes);

        /// <summary>The route types the profile shows, in authored order (a missing list is empty).</summary>
        public List<string> VisibleTypes => Strings(MK.VisibleTypes);

        private List<string> Strings(string key) => Js.Items(Content?[key]).Select(Js.Str).ToList();
    }
}
