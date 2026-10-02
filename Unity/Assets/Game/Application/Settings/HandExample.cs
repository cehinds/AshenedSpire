using System;
using System.Collections.Generic;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MU = Ashen.Generated.MetaUiKeys;
using P = Ashen.Generated.MetaPlaceholders;
using S = Ashen.Generated.MetaStringKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.App.Settings
{
    /// <summary>The hand-rules worked example (W-18 Hand &amp; draw, US-15.2): a heading, the rules in one line and a few turns.</summary>
    public sealed class HandExampleState
    {
        public string Title;
        public string Rules;
        public readonly List<string> Lines = new List<string>();
    }

    /// <summary>
    /// Computes the worked example beside the hand rules from the same formulas combat uses (HandRules.ScaledCards over
    /// the class's attributes; the opening draw, then fill or a fixed draw, capped by capacity; retain or discard at turn
    /// end), playing ui/meta.json handExample.playsPerTurn cards for handExample.turns turns. Engine-free.
    /// </summary>
    public static class HandExample
    {
        public static HandExampleState Build(JObject handRules, JObject attributes, string className, UiData ui)
        {
            var strings = ui.Strings;
            var shape = ui.Meta?.Obj(MU.HandExample) ?? new JObject();
            var turns = (int)Js.Or0(shape[MU.Turns]);
            var plays = (int)Js.Or0(shape[MU.PlaysPerTurn]);
            var capacity = (int)HandRules.ScaledCards(handRules.Obj(K.Capacity), attributes);
            var opening = (int)HandRules.ScaledCards(handRules.Obj(K.Starting), attributes);
            var perTurn = (int)HandRules.ScaledCards(handRules.Obj(K.Turn), attributes);
            var fill = handRules.Str(K.DrawMode) == V.DrawFill;
            var retain = handRules.Is(K.Retain);
            var view = new HandExampleState
            {
                Title = strings.Format(S.HandExampleTitle, new StringArgs().Add(P.Class, className)),
                Rules = strings.Format(S.HandExampleRules, new StringArgs().Add(P.Opening, opening).Add(P.Capacity, capacity)
                    .Add(P.Mode, fill ? strings.Get(S.HandExampleModeFill) : strings.Format(S.HandExampleModeDraw, new StringArgs().Add(P.N, perTurn)))),
            };
            var hand = 0;
            for (var t = 1; t <= turns; t++)
            {
                var room = Math.Max(0, capacity - hand);
                var wanted = t == 1 ? opening : fill ? room : perTurn;
                var draw = Math.Min(room, wanted);
                hand += draw;
                view.Lines.Add(strings.Format(S.HandExampleTurn, new StringArgs().Add(P.N, t).Add(P.Draw, draw).Add(P.Hand, hand).Add(P.Capacity, capacity)));
                var played = Math.Min(plays, hand);
                hand -= played;
                view.Lines.Add(strings.Format(retain ? S.HandExamplePlayRetain : S.HandExamplePlayDiscard, new StringArgs().Add(P.Count, played).Add(P.Kept, hand)));
                if (!retain) hand = 0;
            }
            return view;
        }

        /// <summary>The example for a class as the run would start it (its creation attributes), under the content's hand rules.</summary>
        public static HandExampleState ForClass(RunContent content, UiData ui, string classId)
        {
            var preview = content.Preview(classId);
            var attributes = new JObject();
            if (preview != null) foreach (var a in preview.Attributes) attributes[a.Key] = a.Value;
            var rules = content.Snapshot.Content.Get(ContentFiles.RulesHandRules) as JObject ?? new JObject();
            return Build(rules, attributes, RunHudView.ClassName(ui, classId), ui);
        }
    }
}
