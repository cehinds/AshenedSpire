using System;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// The web's modal shell for the combat overlays (VP-6, D-800; src/ui/components/modalShell.js, styles/kit.css:440-497): a framed panel with a head (eyebrow over a gold title, an
    /// optional status such as "5 cards", the close glyph), a scrolling body and a foot of buttons. The kit only draws; the overlay that fills it decides what Escape and the close glyph do
    /// (<see cref="CloseRequested"/>). The body's scroll view and its bars never take focus themselves: the focus model walks the controls inside.
    /// </summary>
    [UxmlElement]
    public partial class CombatModal : VisualElement
    {
        private readonly LocLabel _eyebrow;
        private readonly LocLabel _title;
        private readonly LocLabel _status;
        private readonly LocButton _close;
        private readonly ScrollView _scroll;
        private readonly VisualElement _foot;
        private string _size;

        public CombatModal()
        {
            AddToClassList(nameof(CombatModal).ToLowerInvariant());
            AddToClassList(UiClasses.Cmodal);
            AddToClassList(UiClasses.Frame);
            AddToClassList(UiClasses.FrameModal);
            UiDom.CloneTemplate(this, UiResources.KitCombatModal);
            _eyebrow = this.Q<LocLabel>(UiNames.ModalEyebrow);
            _title = this.Q<LocLabel>(UiNames.ModalTitle);
            _status = this.Q<LocLabel>(UiNames.ModalStatus);
            _close = this.Q<LocButton>(UiNames.ModalClose);
            _scroll = this.Q<ScrollView>(UiNames.ModalBody);
            _foot = this.Q(UiNames.ModalFoot);
            if (_close != null) _close.clicked += () => CloseRequested?.Invoke();
            if (_scroll != null)
            {
                _scroll.focusable = false;
                _scroll.contentViewport.focusable = false;
                _scroll.horizontalScroller.slider.focusable = false;
                _scroll.verticalScroller.slider.focusable = false;
            }
            size = UiValues.ModalSizeMd;
        }

        /// <summary>The close glyph was pressed (the overlay closes itself, or answers its question with "no").</summary>
        public event Action CloseRequested;

        /// <summary>The body's content container: add the modal's content here.</summary>
        public VisualElement Body => _scroll?.contentContainer ?? this;

        public ScrollView BodyScroll => _scroll;

        /// <summary>The foot: add its buttons here.</summary>
        public VisualElement Foot => _foot ?? this;

        public LocButton CloseButton => _close;

        /// <summary>The width rung (UiValues.ModalSizeMd, ModalSizeXl).</summary>
        [UxmlAttribute]
        public string size
        {
            get => _size;
            set
            {
                if (_size != null) RemoveFromClassList(UiClasses.CmodalSizePrefix + _size);
                _size = value;
                if (_size != null) AddToClassList(UiClasses.CmodalSizePrefix + _size);
            }
        }

        /// <summary>Sets the head: an empty eyebrow or status hides its line.</summary>
        public void SetHead(string eyebrow, string title, string status = null)
        {
            _eyebrow?.SetResolved(eyebrow ?? string.Empty);
            UiDom.Show(_eyebrow, !string.IsNullOrEmpty(eyebrow));
            _title?.SetResolved(title ?? string.Empty);
            _status?.SetResolved(status ?? string.Empty);
            UiDom.Show(_status, !string.IsNullOrEmpty(status));
        }

        /// <summary>Empties the body and the foot for the next content.</summary>
        public void Reset()
        {
            Body.Clear();
            Foot.Clear();
            if (_scroll != null) _scroll.scrollOffset = UnityEngine.Vector2.zero;
        }
    }
}
