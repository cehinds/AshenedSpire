using System.Linq;
using System.Reflection;
using Ashen.Generated;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>US-0.9: generated constants stay in sync with content (suite: Enforcement).</summary>
    [TestFixture, Category("Enforcement")]
    public class CodegenTests
    {
        [Test]
        public void EveryContentFileConstantPointsAtAnExistingFile()
        {
            var source = TestContent.Source;
            var missing = typeof(ContentFiles).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => (string)f.GetValue(null))
                .Where(path => !source.Exists(path))
                .ToList();
            Assert.That(missing, Is.Empty, "ContentFiles is stale — run node Tools/codegen.mjs");
        }

        [Test]
        public void EffectOpWireNamesRoundTrip()
        {
            Assert.That(EffectOpNames.All.Length, Is.GreaterThan(0));
            foreach (var wire in EffectOpNames.All)
            {
                var op = EffectOpNames.Parse(wire);
                Assert.That(op, Is.Not.EqualTo(EffectOp.None), wire);
                Assert.That(EffectOpNames.ToWire(op), Is.EqualTo(wire));
            }
            Assert.That(EffectOpNames.Parse("definitelyNotAnOp"), Is.EqualTo(EffectOp.None));
        }
    }
}
