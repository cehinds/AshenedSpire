using Ashen.App.Ui;
using Newtonsoft.Json.Linq;
using SK = Ashen.Generated.SettingsKeys;

namespace Ashen.App.Settings
{
    public sealed partial class SettingsSession
    {
        /// <summary>The player's key and pad bindings: the profile's settings.keyBindings and settings.bindings over the defaults (web main.js applyDisplaySettings: setBindings, setKeyBindings).</summary>
        public ControlBindings Bindings(ControlsData data) =>
            new ControlBindings(data, Stored?[data.StoredKeyBindings] as JObject, Stored?[data.StoredPadBindings] as JObject);

        /// <summary>Keeps the bindings in the profile (the web persists both maps whole after a rebind) and tells the live consumers.</summary>
        public void SaveBindings(ControlsData data, ControlBindings bindings)
        {
            if (!(_profile.Doc[SK.Settings] is JObject stored)) _profile.Doc[SK.Settings] = stored = new JObject();
            stored[data.StoredKeyBindings] = bindings.KeysJson();
            stored[data.StoredPadBindings] = bindings.PadsJson();
            _profile.Save(_content.ContentHash);
            Changed?.Invoke(this, data.StoredKeyBindings);
        }
    }
}
