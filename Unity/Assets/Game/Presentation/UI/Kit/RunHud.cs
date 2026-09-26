using System;
using Ashen.App.Run;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// RunHud (docs/design/04 §1; W-06, W-10): the band over a run screen — portrait, "{name} · {class} L{level}", the act,
    /// seat and floor trail, the purse (cinders, Smithing Stones, flask charges, deck size), the HP/MP/SP meters and [Menu].
    /// Bound from <see cref="RunHudState"/>; the art is set by the screen (the catalog lives in UiContext).
    /// </summary>
    [UxmlElement]
    public partial class RunHud : VisualElement
    {
        public RunHud()
        {
            AddToClassList(nameof(RunHud).ToLowerInvariant());
            UiDom.CloneTemplate(this, UiResources.KitRunHud);
            MenuButton = this.Q<LocButton>(UiNames.HudMenu);
            if (MenuButton != null) MenuButton.clicked += () => Menu?.Invoke();
        }

        public LocButton MenuButton { get; }

        public VisualElement Portrait => this.Q(UiNames.HudPortrait);

        /// <summary>[Menu] was pressed (the screen opens W-20).</summary>
        public event Action Menu;

        public void Bind(RunHudState s)
        {
            if (s == null) return;
            this.Q<LocLabel>(UiNames.HudName)?.SetResolved(s.Identity);
            this.Q<LocLabel>(UiNames.HudTrail)?.SetResolved(s.Trail);
            this.Q<LocLabel>(UiNames.HudCinders)?.SetResolved(s.Cinders);
            this.Q<LocLabel>(UiNames.HudStones)?.SetResolved(s.Stones);
            this.Q<LocLabel>(UiNames.HudFlasks)?.SetResolved(s.Flasks);
            this.Q<LocLabel>(UiNames.HudDeck)?.SetResolved(s.Deck);
            this.Q<Meter>(UiNames.HudHp)?.Set(s.Hp, s.MaxHp);
            this.Q<Meter>(UiNames.HudMp)?.Set(s.Mana, s.MaxMana);
            this.Q<Meter>(UiNames.HudSp)?.Set(s.Stamina, s.MaxStamina);
        }
    }
}
