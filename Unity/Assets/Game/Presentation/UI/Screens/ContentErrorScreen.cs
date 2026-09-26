using System.Collections.Generic;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// W-24 content error (screen:contentError): in dev builds a scrollable ValidationReport (file · row · field ·
    /// rule) with [Copy] and [Quit]; in player builds a safe notice and [Quit].
    /// </summary>
    public sealed class ContentErrorScreen : ScreenView
    {
        protected override void OnBind(object args)
        {
            var report = args as ValidationReport;
            var issues = report != null ? report.Issues : (IReadOnlyList<ValidationIssue>)new List<ValidationIssue>();
            var dev = Ui.DevBuild;
            var shell = Root.Q<Shell>(UiNames.Shell);
            shell.titleKey = StringKeys.ContentErrorTitle;
            shell.exitVisible = false;
            shell.SetFooter(dev ? StringKeys.ContentErrorCopy : null, StringKeys.ContentErrorQuit);
            shell.Back += () => GUIUtility.systemCopyBuffer = report?.Summary(issues.Count) ?? string.Empty;
            shell.Primary += () => Ui.Quit();
            var body = Root.Q<LocLabel>(UiNames.ErrorBody);
            if (body != null) body.stringKey = dev ? StringKeys.ContentErrorBodyDev : StringKeys.ContentErrorBodyPlayer;
            var list = Root.Q<ListView>(UiNames.ReportList);
            if (list == null) return;
            UiDom.Show(list, dev);
            list.makeItem = () =>
            {
                var row = new LocLabel();
                row.AddToClassList(UiClasses.ReportRow);
                row.AddToClassList(UiClasses.ValueText);
                return row;
            };
            list.bindItem = (element, i) =>
            {
                var issue = issues[i];
                ((LocLabel)element).SetResolved(Ui.Data.Strings.Format(string.IsNullOrEmpty(issue.Path) ? StringKeys.ContentErrorRowNoPath : StringKeys.ContentErrorRow, new StringArgs()
                    .Add(UiPlaceholders.File, issue.File).Add(UiPlaceholders.Path, issue.Path)
                    .Add(UiPlaceholders.Rule, issue.Rule).Add(UiPlaceholders.Message, issue.Message)));
            };
            list.itemsSource = (System.Collections.IList)issues;
            list.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight;
        }
    }
}
