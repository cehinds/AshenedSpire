using Ashen.App.Ui;
using Ashen.Generated;

namespace Ashen.App.Settings
{
    public sealed partial class SettingsSession
    {
        /// <summary>The Text &amp; lore choices as card lore is set (web models/LoreTypeModel.js resolveLoreType over the six rows).</summary>
        public LoreType Lore(LoreLayout layout) =>
            layout.Resolve(Text(LoreKeys.Face), Text(LoreKeys.Size), Text(LoreKeys.Leading), Text(LoreKeys.Tracking), Text(LoreKeys.Slant), Text(LoreKeys.IdentitySlant));
    }
}
