using System;
using System.Collections.Generic;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The map's Potions corner beside the button (web ui/components/runPotions.js mountRunPotions, styles/map.css:150-220): the flasks as minis that come out of the button after the player's
    /// own tooltip delay and go back at once; a tap explains a flask, a second tap opens its menu (Use, Inspect, Drop); what does not fit is a +N tile that opens the Potions list.
    /// </summary>
    public sealed partial class ActMapScreen
    {
        private VisualElement _minisHost;
        private VisualElement _minis;
        private IVisualElementScheduledItem _miniReveal;
        private IVisualElementScheduledItem _miniHide;
        private Tooltip _explained;
        private VisualElement _explainedFor;
        private FlaskMenu _flaskMenu;
        private VisualElement _flaskMenuFor;

        /// <summary>The minis that stand on the tray now (the +N tile is not among them).</summary>
        public IReadOnlyList<VisualElement> Minis => _miniViews;

        private readonly List<VisualElement> _miniViews = new List<VisualElement>();

        private void BindPotions()
        {
            _minisHost = Root.Q(UiNames.MapPotionsHost);
            _minis = Root.Q(UiNames.MapPotionMinis);
            if (_minisHost == null || _minis == null) return;
            _minisHost.RegisterCallback<PointerEnterEvent>(_ =>
            {
                if (TooltipPolicy.HoverEnabled) RevealMinis(TooltipPolicy.Applied ? TooltipPolicy.OpenMs : Ui.Data.Tokens.Duration(TokenKeys.TooltipDelay));
            });
            _minisHost.RegisterCallback<PointerLeaveEvent>(_ => HideMinis());
            _minisHost.RegisterCallback<FocusInEvent>(_ => RevealMinis(Tooltip.Rules.FocusMs));
            _minisHost.RegisterCallback<FocusOutEvent>(_ => _minisHost.schedule.Execute(() =>
            {
                var focused = _minisHost.focusController?.focusedElement as VisualElement;
                if (focused == null || !_minisHost.Contains(focused)) HideMinis();
            }).StartingIn(1));
            Root.RegisterCallback<PointerDownEvent>(OnAnyPointerDown, TrickleDown.TrickleDown);
            RenderMinis();
        }

        /// <summary>The wait is the player's own tooltip delay; a flask tray already out stays.</summary>
        private void RevealMinis(int waitMs)
        {
            _miniHide?.Pause();
            _miniReveal?.Pause();
            if (_miniViews.Count == 0) return;
            _miniReveal = _minis.schedule.Execute(() => _minis.AddToClassList(UiClasses.PotionMinisShown)).StartingIn(waitMs);
        }

        /// <summary>They go back with no wait at all.</summary>
        private void HideMinis()
        {
            _miniReveal?.Pause();
            _minis.RemoveFromClassList(UiClasses.PotionMinisShown);
        }

        private void RenderMinis()
        {
            if (_minis == null) return;
            _minis.Clear();
            _miniViews.Clear();
            ClearExplained();
            CloseFlaskMenu();
            if (!(_climb is Core2RunSession core2)) return;
            var rows = Core2RunPotions.Rows(core2, Strings);
            var slots = Ui.Data.Components.Climb.PotionMinis;
            var shown = rows.Count <= slots ? rows.Count : Math.Max(0, slots - 1);
            for (var i = 0; i < shown; i++) _minis.Add(Mini(rows[i]));
            if (rows.Count > shown)
            {
                var more = new VisualElement { focusable = true };
                more.AddToClassList(UiClasses.PotionMini);
                more.AddToClassList(UiClasses.PotionMiniMore);
                var count = new LocLabel { pickingMode = PickingMode.Ignore };
                count.AddToClassList(UiClasses.PotionMiniCount);
                count.SetResolved(Strings.Format(StringKeys.RunPotionsMore, new StringArgs().Add(UiPlaceholders.Count, rows.Count - shown)));
                more.Add(count);
                more.RegisterCallback<ClickEvent>(_ => OpenPotions());
                more.RegisterCallback<NavigationSubmitEvent>(_ => OpenPotions());
                _minis.Add(more);
            }
        }

        private VisualElement Mini(Core2PotionRow row)
        {
            var mini = new VisualElement { focusable = true };
            mini.AddToClassList(UiClasses.PotionMini);
            mini.EnableInClassList(UiClasses.PotionMiniSpent, row.Count <= 0);
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.AddToClassList(UiClasses.PotionMiniArt);
            var texture = FlaskTexture(row);
            if (texture != null) art.style.backgroundImage = new StyleBackground(texture);
            mini.Add(art);
            var count = new LocLabel { pickingMode = PickingMode.Ignore };
            count.AddToClassList(UiClasses.PotionMiniCount);
            count.SetResolved(row.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            mini.Add(count);
            Tooltip.AttachResolved(mini, Context.Overlay, Ui.Data.Components.TooltipPlain, row.Name, row.Description, Ui.Data.Tokens.Duration(TokenKeys.TooltipDelay));
            mini.RegisterCallback<ClickEvent>(_ => OnMiniTap(mini, row));
            mini.RegisterCallback<NavigationSubmitEvent>(_ => OpenFlaskMenu(mini, row));
            _miniViews.Add(mini);
            return mini;
        }

        private Texture2D FlaskTexture(Core2PotionRow row)
        {
            if (string.IsNullOrEmpty(row.ArtKey) || !row.ArtKey.StartsWith(OverlayValues.FlaskArtPrefix, StringComparison.Ordinal)) return null;
            var name = row.ArtKey.Substring(OverlayValues.FlaskArtPrefix.Length);
            return Ui.Texture(Ui.Data.Art?.Find(ArtKinds.Flask, new StringArgs().Add(ArtPlaceholders.Name, name)));
        }

        /// <summary>A tap explains at once; a second tap on the same flask opens its menu (iconTray.js trayIcon activate).</summary>
        private void OnMiniTap(VisualElement mini, Core2PotionRow row)
        {
            if (_explainedFor == mini)
            {
                ClearExplained();
                OpenFlaskMenu(mini, row);
                return;
            }
            CloseFlaskMenu();
            ClearExplained();
            _explainedFor = mini;
            _explained = Tooltip.ShowNow(mini, Context.Overlay, Ui.Data.Components.TooltipPlain, row.Name, row.Description);
        }

        private void ClearExplained()
        {
            _explained?.Dismiss();
            _explained = null;
            _explainedFor = null;
        }

        /// <summary>A press anywhere else forgets the explained flask and closes the menu (the web's document pointerdown listeners).</summary>
        private void OnAnyPointerDown(PointerDownEvent evt)
        {
            var target = evt.target as VisualElement;
            if (_explainedFor != null && (target == null || !_explainedFor.Contains(target))) ClearExplained();
            if (_flaskMenu != null && target != null && !_flaskMenu.Contains(target) && !_flaskMenuFor.Contains(target)) CloseFlaskMenu();
        }

        private void OpenFlaskMenu(VisualElement anchor, Core2PotionRow row)
        {
            if (_flaskMenuFor == anchor)
            {
                CloseFlaskMenu();
                return;
            }
            CloseFlaskMenu();
            ClearExplained();
            var menu = new FlaskMenu();
            menu.Bind(row.Name, row.Description, row.Actions);
            menu.Chosen += id =>
            {
                CloseFlaskMenu();
                if (id == Core2FlaskActionId.Inspect)
                {
                    Nav.OpenModal(ScreenIds.Core2Potions, new Core2PotionsArgs { Session = (Core2RunSession)_climb, Inspect = row });
                    return;
                }
                if (Core2RunPotions.Apply((Core2RunSession)_climb, row, id)) Render();
            };
            menu.Refused += (button, reason) => Kit.Refusal.Show(button, Context.Overlay, reason, Ui.Data.Tokens.Duration(TokenKeys.Refusal));
            menu.style.visibility = Visibility.Hidden;
            Context.Overlay.Add(menu);
            _flaskMenu = menu;
            _flaskMenuFor = anchor;
            EventCallback<GeometryChangedEvent> placed = null;
            placed = _ =>
            {
                menu.UnregisterCallback(placed);
                if (_flaskMenu != menu) return;
                menu.PlaceUnder(anchor, Context.Overlay);
                menu.style.visibility = StyleKeyword.Null;
                if (menu.Rows.Count > 0) menu.Rows[0].Focus();
            };
            menu.RegisterCallback(placed);
        }

        private void CloseFlaskMenu()
        {
            if (_flaskMenu == null) return;
            _flaskMenu.RemoveFromHierarchy();
            _flaskMenu = null;
            _flaskMenuFor = null;
        }
    }
}
