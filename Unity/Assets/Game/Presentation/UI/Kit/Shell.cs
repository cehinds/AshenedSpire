using System;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// W0 shell (docs/design/04 §0, §1): title top-left, exit [×] top-right, Back bottom-left, Primary bottom-right;
    /// header, body and footer bands. One footer action fills the footer; zero actions hides it. Children added
    /// in UXML go into the body.
    /// </summary>
    [UxmlElement]
    public partial class Shell : VisualElement
    {
        private readonly VisualElement _body;

        public Shell()
        {
            AddToClassList(nameof(Shell).ToLowerInvariant());
            UiDom.CloneTemplate(this, UiResources.KitShell);
            _body = this.Q(UiNames.ShellBody);
            TitleLabel = this.Q<LocLabel>(UiNames.ShellTitle);
            ExitButton = this.Q<LocButton>(UiNames.ShellExit);
            BackButton = this.Q<LocButton>(UiNames.FooterBack);
            PrimaryButton = this.Q<LocButton>(UiNames.FooterPrimary);
            Footer = this.Q(UiNames.ShellFooter);
            if (ExitButton != null) ExitButton.clicked += () => Exit?.Invoke();
            if (BackButton != null) BackButton.clicked += () => Back?.Invoke();
            if (PrimaryButton != null) PrimaryButton.clicked += () => Primary?.Invoke();
        }

        public override VisualElement contentContainer => _body ?? this;

        public LocLabel TitleLabel { get; }
        public LocButton ExitButton { get; }
        public LocButton BackButton { get; }
        public LocButton PrimaryButton { get; }
        public VisualElement Footer { get; }

        public event Action Exit;
        public event Action Back;
        public event Action Primary;

        [UxmlAttribute]
        public string titleKey
        {
            get => TitleLabel?.stringKey;
            set { if (TitleLabel != null) TitleLabel.stringKey = value; }
        }

        [UxmlAttribute]
        public bool exitVisible
        {
            get => ExitButton != null && !ExitButton.ClassListContains(UiClasses.Hidden);
            set => UiDom.Show(ExitButton, value);
        }

        /// <summary>Footer actions by string key; null hides one. One action fills the footer; none hides the footer.</summary>
        public void SetFooter(string backKey, string primaryKey)
        {
            if (BackButton != null) { BackButton.stringKey = backKey; UiDom.Show(BackButton, backKey != null); }
            if (PrimaryButton != null) { PrimaryButton.stringKey = primaryKey; UiDom.Show(PrimaryButton, primaryKey != null); }
            var count = (backKey != null ? 1 : 0) + (primaryKey != null ? 1 : 0);
            Footer?.EnableInClassList(UiClasses.FooterSingle, count == 1);
            Footer?.EnableInClassList(UiClasses.FooterEmpty, count == 0);
        }
    }
}
