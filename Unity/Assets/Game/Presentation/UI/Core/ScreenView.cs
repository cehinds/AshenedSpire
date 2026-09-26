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

        /// <summary>Pad Start / the menu intent reached this screen. True when the screen handled it (combat opens pause).</summary>
        bool HandleMenu();

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

        public virtual string FocusMode => null;

        public virtual void Unbind() { }
    }
}
