using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Screens;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI
{
    /// <summary>A screen on the Navigator's stack.</summary>
    public sealed class ScreenInstance
    {
        public ScreenDef Def;
        public VisualElement Root;
        public IScreenView View;
        public FocusModel Focus;
        public VisualElement LastFocused;
        public object Args;

        public string Id => Def.Id;
    }

    /// <summary>
    /// Screen stack and layout (docs/design/06 PF-08, 09 §5.3). Screens come from ui/screens.json: the UXML is
    /// instantiated from Resources, its background comes from the registry, and its view comes from the
    /// ScreenFactory. The Navigator keeps the height band (.band-standard / .band-compact / .band-gate) and layout
    /// mode (.layout-wide / .layout-narrow) classes on the root, swaps the wide and narrow PanelSettings by layout
    /// mode, enforces the physical minimums after scaling, and routes the one focus model: move, cancel (innermost
    /// layer first), and focus return after a screen or modal closes.
    /// </summary>
    public sealed class Navigator
    {
        private readonly UiHost _host;
        private readonly List<ScreenInstance> _stack = new List<ScreenInstance>();
        private ConditionalWeakTable<VisualElement, StrongBox<float>> _baseFontSize = new ConditionalWeakTable<VisualElement, StrongBox<float>>();
        private IPanel _panel;
        private float _suppressSubmitUntil;
        private int _routedSubmits;

        public Navigator(UiHost host, VisualElement root)
        {
            _host = host;
            Root = root;
            ScreenLayer = new VisualElement { name = UiNames.ScreenLayer, pickingMode = PickingMode.Ignore };
            ModalLayer = new VisualElement { name = UiNames.ModalLayer, pickingMode = PickingMode.Ignore };
            Overlay = new VisualElement { name = UiNames.OverlayLayer, pickingMode = PickingMode.Ignore };
            foreach (var layer in new[] { ScreenLayer, ModalLayer, Overlay })
            {
                layer.AddToClassList(UiClasses.Screen);
                Root.Add(layer);
            }
            Root.RegisterCallback<AttachToPanelEvent>(_ => HookPanel());
            Root.RegisterCallback<DetachFromPanelEvent>(_ => UnhookPanel());
            Root.RegisterCallback<GeometryChangedEvent>(_ => ApplyMinimums());
            HookPanel();
        }

        public VisualElement Root { get; }
        public VisualElement ScreenLayer { get; }
        public VisualElement ModalLayer { get; }
        public VisualElement Overlay { get; }
        public LayoutState Layout { get; private set; }

        public event Action<ScreenInstance> ScreenShown;

        public IReadOnlyList<ScreenInstance> Stack => _stack;
        public ScreenInstance Top => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;
        public string CurrentId => Top?.Id;

        /// <summary>The top non-modal screen.</summary>
        public ScreenInstance TopScreen => _stack.LastOrDefault(s => !s.Def.IsModal);

        private UiContext Ui => _host.Context;

        // ------------------------------------------------------------------ stack

        public ScreenInstance Go(string id, object args = null, bool replace = false)
        {
            var def = Ui.Data.Screens.Get(id);
            if (def == null) { Debug.LogError(Format(UiMessages.UnknownScreen, id)); return null; }
            if (!def.IsBuilt) { Debug.LogWarning(Format(UiMessages.PlannedScreen, id)); return null; }
            if (def.IsModal) return OpenModal(id, args);

            var previous = Top;
            if (previous != null) previous.LastFocused = previous.Focus.Focused ?? previous.LastFocused;
            if (replace)
            {
                while (_stack.Count > 0) Remove(_stack[_stack.Count - 1]);
            }
            foreach (var s in _stack) UiDom.Show(s.Root, false);
            var instance = Instantiate(def, args, ScreenLayer);
            Activate(instance);
            return instance;
        }

        public ScreenInstance OpenModal(string id, object args)
        {
            var def = Ui.Data.Screens.Get(id);
            if (def == null) { Debug.LogError(Format(UiMessages.UnknownScreen, id)); return null; }
            var below = Top;
            if (below != null) below.LastFocused = below.Focus.Focused ?? below.LastFocused;
            var instance = Instantiate(def, args, ModalLayer);
            instance.Root.AddToClassList(UiClasses.ScreenModal);
            Activate(instance);
            return instance;
        }

        /// <summary>Closes the top screen or modal and returns focus to the control that opened it.</summary>
        public void Pop()
        {
            if (_stack.Count <= 1) return;
            Remove(Top);
            var top = Top;
            UiDom.Show(top.Root, true);
            if (!top.Def.IsModal) foreach (var s in _stack.Where(s => s != top && !s.Def.IsModal)) UiDom.Show(s.Root, false);
            top.View.OnReturned();
            FocusSoon(top);
            ScreenShown?.Invoke(top);
        }

        public void Close(ScreenInstance instance)
        {
            if (instance == Top) Pop();
            else if (instance != null && _stack.Contains(instance)) Remove(instance);
        }

        /// <summary>Follows a named transition of the current screen (ui/screens.json).</summary>
        public ScreenInstance Fire(string trigger, object args = null)
        {
            var t = TopScreen?.Def.Transition(trigger);
            return t == null ? null : Go(t.To, args, t.Replaces);
        }

        /// <summary>Escape / pad B: close the innermost layer (open selector list, then modal, then the screen's back action).</summary>
        public void Back()
        {
            var top = Top;
            if (top == null) return;
            var open = top.Root.Q(className: UiClasses.NavOpen);
            if (open != null) { open.RemoveFromClassList(UiClasses.NavOpen); return; }
            if (top.View.HandleBack()) return;
            if (top.Def.Back == UiValues.BackClose || top.Def.Back == UiValues.BackPop) Pop();
        }

        private ScreenInstance Instantiate(ScreenDef def, object args, VisualElement layer)
        {
            var tree = Resources.Load<VisualTreeAsset>(def.Uxml);
            VisualElement root;
            if (tree == null)
            {
                Debug.LogError(Format(UiMessages.MissingTemplate, def.Uxml));
                root = new VisualElement();
            }
            else root = tree.Instantiate();
            root.AddToClassList(UiClasses.Screen);
            root.pickingMode = PickingMode.Position;
            if (def.Background != null)
            {
                var bg = new VisualElement { name = UiNames.Background, pickingMode = PickingMode.Ignore };
                bg.AddToClassList(UiClasses.ScreenBackground);
                Ui.ApplyBackground(bg, def.Background);
                root.Insert(0, bg);
            }
            layer.Add(root);
            var instance = new ScreenInstance { Def = def, Root = root, Args = args };
            instance.Focus = new FocusModel(instance);
            instance.View = ScreenFactory.Create(def.Id);
            _stack.Add(instance);
            instance.View.Bind(new ScreenContext { Host = _host, Navigator = this, Ui = Ui, Instance = instance }, args);
            return instance;
        }

        private void Activate(ScreenInstance instance)
        {
            instance.Root.schedule.Execute(ApplyMinimums).Every(0).ForDuration(Ui.Data.Tokens.Duration(TokenKeys.FocusSettle));
            FocusSoon(instance);
            ScreenShown?.Invoke(instance);
        }

        private void Remove(ScreenInstance instance)
        {
            instance.View.Unbind();
            instance.Root.RemoveFromHierarchy();
            _stack.Remove(instance);
        }

        /// <summary>
        /// Focus once styles and layout have caught up (a screen that was just shown or un-hidden resolves its
        /// display state a frame later): retry each frame for the focusSettle token until an item takes focus.
        /// </summary>
        private void FocusSoon(ScreenInstance instance, VisualElement preferred = null, long delayMs = 0)
        {
            if (!instance.Def.AcceptsInput) return;
            IVisualElementScheduledItem item = null;
            item = instance.Root.schedule.Execute(() =>
            {
                if (instance != Top || instance.View.InputBlocked) { item?.Pause(); return; }
                if (instance.Focus.FocusInitial(preferred ?? instance.LastFocused) != null) item?.Pause();
            }).StartingIn(delayMs).Every(0).ForDuration(Ui.Data.Tokens.Duration(TokenKeys.FocusSettle) + delayMs);
        }

        /// <summary>Re-focus the top screen's first item (e.g. after the title gate lifts).</summary>
        public void FocusTop(VisualElement preferred = null, long delayMs = 0)
        {
            var top = Top;
            if (top != null) FocusSoon(top, preferred, delayMs);
        }

        /// <summary>Ignore Submit until the given realtime (the key that lifted the gate must not also press a menu row).</summary>
        public void SuppressSubmit(float seconds) => _suppressSubmitUntil = Time.realtimeSinceStartup + seconds;

        // ------------------------------------------------------------------ input routing (US-17.1)

        private void HookPanel()
        {
            var panel = Root.panel;
            if (panel == null || panel == _panel) return;
            UnhookPanel();
            _panel = panel;
            _panel.visualTree.RegisterCallback<NavigationMoveEvent>(OnMove, TrickleDown.TrickleDown);
            _panel.visualTree.RegisterCallback<NavigationCancelEvent>(OnCancel, TrickleDown.TrickleDown);
            _panel.visualTree.RegisterCallback<NavigationSubmitEvent>(OnSubmit, TrickleDown.TrickleDown);
        }

        private void UnhookPanel()
        {
            if (_panel == null) return;
            _panel.visualTree.UnregisterCallback<NavigationMoveEvent>(OnMove, TrickleDown.TrickleDown);
            _panel.visualTree.UnregisterCallback<NavigationCancelEvent>(OnCancel, TrickleDown.TrickleDown);
            _panel.visualTree.UnregisterCallback<NavigationSubmitEvent>(OnSubmit, TrickleDown.TrickleDown);
            _panel = null;
        }

        // UI Toolkit's own navigation events are swallowed: the InputRouter is the one source of Move, Submit and
        // Cancel, so keyboard, pad and the editor behave the same and nothing is handled twice. A Submit passes
        // only when the router sent it.
        private void OnMove(NavigationMoveEvent evt)
        {
            evt.StopImmediatePropagation();
            _panel?.focusController?.IgnoreEvent(evt);
        }

        private void OnCancel(NavigationCancelEvent evt)
        {
            evt.StopImmediatePropagation();
            _panel?.focusController?.IgnoreEvent(evt);
        }

        private void OnSubmit(NavigationSubmitEvent evt)
        {
            if (_routedSubmits > 0)
            {
                _routedSubmits--;
                return;
            }
            evt.StopImmediatePropagation();
            _panel?.focusController?.IgnoreEvent(evt);
        }

        /// <summary>Router Move: next (+1) or previous (-1) in the top screen's focus order.</summary>
        public void MoveFocus(int delta)
        {
            var top = Top;
            if (top == null || !top.Def.AcceptsInput || top.View.InputBlocked) return;
            top.Focus.Move(delta);
        }

        /// <summary>Router Submit: activates the focused control (buttons, slot rows, hold buttons).</summary>
        public void Submit()
        {
            var top = Top;
            if (top == null || !top.Def.AcceptsInput || top.View.InputBlocked) return;
            if (Time.realtimeSinceStartup < _suppressSubmitUntil) return;
            var focused = top.Focus.Focused;
            if (focused == null)
            {
                top.Focus.FocusInitial(top.LastFocused);
                return;
            }
            _routedSubmits++;
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = focused;
                focused.SendEvent(e);
            }
        }

        // ------------------------------------------------------------------ layout (D-001, D-016)

        /// <summary>Classify the viewport; set band and layout classes; ask the host to swap PanelSettings when the mode changes.</summary>
        public void ApplyLayout(int width, int height)
        {
            if (Layout != null && Layout.ViewportWidth == width && Layout.ViewportHeight == height) return;
            var state = LayoutClassifier.Classify(width, height, Ui.Data.Layout);
            var modeChanged = Layout == null || Layout.Narrow != state.Narrow;
            Layout = state;
            Root.EnableInClassList(UiClasses.BandStandard, state.Band == HeightBand.Standard);
            Root.EnableInClassList(UiClasses.BandCompact, state.Band == HeightBand.Compact);
            Root.EnableInClassList(UiClasses.BandGate, state.Band == HeightBand.Gate);
            Root.EnableInClassList(UiClasses.LayoutNarrow, state.Narrow);
            Root.EnableInClassList(UiClasses.LayoutWide, !state.Narrow);
            if (modeChanged) _host.UsePanel(state.Narrow);
            ResetMinimumText();
            ApplyMinimums();
        }

        /// <summary>layout.json#minPhysical after scaling: touch targets never below the physical minimum, value and header text never below theirs.</summary>
        public void ApplyMinimums()
        {
            if (Layout == null) return;
            var rules = Ui.Data.Layout;
            var touch = (float)Layout.ReferenceMinimum(rules.MinPhysicalOf(UiKeys.Touch));
            Root.Query(className: UiClasses.Touch).ForEach(e =>
            {
                e.style.minHeight = touch;
                e.style.minWidth = touch;
            });
            MinimumText(UiClasses.ValueText, (float)Layout.ReferenceMinimum(rules.MinPhysicalOf(UiKeys.ValueText)));
            MinimumText(UiClasses.HeaderText, (float)Layout.ReferenceMinimum(rules.MinPhysicalOf(UiKeys.HeaderText)));
        }

        /// <summary>
        /// Raise text below its physical minimum. The declared size is read once the element has been laid out (so
        /// its style is resolved) and kept; an element at or above the minimum keeps its style sheet size.
        /// </summary>
        private void MinimumText(string className, float minimum)
        {
            Root.Query(className: className).ForEach(e =>
            {
                if (!_baseFontSize.TryGetValue(e, out var baseSize))
                {
                    var resolved = e.resolvedStyle.fontSize;
                    if (float.IsNaN(e.layout.width) || float.IsNaN(resolved) || resolved <= 0f) return;
                    baseSize = new StrongBox<float>(resolved);
                    _baseFontSize.Add(e, baseSize);
                }
                if (baseSize.Value >= minimum) e.style.fontSize = StyleKeyword.Null;
                else e.style.fontSize = minimum;
            });
        }

        /// <summary>A band or mode change can change declared sizes: drop the overrides and re-read after the restyle.</summary>
        private void ResetMinimumText()
        {
            foreach (var className in new[] { UiClasses.ValueText, UiClasses.HeaderText })
                Root.Query(className: className).ForEach(e => e.style.fontSize = StyleKeyword.Null);
            _baseFontSize = new ConditionalWeakTable<VisualElement, StrongBox<float>>();
            Root.schedule.Execute(ApplyMinimums).StartingIn(Ui.Data.Tokens.Duration(TokenKeys.FocusSettle));
        }

        private static string Format(string template, object arg) => string.Format(CultureInfo.InvariantCulture, template, arg);
    }
}
