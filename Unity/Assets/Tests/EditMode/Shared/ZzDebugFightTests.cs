using System;
using System.IO;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    [TestFixture]
    public class ZzDebugFightTests
    {
        private static void Diff(string path, JToken a, JToken b, System.Collections.Generic.List<string> into)
        {
            if (into.Count > 60) return;
            if (a is JObject oa && b is JObject ob)
            {
                foreach (var k in oa.Properties().Select(p => p.Name).Union(ob.Properties().Select(p => p.Name)).Distinct()) Diff(path + "." + k, oa[k], ob[k], into);
                return;
            }
            if (a is JArray aa && b is JArray ab && aa.Count == ab.Count)
            {
                for (var i = 0; i < aa.Count; i++) Diff(path + "[" + i + "]", aa[i], ab[i], into);
                return;
            }
            if (a == null && b == null) return;
            if (a is JValue va && b is JValue vb && va.Type != JTokenType.String && vb.Type != JTokenType.String && va.Type != JTokenType.Boolean && !(va.Type == JTokenType.Null) && !(vb.Type == JTokenType.Null) && Math.Abs(va.Value<double>() - vb.Value<double>()) < 1e-9) return;
            if (a != null && b != null && JToken.DeepEquals(a, b)) return;
            into.Add(path + ": web " + (a?.ToString(Newtonsoft.Json.Formatting.None) ?? "(absent)") + " | port " + (b?.ToString(Newtonsoft.Json.Formatting.None) ?? "(absent)"));
        }

        [Test]
        public void Dump()
        {
            var spec = File.ReadAllText("/tmp/claude-0/debug.txt").Split(' ');
            var file = spec[0];
            var index = int.Parse(spec[1]);
            var stop = int.Parse(spec[2]);
            var fight = (JObject)((JArray)Core2StatusParityTests.Read(file)["fights"])[index];
            var c = Core2CombatStartParityTests.Open(fight, out _);
            var n = 0;
            JToken want = fight["start"]["state"].DeepClone();
            foreach (var step in ((JArray)fight["steps"]).OfType<JObject>())
            {
                var intent = (JObject)step["intent"];
                var mark = c.EventLog.Count;
                if (n == stop)
                {
                    TestContext.Out.WriteLine("BEFORE player " + c.Player.ToString(Newtonsoft.Json.Formatting.None));
                    TestContext.Out.WriteLine("BEFORE enemies " + string.Join(" | ", c.Enemies.Select(e => e.ToString(Newtonsoft.Json.Formatting.None))));
                }
                switch (intent.Value<string>("type"))
                {
                    case "setReactions": c.Player["reactionsEnabled"] = intent.Value<bool>("enabled"); break;
                    case "endTurn":
                        foreach (var id in (step["staggered"] as JArray ?? new JArray()).Select(t => t.Value<string>())) c.Enemies.First(e => e.Value<string>("id") == id)["skipNextTurn"] = true;
                        CoreTurns.EndTurn(c, intent["discardIds"].Select(t => t.Value<string>()).ToList());
                        break;
                    case "playCard": CoreCardPlay.Play(c, intent.Value<string>("cardInstanceId"), intent["targetId"]?.Value<string>(), intent["choice"]); break;
                    case "chooseCards": CoreCardPlay.ChooseCards(c, intent["cardInstanceIds"].Select(t => t.Value<string>()).ToList()); break;
                    case "chooseDiscard": CoreCardPlay.ChooseDiscard(c, intent["cardInstanceIds"].Select(t => t.Value<string>()).ToList()); break;
                    case "recoverControl": CoreCardPlay.RecoverControl(c, (JArray)intent["selections"]); break;
                    case "applyStatus":
                        CoreStatuses.Apply(c, Core2StatusParityTests.Entity(c, intent.Value<string>("who")), intent.Value<string>("status"), intent.Value<double>("stacks"), Core2StatusParityTests.Entity(c, intent.Value<string>("from")));
                        CoreActions.Drain(c);
                        break;
                }
                if (n == stop)
                {
                    var diffs = new System.Collections.Generic.List<string>();
                    Diff("$", Core2CombatStartParityTests.Apply(want, step["delta"]), Core2CombatStartParityTests.Project(c), diffs);
                    foreach (var d in diffs) TestContext.Out.WriteLine("DIFF " + d);
                    TestContext.Out.WriteLine("INTENT " + intent.ToString(Newtonsoft.Json.Formatting.None));
                    TestContext.Out.WriteLine("EVENTS " + string.Join(",", c.EventLog.Skip(mark).Select(e => e.Value<string>("type"))));
                    TestContext.Out.WriteLine("WEB    " + string.Join(",", step["events"].Select(t => t.Value<string>())));
                    TestContext.Out.WriteLine("AFTER player " + c.Player.ToString(Newtonsoft.Json.Formatting.None));
                    TestContext.Out.WriteLine("AFTER enemies " + string.Join(" | ", c.Enemies.Select(e => e.ToString(Newtonsoft.Json.Formatting.None))));
                    foreach (var e in c.EventLog.Skip(mark)) TestContext.Out.WriteLine("EV " + e.ToString(Newtonsoft.Json.Formatting.None));
                    return;
                }
                want = Core2CombatStartParityTests.Apply(want, step["delta"]);
                n++;
            }
        }
    }
}
