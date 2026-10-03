using System;
using System.Globalization;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>What a climb screen is opened with: the run, and whether its resume skipped or diverged from the log (D-017).</summary>
    public sealed class RunScreenArgs
    {
        public RunSession Session;
        public bool ResumeWarning;
    }

    /// <summary>
    /// W-06 act map (W4b; AF-04, US-4.1 to US-4.4): the RUN_HUD band, the act, seat and floor header, the board (every node
    /// and edge from the map port; where the run stands; the travelled trail; the reachable row lit, rimmed and cued with ›;
    /// boss destinations at the path ends), and the footer (zoom −/+, centre ⊙, the legend ?, the hint, Potions). Select,
    /// then Enter: the first pick selects a node and the tray slides over the map foot after the tray delay (kind, hint,
    /// the boss at the path's end; Back and Enter); picking it again after the repeat-pick delay, or Enter, travels. The
    /// session routes the node's door (a fight to W-07, a rest to W-10, a treasure to W-08). A merchant, an event or a
    /// legacy dungeon whose screen is not built yet opens the planned door over the map with the one action that keeps the
    /// run moving (D-126, D-127). Fog is not ported: the whole act is drawn (D-128). Escape closes the legend or the
    /// tray, else opens W-20; pad Start opens W-20.
    /// </summary>
    public sealed class ActMapScreen : ScreenView
    {
        private RunSession _session;
        private ActMapViewState _view;
        private MapBoard _board;
        private ScrollView _viewport;
        private RunHud _hud;
        private string _selected;
        private float _selectedAt;
        private int _zoom;
        private bool _legend;
        private bool _tray;
        private PlannedDoorState _door;
        private IVisualElementScheduledItem _trayShow;
        private bool _centred;

        public RunSession Session => _session;
        public ActMapViewState View => _view;
        public MapBoard Board => _board;
        public string Selected => _selected;
        public bool TrayOpen => _tray;
        public bool LegendOpen => _legend;

        /// <summary>The planned door is open (the run stands at a merchant, an event or a legacy dungeon whose screen is planned).</summary>
        public bool PlannedOpen => _door != null;

        public PlannedDoorState Door => _door;
        public LocButton EnterButton => Root.Q<LocButton>(UiNames.TrayEnter);
        public LocButton PlannedAction => Root.Q<LocButton>(UiNames.PlannedAction);

        public override string FocusMode => _door != null ? UiValues.FocusPlanned : _legend ? UiValues.FocusLegend : null;

        private StringTable Strings => Ui.Data.Strings;

        protected override void OnBind(object args)
        {
            _session = (args as RunScreenArgs)?.Session ?? Ui.Session;
            if (_session == null)
            {
                Debug.LogError(UiMessages.NoRun);
                return;
            }
            Ui.Session = _session;
            _hud = Root.Q<RunHud>(UiNames.RunHud);
            if (_hud != null) _hud.Menu += OpenPause;
            _board = Root.Q<MapBoard>(UiNames.MapNodes);
            _viewport = Root.Q<ScrollView>(UiNames.MapViewport);
            var climb = Ui.Data.Components.Climb;
            _board.EdgeColor = Ui.TokenColor(TokenKeys.MapEdge);
            _board.TrailColor = Ui.TokenColor(TokenKeys.MapTrail);
            _board.WayColor = Ui.TokenColor(TokenKeys.MapWay);
            _board.EdgeWidth = (float)climb.EdgeWidth;
            _board.DotSpacing = (float)climb.DotSpacing;
            _board.NodePicked += OnNode;
            _zoom = Math.Max(0, Math.Min(climb.ZoomSteps.Count - 1, climb.ZoomDefault));
            Root.Q<LocButton>(UiNames.TrayBack).clicked += CloseTray;
            var enter = EnterButton;
            enter.clicked += () => Travel(_selected, enter);
            Root.Q<LocButton>(UiNames.MapZoomOut).clicked += () => Zoom(-1);
            Root.Q<LocButton>(UiNames.MapZoomIn).clicked += () => Zoom(1);
            Root.Q<LocButton>(UiNames.MapCenter).clicked += CentreOnRun;
            Root.Q<LocButton>(UiNames.MapLegendToggle).clicked += () => ShowLegend(!_legend);
            Root.Q<LocButton>(UiNames.LegendClose).clicked += () => ShowLegend(false);
            var potions = Root.Q<LocButton>(UiNames.MapPotions);
            potions.clicked += () => RunFlow.Refuse(Context, potions, StringKeys.MapRefusalFlasks);
            var action = PlannedAction;
            action.clicked += () => OnPlanned(action);
            ApplyBackground();
            Render();
            Context.Instance.LastFocused = _door != null ? (VisualElement)action : FirstReachable();
            _viewport.contentViewport.RegisterCallback<GeometryChangedEvent>(OnViewportLaidOut);
            _board.RegisterCallback<GeometryChangedEvent>(OnViewportLaidOut);
        }

        /// <summary>The first layout of the viewport and board scrolls the run's position into view (the start row at an act's start).</summary>
        private void OnViewportLaidOut(GeometryChangedEvent evt)
        {
            if (_centred || float.IsNaN(_viewport.contentViewport.layout.height) || _board.layout.height <= _viewport.contentViewport.layout.height * UiMath.Half) return;
            _centred = true;
            CentreOnRun();
        }

        private void ApplyBackground()
        {
            var element = Root.Q(UiNames.MapBackground);
            if (element == null) return;
            var climb = Ui.Data.Components.Climb;
            var id = StringTable.Fill(climb.MapBackground, new StringArgs().Add(UiPlaceholders.Region, _session.Region));
            Ui.ApplyBackground(element, Ui.Art != null && Ui.Art.Get(id) != null ? id : Ui.Data.Components.CombatFallbackBackground);
        }

        // ------------------------------------------------------------------ render

        public void Render()
        {
            _view = ActMapView.Build(_session, Ui.Data);
            _hud?.Bind(RunHudView.Build(_session, Ui.Data));
            if (_hud?.Portrait != null) Ui.ApplyBackground(_hud.Portrait, _session.Portrait);
            Root.Q<LocLabel>(UiNames.MapTitle)?.SetResolved(_view.Title);
            Root.Q<LocLabel>(UiNames.MapSeat)?.SetResolved(_view.SeatText);
            Root.Q<LocLabel>(UiNames.MapFloor)?.SetResolved(_view.FloorText);
            var climb = Ui.Data.Components.Climb;
            var touch = Nav.Layout == null ? 0d : Nav.Layout.ReferenceMinimum(Ui.Data.Layout.MinPhysicalOf(UiKeys.Touch)) * climb.RowTouchRatio;
            _board.RowHeight = (float)Math.Max(climb.RowHeight * climb.ZoomSteps[_zoom], touch);
            _board.Bind(_view);
            if (_selected != null && _view.Node(_selected)?.Reachable != true) _selected = null;
            _board.Select(_selected);
            Root.Q<LocLabel>(UiNames.MapHint)?.SetResolved(Strings.Get(_selected != null ? StringKeys.MapHintSelected : StringKeys.MapHint));
            RenderTray();
            RenderLegend();
            RenderPlanned();
        }

        private VisualElement FirstReachable() =>
            _view?.Nodes.Where(n => n.Reachable).OrderBy(n => n.Floor).ThenBy(n => n.Col).Select(n => (VisualElement)_board.Node(n.Id)).FirstOrDefault();

        private void RenderTray()
        {
            var tray = Root.Q(UiNames.NodeTray);
            var node = _selected != null ? _view.Node(_selected) : null;
            UiDom.Show(tray, _tray && node != null);
            if (node == null) return;
            Root.Q<LocLabel>(UiNames.TrayKind)?.SetResolved(Strings.Format(StringKeys.MapTrayFloor, new StringArgs().Add(UiPlaceholders.Floor, node.Floor)) + Strings.Get(StringKeys.RestTagJoin) + node.Label);
            Root.Q<LocLabel>(UiNames.TrayHint)?.SetResolved(node.Hint);
            var boss = Root.Q<LocLabel>(UiNames.TrayBoss);
            boss?.SetResolved(string.IsNullOrEmpty(node.BossLabel) ? string.Empty : Strings.Format(StringKeys.MapTrayBoss, new StringArgs().Add(UiPlaceholders.Label, node.BossLabel)));
            UiDom.Show(boss, !string.IsNullOrEmpty(node.BossLabel));
            EnterButton.SetResolved(Strings.Format(StringKeys.MapTrayEnter, new StringArgs().Add(UiPlaceholders.Kind, node.Label)));
        }

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
                var glyph = new LocLabel(entry.GlyphKey);
                glyph.AddToClassList(UiClasses.MapNodeGlyph);
                glyph.AddToClassList(UiClasses.MapNodeKindPrefix + entry.Kind);
                row.Add(glyph);
                var label = new LocLabel();
                label.SetResolved(entry.Label);
                label.AddToClassList(UiClasses.ValueText);
                row.Add(label);
                rows.Add(row);
            }
            foreach (var key in new[] { StringKeys.MapLegendCurrent, StringKeys.MapLegendReachable, StringKeys.MapLegendTravelled })
            {
                var cue = new LocLabel(key);
                cue.AddToClassList(UiClasses.LegendCue);
                cue.AddToClassList(UiClasses.ValueText);
                rows.Add(cue);
            }
        }

        private void RenderPlanned()
        {
            var location = _session.Location;
            var planned = location == RunFlowValues.LocationMerchant || location == RunFlowValues.LocationEvent || location == RunFlowValues.LocationDungeon;
            _door = planned ? PlannedDoorView.Build(_session, Ui.Data) : null;
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
            Root.Q<LocLabel>(UiNames.MapHint)?.SetResolved(Strings.Get(StringKeys.MapHintSelected));
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
            Root.Q<LocLabel>(UiNames.MapHint)?.SetResolved(Strings.Get(StringKeys.MapHint));
            FocusSoon(_board.Node(id) ?? FirstReachable());
        }

        /// <summary>Travel to the node (the session saves at once, PF-06 "node entered") and route to its screen.</summary>
        public void Travel(string id, VisualElement anchor)
        {
            if (id == null) return;
            TravelResult result;
            try { result = _session.Travel(id); }
            catch (Exception e)
            {
                SaveFailed(e, () => Travel(id, anchor));
                return;
            }
            if (!result.Travelled)
            {
                RunFlow.Refuse(Context, anchor, string.Format(CultureInfo.InvariantCulture, UiFormats.MapRefusalKey, result.Refusal));
                return;
            }
            RunFlow.Show(Context, _session);
        }

        /// <summary>The planned door's one action: the merchant's Leave; the event's first open choice, then Continue; the dungeon's next step.</summary>
        private void OnPlanned(VisualElement anchor)
        {
            if (_door == null) return;
            try
            {
                switch (_door.Location)
                {
                    case RunFlowValues.LocationMerchant:
                        _session.LeaveMerchant();
                        break;
                    case RunFlowValues.LocationEvent:
                        if (!_session.EventDone)
                        {
                            var chosen = _session.ChooseEvent(_session.EventStandInChoice());
                            if (!chosen.Ok)
                            {
                                RunFlow.Refuse(Context, anchor, chosen.Refusal.Key);
                                return;
                            }
                            Render();
                            FocusSoon(PlannedAction);
                            return;
                        }
                        _session.FinishEvent();
                        break;
                    case RunFlowValues.LocationDungeon:
                        _session.AdvanceDungeon();
                        if (_session.Location == RunFlowValues.LocationDungeon)
                        {
                            Render();
                            FocusSoon(PlannedAction);
                            return;
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                SaveFailed(e, () => OnPlanned(anchor));
                return;
            }
            RunFlow.Show(Context, _session);
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
            FocusSoon(on ? Root.Q(UiNames.LegendClose) : Root.Q(UiNames.MapLegendToggle));
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

        private void OpenPause() => Nav.OpenModal(ScreenIds.Pause, new PauseArgs { Session = _session });

        public override bool HandleBack()
        {
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
            OpenPause();
            return true;
        }

        public override bool HandleMenu()
        {
            OpenPause();
            return true;
        }

        public override void OnReturned()
        {
            if (_session != null && !_session.RunOver) Render();
        }

        public override void Unbind() => _trayShow?.Pause();

        /// <summary>Opens the legend (review captures).</summary>
        public void LegendForReview() => ShowLegend(true);
    }
}
