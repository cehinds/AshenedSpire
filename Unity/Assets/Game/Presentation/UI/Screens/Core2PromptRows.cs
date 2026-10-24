using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The rows of a Core 2 combat prompt in the web's modal looks (VP-6, D-800): the reaction offer as card tiles with a Play that waits for a pick
    /// (src/ui/components/reactionChoiceModal.js), the card choice as tiles, the flasks as the Potions list with Use HOLD (combatActionRow.js:151-198), and every other prompt (stance,
    /// route, equipment, prediction) as the web's option rows. It draws what the flow's <see cref="Core2Prompt"/> holds and raises nothing itself: a press goes to the screen's
    /// callbacks, which send the flow's intents; a refusal is shown by the screen at the control that was pressed.
    /// </summary>
    internal sealed class Core2PromptRows
    {
        private readonly UiContext _ui;
        private readonly StringTable _strings;
        private readonly Core2RunSession _run;
        private readonly Core2CombatFlow _flow;
        private string _offerId;
        private Core2PromptOption _picked;
        private readonly List<OptionTile> _tiles = new List<OptionTile>();

        public Core2PromptRows(UiContext ui, Core2RunSession run, Core2CombatFlow flow)
        {
            _ui = ui;
            _strings = ui.Data.Strings;
            _run = run;
            _flow = flow;
        }

        /// <summary>The reaction offered card the player has picked and not yet played (null before a pick).</summary>
        public Core2PromptOption PickedOffer => _picked;

        /// <summary>The pick of the reaction offer changed (the Play button follows).</summary>
        public event Action PickChanged;

        /// <summary>
        /// Fills <paramref name="body"/> with the prompt's rows and returns the controls the pad walks, in order. <paramref name="text"/> words a plain option, <paramref name="choose"/> sends a
        /// press to the flow, <paramref name="refuse"/> shows the rules' words at a control.
        /// </summary>
        public List<Button> Fill(VisualElement body, Core2Prompt prompt, Func<Core2PromptOption, string> text, Action<Core2PromptOption, VisualElement> choose, Action<VisualElement, string> refuse)
        {
            body.Clear();
            _tiles.Clear();
            var controls = new List<Button>();
            if (prompt.Kind != Core2UiValues.PromptOffer || prompt.Offer?.Id != _offerId)
            {
                _offerId = prompt.Kind == Core2UiValues.PromptOffer ? prompt.Offer?.Id : null;
                _picked = null;
            }
            // A redrawn prompt holds new option objects: the pick follows its id.
            if (_picked != null) _picked = prompt.Options.FirstOrDefault(o => o.Id == _picked.Id);
            switch (prompt.Kind)
            {
                case Core2UiValues.PromptOffer: Offer(body, prompt, controls, refuse); break;
                case Core2UiValues.PromptCards: Cards(body, prompt, controls, choose); break;
                case Core2UiValues.PromptFlasks: Flasks(body, prompt, controls, choose, refuse); break;
                default: Plain(body, prompt, controls, text, choose); break;
            }
            return controls;
        }

        // ------------------------------------------------------------------ the reaction offer

        private void Offer(VisualElement body, Core2Prompt prompt, List<Button> controls, Action<VisualElement, string> refuse)
        {
            var enemy = _flow.View.Enemies.FirstOrDefault(e => e.Id == prompt.Offer.SourceId);
            var intent = enemy == null ? null : Core2CombatText.Intent(_strings, enemy.Intent);
            var incoming = string.IsNullOrEmpty(intent)
                ? prompt.Offer.SourceName
                : _strings.Format(StringKeys.CombatModalReactionIncoming, new StringArgs().Add(UiPlaceholders.Source, prompt.Offer.SourceName).Add(UiPlaceholders.Label, intent));
            body.Add(Line(incoming, false));
            body.Add(Line(_strings.Get(StringKeys.CombatModalReactionHelp), true));
            var grid = new VisualElement();
            grid.AddToClassList(UiClasses.OptionGrid);
            foreach (var option in prompt.Options)
            {
                var tile = new OptionTile();
                var captured = option;
                var offer = option.Offer;
                var card = _flow.View.Hand.FirstOrDefault(c => c.CardId == offer.CardId);
                var data = new OptionTileData { Name = offer.Name, Effect = card?.Text, Disabled = !option.Enabled, Selected = _picked == option };
                if (offer.Stamina > 0) data.Costs.Add(_strings.Format(StringKeys.CombatModalCostSp, new StringArgs().Add(UiPlaceholders.Value, CombatText.Number(offer.Stamina))));
                if (offer.Mana > 0) data.Costs.Add(_strings.Format(StringKeys.CombatModalCostMp, new StringArgs().Add(UiPlaceholders.Value, CombatText.Number(offer.Mana))));
                tile.Bind(data);
                tile.clicked += () =>
                {
                    _picked = captured;
                    for (var i = 0; i < _tiles.Count; i++) _tiles[i].EnableInClassList(UiClasses.OptionTileSelected, prompt.Options[i] == _picked);
                    PickChanged?.Invoke();
                    if (!captured.Enabled) refuse(tile, captured.Refusal);
                };
                _tiles.Add(tile);
                grid.Add(tile);
                controls.Add(tile);
            }
            body.Add(grid);
        }

        // ------------------------------------------------------------------ a card choice

        private void Cards(VisualElement body, Core2Prompt prompt, List<Button> controls, Action<Core2PromptOption, VisualElement> choose)
        {
            var grid = new VisualElement();
            grid.AddToClassList(UiClasses.OptionGrid);
            foreach (var option in prompt.Options)
            {
                var tile = new OptionTile();
                tile.Bind(new OptionTileData { Name = option.Label ?? option.Id, Selected = option.Selected, Disabled = !option.Enabled });
                var captured = option;
                tile.clicked += () => choose(captured, tile);
                grid.Add(tile);
                controls.Add(tile);
            }
            body.Add(grid);
        }

        // ------------------------------------------------------------------ the Potions list

        private void Flasks(VisualElement body, Core2Prompt prompt, List<Button> controls, Action<Core2PromptOption, VisualElement> choose, Action<VisualElement, string> refuse)
        {
            if (prompt.Options.Count == 0)
            {
                body.Add(Line(_strings.Get(StringKeys.CombatModalPotionsEmpty), true));
                return;
            }
            var holdMs = _ui.Data.Tokens.Duration(TokenKeys.HoldConfirm);
            var holdOn = _run.PlayerSettings.Text(SettingIds.HoldConfirm) != SettingIds.HoldOff;
            // The web lists each carried flask kind once with its count and each charge pool once (PotionContentsModel.js): the group's usable flask is the one Use spends.
            foreach (var group in prompt.Options.GroupBy(o => o.Flask.ChargeKind != null ? o.Flask.ChargeKind : o.Flask.FlaskId))
            {
                var options = group.ToList();
                var option = options.FirstOrDefault(o => o.Flask.Usable) ?? options[0];
                var flask = option.Flask;
                var count = flask.ChargeKind != null ? (int)Math.Floor(flask.Current) : options.Count;
                var row = new PotionRow();
                var description = flask.Text ?? string.Empty;
                if (flask.ChargeKind != null)
                {
                    var remaining = _strings.Format(count == 1 ? StringKeys.CombatModalPotionsRemainingOne : StringKeys.CombatModalPotionsRemainingMany, new StringArgs().Add(UiPlaceholders.Count, count));
                    description = string.IsNullOrEmpty(description) ? remaining : description + OverlayValues.WordJoin + remaining;
                }
                var meterKey = flask.ChargeKind != null
                    ? (count == 1 ? StringKeys.CombatModalPotionsCharge : StringKeys.CombatModalPotionsCharges)
                    : (count == 1 ? StringKeys.CombatModalPotionsCarried : StringKeys.CombatModalPotionsCarriedMany);
                row.Bind(new PotionRowData
                {
                    Art = FlaskArt(flask),
                    Name = flask.Name,
                    Description = description,
                    Meta = _strings.Format(meterKey, new StringArgs().Add(UiPlaceholders.Count, count)),
                    UseLabel = _strings.Get(StringKeys.CombatModalPotionsUse),
                    Refusal = flask.Usable ? null : flask.Refusal,
                    HoldMs = holdMs,
                    HoldEnabled = holdOn,
                });
                row.UseCommitted += () => choose(option, row.Use);
                row.Refused += words => refuse(row.Use, words);
                body.Add(row);
                controls.Add(row.Use);
            }
        }

        private UnityEngine.Texture2D FlaskArt(Core2FlaskView flask)
        {
            if (string.IsNullOrEmpty(flask.ArtKey) || !flask.ArtKey.StartsWith(OverlayValues.FlaskArtPrefix, StringComparison.Ordinal)) return null;
            var name = flask.ArtKey.Substring(OverlayValues.FlaskArtPrefix.Length);
            return _ui.Texture(_ui.Data.Art?.Find(ArtKinds.Flask, new StringArgs().Add(ArtPlaceholders.Name, name)));
        }

        // ------------------------------------------------------------------ stance, route, equipment, prediction

        private static void Plain(VisualElement body, Core2Prompt prompt, List<Button> controls, Func<Core2PromptOption, string> text, Action<Core2PromptOption, VisualElement> choose)
        {
            foreach (var option in prompt.Options)
            {
                var button = new LocButton();
                button.AddToClassList(UiClasses.PromptOption);
                button.SetResolved(text(option));
                button.EnableInClassList(UiClasses.PromptOptionSelected, option.Selected);
                button.EnableInClassList(UiClasses.PromptOptionDisabled, !option.Enabled);
                var captured = option;
                button.clicked += () => choose(captured, button);
                body.Add(button);
                controls.Add(button);
            }
        }

        private static LocLabel Line(string text, bool muted)
        {
            var line = new LocLabel();
            line.AddToClassList(UiClasses.CmodalLine);
            line.AddToClassList(UiClasses.ValueText);
            line.EnableInClassList(UiClasses.CmodalLineMuted, muted);
            line.SetResolved(text);
            return line;
        }
    }
}
