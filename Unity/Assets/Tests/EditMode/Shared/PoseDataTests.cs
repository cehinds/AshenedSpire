using System;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>P-1 (D-1320..): the player figure's pose stage (ui/poses.json, the web's PoseAnimator.js) lands where the web puts it.</summary>
    [TestFixture]
    public class PoseDataTests
    {
        private static UiData Ui => UiData.Load(TestContent.Source);

        private static JObject Registry => (JObject)JsonContent.Parse(TestContent.Source.ReadText(ContentFiles.AssetsRegistry));

        [Test]
        public void EveryFrameOfTheStripIsShippedForEveryClassAndTintTheStageHas()
        {
            var poses = Ui.Poses;
            var json = (JObject)JsonContent.Parse(TestContent.Source.ReadText(ContentFiles.UiPoses));
            var keys = ((JObject)json[PoseKeys.Frames]).Properties().Select(p => p.Name).ToList();
            Assert.That(keys.Count, Is.GreaterThan(500));
            foreach (var key in keys)
            {
                var at = key.Split('_');
                Assert.That(poses.Has(at[0], at[2]), Is.True, key + " has an idle frame to anchor on");
                foreach (var pose in poses.Strip) Assert.That(poses.Frame(at[0], pose, at[2]), Is.Not.Null, key + " lacks " + pose);
            }
        }

        [Test]
        public void EveryFrameIsAnIdOfTheArtRegistry()
        {
            var registry = Registry;
            var json = (JObject)JsonContent.Parse(TestContent.Source.ReadText(ContentFiles.UiPoses));
            foreach (var property in (JObject)json[PoseKeys.Frames])
            {
                var at = property.Key.Split('_');
                Assert.That(registry.ContainsKey("pose." + at[0] + "." + at[1] + "." + at[2]), Is.True, property.Key);
            }
        }

        [TestCase(0.5)]
        [TestCase(1.0)]
        [TestCase(1.6)]
        public void TheIdleFigureStandsOnTheBottomOfTheBoxAtItsCentre(double aspect)
        {
            var poses = Ui.Poses;
            var json = (JObject)JsonContent.Parse(TestContent.Source.ReadText(ContentFiles.UiPoses));
            foreach (var property in ((JObject)json[PoseKeys.Frames]).Properties().Where(p => p.Name.Contains("_idle_")))
            {
                var at = property.Name.Split('_');
                var idle = poses.Frame(at[0], PoseKeys.Idle, at[2]);
                var place = poses.Place(idle, idle, aspect);
                // The crop's bottom edge is within a canvas pixel of the floor line, which the stage seats on the bottom of the box.
                Assert.That(place.Top + place.Height, Is.EqualTo(1).Within(0.03), property.Name + " bottom");
                // The pelvis (rx on the canvas) is the box centre: the crop's left plus the pelvis' distance into the crop, in box widths.
                Assert.That(place.Left + (idle.Rx - idle.X) / idle.W * place.Width, Is.EqualTo(0.5).Within(0.01), property.Name + " pelvis");
            }
        }

        [Test]
        public void ASwingLeansOutPastTheBoxAndKeepsTheFeetOnTheFloor()
        {
            var poses = Ui.Poses;
            var idle = poses.Frame("reaver", PoseKeys.Idle, "ember");
            var swing = poses.Frame("reaver", "attack2", "ember");
            Assert.That(idle, Is.Not.Null);
            Assert.That(swing, Is.Not.Null);
            var a = poses.Place(idle, idle, 1);
            var b = poses.Place(idle, swing, 1);
            // The swing is cropped to its own box but its floor line is the idle's: its bottom edge sits on the same line within a few canvas pixels.
            Assert.That(b.Top + b.Height, Is.EqualTo(a.Top + a.Height).Within(0.06));
            Assert.That(b.Width, Is.Not.EqualTo(a.Width).Within(0.001), "frames differ in size, which is why they are placed by their own crop");
        }

        [Test]
        public void ThePlayedSwingsFollowTheStripInOrder()
        {
            var poses = Ui.Poses;
            Assert.That(poses.Throwable("rogue", "gold"), Is.EqualTo(new[] { "attack1", "attack2", "attack3", "attack4" }));
            Assert.That(poses.Throwable("nobody", "gold"), Is.Empty);
            Assert.That(poses.DefaultPlayMs, Is.GreaterThan(poses.MinimumPlayMs));
        }
    }
}
