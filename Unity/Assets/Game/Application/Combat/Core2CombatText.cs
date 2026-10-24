using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using DK = Ashen.Generated.Core2DamageKeys;
using K = Ashen.Generated.CombatKeys;
using UF = Ashen.Generated.Core2UiFormats;

namespace Ashen.App.Combat
{
    /// <summary>
    /// The words of the Core 2 combat screen (docs/design/11 WP-C1b, D-311): every chip, badge and line is a string-table template filled with the view's numbers
    /// (strings/app.en.json core2.*), so the screen composes no text of its own and the tests check each one resolves. Engine-free.
    /// </summary>
    public static class Core2CombatText
    {
        private static string Num(double d) => CombatText.Number(d);

        private static StringArgs Args() => new StringArgs();

        /// <summary>"Evade 2" while the fighter holds Evade, else null.</summary>
        public static string Evade(StringTable strings, Core2FighterView f) =>
            f.Evade > 0 ? strings.Format(StringKeys.Core2HudEvade, Args().Add(UiPlaceholders.Value, Num(f.Evade))) : null;

        /// <summary>"Block 5" while the fighter holds Block, else null.</summary>
        public static string Block(StringTable strings, Core2FighterView f) =>
            f.Block > 0 ? strings.Format(StringKeys.CombatBlock, Args().Add(UiPlaceholders.Value, Num(f.Block))) : null;

        /// <summary>The stance row: "Stance · Attack", or null when the fighter stands in none.</summary>
        public static string Stance(StringTable strings, Core2FighterView f) =>
            f.StanceName == null ? null : strings.Format(StringKeys.CombatStance, Args().Add(UiPlaceholders.Name, f.StanceName));

        /// <summary>The state badge: Staggered while the fighter is, the defeated mark once it has fallen, else null.</summary>
        public static string Badge(StringTable strings, Core2FighterView f) =>
            !f.Alive ? strings.Get(StringKeys.CombatDefeated) : f.Staggered ? strings.Get(StringKeys.Core2Staggered) : null;

        /// <summary>The stack chips beside the ratings: Precision, Barrier and an armed Counter (Evade has its own rating row).</summary>
        public static List<string> Stacks(StringTable strings, Core2FighterView f)
        {
            var chips = new List<string>();
            if (f.Precision > 0) chips.Add(strings.Format(StringKeys.Core2HudPrecision, Args().Add(UiPlaceholders.Value, Num(f.Precision))));
            if (f.Barrier > 0) chips.Add(strings.Format(StringKeys.Core2HudBarrier, Args().Add(UiPlaceholders.Value, Num(f.Barrier))));
            if (f.Counter != null) chips.Add(strings.Get(StringKeys.Core2HudCounter));
            return chips;
        }

        /// <summary>The status chips (Evade and Precision are shown as stacks, not repeated), capped with a "+N" tail.</summary>
        public static List<string> Statuses(StringTable strings, Core2FighterView f, int cap, IReadOnlyCollection<string> shownAsStacks)
        {
            var chips = new List<string>();
            var shown = f.Statuses.Where(s => !shownAsStacks.Contains(s.Id)).ToList();
            var limit = Math.Max(1, cap);
            var take = shown.Count > limit ? limit - 1 : shown.Count;
            foreach (var s in shown.Take(take))
                chips.Add(s.MeterValue.HasValue && s.MeterMax.HasValue
                    ? strings.Format(StringKeys.CombatStatusMeter, Args().Add(UiPlaceholders.Name, s.Name).Add(UiPlaceholders.Value, Num(s.MeterValue.Value)).Add(UiPlaceholders.Max, Num(s.MeterMax.Value)))
                    : strings.Format(StringKeys.CombatStatus, Args().Add(UiPlaceholders.Name, s.Name).Add(UiPlaceholders.Stacks, Num(s.Stacks))));
            if (shown.Count > take) chips.Add(strings.Format(StringKeys.CombatStatusMore, Args().Add(UiPlaceholders.Count, shown.Count - take)));
            return chips;
        }

        // ------------------------------------------------------------------ the enemy's intent and what the player knows of it

        /// <summary>
        /// The intent chip. A move the player has read shows its category with the live damage (×hits), Block or charging mark, as the 405 chip does; one it has not shows only the knowledge
        /// label ('?', or the clue of its own prediction) and, once the move resolved, whether the prediction was right.
        /// </summary>
        public static string Intent(StringTable strings, Core2IntentView intent)
        {
            if (intent == null) return string.Empty;
            var label = intent.Label ?? strings.Get(StringKeys.Core2IntentUnknown);
            if (!intent.Known)
            {
                if (intent.PredictionCorrect.HasValue)
                    return strings.Format(StringKeys.Core2IntentPredicted, Args().Add(UiPlaceholders.Label, label)
                        .Add(UiPlaceholders.Mark, strings.Get(intent.PredictionCorrect.Value ? StringKeys.Core2IntentRight : StringKeys.Core2IntentWrong)));
                return label;
            }
            var args = Args().Add(UiPlaceholders.Label, label);
            if (intent.Pending || intent.Delayed) return strings.Format(StringKeys.CombatIntentTextCharging, args);
            if (intent.Damage.HasValue)
            {
                args.Add(UiPlaceholders.Damage, Num(intent.Damage.Value));
                if (intent.Hits.HasValue && intent.Hits.Value > 1) return strings.Format(StringKeys.CombatIntentTextMulti, args.Add(UiPlaceholders.Hits, Num(intent.Hits.Value)));
                return strings.Format(StringKeys.CombatIntentTextDamage, args);
            }
            if (intent.Block.HasValue) return strings.Format(StringKeys.CombatIntentTextBlock, args.Add(UiPlaceholders.Block, Num(intent.Block.Value)));
            return strings.Format(StringKeys.CombatIntentTextPlain, args);
        }

