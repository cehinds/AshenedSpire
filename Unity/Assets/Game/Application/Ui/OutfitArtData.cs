using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>
    /// The player's painted figure outside the combat board (ui/outfitArt.json, Tools/outfit-art.mjs; web src/ui/paintedOutfits.js paintedPresentation): the frame of a view (the combatant door's
    /// portrait, the Armoury and creation's stand, the dialogue's conversation) for a class in an armour holding something in each hand. The painted outfit of the class and armour gives the frame,
    /// unless the equipment animation set those hands select has a frame of its own for the view (animationArt). Engine-free.
    /// </summary>
    public sealed class OutfitArtData
    {
        private readonly Dictionary<string, Dictionary<string, string>> _outfits = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private readonly List<Dictionary<string, string>> _sets = new List<Dictionary<string, string>>();
        private readonly Dictionary<string, Dictionary<string, int>> _hands = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        public OutfitArtData(JObject json)
        {
            foreach (var row in (JObject)json[OutfitArtKeys.Outfits]) _outfits[row.Key] = Frames((JObject)row.Value);
            foreach (var set in (JArray)json[OutfitArtKeys.Sets]) _sets.Add(Frames((JObject)set));
            foreach (var row in (JObject)json[OutfitArtKeys.Hands])
                _hands[row.Key] = ((JObject)row.Value).Properties().ToDictionary(p => p.Name, p => p.Value.Value<int>(), StringComparer.Ordinal);
        }

        private static Dictionary<string, string> Frames(JObject row) => row.Properties().ToDictionary(p => p.Name, p => p.Value.Value<string>(), StringComparer.Ordinal);

        private static string Join(params string[] parts) => string.Join(OutfitArtKeys.KeyJoin, parts.Select(p => p ?? string.Empty));

        /// <summary>The art id of a view of the figure, or null when the class has no painted outfit in that armour (the figure then keeps its class art).</summary>
        public string Frame(string classId, string armourId, string rightId, string leftId, string view)
        {
            var outfit = Join(classId, string.IsNullOrEmpty(armourId) ? OutfitArtKeys.DefaultArmour : armourId);
            if (!_outfits.TryGetValue(outfit, out var frames)) return null;
            if (_hands.TryGetValue(outfit, out var byHands) && byHands.TryGetValue(Join(rightId, leftId), out var set) && _sets[set].TryGetValue(view, out var own)) return own;
            return frames.TryGetValue(view, out var id) ? id : null;
        }

        /// <summary>Every art id the tables name (the art import keeps exactly these).</summary>
        public IEnumerable<string> Ids => _outfits.Values.Concat(_sets).SelectMany(f => f.Values).Distinct(StringComparer.Ordinal);
    }
}
