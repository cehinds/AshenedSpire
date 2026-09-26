using System;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;

namespace Ashen.App.Combat
{
    /// <summary>
    /// W-07 display text from the string tables (docs/design/08 §9): refusals (strings/combat.en.json, positional
    /// arguments as the legality service names them), intent chips with their live numbers, and card kinds. Engine-free,
    /// so the tests check every refusal and intent resolves.
    /// </summary>
    public static class CombatText
    {
        /// <summary>The refusal sentence ("Needs 2 Energy (you have 1)."), or null when there is no refusal.</summary>
        public static string Refusal(StringTable strings, Refusal refusal)
        {
            if (refusal == null) return null;
            var template = strings.Get(refusal.Key);
            try { return string.Format(CultureInfo.InvariantCulture, template, refusal.Args.Select(Arg).ToArray()); }
            catch (FormatException) { return template; }
        }

        private static object Arg(object a) => a is double d ? (object)Number(d) : a;

        /// <summary>A number as the player reads it: integers without a fraction, others invariant.</summary>
        public static string Number(double d) =>
            d == Math.Floor(d) && Math.Abs(d) < long.MaxValue ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture);

        /// <summary>An intent chip: the label with its live damage (×hits when more than one), block, or charging mark.</summary>
        public static string Intent(StringTable strings, IntentView intent)
        {
            if (intent == null) return string.Empty;
            var args = new StringArgs().Add(UiPlaceholders.Label, strings.Get(intent.LabelKey));
            if (intent.Pending || intent.Delayed) return strings.Format(StringKeys.CombatIntentTextCharging, args);
            if (intent.Damage.HasValue)
            {
                args.Add(UiPlaceholders.Damage, Number(intent.Damage.Value));
                if (intent.Hits.HasValue && intent.Hits.Value > 1)
                    return strings.Format(StringKeys.CombatIntentTextMulti, args.Add(UiPlaceholders.Hits, Number(intent.Hits.Value)));
                return strings.Format(StringKeys.CombatIntentTextDamage, args);
            }
            if (intent.Block.HasValue) return strings.Format(StringKeys.CombatIntentTextBlock, args.Add(UiPlaceholders.Block, Number(intent.Block.Value)));
            return strings.Format(StringKeys.CombatIntentTextPlain, args);
        }

        /// <summary>The card kind's label (combat.cardKind.&lt;kind&gt;), or empty for a card without a kind.</summary>
        public static string CardKind(StringTable strings, string kind) =>
            kind == null ? string.Empty : strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.CardKindKey, kind));
    }
}
