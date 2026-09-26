using System;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using LV = Ashen.Generated.LoopValues;
using MK = Ashen.Generated.MapKeys;
using RK = Ashen.Generated.RunKeys;

namespace Ashen.Domain.Loop
{
    /// <summary>
    /// Between acts (US-4.6; shipped main.js advanceAct): the act counter climbs, the map position resets, the run is
    /// healed to full (halved under Scarce Embers), and the next seat's map is built — the Endless Spire looping the
    /// acts' content while the real act number keeps counting. The seat's tower is the one the new map belongs to
    /// (<see cref="RunLoop.CurrentSeat"/>); the shipped run keeps no separate "lit towers" field (D-076l).
    /// </summary>
    public static class Acts
    {
        /// <summary>advanceAct(): the next act, a full (or Scarce Embers) heal, a fresh map on the run's RNG.</summary>
        public static void Advance(LoopContext ctx)
        {
            var run = ctx.Run;
            run.Put(RK.ActNumber, run.Num(RK.ActNumber) + 1);
            run.Put(MK.Floor, 0);
            run[MK.MapNodeId] = Js.Null();
            run[LK.Path] = new JArray();
            run[LK.LastEncounters] = new JArray();
            if (ctx.ModOn(LV.LessHealing))
            {
                var mult = ctx.Data.Balance.Obj(LK.CustomMods).Num(LK.LessHealingMult);
                run.Put(K.Hp, Math.Min(run.Num(K.MaxHp), run.Num(K.Hp) + Math.Floor((run.Num(K.MaxHp) - run.Num(K.Hp)) * mult)));
            }
            else run[K.Hp] = run[K.MaxHp]?.DeepClone();
            run[RK.MapGraph] = RunLoop.BuildMap(ctx);
        }
    }
}
