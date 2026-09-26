using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using Ashen.Platform;
using Ashen.Presentation.UI;
using Ashen.Presentation.UI.Kit;
using Ashen.Presentation.UI.Screens;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Ashen.Tests.Play
{
    /// <summary>
    /// SHOOT step (docs/design/09 §6.2, §7): renders every screen fixture (Unity/Assets/Tests/Fixtures/screens/*.json)
    /// into a RenderTexture at each ui/layout.json verification size and writes docs/screens/&lt;fixture&gt;_&lt;w&gt;x&lt;h&gt;.png.
    /// Runs only when ASHEN_CAPTURE=1 (Ashen.EditorTools.Cli.CaptureScreens sets it); otherwise it is ignored.
    /// Needs a graphics device: never run with -nographics.
    /// </summary>
    [TestFixture, Category("Capture")]
    public class ScreenCaptureTests
    {
        public const string CaptureVariable = "ASHEN_CAPTURE";
        private const int SettleFrames = 8;

        /// <summary>A content source that returns mutated text for chosen files (the content-error fixture).</summary>
        private sealed class MutatedSource : IContentSource
        {
            private readonly IContentSource _inner;
            private readonly JArray _mutations;

            public MutatedSource(IContentSource inner, JArray mutations)
            {
                _inner = inner;
                _mutations = mutations;
            }

            public bool Exists(string relativePath) => _inner.Exists(relativePath);

            public string ReadText(string relativePath)
            {
                var text = _inner.ReadText(relativePath);
                foreach (var m in _mutations.OfType<JObject>().Where(m => (string)m["file"] == relativePath))
                    text = text.Replace((string)m["find"], (string)m["replace"]);
                return text;
            }
        }

        private static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

        [UnityTest]
        public IEnumerator CaptureScreens()
        {
            if (Environment.GetEnvironmentVariable(CaptureVariable) != "1") Assert.Ignore("screen capture runs only with " + CaptureVariable + "=1 (Cli.CaptureScreens)");
            var fixtures = Directory.GetFiles(Path.Combine(Application.dataPath, "Tests", "Fixtures", "screens"), "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
            var layout = JObject.Parse(File.ReadAllText(Path.Combine(PlatformPaths.ContentRoot, ContentFiles.UiLayout)));
            var sizes = ((JArray)layout["verification"]).Select(s => new Vector2Int((int)s["width"], (int)s["height"])).ToList();
            var outDir = Path.Combine(RepoRoot, "docs", "screens");
            Directory.CreateDirectory(outDir);
            var only = Environment.GetEnvironmentVariable("ASHEN_CAPTURE_ONLY");
            var written = new List<string>();
            foreach (var file in fixtures)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrEmpty(only) && !only.Split(',').Contains(name)) continue;
                var fixture = JObject.Parse(File.ReadAllText(file));
                foreach (var size in sizes)
                {
                    var path = Path.Combine(outDir, name + "_" + size.x + "x" + size.y + ".png");
                    yield return CaptureOne(fixture, size.x, size.y, path);
                    written.Add(path);
                }
            }
            Debug.Log("CaptureScreens wrote " + written.Count + " screenshots to " + outDir);
            Assert.That(written, Is.Not.Empty);
        }

        private static IEnumerator CaptureOne(JObject fixture, int width, int height, string outPath)
        {
            var saveDir = Path.Combine(Path.GetTempPath(), "ashen-capture-" + Guid.NewGuid().ToString("N"));
            IContentSource source = new DirectoryContentSource(PlatformPaths.ContentRoot);
            if (fixture["mutations"] is JArray mutations) source = new MutatedSource(source, mutations);
            var data = UiData.Load(new DirectoryContentSource(PlatformPaths.ContentRoot));
            var context = UiContext.Create(source, data, saveDir);
            context.Quit = () => { };
            context.DevBuild = true;
            WriteSaves(context.Saves, saveDir, fixture["saves"] as JArray);

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();
            var go = new GameObject("capture");
            TitleScreen.ResetSession();
            var host = UiHost.Create(go, context, rt);
            yield return null;
            Show(host, fixture, source);
            for (var i = 0; i < SettleFrames; i++) yield return null;
            AfterShow(host, fixture);
            for (var i = 0; i < SettleFrames; i++) yield return null;

            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply();
            RenderTexture.active = previous;
            File.WriteAllBytes(outPath, texture.EncodeToPNG());

            UnityEngine.Object.Destroy(texture);
            host.Dispose();
            UnityEngine.Object.Destroy(go);
            rt.Release();
            UnityEngine.Object.Destroy(rt);
            yield return null;
            if (Directory.Exists(saveDir)) Directory.Delete(saveDir, true);
        }

        private static void WriteSaves(SaveService saves, string saveDir, JArray list)
        {
            if (list == null) return;
            foreach (var s in list.OfType<JObject>())
            {
                var slot = saves.Rules.RunSlotName((int)s["slot"]);
                if ((bool?)s["newer"] == true)
                {
                    var rules = new SaveRules { RunSlots = saves.Rules.RunSlots, CurrentVersion = saves.Rules.CurrentVersion + 1, ProfileSlot = saves.Rules.ProfileSlot, ReplaceRetries = saves.Rules.ReplaceRetries, ReplaceRetryDelayMs = saves.Rules.ReplaceRetryDelayMs };
                    new SaveService(saveDir, rules).Checkpoint(slot, new JObject(), string.Empty, string.Empty);
                    continue;
                }
                saves.Checkpoint(slot, new JObject { ["summary"] = s["summary"] }, string.Empty, string.Empty);
            }
        }

        private static void Show(UiHost host, JObject fixture, IContentSource source)
        {
            var nav = host.Navigator;
            var screen = (string)fixture["screen"];
            switch (screen)
            {
                case ScreenIds.Boot:
                    nav.Go(ScreenIds.Boot, new BootArgs { Done = (int)fixture["boot"]["done"], Total = (int)fixture["boot"]["total"] });
                    break;
                case ScreenIds.ContentError:
                    nav.Go(ScreenIds.ContentError, new ContentValidator(source).Validate());
                    break;
                case ScreenIds.Title:
                    nav.Go(ScreenIds.Title, new TitleArgs { Gate = (bool?)fixture["gate"] ?? false });
                    break;
                case ScreenIds.Slots:
                    nav.Go(ScreenIds.Title, new TitleArgs { Gate = false });
                    nav.Go(ScreenIds.Slots, new SlotsArgs { Mode = (string)fixture["mode"] });
                    break;
                case ScreenIds.KitGallery:
                    nav.Go(ScreenIds.KitGallery, KitArgs(host.Context, fixture));
                    break;
                default:
                    nav.Go(screen);
                    break;
            }
        }

        private static void AfterShow(UiHost host, JObject fixture)
        {
            if (!(fixture["confirm"] is JObject confirm)) return;
            var nav = host.Navigator;
            var request = new ConfirmRequest { ConfirmId = (string)confirm["id"] };
            if (confirm["slot"] != null)
            {
                var slot = (int)confirm["slot"];
                request.Args = new StringArgs().Add(UiPlaceholders.Slot, slot);
                if (nav.Top.View is SlotsScreen slots)
                {
                    var row = slots.ViewModel.Rows[slot - 1];
                    request.Target = host.Context.Data.Strings.Format(StringKeys.SlotsTarget, new StringArgs().Add(UiPlaceholders.Label, row.Label).Add(UiPlaceholders.Facts, row.Facts));
                }
            }
            var modal = ConfirmRequest.Open(nav, request);
            if (confirm["holdProgress"] != null) modal.Root.Q<HoldButton>(UiNames.ConfirmHold)?.ShowProgress((double)confirm["holdProgress"]);
        }

        private static KitGalleryArgs KitArgs(UiContext ui, JObject fixture)
        {
            var strings = ui.Data.Strings;
            CardViewData Card(JObject c)
            {
                var id = (string)c["id"];
                var args = new StringArgs();
                foreach (var v in ((JObject)c["vars"]).Properties()) args.Add(v.Name, (string)v.Value);
                return new CardViewData
                {
                    Name = strings.Get("card." + id + ".name"),
                    Text = strings.Format("card." + id + ".text", args),
                    TypeLine = (string)c["typeLine"],
                    Rarity = (string)c["rarity"],
                    Affordable = (bool?)c["affordable"] ?? true,
                    Upgraded = (bool?)c["upgraded"] ?? false,
                    RefusalText = c["refusalKey"] != null ? strings.Get((string)c["refusalKey"]) : null,
                    Costs = ((JArray)c["costs"]).Select(k => new CardCostData { Kind = (string)k["kind"], Text = (string)k["text"] }).ToList(),
                };
            }
            var footer = (JObject)fixture["footer"];
            return new KitGalleryArgs
            {
                Cards = ((JArray)fixture["cards"]).OfType<JObject>().Select(Card).ToList(),
                Hand = ((JArray)fixture["hand"]).OfType<JObject>().Select(Card).ToList(),
                Categories = ((JArray)fixture["categories"]).OfType<JObject>().Select(c => new CategoryItem { Id = (string)c["id"], LabelKey = (string)c["labelKey"], Count = (string)c["count"] }).ToList(),
                Meters = ((JArray)fixture["meters"]).OfType<JObject>().Select(m => new MeterSample { Kind = (string)m["kind"], GlyphKey = (string)m["glyphKey"], Value = (int)m["value"], Max = (int)m["max"], ReferenceMax = (int)m["referenceMax"] }).ToList(),
                Actions = (int)footer["actions"],
                ActionsMax = (int)footer["actionsMax"],
                Draw = (int)footer["draw"],
                Discard = (int)footer["discard"],
                Potions = (int)footer["potions"],
                HoldProgress = (double)fixture["holdProgress"],
            };
        }
    }
}
