using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using PK = Ashen.Generated.PrologueKeys;
using PV = Ashen.Generated.PrologueValues;
using S = Ashen.Generated.MetaStringKeys;

namespace Ashen.App.Run
{
    /// <summary>
    /// The scenes of the opening (ui/prologue.json) for a class: the slots in order, which of them play, and the view of one. The 405 run (<see cref="RunSession"/>) and the in-memory
    /// <see cref="PrologueFlow"/> of a Core 2 run read the same data through this, so the two openings are one. Engine-free.
    /// </summary>
    public static class PrologueScenes
    {
        /// <summary>The slots in order, and whether each plays for this class (enabled, with art for the wide layout).</summary>
        public static List<KeyValuePair<JObject, bool>> Slots(JObject doc, string classId, Func<string, bool> hasAsset) =>
            Js.Items(doc[PK.Scenes]).OfType<JObject>()
                .OrderBy(s => Js.Or0(s[PK.Order]))
                .Select(s => new KeyValuePair<JObject, bool>(s, s.Is(PK.Enabled) && Art(s, PK.Wide, PK.WideFallback, classId, hasAsset) != null))
                .ToList();

        /// <summary>The slot index at or after <paramref name="from"/> that plays (-1: none left).</summary>
        public static int Playable(IReadOnlyList<KeyValuePair<JObject, bool>> slots, int from)
        {
            for (var i = Math.Max(0, from); i < slots.Count; i++)
                if (slots[i].Value) return i;
            return -1;
        }

        /// <summary>The art id for a layout: the class's own when the registry has it, else the fallback; null when neither resolves.</summary>
        public static string Art(JObject scene, string key, string fallbackKey, string classId, Func<string, bool> hasAsset)
        {
            var art = scene.Obj(PK.Art);
            if (art == null) return null;
            var own = Templated(art.Str(key), classId);
            if (own != null && hasAsset(own)) return own;
            var fallback = art.Str(fallbackKey);
            return fallback != null && hasAsset(fallback) ? fallback : null;
        }

        public static string Templated(string text, string classId) => text?.Replace(PV.ClassToken, classId ?? string.Empty);

        /// <summary>The scene at slot <paramref name="at"/> as the screen stages it.</summary>
        public static PrologueViewState View(UiData ui, JObject doc, string classId, string name, Func<string, bool> hasAsset, IReadOnlyList<KeyValuePair<JObject, bool>> slots, int at)
        {
            var strings = ui.Strings;
            var scene = slots[at].Key;
            var playable = slots.Select((s, i) => new { s, i }).Where(x => x.s.Value).Select(x => x.i).ToList();
            var position = playable.IndexOf(at);
            var isLast = position == playable.Count - 1;
            var lineKey = Templated(scene.Str(PK.LineKey), classId);
            if (lineKey == null || !strings.Has(lineKey)) lineKey = scene.Str(PK.LineFallbackKey) ?? scene.Str(PK.LineKey);
            var item = Templated(scene.Str(PK.Item), classId);
            var id = scene.Str(PK.Id);
            var args = new StringArgs().Add(MetaPlaceholders.Class, RunHudView.ClassName(ui, classId)).Add(MetaPlaceholders.Name, name ?? string.Empty);
            var view = new PrologueViewState
            {
                SceneId = id,
                ArtWide = Art(scene, PK.Wide, PK.WideFallback, classId, hasAsset),
                ArtNarrow = Art(scene, PK.Narrow, PK.NarrowFallback, classId, hasAsset) ?? Art(scene, PK.Wide, PK.WideFallback, classId, hasAsset),
                Item = item != null && hasAsset(item) ? item : null,
                Line = lineKey != null ? strings.Get(lineKey) : string.Empty,
                Title = SceneString(strings, PK.TitleKeyFormat, id, args),
                Speaker = SceneString(strings, PK.SpeakerKeyFormat, id, args),
                IsLast = isLast,
                Continue = strings.Get(isLast ? S.PrologueSetForth : S.PrologueContinue),
                Skip = strings.Get(S.PrologueSkip),
                Pause = strings.Get(S.ProloguePause),
                Progress = strings.Format(S.PrologueProgress, new StringArgs().Add(MetaPlaceholders.N, position + 1).Add(MetaPlaceholders.Count, playable.Count)),
                HoldMs = Js.Or0(scene[PK.HoldMs]),
                Music = scene.Str(PK.Music),
                Banner = scene.Is(PK.Banner),
                Stage = scene.Obj(PK.Stage)?.DeepClone() as JObject ?? new JObject(),
                Presentation = doc.Obj(PK.Presentation)?.DeepClone() as JObject ?? new JObject(),
            };
            for (var i = 0; i < playable.Count; i++) view.Dots.Add(i <= position);
            return view;
        }