        /// <summary>The USS intent kind of a chip: the move's kind once read, the unknown kind while it is not.</summary>
        public static string IntentKind(Core2IntentView intent) => intent == null ? null : intent.Known ? intent.Kind : Core2UiValues.IntentUnknown;

        /// <summary>The label of a maneuver the player may predict ('Attack', 'Smash', ...).</summary>
        public static string Maneuver(StringTable strings, string maneuver) => strings.Get(string.Format(CultureInfo.InvariantCulture, UF.ManeuverKey, maneuver));

        // ------------------------------------------------------------------ targets: the hit badge and the damage receipt

        /// <summary>"75% to hit", or "Out of range" when the card cannot reach the target from where the fighters stand; null without a preview.</summary>
        public static string Hit(StringTable strings, Core2HitView hit)
        {
            if (hit == null) return null;
            if (!hit.InRange) return strings.Get(StringKeys.Core2HitOutOfRange);
            return hit.HitChance.HasValue ? strings.Format(StringKeys.Core2Hit, Args().Add(UiPlaceholders.Pct, Num(Math.Round(hit.HitChance.Value)))) : null;
        }

        private static JObject FirstHit(Core2HitView hit) => hit?.Receipt != null ? Js.Items(hit.Receipt[DK.Hits]).OfType<JObject>().FirstOrDefault() : null;

        /// <summary>What a card's hits take from the target, summed: the receipt holds every hit, each priced after the one before (the web's previewDamageHits hpLoss).</summary>
        private static double Total(Core2HitView hit, string key) => hit?.Receipt == null ? 0 : Js.Items(hit.Receipt[DK.Hits]).OfType<JObject>().Sum(h => h.Num(key));

        /// <summary>The ghost HP of a target: "HP 30 → 18" from the engine's receipt (Block absorbing first), or null when the card does no damage.</summary>
        public static string Preview(StringTable strings, Core2FighterView enemy, Core2HitView hit)
        {
            var first = FirstHit(hit);
            if (first == null || enemy == null) return null;
            var after = Math.Max(0, enemy.Hp - Total(hit, DK.HpLoss));
            return strings.Format(StringKeys.CombatTargetPreview, Args().Add(UiPlaceholders.Before, Num(enemy.Hp)).Add(UiPlaceholders.After, Num(after)));
        }

        /// <summary>
        /// The damage receipt in a line: each source of the number as the engine listed it ("Base 8 · AR +3 · Block −4"), then what lands ("−9 HP"). Null when the card does no damage to this target.
        /// </summary>
        public static string Receipt(StringTable strings, Core2HitView hit)
        {
            var first = FirstHit(hit);
            if (first == null) return null;
            var parts = new List<string>();
            foreach (var line in Js.Items(first[DK.Sources]).OfType<JObject>())
            {
                var amount = line.Num(DK.Amount);
                parts.Add(strings.Format(parts.Count > 0 && amount > 0 ? StringKeys.Core2ReceiptGain : StringKeys.Core2ReceiptLine,
                    Args().Add(UiPlaceholders.Label, line.Str(DK.Label) ?? string.Empty).Add(UiPlaceholders.Value, Num(amount))));
            }
            var blocked = Total(hit, K.Blocked);
            if (blocked > 0) parts.Add(strings.Format(StringKeys.Core2ReceiptBlocked, Args().Add(UiPlaceholders.Value, Num(blocked))));
            parts.Add(strings.Format(StringKeys.Core2ReceiptLoss, Args().Add(UiPlaceholders.Value, Num(Total(hit, DK.HpLoss)))));
            return string.Join(strings.Get(StringKeys.Core2ReceiptJoin), parts);
        }

        // ------------------------------------------------------------------ cards, routes, offers, equipment

        /// <summary>The card's type as the type line names it ('Attack', 'Skill'), else the type itself.</summary>
        public static string CardType(StringTable strings, Core2CardView card)
        {
            if (card.Type == null) return string.Empty;
            var key = string.Format(CultureInfo.InvariantCulture, UiFormats.CardKindKey, card.Type);
            return strings.Has(key) ? strings.Get(key) : card.Type;
        }

