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

    /// <summary>The ids of a flask's actions (web flaskActions.js FLASK_ACTION_IDS), in the order the menu lists them.</summary>
    public enum Core2FlaskActionId
    {
        Use,
        Inspect,
        Drop,
    }

    /// <summary>One row of the flask action menu (web flaskActionPlan, context run): what it is called, whether it can be pressed and, when it cannot, why.</summary>
    public sealed class Core2FlaskAction
    {
        public Core2FlaskActionId Id;
        public string Label;
        public bool Enabled;
        public string Reason = string.Empty;
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

        /// <summary>The flask action menu's rows (web runPotionPlan): Use, Inspect, Drop.</summary>
        public IReadOnlyList<Core2FlaskAction> Actions = new List<Core2FlaskAction>();

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
                row.Actions = Plan(strings, row.Enabled, row.Reason, false, strings.Get(StringKeys.RunPotionsKeep));
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
                row.Actions = Plan(strings, false, strings.Get(StringKeys.RunPotionsCombatOnly), true, string.Empty);
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>flaskActionPlan({ context: 'run' }): Use, Inspect (always offered), Drop.</summary>
        private static IReadOnlyList<Core2FlaskAction> Plan(StringTable strings, bool canUse, string useReason, bool canDrop, string dropReason) => new[]
        {
            new Core2FlaskAction { Id = Core2FlaskActionId.Use, Label = strings.Get(StringKeys.CombatModalPotionsUse), Enabled = canUse, Reason = canUse ? string.Empty : useReason },
            new Core2FlaskAction { Id = Core2FlaskActionId.Inspect, Label = strings.Get(StringKeys.RunPotionsInspect), Enabled = true },
            new Core2FlaskAction { Id = Core2FlaskActionId.Drop, Label = strings.Get(StringKeys.RunPotionsDrop), Enabled = canDrop, Reason = canDrop ? string.Empty : dropReason },
        };

        /// <summary>applyRunPotion: runs an enabled Use or Drop on the run; Inspect and a shut action change nothing. True when the run changed.</summary>
        public static bool Apply(Core2RunSession session, Core2PotionRow row, Core2FlaskActionId id)
        {
            var action = row.Actions.FirstOrDefault(a => a.Id == id);
            if (action == null || !action.Enabled) return false;
            if (id == Core2FlaskActionId.Use && row.IsCharge) return session.DrinkPotion(row.ChargeKind);
            if (id == Core2FlaskActionId.Drop && !row.IsCharge) return session.DropPotion(row.FlaskId);
            return false;
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
