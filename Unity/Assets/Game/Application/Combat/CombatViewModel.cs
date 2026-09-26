using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
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

    /// <summary>A flask the player can drink: a charge pool (by kind) or a carried flask (by slot).</summary>
    public sealed class FlaskView
    {
        /// <summary>The carried flask's slot, or -1 for a charge pool.</summary>
        public int Slot = -1;
        public string ChargeKind;
        public string FlaskId;
        public string Name;
        public double Current;
        public double Max;
        public bool Targeted;
        public Refusal Refusal;
        public bool Usable => Refusal == null;
        public bool IsCharge => ChargeKind != null;
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

    /// <summary>One prepared set of a hand slot: what it holds, whether it is the active one, and what cycling to it costs now.</summary>
    public sealed class ArmamentSetView
    {
        public int SetIndex;
        public string ItemId;
        public string Name;
        public bool Active;

        /// <summary>The price the engine would charge (swapCostFor: cost, ruleId, base, categoryTag, gear deltas), or null where it refuses first.</summary>
        public JObject Price;

        public double? Cost;
        public Refusal Refusal;
        public bool Swappable => Refusal == null;

        /// <summary>The command that swaps to this set (hand it to the session).</summary>
        public CombatCommand Command;
    }

    /// <summary>A hand slot as the mid-fight Armoury shows it (label from content, sets in order).</summary>
    public sealed class ArmamentSlotView
    {
        public string SlotId;
        public string Label;
        public string Hand;
        public List<ArmamentSetView> Sets = new List<ArmamentSetView>();
    }

    /// <summary>A carried piece (storage): id and name.</summary>
    public sealed class CarriedItemView
    {
        public string ItemId;
        public string Name;
    }

    /// <summary>One way to re-arm a position (a carried piece, or an empty hand when <see cref="PieceId"/> is null), priced and judged.</summary>
    public sealed class EquipmentChangeView
    {
        public string SlotId;
        public int SetIndex;
        public string PieceId;
        public string Name;
        public JObject Price;
        public double? Cost;
        public Refusal Refusal;
        public bool Allowed => Refusal == null;
        public CombatCommand Command;
    }

    /// <summary>
    /// The mid-fight Armoury as data (us-5.11; the D-010 popover binds it later): the hand slots and their sets with
    /// the swap legality and price, the carried pieces, the currency the price is paid in and, under an allowance,
    /// what is left this turn. Re-arming options for a position come from <see cref="CombatViewModel.EquipmentChanges"/>.
    /// </summary>
    public sealed class ArmouryView
    {
        public List<ArmamentSlotView> Slots = new List<ArmamentSlotView>();
        public List<CarriedItemView> Carried = new List<CarriedItemView>();

        /// <summary>balance.equipment.swapCostKind: energy or allowance.</summary>
        public string CostKind;

        /// <summary>The swaps left this turn under an allowance (null when swaps cost energy).</summary>
        public double? SwapsLeft;
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
        public List<FlaskView> Flasks = new List<FlaskView>();
        public int DrawCount;
        public int DiscardCount;
        public int ExhaustCount;
        public Refusal EndTurnRefusal;
        public bool DiscardChoice;
        public double DiscardMinimum;
        public double DiscardMaximum;

        /// <summary>The mid-fight Armoury (null when the fight carries no loadout or its data has no equipment port).</summary>
        public ArmouryView Armoury;
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
            view.Flasks = Flasks(c);
            view.EndTurnRefusal = CombatLegality.CanEndTurn(c, new string[0]);
            var plan = HandRules.Plan(c);
            view.DiscardChoice = plan.Prompt;
            view.DiscardMinimum = plan.Minimum;
            view.DiscardMaximum = plan.Maximum;
            view.Armoury = Armoury(c);
            return view;
        }

        private static string PieceName(CombatState c, string itemId) =>
            itemId == null ? null
                : Js.Items(c.Data.Equipment[K.Armaments]).OfType<JObject>().FirstOrDefault(a => a.Str(K.Id) == itemId)?.Str(K.Name) ?? itemId;

        private static IEnumerable<JObject> HandSlots(CombatState c) =>
            Js.Items(c.Data.Equipment[K.Slots]).OfType<JObject>().Where(s => s.Str(K.Hand) == V.Right || s.Str(K.Hand) == V.Left);

        private static ArmouryView Armoury(CombatState c)
        {
            if (c.Loadout == null || c.Data.EquipmentPort == null) return null;
            var cfg = c.Data.Balance.Obj(K.Equipment) ?? new JObject();
            var view = new ArmouryView
            {
                CostKind = cfg.Str(K.SwapCostKind),
                SwapsLeft = cfg.Str(K.SwapCostKind) == V.SwapAllowance ? c.SwapsLeft : (double?)null,
            };
            foreach (var slot in HandSlots(c))
            {
                var slotId = slot.Str(K.Id);
                var cells = c.Loadout.Obj(K.Sets)?.Arr(slotId) ?? new JArray();
                var active = (int)Js.Or0(c.Loadout.Obj(K.Active)?[slotId]);
                var slotView = new ArmamentSlotView { SlotId = slotId, Label = slot.Str(K.Label), Hand = slot.Str(K.Hand) };
                for (var i = 0; i < cells.Count; i++)
                {
                    var price = CombatPreview.SwapPrice(c, slotId, i);
                    slotView.Sets.Add(new ArmamentSetView
                    {
                        SetIndex = i,
                        ItemId = Js.Str(cells[i]),
                        Name = PieceName(c, Js.Str(cells[i])),
                        Active = i == active,
                        Price = price,
                        Cost = price != null ? price.Num(K.Cost) : (double?)null,
                        Refusal = CombatLegality.CanSwap(c, slotId, i),
                        Command = CombatCommand.SwapArmament(slotId, i),
                    });
                }
                view.Slots.Add(slotView);
            }
            foreach (var id in Js.Items(c.Loadout[RK.Storage]).Select(Js.Str).Where(id => id != null))
                view.Carried.Add(new CarriedItemView { ItemId = id, Name = PieceName(c, id) });
            return view;
        }

        /// <summary>
        /// Every way to re-arm one position now: an empty hand, each carried piece and each piece held in another hand
        /// cell (a move), each with the engine's refusal and price.
        /// </summary>
        public static List<EquipmentChangeView> EquipmentChanges(CombatState c, string slotId, int setIndex)
        {
            var list = new List<EquipmentChangeView>();
            if (c.Loadout == null) return list;
            var pieces = new List<string> { null };
            foreach (var id in Js.Items(c.Loadout[RK.Storage]).Select(Js.Str)) if (id != null && !pieces.Contains(id)) pieces.Add(id);
            foreach (var slot in HandSlots(c))
                foreach (var id in Js.Items(c.Loadout.Obj(K.Sets)?[slot.Str(K.Id)]).Select(Js.Str))
                    if (!string.IsNullOrEmpty(id) && !pieces.Contains(id)) pieces.Add(id);
            foreach (var pieceId in pieces)
            {
                var price = CombatPreview.ChangePrice(c, slotId, setIndex, pieceId);
                list.Add(new EquipmentChangeView
                {
                    SlotId = slotId,
                    SetIndex = setIndex,
                    PieceId = pieceId,
                    Name = PieceName(c, pieceId),
                    Price = price,
                    Cost = price != null ? price.Num(K.Cost) : (double?)null,
                    Refusal = CombatLegality.CanChangeEquipment(c, slotId, setIndex, pieceId),
                    Command = CombatCommand.ChangeEquipment(slotId, setIndex, pieceId),
                });
            }
            return list;
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

        /// <summary>The charge pools (rules/combatEngine.json flasks.chargeKinds) with their backing flask, then the carried flasks by slot.</summary>
        private static List<FlaskView> Flasks(CombatState c)
        {
            var list = new List<FlaskView>();
            var p = c.Player;
            var charges = p.Obj(K.FlaskCharges);
            foreach (var kind in Js.Items(c.Data.Engine.Obj(K.Flasks)?[K.ChargeKinds]).Select(Js.Str).Where(k => k != null))
            {
                var flaskId = CombatEngine.ChargeFlaskId(c, kind);
                if (flaskId == null || charges == null) continue;
                var def = c.Data.Flasks.Get(flaskId);
                list.Add(new FlaskView
                {
                    ChargeKind = kind,
                    FlaskId = flaskId,
                    Name = def.Str(K.Name),
                    Current = charges.Num(kind + V.CurrentSuffix),
                    Max = charges.Num(kind),
                    Targeted = def.Is(K.Targeted),
                    Refusal = CombatLegality.CanUseFlask(c, 0, kind),
                });
            }
            var carried = p.Arr(K.Flasks) ?? new JArray();
            for (var i = 0; i < carried.Count; i++)
            {
                if (!(carried[i] is JObject slot) || slot.Str(K.FlaskId) == null) continue;
                var def = c.Data.Flasks.Get(slot.Str(K.FlaskId));
                list.Add(new FlaskView
                {
                    Slot = i,
                    FlaskId = slot.Str(K.FlaskId),
                    Name = def.Str(K.Name),
                    Current = 1,
                    Max = 1,
                    Targeted = def.Is(K.Targeted),
                    Refusal = CombatLegality.CanUseFlask(c, i),
                });
            }
            return list;
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
