using System;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// Meter (docs/design/04 §1): value over max, with the track length max/referenceMax so bigger pools read
    /// bigger; the kind (ui/components.json meter.kinds) picks the colour class meter--kind; an optional glyph and
    /// a value text.
    /// </summary>
    [UxmlElement]
    public partial class Meter : VisualElement
    {
        private readonly LocLabel _glyph;
        private readonly VisualElement _track;
        private readonly VisualElement _fill;
        private readonly LocLabel _value;
        private string _kind;
        private int _current;
        private int _max;
        private int _referenceMax;

        public Meter()
        {
            AddToClassList(nameof(Meter).ToLowerInvariant());
            UiDom.CloneTemplate(this, UiResources.KitMeter);
            _glyph = this.Q<LocLabel>(UiNames.MeterGlyph);
            _track = this.Q(UiNames.MeterTrack);
            _fill = this.Q(UiNames.MeterFill);
            _value = this.Q<LocLabel>(UiNames.MeterValue);
            Render();
        }

        [UxmlAttribute]
        public string kind
        {
            get => _kind;
            set
            {
                if (_kind != null) RemoveFromClassList(UiClasses.MeterKindPrefix + _kind);
                _kind = value;
                if (_kind != null) AddToClassList(UiClasses.MeterKindPrefix + _kind);
            }
        }

        [UxmlAttribute]
        public string glyphKey
        {
            get => _glyph?.stringKey;
            set
            {
                if (_glyph == null) return;
                _glyph.stringKey = value;
                UiDom.Show(_glyph, !string.IsNullOrEmpty(value));
            }
        }

        [UxmlAttribute]
        public int value { get => _current; set { _current = value; Render(); } }

        [UxmlAttribute]
        public int max { get => _max; set { _max = value; Render(); } }

        /// <summary>The max that fills the whole track (0 = this meter's own max).</summary>
        [UxmlAttribute]
        public int referenceMax { get => _referenceMax; set { _referenceMax = value; Render(); } }

        /// <summary>Hide the numeric text (the boot bar shows its own count).</summary>
        [UxmlAttribute]
        public bool showValue { get; set; } = true;

        public void Set(int current, int maximum, int reference = 0)
        {
            _current = current;
            _max = maximum;
            _referenceMax = reference;
            Render();
        }

        private void Render()
        {
            var reference = _referenceMax > 0 ? _referenceMax : _max;
            var track = reference > 0 ? Math.Min(1d, (double)_max / reference) : 1d;
            var fill = _max > 0 ? Math.Max(0d, Math.Min(1d, (double)_current / _max)) : 0d;
            if (_track != null) _track.style.width = Length.Percent(UiDom.Percent(track));
            if (_fill != null) _fill.style.width = Length.Percent(UiDom.Percent(fill));
            if (_value == null) return;
            UiDom.Show(_value, showValue);
            _value.SetResolved(Loc.Format(StringKeys.MeterValue, new StringArgs().Add(UiPlaceholders.Value, _current).Add(UiPlaceholders.Max, _max)));
        }
    }
}
