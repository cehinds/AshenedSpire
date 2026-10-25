using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;

namespace Ashen.App.Combat
{
    /// <summary>The words of a tooltip: a title line and the sentence under it.</summary>
    public sealed class Core2Reading
    {
        public string Title;
        public string Body;
    }

    /// <summary>
    /// VP-15 (D-1100..): the tooltips of the board's small round marks, from the web's iconTray (src/ui/components/iconTray.js trayIcon, tip()): a status chip explains itself with
    /// "{name} ×{stacks}" and the status's own tooltip (its tokens filled from its own numbers: uiContent.js statusTooltipText), the turns it has left and a build-up meter; a relic pip
    /// with its name and its text (relicRail.js relicRailTile). Engine-free: the screen attaches the words to the pip.
    /// </summary>
    public static class Core2Readings
    {
        private static string Number(JToken value) => value == null || value.Type == JTokenType.Null ? null : CombatText.Number(Js.D(value));

        /// <summary>statusTooltipText(def): the status's tooltip with {proc.*}, {resists.percent}, {tv.pct}, {decay.duration}, {onEnter.N.amount} and {hooks.N.do.M.amount} read from its own row.</summary>
        public static string StatusText(JObject def)
        {
            var text = def?.Str(UiKeys.Tooltip) ?? string.Empty;
            void Sub(string token, string value)
            {
                if (value != null) text = text.Replace(string.Format(System.Globalization.CultureInfo.InvariantCulture, CombatPatterns.TokenFormat, token), value);
            }
            var proc = def?.Obj(K.Proc);
            if (proc != null)
                foreach (var key in new[] { K.Threshold, K.BurstPercent, K.BurstMin, K.BurstMax, K.PoiseDamage })
                    Sub(K.Proc + UiFormats.TokenDot + key, Number(proc[key]));
            Sub(K.Resists + UiFormats.TokenDot + K.Percent, Number(def?.Obj(K.Resists)?[K.Percent]));
            var tagged = def?.Obj(K.TaggedVulnerability);
            if (tagged != null && Js.IsNum(tagged[K.Mult])) Sub(UiFormats.TaggedPercentToken, CombatText.Number(Math.Round((tagged.Num(K.Mult) - 1) * CombatMath.Percent, MidpointRounding.AwayFromZero)));
            if (def?[K.Decay] is JObject decay) Sub(K.Decay + UiFormats.TokenDot + K.Duration, Number(decay[K.Duration]));
            var enter = Js.Items(def?[K.OnEnter]).ToList();
            for (var i = 0; i < enter.Count; i++) Sub(K.OnEnter + UiFormats.TokenDot + i + UiFormats.TokenDot + K.Amount, Number(enter[i][K.Amount]));
            var hooks = Js.Items(def?[K.Hooks]).ToList();
            for (var i = 0; i < hooks.Count; i++)
            {
                var effects = Js.Items(hooks[i][K.Do]).ToList();
                for (var j = 0; j < effects.Count; j++) Sub(K.Hooks + UiFormats.TokenDot + i + UiFormats.TokenDot + K.Do + UiFormats.TokenDot + j + UiFormats.TokenDot + K.Amount, Number(effects[j][K.Amount]));
            }
            return text;
        }

        /// <summary>statusInstancePresentation + the combat statusTip: "{name} ×{stacks}" (a percent payload "{name} {value}% · {turns}"), the tooltip, " Turns left: N." and the build-up on its own line.</summary>
        public static Core2Reading Status(StringTable strings, Core2StatusView status)
        {
            var value = CombatText.Number(status.MeterValue ?? status.Stacks);
            var turns = status.Duration.HasValue ? status.Duration.Value : (double?)null;
            var turnWord = turns.HasValue ? strings.Get(turns.Value == 1 ? StringKeys.StatusTipTurnSingular : StringKeys.StatusTipTurnPlural) : null;
            var title = status.Percent
                ? strings.Format(StringKeys.StatusTipPercent, new StringArgs().Add(UiPlaceholders.Name, status.Name).Add(UiPlaceholders.Value, value + UiFormats.PercentSign)
                    .Add(UiPlaceholders.Duration, turns.HasValue ? strings.Get(StringKeys.StatusTipJoiner) + CombatText.Number(turns.Value) + UiFormats.Space + turnWord : string.Empty))
                : strings.Format(StringKeys.StatusTipStack, new StringArgs().Add(UiPlaceholders.Name, status.Name).Add(UiPlaceholders.Value, value));
            var body = status.Tooltip ?? string.Empty;
            if (turns.HasValue) body += strings.Format(StringKeys.StatusTipTurnsLeft, new StringArgs().Add(UiPlaceholders.Duration, CombatText.Number(turns.Value)));
            if (status.MeterValue.HasValue && status.MeterMax.HasValue)
                body += UiFormats.Newline + strings.Format(StringKeys.StatusTipBuildUp, new StringArgs().Add(UiPlaceholders.Value, CombatText.Number(status.MeterValue.Value)).Add(UiPlaceholders.Max, CombatText.Number(status.MeterMax.Value)));
            return new Core2Reading { Title = title, Body = body };
        }

        /// <summary>relicRailTile's tip: the relic's name and its text with its numbers filled.</summary>
        public static Core2Reading Relic(CombatData data, ComponentDefaults components, string id)
        {
            var def = data.Relics.Has(id) ? data.Relics.Get(id) : new JObject();
            return new Core2Reading { Title = def.Str(K.Name) ?? id, Body = RewardsView.Sentence(data, def.Str(K.TextTemplate), RewardsView.RelicTokens(data, def, components)) };
        }
    }
}
