using System;
using System.Linq;
using System.Reflection;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;
using N = Ashen.Generated.LinkTagNames;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// The pointer over a <c>&lt;link&gt;</c> run of a rich text label (P-5, D-1592). Unity 6 text elements raise <c>PointerOverLinkTagEvent</c> and <c>PointerOutLinkTagEvent</c> (member <c>linkID</c>, a string;
    /// the manual's "Add hyperlinks in text" page, where they sit in <c>UnityEngine.UIElements.Experimental</c> and are marked experimental). The branch is compiled against reference assemblies that may predate
    /// them, so the types are looked up by name when the label is built and registered through <c>RegisterCallback&lt;T&gt;</c> by reflection: where they do not exist, <see cref="Register"/> returns false and the
    /// caller keeps hovering the whole label. Nothing here changes what the rules say.
    /// </summary>
    public static class LinkTagEvents
    {
        private static readonly string[] Namespaces = { N.Namespace, N.ExperimentalNamespace };

        private static Type Find(string name)
        {
            var assembly = typeof(VisualElement).Assembly;
            foreach (var space in Namespaces)
            {
                var type = assembly.GetType(space + N.NamespaceJoin + name);
                if (type != null) return type;
            }
            return null;
        }

        /// <summary>The bridge from a typed event to the caller's action: the event's <c>linkID</c> is read by name.</summary>
        private sealed class Bridge<T> where T : class
        {
            public Action<string> Act;
            public PropertyInfo Id;

            public void On(T evt) => Act?.Invoke(Id.GetValue(evt) as string);
        }

        /// <summary>True when the platform has the events and both are registered on <paramref name="label"/>: <paramref name="over"/> and <paramref name="leave"/> get the id of the link under the pointer.</summary>
        public static bool Register(VisualElement label, Action<string> over, Action<string> leave)
        {
            try
            {
                var overType = Find(N.OverEvent);
                var outType = Find(N.OutEvent);
                if (overType == null || outType == null) return false;
                return Add(label, overType, over) && Add(label, outType, leave);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool Add(VisualElement label, Type eventType, Action<string> act)
        {
            var id = eventType.GetProperty(N.LinkId, BindingFlags.Public | BindingFlags.Instance);
            var register = typeof(CallbackEventHandler).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == N.RegisterMethod && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == typeof(Bridge<>).GetGenericArguments().Length && m.GetParameters().Last().ParameterType == typeof(TrickleDown));
            if (id == null || register == null) return false;
            var bridgeType = typeof(Bridge<>).MakeGenericType(eventType);
            var bridge = Activator.CreateInstance(bridgeType);
            bridgeType.GetField(nameof(Bridge<object>.Act)).SetValue(bridge, act);
            bridgeType.GetField(nameof(Bridge<object>.Id)).SetValue(bridge, id);
            var callback = Delegate.CreateDelegate(typeof(EventCallback<>).MakeGenericType(eventType), bridge, bridgeType.GetMethod(N.BridgeMethod));
            register.MakeGenericMethod(eventType).Invoke(label, new object[] { callback, TrickleDown.NoTrickleDown });
            return true;
        }
    }
}
