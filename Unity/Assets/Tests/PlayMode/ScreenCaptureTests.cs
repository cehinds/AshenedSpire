using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Domain.Combat;
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

        [UnityTest, Timeout(1800000)]
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
                var skip = new HashSet<string>((fixture["skipSizes"] as JArray ?? new JArray()).Select(t => (string)t));
                foreach (var size in sizes)
                {
                    if (skip.Contains(size.x + "x" + size.y)) continue;
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
            var settleMs = (int?)fixture["settleMs"] ?? 0;
            if (settleMs > 0) yield return new WaitForSecondsRealtime(settleMs / 1000f);

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
                case ScreenIds.Creation:
                    nav.Go(ScreenIds.Title, new TitleArgs { Gate = false });
                    nav.Go(ScreenIds.Creation, new CreationArgs { SlotIndex = (int?)fixture["slot"] ?? 1 });
                    break;
                case ScreenIds.Combat:
                case ScreenIds.Pause:
                    nav.Go(ScreenIds.Combat, new CombatArgs { Session = Fight(host.Context, source, fixture) }, true);
                    break;
                case ScreenIds.Rewards:
                {
                    var session = Fight(host.Context, source, fixture);
                    foreach (var claim in (fixture["claims"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var key = (string)claim["key"];
                        var row = RewardsView.Build(session, host.Context.Data).Rows.First(r => r.Key == key || r.Kind == key);
                        if ((bool?)claim["skip"] == true) session.SkipReward(row.Key);
                        else session.ClaimReward(row.Key, row.Picks.Count > 0 ? row.Picks[(int?)claim["pick"] ?? 0].Id : null);
                    }
                    nav.Go(ScreenIds.Rewards, new RewardsArgs { Session = session }, true);
                    break;
                }
                case ScreenIds.ActMap:
                case ScreenIds.Rest:
                case ScreenIds.RunEnd:
                {
                    var session = Climb(host.Context, source, fixture);
                    nav.Go(screen, new RunScreenArgs { Session = session }, true);
                    break;
                }
                default:
                    nav.Go(screen);
                    break;
            }
        }

        /// <summary>
        /// A climb for a W-06/W-10/W-15 fixture: a new climb (Classic, or the short Custom Climb with 'short'), played by the
        /// smoke policy (ClimbPilot; enemy HP on the assisted scale with 'assisted'; 'prefer' picks the first reachable node of
        /// that kind) until 'until' holds: start, mid (half the act climbed), boss (the keeper reachable), rest, restSmith (a
        /// rest place whose smith is offered), merchant, death (every turn ended) or end (the run is over). 'act' first climbs
        /// to that act. 'grantPoints' and 'grantStones' edit the run for review before the screen opens.
        /// </summary>
        private static RunSession Climb(UiContext ui, IContentSource source, JObject fixture)
        {
            var content = RunContent.Load(source);
            var seed = (uint)((long?)fixture["seed"] ?? 1L);
            RunSession.ReviewEnemyHpScale = (bool?)fixture["assisted"] == true ? ClimbPilot.Assisted : (Func<string, double>)null;
            try
            {
                var session = RunSession.New(content, ui.Saves, 1, seed, (string)fixture["classId"] ?? content.DefaultClass(), ui.Data.Strings.Get(content.Flow.NameKey),
                    null, (bool?)fixture["short"] == true ? ClimbPilot.ShortClimb() : null);
                var pilot = new ClimbPilot(seed);
                var until = (string)fixture["until"] ?? "start";
                var act = (int?)fixture["act"] ?? 1;
                var prefer = (string)fixture["prefer"];
                bool Done()
                {
                    if (session.RunOver) return true;
                    if (session.Act < act) return false;
                    var graph = session.Run["mapGraph"];
                    switch (until)
                    {
                        case "start": return session.Location == "map";
                        case "mid": return session.Location == "map" && session.Floor * 2 >= ActMapView.Build(session, ui.Data).Floors;
                        case "boss": return session.Location == "map" && session.ReachableNodes().Any(id => (string)graph["nodes"][id]["type"] == "boss");
                        case "rest": return session.Location == "rest";
                        case "restSmith": return session.Location == "rest" && RestView.Build(session, ui.Data).Option("smith") != null;
                        case "merchant": return session.Location == "merchant";
                        default: return false;
                    }
                }
                for (var i = 0; i < 2000 && !Done(); i++)
                {
                    if (until == "death" && session.IsInCombat)
                    {
                        session.Execute(ClimbPilot.EndTurn(session.Combat.State));
                        continue;
                    }
                    if (session.Location == "map" && prefer != null)
                    {
                        var graph = session.Run["mapGraph"];
                        var nodes = session.ReachableNodes();
                        session.Travel(nodes.FirstOrDefault(id => (string)graph["nodes"][id]["type"] == prefer) ?? nodes[0]);
                        continue;
                    }
                    if (!pilot.Step(session, ui.Data)) break;
                }
                var points = (int?)fixture["grantPoints"];
                var stones = (int?)fixture["grantStones"];
                if (points != null || stones != null)
                    session.EditRunForReview(run =>
                    {
                        if (points != null) ((JObject)run["level"])["unspentPoints"] = (double)points.Value;
                        if (stones != null) run["smithingStones"] = (double)stones.Value;
                    });
                ui.Session = session;
                return session;
            }
            finally
            {
                RunSession.ReviewEnemyHpScale = null;
            }
        }

        /// <summary>A fight for a combat fixture: the run content (with the fixture's layer patches), a new run in slot 1, the encounter (the F1 rule unless named), then bot commands.</summary>
        private static RunSession Fight(UiContext ui, IContentSource source, JObject fixture)
        {
            var patches = (fixture["patches"] as JArray)?.OfType<JObject>().ToList();
            var content = RunContent.Load(source, null, patches);
            var session = RunSession.New(content, ui.Saves, 1, (uint)((long?)fixture["seed"] ?? 1L), (string)fixture["classId"] ?? content.DefaultClass(), ui.Data.Strings.Get(content.Flow.NameKey));
            session.StartEncounter((string)fixture["encounter"]);
            var commands = (int?)fixture["commands"] ?? 0;
            for (var i = 0; i < commands && !session.Combat.IsOver; i++) session.Execute(NextCommand(session.Combat.State));
            if ((bool?)fixture["intentVariety"] == true)
            {
                // End turns (the enemies act and roll new intents) until two intent kinds show, then land one play.
                for (var i = 0; i < 12 && !session.Combat.IsOver; i++)
                {
                    if (CombatViewModel.Build(session.Combat.State).Enemies.Where(e => e.Alive && e.Intent != null).Select(e => e.Intent.Kind).Distinct().Count() > 1) break;
                    var plan = HandRules.Plan(session.Combat.State);
                    session.Execute(CombatCommand.EndTurn(plan.Cards.Take((int)plan.Minimum).Select(c => c.Value<string>("instanceId"))));
                }
                var play = CombatViewModel.Build(session.Combat.State).Hand.FirstOrDefault(c => c.Playable && c.NeedsTarget);
                if (play != null && !session.Combat.IsOver) session.Execute(CombatCommand.PlayCard(play.InstanceId, play.Targets.FirstOrDefault()));
            }
            if ((bool?)fixture["playUntilStuck"] == true)
                for (var i = 0; i < 20; i++)
                {
                    var card = CombatViewModel.Build(session.Combat.State).Hand.FirstOrDefault(c => c.Playable);
                    if (card == null) break;
                    session.Execute(CombatCommand.PlayCard(card.InstanceId, card.Targets.FirstOrDefault()));
                }
            if ((bool?)fixture["queueClassDraft"] == true)
                session.EditRunForReview(run =>
                {
                    var skills = run["skills"] as JObject ?? new JObject();
                    skills[Ashen.Domain.Rewards.Skills.ClassSkillId(session.ClassId)] = new JObject { ["xp"] = 0.0, ["level"] = 1.0, ["pendingDrafts"] = 1.0 };
                    run["skills"] = skills;
                });
            if ((bool?)fixture["finish"] == true)
                for (var i = 0; i < 400 && !session.Combat.IsOver; i++) session.Execute(NextCommand(session.Combat.State));
            ui.Session = session;
            return session;
        }

        private static CombatCommand NextCommand(CombatState state)
        {
            var card = CombatViewModel.Build(state).Hand.FirstOrDefault(c => c.Playable);
            if (card != null) return CombatCommand.PlayCard(card.InstanceId, card.Targets.FirstOrDefault());
            var plan = HandRules.Plan(state);
            return CombatCommand.EndTurn(plan.Cards.Take((int)plan.Minimum).Select(c => c.Value<string>("instanceId")));
        }

        private static void AfterShow(UiHost host, JObject fixture)
        {
            if (host.Navigator.Top?.View is CombatScreen combat)
            {
                switch ((string)fixture["state"])
                {
                    case "targeting":
                        combat.ArmForReview(combat.View.Hand.FindIndex(c => c.Playable && c.NeedsTarget));
                        break;
                    case "discard":
                        combat.DiscardForReview((int?)fixture["chosen"] ?? 1);
                        break;
                    case "refusal":
                        combat.RefuseForReview(0);
                        break;
                    case "flasks":
                        combat.FlasksForReview();
                        break;
                }
                if ((string)fixture["screen"] == ScreenIds.Pause) host.Navigator.OpenModal(ScreenIds.Pause, new PauseArgs { Session = combat.Session });
            }
            if (host.Navigator.Top?.View is RewardsScreen rewards && (string)fixture["state"] == "pick")
                rewards.PickForReview((string)fixture["pickKind"], (int?)fixture["select"] ?? -1);
            if (host.Navigator.Top?.View is ActMapScreen map)
            {
                if ((bool?)fixture["select"] == true && map.Session.ReachableNodes().Count > 0) map.Select(map.Session.ReachableNodes()[0]);
                if ((bool?)fixture["legend"] == true) map.LegendForReview();
            }
            if (host.Navigator.Top?.View is RestScreen rest && fixture["pane"] != null)
                rest.PaneForReview((string)fixture["pane"], (bool?)fixture["selectFirst"] ?? false, (int?)fixture["assign"] ?? 0);
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
