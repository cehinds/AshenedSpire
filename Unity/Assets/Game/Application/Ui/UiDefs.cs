using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>A named transition of a screen (ui/screens.json 'transitions').</summary>
    public sealed class ScreenTransition
    {
        public string Trigger;
        public string To;
        public string Mode;
        public bool Replaces => Mode == UiValues.ModeReplace;
    }

    /// <summary>One row of the screen registry (ui/screens.json; docs/design/08 §10, PF-08).</summary>
    public sealed class ScreenDef
    {
        public string Id;
        public string Uxml;
        public string Layer;
        public string Background;
        public string Input;
        public string InitialFocus;
        public string Back;
        public bool Gate;
        public bool Planned;
        public bool Dev;
        public IReadOnlyList<string> FocusOrder = Array.Empty<string>();
        public IReadOnlyList<ScreenTransition> Transitions = Array.Empty<ScreenTransition>();

        public bool IsModal => Layer == UiValues.LayerModal;
        public bool AcceptsInput => Input != UiValues.InputNone;
        public bool IsBuilt => !Planned && !string.IsNullOrEmpty(Uxml);

        public ScreenTransition Transition(string trigger) => Transitions.FirstOrDefault(t => t.Trigger == trigger);
    }

    /// <summary>The screen registry, in file order.</summary>
    public sealed class ScreenRegistry
    {
        private readonly List<ScreenDef> _screens = new List<ScreenDef>();

        public string Initial { get; private set; }
        public IReadOnlyList<ScreenDef> All => _screens;

        public ScreenDef Get(string id) => _screens.FirstOrDefault(s => s.Id == id);
        public bool IsBuilt(string id) => Get(id)?.IsBuilt ?? false;

        public static ScreenRegistry From(JObject json)
        {
            var registry = new ScreenRegistry { Initial = (string)json[UiKeys.Initial] };
            foreach (var p in ((JObject)json[UiKeys.Screens]).Properties())
            {
                var row = (JObject)p.Value;
                var transitions = new List<ScreenTransition>();
                if (row[UiKeys.Transitions] is JObject t)
                    foreach (var tp in t.Properties())
                        transitions.Add(new ScreenTransition { Trigger = tp.Name, To = (string)tp.Value[UiKeys.To], Mode = (string)tp.Value[UiKeys.Mode] });
                registry._screens.Add(new ScreenDef
                {
                    Id = p.Name,
                    Uxml = (string)row[UiKeys.Uxml],
                    Layer = (string)row[UiKeys.Layer],
                    Background = (string)row[UiKeys.Background],
                    Input = (string)row[UiKeys.Input],
                    InitialFocus = (string)row[UiKeys.InitialFocus],
                    Back = (string)row[UiKeys.Back],
                    Gate = (bool?)row[UiKeys.Gate] ?? false,
                    Planned = (bool?)row[UiKeys.Planned] ?? false,
                    Dev = (bool?)row[UiKeys.Dev] ?? false,
                    FocusOrder = Strings(row[UiKeys.FocusOrder]),
                    Transitions = transitions,
                });
            }
            return registry;
        }

        internal static IReadOnlyList<string> Strings(JToken token) =>
            token is JArray a ? a.Select(x => (string)x).ToList() : (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>A menu row (ui/menus.json).</summary>
    public sealed class MenuEntryDef
    {
        public string Id;
        public string LabelKey;
        public string Action;
        public string Target;
        public string Mode;
        public string Confirm;
        public string EnabledWhen;
        public bool Visible;
    }

    /// <summary>A W2 confirmation door (ui/menus.json 'confirms'; 04 W-23).</summary>
    public sealed class ConfirmDef
    {
        public string Id;
        public string TitleKey;
        public string BodyKey;
        public string OccupiedBodyKey;
        public string PrimaryKey;
        public string BackKey;
        public string Policy;
        public bool Hold;
        public bool Single;
    }

    public sealed class MenuSet
    {
        private readonly List<ConfirmDef> _confirms = new List<ConfirmDef>();

        public IReadOnlyList<MenuEntryDef> Title { get; private set; } = Array.Empty<MenuEntryDef>();
        public IReadOnlyList<ConfirmDef> Confirms => _confirms;

        public ConfirmDef Confirm(string id) => _confirms.FirstOrDefault(c => c.Id == id);

        public static MenuSet From(JObject json)
        {
            var set = new MenuSet
            {
                Title = ((JArray)json[UiKeys.Title]).OfType<JObject>().Select(r => new MenuEntryDef
                {
                    Id = (string)r[UiKeys.Id],
                    LabelKey = (string)r[UiKeys.LabelKey],
                    Action = (string)r[UiKeys.Action],
                    Target = (string)r[UiKeys.Target],
                    Mode = (string)r[UiKeys.Mode],
                    Confirm = (string)r[UiKeys.Confirm],
                    EnabledWhen = (string)r[UiKeys.EnabledWhen],
                    Visible = (bool?)r[UiKeys.Visible] ?? true,
                }).ToList(),
            };
            foreach (var p in ((JObject)json[UiKeys.Confirms]).Properties())
            {
                var r = (JObject)p.Value;
                set._confirms.Add(new ConfirmDef
                {
                    Id = p.Name,
                    TitleKey = (string)r[UiKeys.TitleKey],
                    BodyKey = (string)r[UiKeys.BodyKey],
                    OccupiedBodyKey = (string)r[UiKeys.OccupiedBodyKey],
                    PrimaryKey = (string)r[UiKeys.PrimaryKey],
                    BackKey = (string)r[UiKeys.BackKey],
                    Policy = (string)r[UiKeys.Policy],
                    Hold = (bool?)r[UiKeys.Hold] ?? false,
                    Single = (bool?)r[UiKeys.Single] ?? false,
                });
            }
            return set;
        }
    }

    /// <summary>A PanelSettings size (ui/layout.json 'panels').</summary>
    public sealed class PanelDef
    {
        public string Id;
        public int Width;
        public int Height;
        public string ScreenMatch;
        public double Match;
    }

    /// <summary>Rule 11 rail-or-selector inputs (ui/layout.json 'categoryNav').</summary>
    public sealed class CategoryNavRules
    {
        public double RailWidth;
        public double MinPaneWidth;
        public double TapFloor;
        public double Chrome;
        public double Hysteresis;
    }

    /// <summary>ui/layout.json (docs/design/04 §0; D-001, D-016).</summary>
    public sealed class LayoutRules
    {
        public PanelDef Wide;
        public PanelDef Narrow;
        public int NarrowMaxWidth;
        public int StandardMinHeight;
        public int CompactMinHeight;
        public int GateBelowHeight;
        public CategoryNavRules CategoryNav;
        public IReadOnlyDictionary<string, int> MinPhysical = new Dictionary<string, int>();

        public int MinPhysicalOf(string key) => MinPhysical.TryGetValue(key, out var v) ? v : 0;

        public static LayoutRules From(JObject json)
        {
            var panels = (JObject)json[UiKeys.Panels];
            var bands = (JObject)json[UiKeys.Bands];
            var nav = (JObject)json[UiKeys.CategoryNav];
            var min = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in ((JObject)json[UiKeys.MinPhysical]).Properties()) min[p.Name] = (int)p.Value;
            return new LayoutRules
            {
                Wide = Panel(UiKeys.Wide, (JObject)panels[UiKeys.Wide]),
                Narrow = Panel(UiKeys.Narrow, (JObject)panels[UiKeys.Narrow]),
                NarrowMaxWidth = (int)json[UiKeys.NarrowMaxWidth],
                StandardMinHeight = (int)bands[UiKeys.StandardMinHeight],
                CompactMinHeight = (int)bands[UiKeys.CompactMinHeight],
                GateBelowHeight = (int)json[UiKeys.GateBelowHeight],
                MinPhysical = min,
                CategoryNav = new CategoryNavRules
                {
                    RailWidth = (double)nav[UiKeys.RailWidth],
                    MinPaneWidth = (double)nav[UiKeys.MinPaneWidth],
                    TapFloor = (double)nav[UiKeys.TapFloor],
                    Chrome = (double)nav[UiKeys.Chrome],
                    Hysteresis = (double)nav[UiKeys.Hysteresis],
                },
            };
        }

        private static PanelDef Panel(string id, JObject p) => new PanelDef
        {
            Id = id,
            Width = (int)p[UiKeys.Width],
            Height = (int)p[UiKeys.Height],
            ScreenMatch = (string)p[UiKeys.ScreenMatch],
            Match = (double?)p[UiKeys.Match] ?? 0d,
        };
    }

    /// <summary>ui/tokens.json values code needs (durations in ms, colours as strings).</summary>
    public sealed class UiTokens
    {
        private readonly JObject _json;

        public UiTokens(JObject json) => _json = json;

        public int Duration(string key) => (int?)_json[UiKeys.Duration]?[key] ?? 0;
        public string Color(string key) => (string)_json[UiKeys.Color]?[key];
        public bool Has(string group, string key) => _json[group]?[key] != null;
    }

    /// <summary>ui/components.json (kit defaults).</summary>
    public sealed class ComponentDefaults
    {
        public IReadOnlyList<string> ArtInclude = Array.Empty<string>();
        public IReadOnlyList<string> MeterKinds = Array.Empty<string>();
        public IReadOnlyList<string> TooltipSizes = Array.Empty<string>();
        public IReadOnlyList<string> CostKinds = Array.Empty<string>();
        public double DegreesPerCard;
        public double MaxSpread;
        public int ScrollAfter;
        public int BootFilesPerFrame;

        public static ComponentDefaults From(JObject json)
        {
            var fan = (JObject)json[UiKeys.HandFan];
            return new ComponentDefaults
            {
                ArtInclude = ScreenRegistry.Strings(json[UiKeys.Art]?[UiKeys.Include]),
                MeterKinds = ScreenRegistry.Strings(json[UiKeys.Meter]?[UiKeys.Kinds]),
                TooltipSizes = ScreenRegistry.Strings(json[UiKeys.Tooltip]?[UiKeys.Sizes]),
                CostKinds = ScreenRegistry.Strings(json[UiKeys.Card]?[UiKeys.CostKinds]),
                DegreesPerCard = (double)fan[UiKeys.DegreesPerCard],
                MaxSpread = (double)fan[UiKeys.MaxSpread],
                ScrollAfter = (int)fan[UiKeys.ScrollAfter],
                BootFilesPerFrame = (int)json[UiKeys.Boot][UiKeys.FilesPerFrame],
            };
        }
    }

    /// <summary>ui/confirmationPolicies.json: which policies are destructive (04 §0 destructive tone).</summary>
    public sealed class ConfirmPolicies
    {
        private readonly Dictionary<string, string> _levels = new Dictionary<string, string>(StringComparer.Ordinal);

        public static ConfirmPolicies From(JObject json)
        {
            var p = new ConfirmPolicies();
            foreach (var row in (json[UiKeys.Policies] as JArray ?? new JArray()).OfType<JObject>())
                p._levels[(string)row[UiKeys.Id]] = (string)row[UiKeys.Level];
            return p;
        }

        public bool Exists(string policy) => policy != null && _levels.ContainsKey(policy);

        public bool IsDestructive(string policy) =>
            policy != null && _levels.TryGetValue(policy, out var level) && level == UiValues.PolicyDestructive;
    }
}
