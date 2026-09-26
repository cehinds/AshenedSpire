using System.Collections.Generic;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>Sample values for the review sheet (supplied by the capture fixtures; never content ids in code).</summary>
    public sealed class KitGalleryArgs
    {
        public List<CardViewData> Cards = new List<CardViewData>();
        public List<CardViewData> Hand = new List<CardViewData>();
        public List<CategoryItem> Categories = new List<CategoryItem>();
        public List<MeterSample> Meters = new List<MeterSample>();
        public int Actions;
        public int ActionsMax;
        public int Draw;
        public int Discard;
        public int Potions;
        public double HoldProgress;
    }

    public sealed class MeterSample
    {
        public string Kind;
        public string GlyphKey;
        public int Value;
        public int Max;
        public int ReferenceMax;
    }

    /// <summary>
    /// Dev-only review sheet (ui/screens.json dev: true) that shows every kit component with fixture values, so
    /// the kit goes through SHOOT and REVIEW before the combat screens use it.
    /// </summary>
    public sealed class KitGalleryScreen : ScreenView
    {
        protected override void OnBind(object args)
        {
            var a = args as KitGalleryArgs ?? new KitGalleryArgs();
            var shell = Root.Q<Shell>(UiNames.Shell);
            shell.titleKey = StringKeys.KitTitle;
            shell.SetFooter(StringKeys.ConfirmBack, null);
            shell.Back += () => Nav.Pop();
            shell.Exit += () => Nav.Pop();

            var workspace = Root.Q<Workspace>(UiNames.Workspace);
            if (workspace != null)
            {
                workspace.Rules = Ui.Data.Layout.CategoryNav;
                if (a.Categories.Count > 0) workspace.SetCategories(a.Categories, a.Categories[0].Id);
            }

            var meters = Root.Q(UiNames.KitMeters);
            foreach (var m in a.Meters)
            {
                var meter = new Meter { kind = m.Kind, glyphKey = m.GlyphKey };
                meter.Set(m.Value, m.Max, m.ReferenceMax);
                meters?.Add(meter);
            }

            var cards = Root.Q(UiNames.KitCards);
            foreach (var c in a.Cards)
            {
                var view = new CardView();
                view.Bind(c);
                cards?.Add(view);
            }

            var fan = Ui.Data.Components;
            Root.Q<HandFan>(UiNames.KitHand)?.SetCards(a.Hand, fan.DegreesPerCard, fan.MaxSpread, fan.ScrollAfter);
            Root.Q<FooterGroup>(UiNames.KitFooterGroup)?.Set(a.Actions, a.ActionsMax, a.Draw, a.Discard, a.Potions, a.Actions == 0);

            var hold = Root.Q<HoldButton>(UiNames.KitHold);
            if (hold != null)
            {
                hold.HoldMs = Ui.Data.Tokens.Duration(TokenKeys.HoldConfirm);
                hold.ShowProgress(a.HoldProgress);
            }

            var tips = Root.Q(UiNames.KitTooltips);
            if (tips != null)
                foreach (var size in Ui.Data.Components.TooltipSizes)
                {
                    var tip = new Tooltip { size = size, glyphKey = StringKeys.GlyphActions, titleKey = StringKeys.KitTooltipTitle, bodyKey = StringKeys.KitTooltipBody };
                    tip.AddToClassList(UiClasses.TooltipVisible);
                    tips.Add(tip);
                }
        }
    }
}
