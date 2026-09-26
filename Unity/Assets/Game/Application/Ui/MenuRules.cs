using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;

namespace Ashen.App.Ui
{
    /// <summary>What the menu rules can see.</summary>
    public sealed class MenuContext
    {
        /// <summary>At least one run slot holds a save this build can load.</summary>
        public bool HasValidSlot;

        /// <summary>Whether a screen id is built (not 'planned' in ui/screens.json).</summary>
        public Func<string, bool> IsBuilt = _ => false;
    }

    public sealed class MenuEntryState
    {
        public MenuEntryDef Def;
        public bool Visible;
        public bool Enabled;
    }

    /// <summary>
    /// Title menu enablement (US-1.3; ui/menus.json 'enabledWhen'). Rows keep their data order. Continue is shown
    /// disabled without a valid slot (shipped AW:245); rows that open a planned screen are disabled. An unknown
    /// rule disables its row rather than guessing.
    /// </summary>
    public static class MenuRules
    {
        public static IReadOnlyList<MenuEntryState> Evaluate(IReadOnlyList<MenuEntryDef> entries, MenuContext context) =>
            entries.Select(e => new MenuEntryState { Def = e, Visible = e.Visible, Enabled = e.Visible && IsEnabled(e, context) }).ToList();

        public static bool IsEnabled(MenuEntryDef entry, MenuContext context)
        {
            switch (entry.EnabledWhen)
            {
                case MenuRuleIds.Always: return true;
                case MenuRuleIds.HasValidSlot: return context.HasValidSlot;
                case MenuRuleIds.TargetBuilt: return entry.Target != null && context.IsBuilt(entry.Target);
                default: return false;
            }
        }

        /// <summary>Index (into the visible rows) of the first enabled row, or -1. The gate hands focus here (US-1.2).</summary>
        public static int FirstEnabled(IReadOnlyList<MenuEntryState> states)
        {
            var visible = states.Where(s => s.Visible).ToList();
            for (var i = 0; i < visible.Count; i++) if (visible[i].Enabled) return i;
            return -1;
        }
    }
}
