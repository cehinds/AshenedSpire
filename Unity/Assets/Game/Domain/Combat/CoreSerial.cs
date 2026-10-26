using System.Globalization;
using Newtonsoft.Json.Linq;
using DK = Ashen.Generated.Core2DamageKeys;
using RV = Ashen.Generated.Core2RemainingValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The expansion action serial as the web counts it, JavaScript arithmetic and all (WP-C0, D-291). A group takes `++ctx.expansionActionSerial || (ctx.expansionActionSerial = 1)`:
    /// the first count of a fresh fight is NaN, which is falsy, so it becomes 1. A trigger's range root takes `++ctx.expansionActionSerial` alone: on a fight that has not counted
    /// yet it is NaN, the root id reads "...:trigger:NaN", and the NaN stays until the next group resets it. A NaN is held as its text, as the web's own JSON holds it.
    /// </summary>
    public static class CoreSerial
    {
        private static double Current(CombatState c)
        {
            var value = c.Fields[DK.ExpansionActionSerial];
            return Js.IsNum(value) ? Js.D(value) : double.NaN;
        }

        private static void Store(CombatState c, double value) =>
            c.Fields[DK.ExpansionActionSerial] = double.IsNaN(value) ? (JToken)RV.NotANumber : Js.N(value);

        /// <summary>`++ctx.expansionActionSerial || (ctx.expansionActionSerial = 1)`: the count a new expansion group takes.</summary>
        public static double NextGroup(CombatState c)
        {
            var next = Current(c) + 1;
            if (double.IsNaN(next) || next == 0) next = 1;
            Store(c, next);
            return next;
        }

        /// <summary>`++ctx.expansionActionSerial`: the count a trigger's range root takes, as text (a fight that has not counted yet reads NaN).</summary>
        public static string NextTrigger(CombatState c)
        {
            var next = Current(c) + 1;
            Store(c, next);
            return double.IsNaN(next) ? RV.NotANumber : next.ToString(CultureInfo.InvariantCulture);
        }
    }
}