        private static string SceneString(StringTable strings, string format, string id, StringArgs args)
        {
            var key = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, id);
            return strings.Has(key) ? strings.Format(key, args) : string.Empty;
        }
    }

    /// <summary>What the opening screen drives: the scene in front, Continue, and skip. The 405 run answers it from its saved prologue row; a Core 2 run from <see cref="PrologueFlow"/>.</summary>
    public interface IPrologue
    {
        /// <summary>The opening has scenes left to play.</summary>
        bool Pending { get; }

        /// <summary>The scene in front, or null when none is left.</summary>
        PrologueViewState View(UiData ui);

        /// <summary>Continue: the next scene, or the opening is done ("Set forth").</summary>
        void Advance();

        /// <summary>Skip opening: the opening is over.</summary>
        void Skip();
    }

    /// <summary>
    /// The opening of a Core 2 climb (W-05; web src/ui/screens/prologue.js): the scenes of ui/prologue.json for the class, played before the first step of the map. It lives only in
    /// memory (the Core 2 run document has no prologue row): a climb saved during it resumes at the map. Whether it plays is the player's setting (settings.playPrologue) and the
    /// data's (rules/runFlow.json newRun.opening). Engine-free.
    /// </summary>
    public sealed class PrologueFlow : IPrologue
    {
        private readonly RunContent _content;
        private readonly string _classId;
        private readonly string _name;
        private readonly List<KeyValuePair<JObject, bool>> _slots;
        private int _at;

        public PrologueFlow(RunContent content, string classId, string name)
        {
            _content = content;
            _classId = classId;
            _name = name;
            _slots = PrologueScenes.Slots(Doc(content), classId, content.HasAsset);
            _at = PrologueScenes.Playable(_slots, 0);
        }

        private static JObject Doc(RunContent content) => content.Snapshot.Content.Get(ContentFiles.UiPrologue) as JObject ?? new JObject();

        /// <summary>The opening to play for a new Core 2 climb, or null when it is switched off (the data or the player) or has no scene that plays for the class.</summary>
        public static PrologueFlow For(RunContent content, Ashen.App.Settings.SettingsSession settings, string classId, string name)
        {
            if (!content.Flow.Opening || !PlaysFor(settings)) return null;
            var flow = new PrologueFlow(content, classId, name);
            return flow.Pending ? flow : null;
        }

        /// <summary>shouldPlayPrologue(settings, seen): every new game, or once per profile (web model/prologue.js).</summary>
        public static bool PlaysFor(Ashen.App.Settings.SettingsSession settings)
        {
            var mode = settings.Text(SettingIds.OpeningPlayback);
            return mode == Ashen.Generated.SettingsValues.OpeningEvery || (mode == Ashen.Generated.SettingsValues.OpeningOnce && !settings.PrologueSeen);
        }

        public bool Pending => _at >= 0;

        /// <summary>The scene's place among those that play (0-based), or -1.</summary>
        public int Position => _at < 0 ? -1 : _slots.Select((s, i) => new { s, i }).Where(x => x.s.Value).Select(x => x.i).ToList().IndexOf(_at);

        public PrologueViewState View(UiData ui) => Pending ? PrologueScenes.View(ui, Doc(_content), _classId, _name, _content.HasAsset, _slots, _at) : null;

        public void Advance()
        {
            if (!Pending) return;
            _at = PrologueScenes.Playable(_slots, _at + 1);
        }

        public void Skip() => _at = -1;
    }
}
