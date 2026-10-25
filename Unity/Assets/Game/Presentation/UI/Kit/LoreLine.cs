using System;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// How card lore is set (web models/LoreTypeModel.js applyLoreType, read through styles/kit.css :root[data-lore-*]): the Typeface, Size, Letter spacing, Line spacing and the two slants the player chose in Settings, as classes and
    /// sizes on each lore label drawn from now on. <see cref="Current"/> is stamped by the settings applier; a screen that draws lore reads it as it draws, so a change shows the next time the screen is drawn.
    /// </summary>
    public static class LoreStyle
    {
        /// <summary>The lists and scales (ui/lore.json), set with the UI data.</summary>
        public static LoreLayout Layout;

        /// <summary>The player's choices as the line is set; null until the applier ran (the defaults).</summary>
        public static LoreType Current;

        private static LoreType Resolved => Current ?? Layout?.Resolve(null, null, null, null, null, null);

        private static void Face(Label label, bool italic)
        {
            if (Layout == null) return;
            foreach (var face in Layout.Faces)
            {
                var name = face == Layout.DefaultFace ? LoreClasses.Fell : LoreClasses.FacePrefix + face;
                label.RemoveFromClassList(name);
                label.RemoveFromClassList(name + LoreClasses.ItalicSuffix);
            }
            label.RemoveFromClassList(LoreClasses.FellItalic);
            var type = Resolved;
            var shown = type.Face == Layout.DefaultFace ? LoreClasses.Fell : LoreClasses.FacePrefix + type.Face;
            label.AddToClassList(italic ? (type.Face == Layout.DefaultFace ? LoreClasses.FellItalic : shown + LoreClasses.ItalicSuffix) : shown);
        }

        /// <summary>The identity line of card inspection: its face, slant and size, one line.</summary>
        public static void Identity(Label label)
        {
            var type = Resolved;
            if (type == null) return;
            Face(label, type.IdentityItalic);
            label.style.fontSize = (float)type.IdentityPx;
            label.style.letterSpacing = (float)(type.TrackingEm * type.IdentityPx);
        }

        /// <summary>A paragraph of the lore window: its face, slant, size (the identity paragraph a little larger) and line height.</summary>
        public static void Window(Label label, bool identity)
        {
            var type = Resolved;
            if (type == null) return;
            Face(label, identity ? type.IdentityItalic : type.WindowItalic);
            var px = identity ? type.WindowIdentityPx : type.WindowPx;
            label.style.fontSize = (float)px;
            label.style.letterSpacing = (float)(type.TrackingEm * px);
            label.enableRichText = true;
            label.text = string.Format(System.Globalization.CultureInfo.InvariantCulture, LoreFormats.LineHeight, (int)Math.Round(type.LeadingOfNatural * UiMath.Percent)) + label.text;
        }
    }

    /// <summary>
    /// The lore line of an inspection (web ui/components/loreLine.js loreLine): the identity line of a piece's lore, on one line with an ellipsis, and, when the lore has more to say, a Read cue; pressing it opens the lore window over
    /// the inspection (<see cref="Window"/>). Lore with nothing behind it is plain text.
    /// </summary>
    public static class LoreLine
    {
        public static VisualElement Build(StringTable strings, string text, Action<LoreParts> openWindow, string key = null)
        {
            var parts = LoreParts.Split(text);
            if (parts.Identity.Length == 0) return null;
            var more = parts.HasMore && openWindow != null;
            var line = new VisualElement { focusable = more, pickingMode = more ? PickingMode.Position : PickingMode.Ignore, userData = key };
            line.AddToClassList(LoreClasses.Line);
            line.EnableInClassList(LoreClasses.LineMore, more);
            var identity = new LocLabel { pickingMode = PickingMode.Ignore };
            identity.AddToClassList(LoreClasses.Identity);
            identity.EnableInClassList(LoreClasses.IdentityPlain, !more);
            identity.SetResolved(parts.Identity);
            LoreStyle.Identity(identity);
            line.Add(identity);
            if (!more) return line;
            var cue = new LocLabel { pickingMode = PickingMode.Ignore };
            cue.AddToClassList(LoreClasses.Read);
            cue.AddToClassList(UiClasses.Upper);
            cue.SetResolved(strings.Get(StringKeys.LoreLineRead));
            line.Add(cue);
            line.RegisterCallback<ClickEvent>(e =>
            {
                e.StopPropagation();
                openWindow(parts);
            });
            return line;
        }

        /// <summary>openLoreModal: the identity line, the history and the closing line set apart, in the lore type the player chose.</summary>
        public static void Window(VisualElement body, LoreParts parts)
        {
            var text = new VisualElement { pickingMode = PickingMode.Ignore };
            text.AddToClassList(LoreClasses.Window);
            body.Add(text);
            void Paragraph(string words, string cls, bool identity)
            {
                var label = new LocLabel { pickingMode = PickingMode.Ignore };
                label.AddToClassList(cls);
                label.SetResolved(words);
                LoreStyle.Window(label, identity);
                text.Add(label);
            }
            Paragraph(parts.Identity, LoreClasses.WindowIdentity, true);
            foreach (var paragraph in parts.Body) Paragraph(paragraph, LoreClasses.History, false);
            if (parts.Closing.Length > 0) Paragraph(parts.Closing, LoreClasses.Closing, false);
        }
    }
}
