using System;
using System.Globalization;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;

namespace Ashen.App.Nodes
{
    /// <summary>The RUN_HUD band of the node screens (04 §1 RunHud: identity, act and floor, HP and MP, cinders; never potions).</summary>
    public sealed class NodeHudView
    {
        public string Identity;
        public string Trail;
        public string Portrait;
        public int Hp;
        public int HpMax;
        public int Mana;
        public int ManaMax;
        public string Cinders;

        public static NodeHudView Build(JObject run, string name, string portrait, UiData ui)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            var strings = ui.Strings;
            var className = strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.ClassNameKey, run.Str(RK.Class)));
            return new NodeHudView
            {
                Identity = strings.Format(StringKeys.CombatIdentity, new StringArgs().Add(UiPlaceholders.Name, name).Add(UiPlaceholders.Class, className)),
                Trail = strings.Format(NodeStringKeys.NodesHudTrail, new StringArgs().Add(NodePlaceholders.Act, NodeText.Number(Js.Or0(run[RK.ActNumber]))).Add(NodePlaceholders.Floor, NodeText.Number(Js.Or0(run[RK.Floor])))),
                Portrait = portrait,
                Hp = (int)Math.Floor(Js.Or0(run[K.Hp])),
                HpMax = (int)Math.Floor(Js.Or0(run[K.MaxHp])),
                Mana = (int)Math.Floor(Js.Or0(run[K.Mana])),
                ManaMax = (int)Math.Floor(Js.Or0(run[K.MaxMana])),
                Cinders = strings.Format(NodeStringKeys.NodesHudCinders, new StringArgs().Add(NodePlaceholders.Cinders, NodeText.Number(Js.Or0(run[RK.Cinders])))),
            };
        }
    }
}
