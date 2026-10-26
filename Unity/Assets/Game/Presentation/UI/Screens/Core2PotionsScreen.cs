using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>What the map's Potions list opens with: the run.</summary>
    public sealed class Core2PotionsArgs
    {
        public Core2RunSession Session;

        /// <summary>A flask to read about (the flask menu's Inspect, web openFlaskInspectModal) instead of the list.</summary>
        public Core2PotionRow Inspect;
    }

    /// <summary>
    /// The Potions list between fights (web ui/components/runPotions.js openRunPotions; docs/qa/web-screens/map-potions): a modal titled Potions over the map with one row per entry, the flask's art, its
    /// name in gold, what it does and how many charges it has left (or how many are carried), and its one verb, Drink for a charge flask and Drop for a carried potion, dimmed with the
    /// reason when the rules shut it. A press runs the verb and closes the list (the map redraws from the run). Escape and the close glyph leave.
    /// </summary>
    public sealed class Core2PotionsScreen : ScreenView
    {
        private Core2RunSession _run;
        private CombatModal _modal;
        private readonly List<PotionRow> _rows = new List<PotionRow>();

        public IReadOnlyList<PotionRow> Rows => _rows;

        private StringTable Strings => Ui.Data.Strings;

        protected override void OnBind(object args)
        {
            _run = (args as Core2PotionsArgs)?.Session ?? Ui.Core2Session;
            _modal = Root.Q<CombatModal>(UiNames.PotionsModal);
            if (_run == null || _modal == null)
            {
                Debug.LogError(UiMessages.NoRun);
                return;
            }
            _modal.size = UiValues.ModalSizeMd;
            _modal.CloseRequested += Leave;
            var inspect = (args as Core2PotionsArgs)?.Inspect;
            if (inspect != null) RenderInspect(inspect);
            else Render();
        }

        /// <summary>
        /// Inspect is a door (flask.js openFlaskInspectModal): the flask's art at a size worth looking at beside what it does and its charges, why it cannot be used here when it cannot, and Use as the one footer action.
        /// </summary>
        private void RenderInspect(Core2PotionRow row)
        {
            _modal.Reset();
            _modal.SetHead(Strings.Get(StringKeys.RunPotionsEyebrow), row.Name);
            var layout = new VisualElement();
            layout.AddToClassList(UiClasses.FlaskInspect);
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.AddToClassList(UiClasses.FlaskInspectArt);
            var texture = FlaskArt(row);
            if (texture != null) art.style.backgroundImage = new StyleBackground(texture);
            layout.Add(art);
            var text = new VisualElement();
            text.AddToClassList(UiClasses.FlaskInspectText);
            var line = new LocLabel { pickingMode = PickingMode.Ignore };
            line.AddToClassList(UiClasses.FlaskInspectLine);
            line.AddToClassList(UiClasses.ValueText);
            line.SetResolved(row.Description ?? string.Empty);
            text.Add(line);
            var use = row.Actions.FirstOrDefault(a => a.Id == Core2FlaskActionId.Use);
            if (use != null && !use.Enabled && !string.IsNullOrEmpty(use.Reason))
            {
                var reason = new LocLabel { pickingMode = PickingMode.Ignore };
                reason.AddToClassList(UiClasses.FlaskInspectReason);
                reason.AddToClassList(UiClasses.ValueText);
                reason.SetResolved(use.Reason);
                text.Add(reason);
            }
            layout.Add(text);
            _modal.Body.Add(layout);
            if (use == null) return;
            var button = new LocButton();
            button.AddToClassList(UiClasses.ButtonReady);
            button.SetResolved(use.Label);
            button.SetEnabled(use.Enabled);
            button.clicked += () =>
            {
                if (Core2RunPotions.Apply(_run, row, Core2FlaskActionId.Use)) Nav.Close(Context.Instance);
            };
            _modal.Foot.Add(button);
            Context.Instance.LastFocused = use.Enabled ? button : (VisualElement)_modal.CloseButton;
        }

        private void Render()
        {
            _modal.Reset();
            _modal.SetHead(null, Strings.Get(StringKeys.RunPotionsTitle));
            _rows.Clear();
            var list = Core2RunPotions.Rows(_run, Strings);
            if (list.Count == 0)
            {
                var none = new LocLabel();
                none.AddToClassList(UiClasses.CmodalLine);
                none.AddToClassList(UiClasses.CmodalLineMuted);
                none.AddToClassList(UiClasses.ValueText);
                none.SetResolved(Strings.Get(StringKeys.RunPotionsNone));
                _modal.Body.Add(none);
                return;
            }
            foreach (var entry in list)
            {
                var row = new PotionRow();
                row.Bind(new PotionRowData
                {
                    Art = FlaskArt(entry),
                    Name = entry.Name,
                    Description = entry.Description,
                    Meta = entry.Enabled || string.IsNullOrEmpty(entry.Reason) ? entry.Meta : entry.Meta + OverlayValues.WordJoin + entry.Reason,
                    UseLabel = entry.VerbLabel,
                    Refusal = entry.Enabled ? null : entry.Reason,
                    HoldMs = 0,
                    HoldEnabled = false,
                });
                var captured = entry;
                row.UseCommitted += () => Press(captured);
                row.Refused += words => Kit.Refusal.Show(row.Use, Context.Overlay, words, Ui.Data.Tokens.Duration(TokenKeys.Refusal));
                _rows.Add(row);
                _modal.Body.Add(row);
            }
            Context.Instance.LastFocused = _rows[0].Use;
        }

        private Texture2D FlaskArt(Core2PotionRow entry)
        {
            if (string.IsNullOrEmpty(entry.ArtKey) || !entry.ArtKey.StartsWith(OverlayValues.FlaskArtPrefix, StringComparison.Ordinal)) return null;
            var name = entry.ArtKey.Substring(OverlayValues.FlaskArtPrefix.Length);
            return Ui.Texture(Ui.Data.Art?.Find(ArtKinds.Flask, new StringArgs().Add(ArtPlaceholders.Name, name)));
        }

        /// <summary>The verb runs on the run, then the list closes; the map redraws when it returns.</summary>
        private void Press(Core2PotionRow entry)
        {
            if (entry.Verb == Core2PotionVerb.Drink) _run.DrinkPotion(entry.ChargeKind);
            else _run.DropPotion(entry.FlaskId);
            Nav.Close(Context.Instance);
        }

        private void Leave() => Nav.Close(Context.Instance);

        public override bool HandleBack()
        {
            Leave();
            return true;
        }
    }
}
