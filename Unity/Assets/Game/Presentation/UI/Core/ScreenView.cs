using Ashen.App.Ui;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI
{
    /// <summary>What a screen gets when the Navigator shows it.</summary>
    public sealed class ScreenContext
    {
        public UiHost Host;
        public Navigator Navigator;
        public UiContext Ui;
        public ScreenInstance Instance;

        public VisualElement Root => Instance.Root;
        public ScreenDef Def => Instance.Def;
        public VisualElement Overlay => Navigator.Overlay;
    }

    /// <summary>A screen's view: binds its UXML to a view-model and turns input into commands.</summary>
    public interface IScreenView
    {
        void Bind(ScreenContext context, object args);

        /// <summary>The screen is on top again (a screen above it closed); refresh from services.</summary>
        void OnReturned();

        /// <summary>Escape / pad B reached this screen with nothing inner open. True when the screen handled it.</summary>
        bool HandleBack();

        /// <summary>True while the screen takes no focus or navigation (the title gate).</summary>
        bool InputBlocked { get; }

        /// <summary>Retained for the screens that override it; the input layer no longer raises it (the web has no pad-Start door on the screens that are not the fight or the map: the Menu action is a HandleHotkey).</summary>
        bool HandleMenu();

        /// <summary>A veil stands over the screen's own surface (a pile, a prompt, a planned door; web veilIsOpen): every screen hotkey is swallowed while it does.</summary>
        bool VeilOpen { get; }

        /// <summary>
        /// The input layer resolved a key, a pad button or a hint chip to something this screen's hotkey scope names (ui/controls.json hotkeys.scopes): a registry action, a screen command or a positional card key.
        /// True when the screen did it.
        /// </summary>
        bool HandleHotkey(HotkeyResult result);

        /// <summary>The screen holds the bumpers and [ ] for a tab set while it is open (web setTabRing).</summary>
        bool HasTabRing { get; }

        /// <summary>Steps the tab set one place (-1 previous, 1 next), wrapping at both ends.</summary>
        void StepTabs(int delta);

        /// <summary>The screens.json focusModes entry in force (null = the screen's focusOrder).</summary>
        string FocusMode { get; }

        void Unbind();
    }

    /// <summary>Convenience base for screen views.</summary>
    public abstract class ScreenView : IScreenView
    {
        protected ScreenContext Context { get; private set; }
        protected VisualElement Root => Context.Root;
        protected UiContext Ui => Context.Ui;
        protected Navigator Nav => Context.Navigator;

        public void Bind(ScreenContext context, object args)
        {
            Context = context;
            OnBind(args);
        }

        protected abstract void OnBind(object args);

        public virtual void OnReturned() { }

        public virtual bool HandleBack() => false;

        public virtual bool InputBlocked => false;

        public virtual bool HandleMenu() => false;

        public virtual bool VeilOpen => false;

        public virtual bool HandleHotkey(HotkeyResult result) => false;

        public virtual bool HasTabRing => false;

        public virtual void StepTabs(int delta) { }

        public virtual string FocusMode => null;

        public virtual void Unbind() { }
    }
}
