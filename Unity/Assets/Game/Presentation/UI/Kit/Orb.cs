using System;
using System.Collections.Generic;
using System.Globalization;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>The textures of the stamina orb (art lookups orb.*; null leaves a layer empty).</summary>
    public sealed class OrbArt
    {
        public Texture2D Disc;
        public Texture2D Frame;
        public Texture2D Diamond;
        public Texture2D Spent;
    }

    /// <summary>
    /// Orb (VP-4, D-700): the stamina orb of the combat footer, as the web's footer art draws it: the inner orb and the harness with the Stamina number and the SP
    /// label, and one diamond per Mana point (filled while it is available, the spent diamond once it is not) on a ring around it, a diamond every
    /// <see cref="BoardMath.OrbStepDegrees"/> degrees, the first at the top and the rest counter-clockwise, at most <see cref="BoardMath.OrbMaxDiamonds"/>.
    /// </summary>
    [UxmlElement]
    public partial class Orb : VisualElement
    {
        private readonly VisualElement _ring;
        private readonly VisualElement _disc;
        private readonly VisualElement _frame;
        private readonly LocLabel _value;
        private readonly List<VisualElement> _diamonds = new List<VisualElement>();
        private OrbArt _art = new OrbArt();
        private int _count = -1;

        public Orb()
        {
            AddToClassList(nameof(Orb).ToLowerInvariant());
            UiDom.CloneTemplate(this, UiResources.KitOrb);
            _ring = this.Q(UiNames.OrbRing);
            _disc = this.Q(UiNames.OrbDisc);
            _frame = this.Q(UiNames.OrbFrame);
            _value = this.Q<LocLabel>(UiNames.OrbValue);
        }

        /// <summary>Sets the art of the layers (the diamonds take theirs on the next <see cref="Set"/>).</summary>
        public void SetArt(OrbArt art)
        {
            _art = art ?? new OrbArt();
            Image(_disc, _art.Disc);
            Image(_frame, _art.Frame);
        }

        /// <summary>The Stamina number and the Mana ring: <paramref name="mana"/> available out of <paramref name="manaMax"/> diamonds.</summary>
        public void Set(int stamina, int mana, int manaMax)
        {
            _value?.SetResolved(stamina.ToString(CultureInfo.InvariantCulture));
            var count = (int)Math.Max(0d, Math.Min(manaMax, BoardMath.OrbMaxDiamonds));
            if (_ring != null && count != _count)
            {
                _count = count;
                _ring.Clear();
                _diamonds.Clear();
                for (var i = 0; i < count; i++)
                {
                    var step = (float)(i * BoardMath.OrbStepDegrees);
                    var holder = new VisualElement { pickingMode = PickingMode.Ignore };
                    holder.AddToClassList(UiClasses.OrbDiamond);
                    holder.style.rotate = new Rotate(new Angle(-step, AngleUnit.Degree));
                    var art = new VisualElement { pickingMode = PickingMode.Ignore };
                    art.AddToClassList(UiClasses.OrbDiamondArt);
                    art.style.rotate = new Rotate(new Angle(step, AngleUnit.Degree));
                    holder.Add(art);
                    _ring.Add(holder);
                    _diamonds.Add(art);
                }
            }
            for (var i = 0; i < _diamonds.Count; i++) Image(_diamonds[i], i < mana ? _art.Diamond : _art.Spent);
        }

        private static void Image(VisualElement element, Texture2D texture)
        {
            if (element == null) return;
            element.style.backgroundImage = texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);
        }
    }
}
