using System.Globalization;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI
{
    /// <summary>Small UI Toolkit helpers shared by the kit and the screens.</summary>
    public static class UiDom
    {
        /// <summary>Clones a kit UXML template (and its style sheets) into host. Missing templates log and leave host empty.</summary>
        public static void CloneTemplate(VisualElement host, string resourcePath)
        {
            var tree = Resources.Load<VisualTreeAsset>(resourcePath);
            if (tree == null)
            {
                Debug.LogWarning(string.Format(CultureInfo.InvariantCulture, UiMessages.MissingTemplate, resourcePath));
                return;
            }
            tree.CloneTree(host);
        }

        public static void Show(VisualElement e, bool visible)
        {
            if (e == null) return;
            e.EnableInClassList(UiClasses.Hidden, !visible);
        }

        public static bool IsShown(VisualElement e, VisualElement stopAt = null)
        {
            for (var v = e; v != null && v != stopAt; v = v.parent)
            {
                if (v.ClassListContains(UiClasses.Hidden)) return false;
                if (v.resolvedStyle.display == DisplayStyle.None || v.resolvedStyle.visibility == Visibility.Hidden) return false;
            }
            return true;
        }

        public static T Q<T>(VisualElement root, string name) where T : VisualElement => root?.Q<T>(name);

        public static float Percent(double fraction) => (float)(fraction * UiMath.Percent);

        public static string Pct(double fraction) =>
            System.Math.Floor(fraction * UiMath.Percent).ToString(UiFormats.Integer, CultureInfo.InvariantCulture);
    }
}