        /// <summary>The type line of a card: its type, then its rank when it has one.</summary>
        public static string TypeLine(StringTable strings, Core2CardView card)
        {
            var type = CardType(strings, card);
            var rank = Rank(strings, card);
            return rank == null ? type : type + strings.Get(StringKeys.Core2OptionJoin) + rank;
        }

        /// <summary>The rank badge of a card ("Rank 2"), or null for a card without one.</summary>
        public static string Rank(StringTable strings, Core2CardView card) =>
            card.Rank.HasValue ? strings.Format(StringKeys.Core2CardRank, Args().Add(UiPlaceholders.Value, Num(card.Rank.Value))) : null;

        /// <summary>The trait chips of a card, one line.</summary>
        public static string Traits(StringTable strings, Core2CardView card) =>
            card.Tags.Count == 0 ? null : string.Join(strings.Get(StringKeys.Core2TraitJoin), card.Tags);

        /// <summary>A route option: "Advance · 75% · 1 Stamina" (the odds against the chosen target and what the route costs).</summary>
        public static string Route(StringTable strings, Core2PromptOption option)
        {
            var name = strings.Get(string.Format(CultureInfo.InvariantCulture, UF.RouteKey, option.Id));
            var parts = new List<string> { name };
            var odds = option.Odds?.CurrentHitChance ?? option.Odds?.HitChance;
            if (odds.HasValue) parts.Add(strings.Format(StringKeys.Core2Hit, Args().Add(UiPlaceholders.Pct, Num(Math.Round(odds.Value)))));
            var stamina = option.Route != null ? option.Route.AdvanceSp : 0;
            if (stamina > 0) parts.Add(strings.Format(StringKeys.Core2RouteCost, Args().Add(UiPlaceholders.Value, Num(stamina))));
            return string.Join(strings.Get(StringKeys.Core2OptionJoin), parts);
        }

        /// <summary>A reaction option: "Play Parry · 1 Stamina".</summary>
        public static string OfferOption(StringTable strings, Core2OfferOptionView option)
        {
            var costs = new List<string>();
            if (option.Stamina > 0) costs.Add(strings.Format(StringKeys.Core2CostStamina, Args().Add(UiPlaceholders.Value, Num(option.Stamina))));
            if (option.Mana > 0) costs.Add(strings.Format(StringKeys.Core2CostMana, Args().Add(UiPlaceholders.Value, Num(option.Mana))));
            return costs.Count == 0
                ? strings.Format(StringKeys.Core2OfferPlayFree, Args().Add(UiPlaceholders.Name, option.Name))
                : strings.Format(StringKeys.Core2OfferPlay, Args().Add(UiPlaceholders.Name, option.Name).Add(UiPlaceholders.Cost, string.Join(strings.Get(StringKeys.Core2OptionJoin), costs)));
        }

        /// <summary>A hand set: "Right 2 · Greatsword" with its swap price or the active mark.</summary>
        public static string Set(StringTable strings, Core2SlotView slot, Core2SetView set)
        {
            var head = strings.Format(StringKeys.Core2EquipmentSet, Args().Add(UiPlaceholders.Slot, slot.Label ?? slot.SlotId).Add(UiPlaceholders.Number, set.SetIndex + 1)
                .Add(UiPlaceholders.Name, set.Name ?? strings.Get(StringKeys.Core2EquipmentEmpty)));
            if (set.Active) return head + strings.Get(StringKeys.Core2OptionJoin) + strings.Get(StringKeys.Core2EquipmentActive);
            return set.Cost.HasValue ? head + strings.Get(StringKeys.Core2OptionJoin) + strings.Format(StringKeys.Core2CostStamina, Args().Add(UiPlaceholders.Value, Num(set.Cost.Value))) : head;
        }

        /// <summary>A way to re-arm a set: "Dagger · 1 Stamina", or the empty hand.</summary>
        public static string Change(StringTable strings, Core2ChangeView change)
        {
            var name = change.Name ?? strings.Get(StringKeys.Core2EquipmentEmpty);
            return change.Cost.HasValue ? name + strings.Get(StringKeys.Core2OptionJoin) + strings.Format(StringKeys.Core2CostStamina, Args().Add(UiPlaceholders.Value, Num(change.Cost.Value))) : name;
        }

        /// <summary>A way to recover: "Recover from Stunned".</summary>
        public static string Recover(StringTable strings, Core2PromptOption option) =>
            strings.Format(StringKeys.Core2RecoverOption, Args().Add(UiPlaceholders.Name, option.Label ?? option.Id ?? string.Empty));

        /// <summary>The reactions switch caption ("Reactions: on").</summary>
        public static string Reactions(StringTable strings, bool on) => strings.Get(on ? StringKeys.Core2ReactionsOn : StringKeys.Core2ReactionsOff);

        /// <summary>The log line as the panel shows it, or null for an event the string table has no sentence for.</summary>
        public static string LogLine(Core2LogLine line) => line.Text;
    }
}
