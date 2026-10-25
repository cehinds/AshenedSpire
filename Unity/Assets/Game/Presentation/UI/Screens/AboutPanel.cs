using Ashen.App.Run;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>The About text as the web draws it (settings → Advanced → About; src/ui/screens/about.js): the eyebrow, the heading, the owner's acknowledgement, the credits and the build.</summary>
    internal static class AboutPanel
    {
        public static void Fill(VisualElement host, AboutViewState view)
        {
            Add(host, view.Eyebrow, FrontClasses.AboutEyebrow, true);
            Add(host, view.Heading, FrontClasses.AboutHeading, false);
            Add(host, view.Body, FrontClasses.AboutBody, false);
            Add(host, view.CreditsHeading, FrontClasses.AboutCreditsHeading, false);
            foreach (var credit in view.Credits) Add(host, credit, FrontClasses.AboutCredit, false);
            Add(host, view.Build, FrontClasses.AboutBuild, true);
        }

        private static void Add(VisualElement host, string text, string cls, bool upper)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            label.AddToClassList(cls);
            if (upper) label.AddToClassList(UiClasses.Upper);
            label.SetResolved(text);
            host.Add(label);
        }
    }
}
