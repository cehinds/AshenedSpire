using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;

namespace Ashen.Tests.Play
{
    /// <summary>
    /// The climb smoke's policy, mirrored key for key from the EditMode driver (ClimbSessionTests.Driver with Smoke on): the
    /// fight's legal-move choice on the bot's own LCG, and — for the capture fixtures, which drive the session without the
    /// screens — the smoke's step: the first reachable node, Continue at every reward door, Rest when offered then Leave,
    /// the merchant's Leave, the event's stand-in choice then Continue, the dungeon's Advance. Test code only.
    /// </summary>
    public sealed class ClimbPilot
    {
        /// <summary>The short Custom Climb seed the EditMode driver wins with this policy under the assisted scale (ClimbSessionTests.ShortWinSeed).</summary>
        public const uint ShortWinSeed = 3u;

        private uint _s;

        public ClimbPilot(uint seed)
        {
            _s = seed ^ 0x5bd1e995u;
            if (_s == 0) _s = 1;
        }

        private int Next(int n)
        {
            _s = unchecked(_s * 1664525u + 1013904223u);
            return (int)(_s % (uint)n);
        }

        public static double Assisted(string pool) => pool == "boss" ? 0.12 : pool == "elite" ? 0.25 : 0.35;

        public static JObject ShortClimb() => JObject.Parse("{\"ascension\":0,\"mods\":{},\"deckMode\":\"standard\",\"mapShape\":{\"floors\":7,\"columns\":3}}");

        /// <summary>A Crimson charge when low, sometimes an Azure one when dry, else a legal card aimed at a living enemy first, else End Turn.</summary>
        public CombatCommand Choose(CombatState c)
        {
            var p = c.Player;
            if (p.Num("hp") * 10 < p.Num("maxHp") * 4 && CombatLegality.CanUseFlask(c, 0, "hp") == null) return CombatCommand.UseFlask(0, "hp");
            if (p.Num("mana") <= 0 && p.Num("maxMana") > 0 && CombatLegality.CanUseFlask(c, 0, "mana") == null && Next(2) == 0) return CombatCommand.UseFlask(0, "mana");
            var hand = c.Piles.Hand.Select(card => card.Str("instanceId")).ToList();
            var start = hand.Count == 0 ? 0 : Next(hand.Count);
            var order = Enumerable.Range(0, hand.Count).Select(k => hand[(start + k) % hand.Count]).ToList();
            foreach (var aimed in new[] { true, false })
                foreach (var id in order)
                {
                    var targets = CombatLegality.Targets(c, id);
                    if ((targets.Count > 0) != aimed) continue;
                    var target = targets.Count > 0 ? targets[Next(targets.Count)] : null;
                    if (CombatLegality.CanPlay(c, id, target) == null) return CombatCommand.PlayCard(id, target);
                }
            return EndTurn(c);
        }

        public static CombatCommand EndTurn(CombatState c)
        {
            var plan = HandRules.Plan(c);
            return CombatCommand.EndTurn(plan.Cards.Take((int)plan.Minimum).Select(card => card.Str("instanceId")));
        }

        /// <summary>One smoke-policy step on the session alone (captures): true while the run goes on.</summary>
        public bool Step(RunSession s, UiData ui)
        {
            switch (s.Location)
            {
                case "map":
                    s.Travel(s.ReachableNodes()[0]);
                    break;
                case "combat":
                    for (var i = 0; i < 4000 && s.IsInCombat; i++) s.Execute(Choose(s.Combat.State));
                    break;
                case "rewards":
                    s.FinishRewards();
                    break;
                case "rest":
                    if (RestView.Build(s, ui).Option("rest").Available) s.RestHere();
                    if (s.Location == "rest") s.LeaveRest();
                    break;
                case "merchant":
                    s.LeaveMerchant();
                    break;
                case "event":
                    if (!s.EventDone) s.ChooseEvent(s.EventStandInChoice());
                    else s.FinishEvent();
                    break;
                case "dungeon":
                    s.AdvanceDungeon();
                    break;
                default:
                    return false;
            }
            return !s.RunOver;
        }
    }
}
