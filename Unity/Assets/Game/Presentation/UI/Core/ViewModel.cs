using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Properties;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI
{
    /// <summary>
    /// View-model base (docs/design/09 §5.3): display-ready values only, change notification through Unity 6's
    /// INotifyBindablePropertyChanged so views can use runtime data binding or listen directly. Views never call
    /// the Domain; view-models are filled by screens from Application services.
    /// </summary>
    public abstract class ViewModel : INotifyBindablePropertyChanged
    {
        public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;

        /// <summary>Bumped on every change, so a view can bind one property and redraw.</summary>
        [CreateProperty]
        public int Version { get; private set; }

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string property = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Notify(property);
            return true;
        }

        protected void Notify([CallerMemberName] string property = null)
        {
            Version++;
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(property));
        }

        /// <summary>Runs render now and whenever the view-model changes.</summary>
        public void Observe(Action render)
        {
            propertyChanged += (_, __) => render();
            render();
        }
    }
}
