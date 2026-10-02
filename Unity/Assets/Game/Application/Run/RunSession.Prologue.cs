using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using LK = Ashen.Generated.LoopKeys;
using PK = Ashen.Generated.PrologueKeys;
using PV = Ashen.Generated.PrologueValues;
using RK = Ashen.Generated.RunKeys;
using S = Ashen.Generated.MetaStringKeys;

namespace Ashen.App.Run
{
    /// <summary>One W-05 scene as the screen stages it: art for both layouts, the class item layer, the line and the controls.</summary>
    public sealed class PrologueViewState
    {
        public string SceneId;
        public string ArtWide;
        public string ArtNarrow;
        public string Item;
        public string Line;
        public string Continue;
        public bool IsLast;
        public string Skip;
        public string Pause;
        public string Progress;

        /// <summary>One dot per playable scene; true up to and including this one.</summary>
        public readonly List<bool> Dots = new List<bool>();

        public double HoldMs;
        public string Music;
        public bool Banner;
        public JObject Stage;
        public JObject Presentation;
    }

    /// <summary>
    /// W-05 the prologue (US-3.1-3.3, AF-03) over the run's prologue row (<c>run.prologue</c>: version, status, scene).
    /// The scene index counts the nine slots of ui/prologue.json in order, so a saved slot that has since been disabled
    /// resumes at the next playable one. A slot plays when it is enabled and its art resolves for the class. Every step
    /// saves. While the prologue is pending the map offers no node.
    /// </summary>
    public sealed partial class RunSession
    {
        private JObject PrologueDoc => Content.Snapshot.Content.Get(ContentFiles.UiPrologue) as JObject ?? new JObject();

        private JObject PrologueRow => _run?[LK.Prologue] as JObject;

        /// <summary>The run waits on its prologue (a new run with playback on that has not finished or skipped it).</summary>
        public bool ProloguePending => !RunOver && PrologueRow?.Str(PK.Status) == PV.Pending && CurrentPrologueSlot() >= 0;

        /// <summary>The slots in order, and whether each plays for this class.</summary>
        private List<KeyValuePair<JObject, bool>> PrologueSlots()
        {
            var classId = ClassId;
            return Js.Items(PrologueDoc[PK.Scenes]).OfType<JObject>()
                .OrderBy(s => Js.Or0(s[PK.Order]))
                .Select(s => new KeyValuePair<JObject, bool>(s, s.Is(PK.Enabled) && Art(s, PK.Wide, PK.WideFallback, classId) != null))
                .ToList();
        }

        /// <summary>The slot index the run stands at: the saved one, or the next playable after it (-1: none left).</summary>
        private int CurrentPrologueSlot()
        {
            var slots = PrologueSlots();
            var saved = (int)Js.Or0(PrologueRow?[PK.Scene]);
            for (var i = Math.Max(0, saved); i < slots.Count; i++)
                if (slots[i].Value) return i;
            return -1;
        }

        /// <summary>Continue: the next playable scene, or the prologue is done ("Set forth"). Saved.</summary>
        public void AdvancePrologue()
        {
            if (!ProloguePending) return;
            var at = CurrentPrologueSlot();
            var slots = PrologueSlots();
            var next = -1;
            for (var i = at + 1; i < slots.Count; i++)
                if (slots[i].Value) { next = i; break; }
            CommitPrologue(row =>
            {
                if (next < 0) row[PK.Status] = PV.Done;
                else row[PK.Scene] = next;
            });
        }

        /// <summary>Hold to skip: the prologue is over and the run stands at the map. Saved.</summary>
        public void SkipPrologue()
        {
            if (!ProloguePending) return;
            CommitPrologue(row => row[PK.Status] = PV.Skipped);
        }

        private void CommitPrologue(Action<JObject> change)
        {
            var before = PrologueRow.DeepClone();
            change(PrologueRow);
            try { Save(); }
            catch
            {
                _run[LK.Prologue] = before;
                throw;
            }
        }

        public PrologueViewState PrologueView(UiData ui)
        {
            if (!ProloguePending) return null;
            var strings = ui.Strings;
            var slots = PrologueSlots();
            var at = CurrentPrologueSlot();
            var scene = slots[at].Key;
            var playable = slots.Select((s, i) => new { s, i }).Where(x => x.s.Value).Select(x => x.i).ToList();
            var position = playable.IndexOf(at);
            var isLast = position == playable.Count - 1;
            var classId = ClassId;
            var lineKey = Templated(scene.Str(PK.LineKey), classId);
            if (lineKey == null || !strings.Has(lineKey)) lineKey = scene.Str(PK.LineFallbackKey) ?? scene.Str(PK.LineKey);
            var item = Templated(scene.Str(PK.Item), classId);
            var view = new PrologueViewState
            {
                SceneId = scene.Str(PK.Id),
                ArtWide = Art(scene, PK.Wide, PK.WideFallback, classId),
                ArtNarrow = Art(scene, PK.Narrow, PK.NarrowFallback, classId) ?? Art(scene, PK.Wide, PK.WideFallback, classId),
                Item = item != null && Content.HasAsset(item) ? item : null,
                Line = lineKey != null ? strings.Get(lineKey) : string.Empty,
                IsLast = isLast,
                Continue = strings.Get(isLast ? S.PrologueSetForth : S.PrologueContinue),
                Skip = strings.Get(S.PrologueSkip),
                Pause = strings.Get(S.ProloguePause),
                Progress = strings.Format(S.PrologueProgress, new StringArgs().Add(MetaPlaceholders.N, position + 1).Add(MetaPlaceholders.Count, playable.Count)),
                HoldMs = Js.Or0(scene[PK.HoldMs]),
                Music = scene.Str(PK.Music),
                Banner = scene.Is(PK.Banner),
                Stage = scene.Obj(PK.Stage)?.DeepClone() as JObject ?? new JObject(),
                Presentation = PrologueDoc.Obj(PK.Presentation)?.DeepClone() as JObject ?? new JObject(),
            };
            for (var i = 0; i < playable.Count; i++) view.Dots.Add(i <= position);
            return view;
        }

        /// <summary>The art id for a layout: the class's own when the registry has it, else the fallback; null when neither resolves.</summary>
        private string Art(JObject scene, string key, string fallbackKey, string classId)
        {
            var art = scene.Obj(PK.Art);
            if (art == null) return null;
            var own = Templated(art.Str(key), classId);
            if (own != null && Content.HasAsset(own)) return own;
            var fallback = art.Str(fallbackKey);
            return fallback != null && Content.HasAsset(fallback) ? fallback : null;
        }

        private static string Templated(string text, string classId) =>
            text?.Replace(PV.ClassToken, classId ?? string.Empty);
    }
}
