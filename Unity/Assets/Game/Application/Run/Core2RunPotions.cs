using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Events;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.App.Run
{
    /// <summary>The one verb a flask offers between fights (web RunPotionModel.js runPotionVerb): Drink a charge flask, Drop a carried one.</summary>
    public enum Core2PotionVerb
    {
        Drink,
        Drop,
    }

    /// <summary>One entry of the map's Potions list (web PotionContentsModel.js, runPotions.js openRunPotions): a charge pool or a carried kind, its words, its count and its verb with the reason it is shut.</summary>
    public sealed class Core2PotionRow
    {
        /// <summary>The charge kind (hp, mana) of a charge flask, else null.</summary>
        public string ChargeKind;

        public string FlaskId;
        public string Name;

        /// <summary>The web's "Heal 25% of your max HP. 2 charges remaining." (the flask's text, then the charges left for a charge flask).</summary>
        public string Description;

        /// <summary>"2 charges" or "3 carried potions" (the web's potions.count.*), followed by the reason the verb is shut when it is.</summary>
        public string Meta;

        public string ArtKey;
        public int Count;
        public Core2PotionVerb Verb;
        public string VerbLabel;

        /// <summary>The verb can be pressed now; when it cannot, <see cref="Reason"/> says why.</summary>
        public bool Enabled;

        public string Reason = string.Empty;

        public bool IsCharge => ChargeKind != null;
    }

    /// <summary>
    /// The Potions list between fights (web ui/components/runPotions.js, ui/models/RunPotionModel.js, PotionContentsModel.js): the HP and MP charge flasks, each with the charges it holds, and the carried
    /// potions grouped by kind with their count. A charge flask is drunk only with "Use flasks outside combat" on and a charge left, and stays with the run (never dropped); a carried potion is for
    /// combat only and may be dropped here (one of its kind, the first slot holding it). Engine-free.
    /// </summary>
    public static class Core2RunPotions
    {
        /// <summary>The list in the web's order: the charge pools in the engine's kind order, then each carried flask kind in slot order.</summary>
        public static IReadOnlyList<Core2PotionRow> Rows(Core2RunSession session, StringTable strings)
        {
            var run = session.Run;
            var data = session.CombatData;
            var rows = new List<Core2PotionRow>();
            var drinkOutside = session.PlayerSettings.On(SettingIds.UseRestorativeFlasksOutsideCombat);
            var charges = run.Obj(K.FlaskCharges);
            foreach (var kind in RunEffects.ChargeKinds(data))
            {
                var def = ChargeFlask(data, kind);
                if (def == null) continue;
                var raw = charges?[kind + V.CurrentSuffix];
                var count = Js.IsNum(raw) && Js.D(raw) > 0 ? (int)Math.Floor(Js.D(raw)) : 0;
                var row = Row(strings, def, kind, count);
                row.Verb = Core2PotionVerb.Drink;
                row.VerbLabel = strings.Get(StringKeys.RunPotionsDrink);
                row.Enabled = drinkOutside && count > 0;
                row.Reason = row.Enabled ? string.Empty : strings.Get(count <= 0 ? StringKeys.RunPotionsEmpty : StringKeys.RunPotionsSetting);
                rows.Add(row);
            }
            var carried = new List<string>();
            foreach (var flask in Js.Items(run[K.Flasks]).OfType<JObject>())
            {
                var id = flask.Str(K.FlaskId);
                if (!string.IsNullOrEmpty(id) && data.Flasks.Has(id)) carried.Add(id);
            }
            foreach (var group in carried.GroupBy(id => id))
            {
                var row = Row(strings, data.Flasks.Get(group.Key), null, group.Count());
                row.Verb = Core2PotionVerb.Drop;
                row.VerbLabel = strings.Get(StringKeys.RunPotionsDrop);
                row.Enabled = true;
                rows.Add(row);
            }
            return rows;
        }

        private static Core2PotionRow Row(StringTable strings, JObject def, string chargeKind, int count)
        {
            var description = (def.Str(K.TextTemplate) ?? string.Empty).Trim();
            if (chargeKind != null)
            {
                var remaining = strings.Format(count == 1 ? StringKeys.CombatModalPotionsRemainingOne : StringKeys.CombatModalPotionsRemainingMany, new StringArgs().Add(UiPlaceholders.Count, count));
                description = description.Length == 0 ? remaining : description + OverlayValues.WordJoin + remaining;
            }
            var countKey = chargeKind != null
                ? (count == 1 ? StringKeys.CombatModalPotionsCharge : StringKeys.CombatModalPotionsCharges)
                : (count == 1 ? StringKeys.CombatModalPotionsCarried : StringKeys.CombatModalPotionsCarriedMany);
            return new Core2PotionRow
            {
                ChargeKind = chargeKind,
                FlaskId = def.Str(K.Id),
                Name = def.Str(K.Name),
                Description = description,
                ArtKey = def.Str(OverlayKeys.FlaskArtKey),
                Count = count,
                Meta = strings.Format(countKey, new StringArgs().Add(UiPlaceholders.Count, count)),
            };
        }

        /// <summary>chargeFlaskDefinition(registries, kind): the first authored flask of that kind (rules/combatEngine.json flasks.kindByOp), or null for a kind that is no charge pool.</summary>
        public static JObject ChargeFlask(CombatData data, string kind)
        {
            var cfg = data.Engine.Obj(K.Flasks);
            if (kind == null || !Js.Includes(cfg?[K.ChargeKinds], kind)) return null;
            foreach (var def in data.Flasks.All)
            {
                string defKind = null;
                if (Js.IsStr(def[K.Kind])) defKind = def.Str(K.Kind);
                else
                    foreach (var rule in Js.Items(cfg[K.KindByOp]).OfType<JObject>())
                        if (Js.Items(def[K.Effects]).OfType<JObject>().Any(e => e.Str(K.Op) == rule.Str(K.Op))) { defKind = rule.Str(K.Kind); break; }
                if (defKind == kind) return def;
            }
            return null;
        }
    }
}
