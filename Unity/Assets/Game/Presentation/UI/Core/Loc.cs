using Ashen.App.Ui;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI
{
    /// <summary>The active string table (docs/design/08 §9). Set once at boot; LocLabel and LocButton read it.</summary>
    public static class Loc
    {
        public static StringTable Table { get; set; }

        public static string Get(string key) => Table != null ? Table.Get(key) : key ?? string.Empty;

        public static string Format(string key, StringArgs args) => Table != null ? Table.Format(key, args) : key ?? string.Empty;
    }

    /// <summary>A label whose text is a string key (Unity 6 drops undeclared UXML attributes, so the key is a declared attribute: string-key).</summary>
    [UxmlElement]
    public partial class LocLabel : Label
    {
        private string _key;

        [UxmlAttribute]
        public string stringKey
        {
            get => _key;
            set
            {
                _key = value;
                Refresh();
            }
        }

        public LocLabel()
        {
            RegisterCallback<AttachToPanelEvent>(_ => Refresh());
        }

        public LocLabel(string key) : this() => stringKey = key;

        public void Refresh()
        {
            if (!string.IsNullOrEmpty(_key)) text = Loc.Get(_key);
        }

        /// <summary>Shows an already-resolved string (a filled template) and forgets the key.</summary>
        public void SetResolved(string value)
        {
            _key = null;
            text = value;
        }
    }

    /// <summary>A button whose label is a string key.</summary>
    [UxmlElement]
    public partial class LocButton : Button
    {
        private string _key;

        [UxmlAttribute]
        public string stringKey
        {
            get => _key;
            set
            {
                _key = value;
                Refresh();
            }
        }

        public LocButton()
        {
            AddToClassList(Ashen.Generated.UiClasses.Button);
            AddToClassList(Ashen.Generated.UiClasses.Touch);
            AddToClassList(Ashen.Generated.UiClasses.ValueText);
            RegisterCallback<AttachToPanelEvent>(_ => Refresh());
        }

        public LocButton(string key) : this() => stringKey = key;

        public void Refresh()
        {
            if (!string.IsNullOrEmpty(_key)) text = Loc.Get(_key);
        }

        public void SetResolved(string value)
        {
            _key = null;
            text = value;
        }
    }
}
