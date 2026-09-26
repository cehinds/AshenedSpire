using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.App.Combat
{
    /// <summary>A status on a combatant, display-ready (name and icon from its row; stacks or build-up meter).</summary>
    public sealed class StatusView
    {
        public string Id;
        public string Name;
        public string Icon;
        public string Tint;
        public double Stacks;
        public double? MeterValue;
        public double? MeterMax;
        public double? Duration;
    }

    /// <summary>An enemy's telegraphed move with its live numbers (previewIntent).</summary>
    public sealed class IntentView
    {
        public string Kind;
        public string LabelKey;
        public string MoveId;
        public double? Damage;
        public double? Hits;
        public double? TotalDamage;
        public double? Block;
        public bool Pending;
        public bool Delayed;
    }

    /// <summary>A break meter (Poise or Ward): value toward max.</summary>
    public sealed class MeterView
    {
        public double Value;
        public double Max;
    }

    public sealed class CombatantView
    {
        public string Id;
        public string DefId;
        public string Name;
        public double Hp;
        public double MaxHp;
        public double Block;
        public bool Alive;
        public MeterView Poise;
        public MeterView Ward;
        public List<StatusView> Statuses = new List<StatusView>();
        public IntentView Intent;
    }

    /// <summary>A card in hand as the player sees it: numbers resolved by the SAME math the engine runs.</summary>
    public sealed class CardView
    {
        public string InstanceId;
        public string CardId;
        public string Name;
        public string Text;
        public string Kind;
        public bool Upgraded;
        public double Cost;
        public bool CostIsX;
        public double ManaCost;
        public double StaminaCost;
        public bool NeedsTarget;
        public IReadOnlyList<string> Targets = new string[0];
        public Refusal Refusal;
        public bool Playable => Refusal == null;
        public Dictionary<string, double> DamageByTarget = new Dictionary<string, double>();
    }

    public sealed class PlayerView
    {
        public CombatantView Body;
        public string ClassId;
        public double Energy;
        public double EnergyMax;
        public double Mana;
        public double MaxMana;
        public double Stamina;
        public double MaxStamina;
        public string StanceId;
        public string StanceName;
        public double PendingActionLoss;
    }

    /// <summary>Everything W-07 draws, derived from the committed combat state (views never call Domain directly).</summary>
    public sealed class CombatViewState
    {
        public double Turn;
        public string Phase;
        public string Result;
        public PlayerView Player;
        public List<CombatantView> Enemies = new List<CombatantView>();
        public List<CardView> Hand = new List<CardView>();
        public int DrawCount;
        public int DiscardCount;
        public int ExhaustCount;
        public Refusal EndTurnRefusal;
        public bool DiscardChoice;
        public double DiscardMinimum;
        public double DiscardMaximum;
    }

    /// <summary>
    /// Builds the display-ready combat view (component loop step 4, VM): strings come from the content rows (names,
    /// text templates), numbers from the engine's previews and legality, never recomputed here.
    /// </summary>
    public static class CombatViewModel
    {
        private static readonly Regex Token = new Regex(Ashen.Generated.CombatPatterns.TextToken, RegexOptions.CultureInvariant);

        public static CombatViewState Build(CombatState c)
        {
            var view = new CombatViewState
            {
                Turn = c.Turn,
                Phase = c.Phase,
                Result = c.Result,
                DrawCount = c.Piles.Draw.Count,
                DiscardCount = c.Piles.Discard.Count,
                ExhaustCount = c.Piles.Exhaust.Count,
                Player = Player(c),
            };
            foreach (var enemy in c.Enemies) view.Enemies.Add(Enemy(c, enemy));
            foreach (var inst in c.Piles.Hand) view.Hand.Add(Card(c, inst));
            view.EndTurnRefusal = CombatLegality.CanEndTurn(c, new string[0]);
            var plan = HandRules.Plan(c);
            view.DiscardChoice = plan.Prompt;
            view.DiscardMinimum = plan.Minimum;
            view.DiscardMaximum = plan.Maximum;
            return view;
        }

        /// <summary>A text template with its {tokens} replaced by the preview's values; unknown tokens stay as written.</summary>
        public static string ResolveText(string template, JObject tokens)
        {
            if (string.IsNullOrEmpty(template)) return string.Empty;
            return Token.Replace(template, m =>
            {
                var value = tokens?[m.Groups[V.GroupToken].Value];
                return Js.IsNum(value) ? Js.D(value).ToString(CultureInfo.InvariantCulture) : m.Value;
            });
        }

        private static CardView Card(CombatState c, JObject inst)
        {
            var id = inst.Str(K.InstanceId);
            var def = Cards.Resolve(c.Data, inst);
            var preview = CombatPreview.PreviewCard(c, id);
            var card = new CardView
            {
                InstanceId = id,
                CardId = inst.Str(K.CardId),
                Name = def.Str(K.Name),
                Text = ResolveText(def.Str(K.TextTemplate), preview.Obj(K.Tokens)),
                Kind = Cards.Kind(c.Data, def),
                Upgraded = inst.Is(K.Upgraded),
                Cost = preview.Num(K.Cost),
                CostIsX = preview.Is(K.CostIsX),
                ManaCost = preview.Num(K.ManaCost),
                StaminaCost = preview.Num(K.StaminaCost),
                NeedsTarget = preview.Is(K.NeedsTarget),
                Targets = CombatLegality.Targets(c, id),
                Refusal = CombatLegality.CanPlay(c, id, CombatLegality.Targets(c, id).FirstOrDefault()),
            };
            foreach (var value in Js.Items(preview[K.Values]).OfType<JObject>())
            {
                if (value.Str(K.Op) != Ashen.Generated.CombatOps.Damage || !(value[K.PerTarget] is JObject perTarget)) continue;
                foreach (var p in perTarget.Properties())
                    card.DamageByTarget[p.Name] = (card.DamageByTarget.TryGetValue(p.Name, out var d) ? d : 0) + Js.D(p.Value) * value.Num(K.Hits);
            }
            return card;
        }

        private static List<StatusView> StatusList(CombatState c, JObject entity)
        {
            var list = new List<StatusView>();
            foreach (var p in (entity.Obj(K.Statuses) ?? new JObject()).Properties())
            {
                if (!(p.Value is JObject inst)) continue;
                var def = c.Data.Statuses.Get(p.Name);
                var meter = inst.Obj(K.Meter);
                list.Add(new StatusView
                {
                    Id = p.Name,
                    Name = def.Str(K.Name) ?? p.Name,
                    Icon = def.Str(K.Icon),
                    Tint = def.Str(K.Tint),
                    Stacks = Ashen.Domain.Combat.Statuses.Stacks(entity, p.Name),
                    MeterValue = meter != null ? meter.Num(K.Value) : (double?)null,
                    MeterMax = meter != null ? meter.Num(K.Max) : (double?)null,
                    Duration = Js.IsNum(inst[K.Duration]) ? inst.Num(K.Duration) : (double?)null,
                });
            }
            return list;
        }

        private static MeterView Meter(JObject entity, string key)
        {
            var m = entity.Obj(key);
            return m == null ? null : new MeterView { Value = m.Num(K.Value), Max = m.Num(K.Max) };
        }

        private static CombatantView Body(CombatState c, JObject entity, string defId, string name) => new CombatantView
        {
            Id = entity.Str(K.Id),
            DefId = defId,
            Name = name,
            Hp = entity.Num(K.Hp),
            MaxHp = entity.Num(K.MaxHp),
            Block = entity.Num(K.Block),
            Alive = entity.Is(K.Alive),
            Poise = Meter(entity, K.Poise + V.MeterSuffix),
            Ward = Meter(entity, K.Ward + V.MeterSuffix),
            Statuses = StatusList(c, entity),
        };

        private static PlayerView Player(CombatState c)
        {
            var p = c.Player;
            var classId = p.Str(K.ClassId);
            var cls = classId != null && c.Data.Classes.Has(classId) ? c.Data.Classes.Get(classId) : null;
            var stanceId = p.Str(K.StanceId);
            return new PlayerView
            {
                Body = Body(c, p, classId, cls?.Str(K.Name)),
                ClassId = classId,
                Energy = p.Num(K.Energy),
                EnergyMax = p.Num(K.EnergyMax),
                Mana = p.Num(K.Mana),
                MaxMana = p.Num(K.MaxMana),
                Stamina = p.Num(K.Stamina),
                MaxStamina = p.Num(K.MaxStamina),
                StanceId = stanceId,
                StanceName = stanceId != null ? c.Data.Stances.Get(stanceId).Str(K.Name) : null,
                PendingActionLoss = p.Or0(K.PendingActionLoss),
            };
        }

        private static CombatantView Enemy(CombatState c, JObject enemy)
        {
            var enemyId = enemy.Str(K.EnemyId);
            var body = Body(c, enemy, enemyId, c.Data.Enemies.Get(enemyId).Str(K.Name));
            if (enemy.Is(K.Alive) && Js.Truthy(enemy[K.Intent]))
            {
                var i = CombatPreview.PreviewIntent(c, enemy.Str(K.Id));
                var kind = i.Str(K.Kind) ?? V.IntentUnknown;
                body.Intent = new IntentView
                {
                    Kind = kind,
                    LabelKey = V.IntentLabelPrefix + kind,
                    MoveId = i.Str(K.MoveId),
                    Damage = Js.IsNum(i[K.Damage]) ? i.Num(K.Damage) : (double?)null,
                    Hits = Js.IsNum(i[K.Hits]) ? i.Num(K.Hits) : (double?)null,
                    TotalDamage = Js.IsNum(i[K.TotalDamage]) ? i.Num(K.TotalDamage) : (double?)null,
                    Block = Js.IsNum(i[K.Block]) ? i.Num(K.Block) : (double?)null,
                    Pending = i.Is(K.Pending),
                    Delayed = i.Is(K.Delayed),
                };
            }
            return body;
        }
    }
}
