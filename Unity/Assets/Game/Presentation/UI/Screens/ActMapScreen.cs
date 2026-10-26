using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>What a screen of a Core 2 run is opened with: the run, and whether its resume skipped or diverged from the log.</summary>
    public sealed class Core2RunArgs
    {
        public Core2RunSession Session;
        public bool ResumeWarning;
    }

    /// <summary>
    /// W-06 act map (W4b; AF-04, US-4.1 to US-4.4; the web's, D-900..D-902): the board HUD, the progress strip, the parchment board (the painted atlas revealed around the fog's lights, the walked
    /// route, a disc per drawn node; the reachable row lit, haloed and focusable; boss destinations at the path ends), the zoom bar, Potions and the legend over it, and the key bar (Deck, Equipment,
    /// Stats, Menu). Select, then Enter: the first pick selects a node and the tray slides over the map foot after the tray delay (floor, kind, blurb, the boss at the path's end; Back and Enter);
    /// picking it again after the repeat-pick delay, or Enter, travels. The session routes the node's door (a fight to W-07, a rest to W-10, a treasure to W-08). A merchant, an event or a legacy
    /// dungeon whose screen is not built yet opens the planned door over the map with the one action that keeps the run moving (D-126, D-127). Deck, Equipment and Stats open the Armoury (D-903).
    /// Escape closes the legend or the tray, else opens W-20; pad Start opens W-20.
    /// </summary>
    public sealed partial class ActMapScreen : ScreenView
    {
        private Core2RunSession _core2;
        private IClimb _climb;
        private ActMapViewState _view;
        private MapBoard _board;
        private ScrollView _viewport;
        private Meter _hudHp;
        private string _selected;
        private float _selectedAt;
        private int _zoom;
        private bool _legend;
        private bool _tray;
        private PlannedDoorState _door;
        private IVisualElementScheduledItem _trayShow;
        private bool _centred;

        /// <summary>The Core 2 run this map shows.</summary>
        public Core2RunSession Core2 => _core2;

        /// <summary>The run.</summary>
        public IClimb Climb => _climb;

        public ActMapViewState View => _view;
        public MapBoard Board => _board;
        public string Selected => _selected;
        public bool TrayOpen => _tray;
        public bool LegendOpen => _legend;

        /// <summary>The planned door is open (the run stands at a place whose screen is planned).</summary>
        public bool PlannedOpen => _door != null;

        public PlannedDoorState Door => _door;
        public LocButton EnterButton => Root.Q<LocButton>(UiNames.TrayEnter);
        public LocButton PlannedAction => Root.Q<LocButton>(UiNames.PlannedAction);

        public override string FocusMode => _door != null ? UiValues.FocusPlanned : _legend ? UiValues.FocusLegend : null;

        private StringTable Strings => Ui.Data.Strings;

        protected override void OnBind(object args)
        {
            _core2 = (args as Core2RunArgs)?.Session ?? Ui.Core2Session;
            _climb = _core2;
            if (_climb == null)
            {
                Debug.LogError(UiMessages.NoRun);
                return;
            }
            Ui.Core2Session = _core2;
            _hudHp = Root.Q<Meter>(UiNames.HudHp);
            var menu = Root.Q<LocButton>(UiNames.HudMenu);
            menu.clicked += PressMenuButton;
            _menuTip = MenuDoor.Tip(Context, menu, _climb);
            var armoury = Root.Q<LocButton>(UiNames.ToolArmaments);
            armoury.clicked += () => OpenArmoury(ArmouryValues.TabEquipment, armoury);
            _board = Root.Q<MapBoard>(UiNames.MapNodes);
            _viewport = Root.Q<ScrollView>(UiNames.MapViewport);
            var climb = Ui.Data.Components.Climb;
            _board.RouteColor = Ui.TokenColor(TokenKeys.MapRoute);
            _board.RouteOutlineColor = Ui.TokenColor(TokenKeys.MapRouteOutline);
            _board.EdgeWidth = (float)climb.EdgeWidth;
            _board.LaneColor = Ui.TokenColor(TokenKeys.MapLane);
            _board.LaneWidth = (float)(climb.EdgeWidth * climb.LaneEdgeRatio);
            _board.OutlineWidth = (float)climb.EdgeOutlineWidth;
            _board.Atlas = climb.Atlas;
            _board.IconTexture = Ui.Icon;
            _board.TintColor = Ui.TokenColor;
            _board.TileTexture = (x, y) => Ui.Texture(MapAtlas.TileId(Ui.Data.Art, climb.Atlas, _view?.World, x, y));
            _board.NodePicked += OnNode;
            BindHints();
            PulseLane();
            _zoom = Math.Max(0, Math.Min(climb.ZoomSteps.Count - 1, StartingZoom(climb)));
            Root.Q<LocButton>(UiNames.TrayBack).clicked += CloseTray;
            var enter = EnterButton;
            enter.clicked += () => Travel(_selected, enter);
            Root.Q<LocButton>(UiNames.MapZoomOut).clicked += () => Zoom(-1);
            Root.Q<LocButton>(UiNames.MapZoomIn).clicked += () => Zoom(1);
            Root.Q<LocButton>(UiNames.MapCenter).clicked += CentreOnRun;
            Root.Q<LocButton>(UiNames.MapLegendToggle).clicked += () => ShowLegend(!_legend);
            var potions = Root.Q<LocButton>(UiNames.MapPotions);
            potions.clicked += OpenPotions;
            BindPotions();
            var action = PlannedAction;
            action.clicked += () => OnPlanned(action);
            ApplyHudArt();
            Render();
            Context.Instance.LastFocused = _door != null ? (VisualElement)action : FirstReachable();
            _viewport.contentViewport.RegisterCallback<GeometryChangedEvent>(OnViewportLaidOut);
            _board.RegisterCallback<GeometryChangedEvent>(OnViewportLaidOut);
        }

        /// <summary>The lane's glow breathes (map.css shrineLanePulse, 2.2 s alternate); Reduced motion keeps it lit and still.</summary>
        private void PulseLane()
        {
            if (_climb.PlayerSettings.On(SettingIds.ReducedMotion)) return;
            Root.schedule.Execute(() => _board.ToggleInClassList(UiClasses.MapLaneHigh)).Every(Ui.Data.Tokens.Duration(TokenKeys.MapLanePulse));
        }

        /// <summary>
        /// The zoom the map opens at (the player's Map zoom, web mapZoom): Fit is the framed default, a percentage is that rung of the ladder (the choices after Fit, in order, are the rungs;
        /// the ladder is the web's, scaled to this board).
        /// </summary>
        private int StartingZoom(ClimbDefaults climb)
        {
            var settings = _climb.PlayerSettings;
            var choice = settings.Text(SettingIds.MapZoom);
            if (choice == SettingsValues.ZoomFit) return climb.ZoomDefault;
            var options = settings.Defs.Find(SettingIds.MapZoom).Options;
            var rung = options.ToList().IndexOf(choice) - 1;
            return rung < 0 ? climb.ZoomDefault : rung;
        }

        /// <summary>The first layout of the viewport and board scrolls the run's position into view (the start row at an act's start).</summary>
        private void OnViewportLaidOut(GeometryChangedEvent evt)
        {
            if (_centred || float.IsNaN(_viewport.contentViewport.layout.height) || _board.layout.height <= _viewport.contentViewport.layout.height * UiMath.Half) return;
            _centred = true;
            CentreOnRun();
        }

        /// <summary>The HUD's raster art, as the combat board wears it (art lookups by name, ui/art.json): the heart, the armoury and menu buttons.</summary>
        private void ApplyHudArt()
        {
            SetImage(Root.Q(UiNames.HudHeart), Ui.Icon(BoardIcons.Health));
            SetImage(Root.Q(UiNames.ToolArmaments), BoardTexture(BoardArt.ComponentArmoury));
            SetImage(Root.Q(UiNames.HudMenu), BoardTexture(BoardArt.ComponentMenu));
        }

        private Texture2D BoardTexture(string name) => Ui.Texture(Ui.Data.Art?.Find(ArtKinds.Component, new StringArgs().Add(ArtPlaceholders.Name, name)));

        private static void SetImage(VisualElement element, Texture2D texture)
        {
            if (element == null) return;
            element.style.backgroundImage = texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);
        }

        /// <summary>Opens the Armoury modal on a tab (the Deck, Equipment and Stats of the map); until it is built the press is refused where it was made.</summary>
        private void OpenArmoury(string tab, VisualElement anchor)
        {
            if (_door != null) return;
            if (!ArmouryDoor.Open(Context, _climb, tab)) RunFlow.Refuse(Context, anchor, StringKeys.MapRefusalArmoury);
        }

        // ------------------------------------------------------------------ render

        public void Render()
        {
            _view = ActMapView.Build(_climb, Ui.Data);
            RenderHud();
            Root.Q<LocLabel>(UiNames.MapTitle)?.SetResolved(_view.StripTitle);
            RenderRoute();
            var climb = Ui.Data.Components.Climb;
            var zoom = (float)climb.ZoomSteps[_zoom];
            var touch = Nav.Layout == null ? 0d : Nav.Layout.ReferenceMinimum(Ui.Data.Layout.MinPhysicalOf(UiKeys.Touch)) * climb.RowTouchRatio;
            _board.RowHeight = (float)Math.Max(climb.RowHeight * zoom, touch);
            _board.ColPitch = (float)climb.ColPitch * zoom;
            _board.PadX = (float)climb.BoardPadX * zoom;
            _board.PadTop = (float)climb.BoardPadTop * zoom;
            _board.Zoom = zoom;
            _board.Bind(_view);
            if (_selected != null && _view.Node(_selected)?.Reachable != true) _selected = null;
            _board.Select(_selected);
            UiDom.Show(Root.Q(UiNames.MapVignette), _view.Fog);
            RenderTray();
            RenderLegend();
            RenderPlanned();
            RenderMinis();
        }

        /// <summary>The HUD band: the hit points, the relics as pips and the cinders (the combat board's, shared by every room of a run).</summary>
        private void RenderHud()
        {
            var hud = RunHudView.Build(_climb, Ui.Data);
            _hudHp?.Set(hud.Hp, hud.MaxHp);
            var run = _climb.Run;
            var cinders = (double?)run[RunKeys.Cinders] ?? 0d;
            Root.Q<LocLabel>(UiNames.HudCinders)?.SetResolved(Strings.Format(StringKeys.Core2BoardCinders, new StringArgs().Add(UiPlaceholders.Amount, (int)Math.Floor(cinders))));
            var pips = Root.Q(UiNames.HudPips);
            if (pips == null) return;
            pips.Clear();
            if (!(run[CombatKeys.Relics] is JArray relics)) return;
            foreach (var relic in relics)
            {
                var pip = new VisualElement { pickingMode = PickingMode.Ignore };
                pip.AddToClassList(UiClasses.HudPip);
                SetImage(pip, Ui.Texture(StringTable.Fill(Ui.Data.Components.RewardRelicArt, new StringArgs().Add(UiPlaceholders.Id, (string)relic))));
                pips.Add(pip);
            }
        }

        /// <summary>The progress strip's rail: a slot per floor before the boss, the walked ones showing their kind, the one the run stands on ringed; Boss turns gold once reached.</summary>
        private void RenderRoute()
        {
            var rail = Root.Q(UiNames.MapRail);
            if (rail == null) return;
            rail.Clear();
            foreach (var step in _view.Route)
            {
                var slot = new VisualElement { pickingMode = PickingMode.Ignore };
                slot.AddToClassList(MapClasses.Slot);
                var dot = new VisualElement { pickingMode = PickingMode.Ignore };
                dot.AddToClassList(MapClasses.SlotDot);
                dot.EnableInClassList(MapClasses.SlotWalked, step.Kind != null);
                dot.EnableInClassList(MapClasses.SlotCurrent, step.Current);
                if (step.Kind != null)
                {
                    var icon = string.IsNullOrEmpty(step.IconName) ? null : Ui.Icon(step.IconName);
                    if (icon != null)
                    {
                        var face = new VisualElement { pickingMode = PickingMode.Ignore };
                        face.AddToClassList(MapClasses.SlotIcon);
                        face.style.backgroundImage = new StyleBackground(icon);
                        if (step.TintToken != null) face.style.unityBackgroundImageTintColor = Ui.TokenColor(step.TintToken);
                        dot.Add(face);
                    }
                    else
                    {
                        var mark = new LocLabel(string.Format(CultureInfo.InvariantCulture, UiFormats.MapGlyphKey, step.Kind)) { pickingMode = PickingMode.Ignore };
                        mark.AddToClassList(MapClasses.SlotMark);
                        dot.Add(mark);
                    }
                }
                slot.Add(dot);
                rail.Add(slot);
            }
            Root.Q(UiNames.MapRailEnd)?.EnableInClassList(MapClasses.RailEndReached, _view.BossVisited);
        }

        private VisualElement FirstReachable() =>
            _view?.Nodes.Where(n => n.Reachable).OrderBy(n => n.Floor).ThenBy(n => n.Col).Select(n => (VisualElement)_board.Node(n.Id)).FirstOrDefault();

        private void RenderTray()
        {
            var tray = Root.Q(UiNames.NodeTray);
            var node = _selected != null ? _view.Node(_selected) : null;
            UiDom.Show(tray, _tray && node != null);
            if (node == null) return;
            Root.Q<LocLabel>(UiNames.TrayFloor)?.SetResolved(Strings.Format(StringKeys.MapTrayFloor, new StringArgs().Add(UiPlaceholders.Floor, node.Floor)));
            Root.Q<LocLabel>(UiNames.TrayKind)?.SetResolved(node.Label);
            Root.Q<LocLabel>(UiNames.TrayHint)?.SetResolved(node.Hint);
            var boss = Root.Q<LocLabel>(UiNames.TrayBoss);
            boss?.SetResolved(string.IsNullOrEmpty(node.BossLabel) ? string.Empty : Strings.Format(StringKeys.MapTrayBoss, new StringArgs().Add(UiPlaceholders.Label, node.BossLabel)));
            UiDom.Show(boss, !string.IsNullOrEmpty(node.BossLabel));
            EnterButton.SetResolved(Strings.Format(StringKeys.MapTrayEnter, new StringArgs().Add(UiPlaceholders.Kind, node.Label)));
        }

        /// <summary>The legend popover: one row per kind, its icon in the kind's tint (the web's `?` for an unknown place).</summary>
        private void RenderLegend()
        {
            UiDom.Show(Root.Q(UiNames.MapLegend), _legend);
            var rows = Root.Q(UiNames.LegendRows);
            if (rows == null) return;
            rows.Clear();
            foreach (var entry in _view.Legend)
            {
                var row = new VisualElement();
                row.AddToClassList(UiClasses.LegendRow);
                var icon = string.IsNullOrEmpty(entry.IconName) ? null : Ui.Icon(entry.IconName);
                if (icon != null)
                {
                    var face = new VisualElement { pickingMode = PickingMode.Ignore };
                    face.AddToClassList(MapClasses.LegendIcon);
                    face.style.backgroundImage = new StyleBackground(icon);
                    if (entry.TintToken != null) face.style.unityBackgroundImageTintColor = Ui.TokenColor(entry.TintToken);
                    row.Add(face);
                }
                else
                {
                    var mark = new LocLabel(entry.GlyphKey) { pickingMode = PickingMode.Ignore };
                    mark.AddToClassList(MapClasses.NodeMark);
                    row.Add(mark);
                }
                var label = new LocLabel();
                label.SetResolved(entry.Label);
                label.AddToClassList(UiClasses.ValueText);
                row.Add(label);
                rows.Add(row);
            }
        }

        private void RenderPlanned()
        {
            _door = _core2.PendingPort != null ? Core2PortView.Build(_core2, Ui.Data) : null;
            UiDom.Show(Root.Q(UiNames.PlannedDoor), _door != null);
            if (_door == null) return;
            Root.Q<LocLabel>(UiNames.PlannedTitle)?.SetResolved(_door.Title);
            Root.Q<LocLabel>(UiNames.PlannedBody)?.SetResolved(_door.Body);
            var result = Root.Q<LocLabel>(UiNames.PlannedResult);
            result?.SetResolved(_door.Result ?? string.Empty);
            UiDom.Show(result, !string.IsNullOrEmpty(_door.Result));
            PlannedAction.SetResolved(_door.ActionText);
        }

        // ------------------------------------------------------------------ intents

        /// <summary>A node pressed (click or Submit): an unreachable one refuses; the first pick selects; a repeat pick after the delay travels.</summary>
        private void OnNode(string id, VisualElement anchor)
        {
            if (_door != null) return;
            var node = _view.Node(id);
            if (node == null) return;
            if (!node.Reachable)
            {
                RunFlow.Refuse(Context, anchor, StringKeys.MapRefusalNotReachable);
                return;
            }
            var now = Time.realtimeSinceStartup;
            if (_selected == id && (now - _selectedAt) * UiMath.MillisPerSecond >= Ui.Data.Tokens.Duration(TokenKeys.RepeatPick))
            {
                Travel(id, anchor);
                return;
            }
            Select(id);
        }

        /// <summary>Selects a reachable node; the tray opens after the tray delay (04 W-06: 150 ms).</summary>
        public void Select(string id)
        {
            _selected = id;
            _selectedAt = Time.realtimeSinceStartup;
            _board.Select(id);
            _trayShow?.Pause();
            _trayShow = Root.schedule.Execute(() =>
            {
                _tray = true;
                RenderTray();
            }).StartingIn(Ui.Data.Tokens.Duration(TokenKeys.TrayDelay));
        }

        private void CloseTray()
        {
            var id = _selected;
            _trayShow?.Pause();
            _tray = false;
            _selected = null;
            _board.Select(null);
            RenderTray();
            FocusSoon(_board.Node(id) ?? FirstReachable());
        }

        /// <summary>Travel to the node (the session saves at once, PF-06 "node entered") and route to its screen.</summary>
        public void Travel(string id, VisualElement anchor)
        {
            if (id == null) return;
            TravelCore2(id, anchor);
        }

        /// <summary>Core 2: a fight opens its screen; a place whose rules are a later work package stays on the map behind its notice (the run stands at the node and goes on from it).</summary>
        private void TravelCore2(string id, VisualElement anchor)
        {
            Core2TravelResult result;
            try { result = _core2.Travel(id); }
            catch (Exception e)
            {
                SaveFailed(e, () => TravelCore2(id, anchor));
                return;
            }
            if (!result.Travelled)
            {
                RunFlow.Refuse(Context, anchor, string.Format(CultureInfo.InvariantCulture, UiFormats.MapRefusalKey, result.Refusal));
                return;
            }
            if (result.NotYetPorted)
            {
                _selected = null;
                _tray = false;
                Render();
                FocusSoon(PlannedAction);
                return;
            }
            RunFlow.Show(Context, _core2);
        }

        /// <summary>The planned door's one action: Continue past the notice.</summary>
        private void OnPlanned(VisualElement anchor)
        {
            if (_door == null) return;
            try
            {
                _core2.DismissPort();
                _core2.Save();
            }
            catch (Exception e)
            {
                SaveFailed(e, () => OnPlanned(anchor));
                return;
            }
            // Leaving the spoils may end the climb (a death or the last boss once its level notices are read): the run end is the next screen.
            if (_core2.RunOver)
            {
                RunFlow.Show(Context, _core2);
                return;
            }
            Render();
            FocusSoon(FirstReachable());
        }

        private void Zoom(int step)
        {
            var steps = Ui.Data.Components.Climb.ZoomSteps.Count;
            var next = Math.Max(0, Math.Min(steps - 1, _zoom + step));
            if (next == _zoom) return;
            _zoom = next;
            Render();
            Root.schedule.Execute(CentreOnRun).StartingIn(Ui.Data.Tokens.Duration(TokenKeys.FocusSettle));
        }

        /// <summary>⊙: scroll the board so the run's position (or the start row) sits mid-viewport.</summary>
        public void CentreOnRun()
        {
            if (_viewport == null || _view == null) return;
            var id = _view.CurrentId ?? _view.Nodes.Where(n => n.Reachable).Select(n => n.Id).FirstOrDefault();
            var height = _viewport.contentViewport.layout.height;
            if (float.IsNaN(height) || id == null) return;
            _viewport.scrollOffset = new Vector2(0f, _board.OffsetFor(id, height));
        }

        private void ShowLegend(bool on)
        {
            _legend = on;
            RenderLegend();
            FocusSoon(Root.Q(UiNames.MapLegendToggle));
        }

        private void FocusSoon(VisualElement element)
        {
            if (element == null) return;
            Context.Instance.LastFocused = element;
            Nav.FocusTop(element);
        }

        private void SaveFailed(Exception e, Action retry)
        {
            Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.SaveFailed, e.Message));
            ConfirmRequest.Open(Nav, new ConfirmRequest { ConfirmId = ConfirmIds.SaveFailed, OnConfirm = () => retry() });
        }

        private void OpenPause() => Nav.OpenModal(ScreenIds.Pause, new PauseArgs { Session = _climb });

        public override bool HandleBack()
        {
            if (_flaskMenu != null)
            {
                CloseFlaskMenu();
                return true;
            }
            if (_explainedFor != null)
            {
                ClearExplained();
                return true;
            }
            if (_legend)
            {
                ShowLegend(false);
                return true;
            }
            if (_selected != null && _door == null)
            {
                CloseTray();
                return true;
            }
            // web map.js:455-490 and input.js:790-860: with nothing open Escape and pad B do nothing (D-1770); the list opens from the menu button or the Menu key.
            return true;
        }

        /// <summary>The Potions disc: the web's list of flasks between fights (a Core 2 run); the 405 run has none.</summary>
        private void OpenPotions()
        {
            if (_climb is Core2RunSession core2 && Ui.Data.Screens.IsBuilt(ScreenIds.Core2Potions)) Nav.OpenModal(ScreenIds.Core2Potions, new Core2PotionsArgs { Session = core2 });
            else RunFlow.Refuse(Context, Root.Q<LocButton>(UiNames.MapPotions), StringKeys.MapRefusalFlasks);
        }

        public override void OnReturned()
        {
            MenuDoor.Retip(_menuTip, Context, _climb);
            if (_climb != null && !_climb.RunOver) Render();
        }

        public override void Unbind()
        {
            _trayShow?.Pause();
            _miniReveal?.Pause();
            _hints?.Dispose();
            ClearExplained();
            CloseFlaskMenu();
        }

        /// <summary>Opens the legend (review captures).</summary>
        public void LegendForReview() => ShowLegend(true);
    }
}
