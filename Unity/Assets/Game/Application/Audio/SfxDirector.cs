using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using MK = Ashen.Generated.MusicKeys;

namespace Ashen.App.Audio
{
    /// <summary>
    /// Sound effects for what combat did (US-16.2): each accepted command's events map to audio/sfx.json recipes through
    /// audio/beds.json sfx (a family field appends the payload's value, so a frost burst asks for procBurst_frost and the
    /// synth falls back family-then-default), in event order, each recipe once, at most maxPerCommand. Engine-free; the
    /// player renders the ids through <see cref="SynthRenderer"/>.
    /// </summary>
    public sealed class SfxDirector
    {
        private readonly JObject _sfx;
        private readonly string _separator;

        public SfxDirector(JObject beds, JObject synth)
        {
            _sfx = beds?.Obj(MK.Sfx) ?? new JObject();
            _separator = (string)synth?.Obj(MK.RecipeSection)?[MK.FamilySeparator] ?? string.Empty;
        }

        public static SfxDirector From(IContentSource source) => new SfxDirector(
            (JObject)JsonContent.Parse(source.ReadText(ContentFiles.AudioBeds)), (JObject)JsonContent.Parse(source.ReadText(ContentFiles.AudioSynth)));

        public IReadOnlyList<string> For(IEnumerable<JObject> events)
        {
            var map = _sfx.Obj(MK.Events) ?? new JObject();
            var families = _sfx.Obj(MK.FamilyFields) ?? new JObject();
            var max = (int)Js.Or0(_sfx[MK.MaxPerCommand]);
            var ids = new List<string>();
            foreach (var e in events ?? Enumerable.Empty<JObject>())
            {
                var type = e.Str(MK.Type);
                var recipe = type == null ? null : (string)map[type];
                if (recipe == null) continue;
                var field = (string)families[type];
                var suffix = field == null ? null : Js.Str(e[field]);
                var id = string.IsNullOrEmpty(suffix) ? recipe : recipe + _separator + suffix;
                if (ids.Contains(id)) continue;
                ids.Add(id);
                if (ids.Count >= max) break;
            }
            return ids;
        }
    }
}
