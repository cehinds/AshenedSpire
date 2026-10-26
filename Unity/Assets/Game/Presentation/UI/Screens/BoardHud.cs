using System;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The board HUD band the rooms of a Core 2 run wear (the combat board's and the act map's: the heart, the long hit-point line, the relics as pips, the armoury and menu buttons, the cinders; D-700,
    /// D-902), for the rooms that share it (the rest site and the shop, VP-8 and VP-9). The UXML carries the elements (HUD_HEART, HUD_HP, HUD_PIPS, TOOL_ARMAMENTS, HUD_MENU, HUD_CINDERS); this binds
    /// them to the run and gives the two buttons their art and their doors.
    /// </summary>
    internal static class BoardHud
    {
        /// <summary>The art of a registry lookup kind and name (ui/art.json), or null.</summary>
        public static Texture2D Art(UiContext ui, string kind, string name) =>
            ui.Texture(ui.Data.Art?.Find(kind, new StringArgs().Add(ArtPlaceholders.Name, name)));

        public static void SetImage(VisualElement element, Texture2D texture)
        {
            if (element == null) return;
            element.style.backgroundImage = texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);
        }

        /// <summary>Gives the heart and the two buttons their art and wires the buttons: the menu opens the pause screen, the armoury button the Armoury (refused where it was pressed until it is built).</summary>
        public static void Wire(ScreenContext context, IClimb climb, Action openPause)
        {
            var root = context.Root;
            var ui = context.Ui;
            SetImage(root.Q(UiNames.HudHeart), ui.Icon(BoardIcons.Health));
            SetImage(root.Q(UiNames.ToolArmaments), Art(ui, ArtKinds.Component, BoardArt.ComponentArmoury));
            SetImage(root.Q(UiNames.HudMenu), Art(ui, ArtKinds.Component, BoardArt.ComponentMenu));
            var menu = root.Q<LocButton>(UiNames.HudMenu);
            if (menu != null) menu.clicked += () => openPause();
            var armoury = root.Q<LocButton>(UiNames.ToolArmaments);
            if (armoury != null)
                armoury.clicked += () =>
                {
                    if (!ArmouryDoor.Open(context, climb, ArmouryValues.TabEquipment)) RunFlow.Refuse(context, armoury, StringKeys.MapRefusalArmoury);
                };
        }

        /// <summary>The hit points, the relics as pips and the cinders (what the run holds now).</summary>
        public static void Render(ScreenContext context, IClimb climb)
        {
            var root = context.Root;
            var ui = context.Ui;
            var hud = RunHudView.Build(climb, ui.Data);
            root.Q<Meter>(UiNames.HudHp)?.Set(hud.Hp, hud.MaxHp);
            var run = climb.Run;
            var cinders = (double?)run[RunKeys.Cinders] ?? 0d;
            root.Q<LocLabel>(UiNames.HudCinders)?.SetResolved(ui.Data.Strings.Format(StringKeys.Core2BoardCinders, new StringArgs().Add(UiPlaceholders.Amount, (int)Math.Floor(cinders))));
            var pips = root.Q(UiNames.HudPips);
            if (pips == null) return;
            pips.Clear();
            if (!(run[CombatKeys.Relics] is JArray relics)) return;
            foreach (var relic in relics)
            {
                var pip = new VisualElement { pickingMode = PickingMode.Ignore };
                pip.AddToClassList(UiClasses.HudPip);
                SetImage(pip, ui.Texture(StringTable.Fill(ui.Data.Components.RewardRelicArt, new StringArgs().Add(UiPlaceholders.Id, (string)relic))));
                pips.Add(pip);
            }
        }
    }
}
