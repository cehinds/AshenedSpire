using System;
using System.IO;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The About text (web src/ui/screens/about.js renderAboutSection): the lead in paragraphs, each section as an eyebrow over its heading and its paragraph, Save this to a file and the two build lines. Saving writes the
    /// acknowledgement as plain text into the player's data folder (the web downloads it) and says where.
    /// </summary>
    internal static class AboutPanel
    {
        public static void Fill(VisualElement host, AboutViewState view, StringTable strings)
        {
            foreach (var paragraph in view.Lead) Add(host, paragraph, FrontClasses.AboutBody, false);
            foreach (var section in view.Sections)
            {
                Add(host, section.Eyebrow, FrontClasses.AboutEyebrow, true);
                Add(host, section.Heading, FrontClasses.AboutHeading, false);
                Add(host, section.Body, FrontClasses.AboutBody, false);
            }
            var save = new LocButton();
            save.AddToClassList(FrontClasses.AboutSave);
            save.SetResolved(view.SaveLabel);
            host.Add(save);
            var result = Add(host, string.Empty, FrontClasses.AboutResult, false);
            save.clicked += () => result.SetResolved(Save(view, strings));
            var build = Add(host, view.Build, FrontClasses.AboutBuild, false);
            build.AddToClassList(FrontClasses.AboutBuildFirst);
            Add(host, view.Provenance, FrontClasses.AboutBuild, false);
        }

        private static string Save(AboutViewState view, StringTable strings)
        {
            try
            {
                var folder = Path.Combine(Application.persistentDataPath, UiResources.ExportsFolder);
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, view.FileName);
                File.WriteAllText(path, view.FileText);
                return strings.Format(StringKeys.AboutSaved, new StringArgs().Add(UiPlaceholders.Path, path));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is NotSupportedException)
            {
                return strings.Get(StringKeys.AboutSaveFailed);
            }
        }

        private static LocLabel Add(VisualElement host, string text, string cls, bool upper)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            label.AddToClassList(cls);
            if (upper) label.AddToClassList(UiClasses.Upper);
            label.SetResolved(text);
            host.Add(label);
            return label;
        }
    }
}
