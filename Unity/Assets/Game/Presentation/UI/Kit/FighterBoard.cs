using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// Where a fighter stands on the board's field (P-4, D-1540..), in the field's own pixels: the column its readout hangs from (the web's mini HUD: the cell's width, from the group's top down to the
    /// foot), the box of its figure (the stage root: as tall as the fit says, its foot on the cell's foot anchor), and its floor shadow.
    /// </summary>
    public sealed class FighterPlace
    {
        public float Left;
        public float Top;
        public float Width;
        public float Height;

        /// <summary>The figure's box, from the column's top left.</summary>
        public float ArtLeft;
        public float ArtTop;
        public float ArtWidth;
        public float ArtHeight;

        /// <summary>The floor shadow's box, from the column's top left.</summary>
        public float ShadowLeft;
        public float ShadowTop;
        public float ShadowWidth;
        public float ShadowHeight;
    }

    public partial class Fighter
    {
        private VisualElement _shadow;
        private static Texture2D _shadowTexture;

        /// <summary>Puts the fighter where the board's formation says: its column, its figure's box and its shadow, as inline styles over the kit's own flow (the web's battlefieldStage.js writes the same left, width and zoom).</summary>
        public void PlaceOnBoard(FighterPlace place, Texture2D shadow)
        {
            _shadow ??= this.Q(UiNames.FighterShadow);
            style.position = Position.Absolute;
            style.left = place.Left;
            style.top = place.Top;
            style.width = place.Width;
            style.height = place.Height;
            style.marginLeft = 0;
            style.marginRight = 0;
            style.marginTop = 0;
            style.marginBottom = 0;
            style.paddingLeft = 0;
            style.paddingRight = 0;
            if (_art != null)
            {
                _art.style.position = Position.Absolute;
                _art.style.left = place.ArtLeft;
                _art.style.top = place.ArtTop;
                _art.style.width = place.ArtWidth;
                _art.style.height = place.ArtHeight;
            }
            if (_shadow != null)
            {
                _shadow.style.left = place.ShadowLeft;
                _shadow.style.top = place.ShadowTop;
                _shadow.style.width = place.ShadowWidth;
                _shadow.style.height = place.ShadowHeight;
                _shadow.style.right = StyleKeyword.Auto;
                _shadow.style.bottom = StyleKeyword.Auto;
                _shadow.style.backgroundColor = Color.clear;
                _shadow.style.borderTopLeftRadius = 0;
                _shadow.style.borderTopRightRadius = 0;
                _shadow.style.borderBottomLeftRadius = 0;
                _shadow.style.borderBottomRightRadius = 0;
                _shadow.style.backgroundImage = shadow != null ? new StyleBackground(shadow) : new StyleBackground(StyleKeyword.None);
                _shadow.style.unityBackgroundScaleMode = ScaleMode.StretchToFill;
            }
            // The figure box has a new size: the stage draws itself over it again.
            FitPose();
        }
    }
}
