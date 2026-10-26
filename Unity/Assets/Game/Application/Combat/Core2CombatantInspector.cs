using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using J = Ashen.Generated.InspectorKeys;
using N = Ashen.Generated.InspectorNumbers;
using S = Ashen.Generated.StringKeys;
using X = Ashen.Generated.InspectorValues;

namespace Ashen.App.Combat
{
    /// <summary>One pool meter beside the figure of the combatant door (web combatantInspectorPreview: a Meter per pool with its value on the plate).</summary>
    public sealed class InspectorMeter
    {
        public string Id;
        public string Label;
        public string Value;
        public double Cur;
        public double Max;
        public double Pct;
    }

    /// <summary>One row of a section (a Row of the kit: its label and the status line on its plate); a disabled row says the section is empty or unknown.</summary>
    public sealed class InspectorRow
    {
        public string Label;
        public string Status;
        public bool Disabled;
    }

    /// <summary>An entry of the active list: its name, and what it does under it.</summary>
    public sealed class InspectorItem
    {
        public string Name;
        public string Detail;
    }

    /// <summary>An inert move card (the kit's option card): name, what it does, the meta line, and whether it is the live one.</summary>
    public sealed class InspectorCard
    {
        public string Name;
        public string Detail;
        public string Meta;
        public bool Selected;
    }

    /// <summary>One line of a panel (the bestiary, the prediction): the piece's kind and what it says.</summary>
    public sealed class InspectorLine
    {
        public string Kind;
        public string Text;
        public string Label;
        public string Status;
        public bool Learned;
        public string Aria;
        public List<InspectorCard> Cards;
        public List<string> Choices;
    }

    /// <summary>A fold of the deck section: its summary line and the cards under it.</summary>
    public sealed class InspectorFold
    {
        public string Summary;
        public List<InspectorCard> Cards = new List<InspectorCard>();
    }

    /// <summary>One block of the details pane, in the order the web puts them (see <see cref="X"/> kinds): a section, the active list, the bestiary, the prediction, the enemy deck or the revealed hand.</summary>
    public sealed class InspectorBlock
    {
        public string Kind;
        public string Id;
        public string Title;
        public string Knowledge;
        public List<InspectorRow> Rows = new List<InspectorRow>();
        public List<InspectorItem> Items = new List<InspectorItem>();
        public string Empty;
        public int Stage;
        public bool Eligible;
        public double Serial;
        public List<InspectorLine> Lines = new List<InspectorLine>();
        public string Note;
        public InspectorFold Current;
        public InspectorFold Last;
        public InspectorFold Authored;
        public string Counts;
    }

    /// <summary>
    /// What the combatant door says of one fighter (web ui/screens/combat.js combatantSubject through ui/components/combatantInspector.js combatantDetailBody and combatantInspectorPreview): the head, the
    /// pool meters beside the figure and the blocks of the details pane in order. Pure data: the screen draws it, the tests compare it with the web's.
    /// </summary>
    public sealed class CombatantInspectorView
    {
        public string Eyebrow;
        public string Title;
        public string PreviewName;
        public string EnemyId;
        public List<InspectorMeter> Meters = new List<InspectorMeter>();
        public List<InspectorBlock> Blocks = new List<InspectorBlock>();

        private static JObject Row(InspectorRow r)
        {
            var o = new JObject { [J.Label] = r.Label, [J.Status] = r.Status };
            if (r.Disabled) o[J.Disabled] = true;
            return o;
        }

        private static JArray Cards(IEnumerable<InspectorCard> cards) => new JArray(cards.Select(c => new JObject { [J.Name] = c.Name, [J.Detail] = c.Detail, [J.Meta] = c.Meta, [J.Selected] = c.Selected }));

        private static JObject Fold(InspectorFold f) => new JObject { [J.Summary] = f.Summary, [J.Cards] = Cards(f.Cards) };

        private static JObject Line(InspectorLine l)
        {
            var o = new JObject { [J.K] = l.Kind };
            if (l.Text != null) o[J.Text] = l.Text;
            if (l.Label != null) o[J.Label] = l.Label;
            if (l.Status != null) o[J.Status] = l.Status;
            if (l.Kind == X.LineStage) o[J.Learned] = l.Learned;
            if (l.Aria != null) o[J.Aria] = l.Aria;
            if (l.Cards != null) o[J.Cards] = Cards(l.Cards);
            if (l.Choices != null) o[J.Choices] = new JArray(l.Choices);
            return o;
        }

        /// <summary>The view as the oracle records it (Tools/oracle-core2-inspector.mjs): the same names, the same order of blocks.</summary>
        public JObject ToJson()
        {
            var blocks = new JArray();
            foreach (var b in Blocks)
            {
                var o = new JObject { [J.Kind] = b.Kind };
                if (b.Kind == X.BlockSection)
                {
                    o[J.Id] = b.Id; o[J.Title] = b.Title; o[J.Knowledge] = b.Knowledge; o[J.Rows] = new JArray(b.Rows.Select(Row));
                }
                else if (b.Kind == X.BlockAbilities)
                {
                    o[J.Title] = b.Title; o[J.Items] = new JArray(b.Items.Select(i => new JObject { [J.Name] = i.Name, [J.Detail] = i.Detail })); o[J.Empty] = b.Empty == null ? JValue.CreateNull() : new JValue(b.Empty);
                }
                else if (b.Kind == X.BlockKnowledge) { o[J.Stage] = b.Stage; o[J.Lines] = new JArray(b.Lines.Select(Line)); }
                else if (b.Kind == X.BlockPrediction)
                {
                    o[J.Eligible] = b.Eligible;
                    if (b.Eligible) o[J.Serial] = b.Serial;
                    o[J.Lines] = new JArray(b.Lines.Select(Line));
                }
                else if (b.Kind == X.BlockDeck)
                {
                    o[J.Eyebrow] = b.Title; o[J.Note] = b.Note; o[J.Current] = Fold(b.Current); o[J.Last] = Fold(b.Last); o[J.Authored] = Fold(b.Authored);
                }
                else if (b.Kind == X.BlockRevealedHand)
                {
                    o[J.Title] = b.Title; o[J.Rows] = new JArray(b.Rows.Select(Row)); o[J.Empty] = b.Empty == null ? JValue.CreateNull() : new JValue(b.Empty); o[J.Counts] = b.Counts;
                }
                blocks.Add(o);
            }
            return new JObject
            {
                [J.Eyebrow] = Eyebrow, [J.Title] = Title,
                [J.Preview] = new JObject
                {
                    [J.Name] = PreviewName,
                    [J.Meters] = new JArray(Meters.Select(m => new JObject { [J.Label] = m.Label, [J.Value] = m.Value, [J.Cur] = m.Cur, [J.Max] = m.Max, [J.Pct] = m.Pct })),
                },
                [J.Blocks] = blocks,
            };
        }
    }

    /// <summary>What the door needs beyond the fight: the words, the card text data (the Smash help of a move's tags) and the name the player gave the character.</summary>
    public sealed class InspectorContext
    {
        public StringTable Strings;
        public CardTextData Text;

        /// <summary>run.customization.name: the name the player gave the character (the door shows it in capitals), or null for the class's own name.</summary>
        public string PlayerName;
    }

    /// <summary>
    /// The combatant door of a Core 2 fight (P-7b, docs/DECISIONS.md D-1730..): the web's <c>combatantSubject</c> (ui/screens/combat.js) projected through
    /// <c>projectCombatantInspector</c> (ui/models/CombatantInspectorSections.js) and laid out as <c>combatantDetailBody</c> (ui/components/combatantInspector.js) lays it out.
    /// A Core 2 enemy always reads through its bestiary (enemyKnowledge.js) and no run keeps a ledger of what it learned, so the bestiary is the web's first stage.
    /// </summary>
    public static class Core2CombatantInspector
    {
        private sealed class Pool
        {
            public string Id;
            public string Label;
            public double Value;
            public double? Max;
        }

        private sealed class IntentRead
        {
            public string Name;
            public string Detail;
            public bool Hidden;
            public string KnowledgeRead;
            public string Stance;
        }

        private sealed class RevealedHand
        {
            public List<InspectorRow> Rows = new List<InspectorRow>();
            public string Counts;
        }

        private sealed class Subject
        {
            public bool Player;
            public string Name;
            public string Subtitle;
            public List<Pool> Pools = new List<Pool>();
            public IntentRead Intent;
            public List<KeyValuePair<string, string>> Statuses = new List<KeyValuePair<string, string>>();
            public List<InspectorItem> Abilities;
            public string SkillLabel;
            public List<KeyValuePair<string, string>> History;
            public bool Enemy;
            public RevealedHand Burden;
            public double Perception;
            public InspectorBlock Prediction;
            public InspectorBlock Deck;
        }

        // ------------------------------------------------------------------ words

        private static string Say(StringTable strings, string key, params string[] pairs)
        {
            var args = new StringArgs();
            for (var i = 0; i + 1 < pairs.Length; i += (int)N.Two) args.Add(pairs[i], pairs[i + 1]);
            return strings.Format(key, args);
        }

        private static string Num(double value) => CombatText.Number(value);

        /// <summary>combat.js words(value): every word capitalised, camel case and dashes and underscores spaced.</summary>
        public static string TitleWords(string value)
        {
            var text = Regex.Replace(Regex.Replace(value ?? string.Empty, X.WordsCamel, X.WordsCamelTo), X.WordsSeparators, X.Space);
            return Regex.Replace(text, X.WordsInitial, m => m.Value.ToUpperInvariant());
        }

        private static string TitleCase(string value) => Regex.Replace(value ?? string.Empty, X.WordsInitial, m => m.Value.ToUpperInvariant());

        private static string Join(StringTable strings, IEnumerable<string> parts) => string.Join(strings.Get(S.CombatantJoin), parts.Where(p => !string.IsNullOrEmpty(p)));

        // ------------------------------------------------------------------ statuses

        private sealed class StatusRead
        {
            public string Name;
            public string Label;
            public string Tooltip;
        }

        /// <summary>statusInstancePresentation(def, inst): the label a stack wears ("Strength ×3", a percent payload "Magic Vulnerable 25% · 2 turns") and the tooltip with the turns left.</summary>
        private static StatusRead ReadStatus(CombatState c, StringTable strings, string id, JObject inst)
        {
            if (!c.Data.Statuses.Has(id)) return null;
            var def = c.Data.Statuses.Get(id);
            var meter = inst.Obj(J.Meter);
            var raw = meter != null ? meter[J.Value] : inst[J.Stacks];
            var value = Js.IsNum(raw) ? Js.D(raw) : Js.Truthy(raw) ? Js.D(raw) : 0;
            var token = def.Obj(J.InstancePresentation)?.Str(J.ValueToken) ?? X.TokenStacks;
            var percent = token == X.TokenPercent;
            var duration = Js.IsFinite(inst[J.Duration]) ? Js.D(inst[J.Duration]) : (double?)null;
            var durationText = duration.HasValue ? Num(duration.Value) + X.Space + strings.Get(duration.Value == 1 ? S.StatusTipTurnSingular : S.StatusTipTurnPlural) : string.Empty;
            var name = def.Str(J.Name);
            var label = percent
                ? Say(strings, S.StatusTipPercent, X.ArgName, name, X.ArgValue, Num(value) + X.Percent, X.ArgDuration, durationText.Length > 0 ? strings.Get(S.StatusTipJoiner) + durationText : string.Empty)
                : Say(strings, S.StatusTipStack, X.ArgName, name, X.ArgValue, Num(value));
            var tooltip = Core2Readings.StatusText(def) + (duration.HasValue ? Say(strings, S.StatusTipTurnsLeft, X.ArgDuration, Num(duration.Value)) : string.Empty);
            return new StatusRead { Name = name, Label = label, Tooltip = tooltip };
        }

        private static double StatusAmount(JObject inst) => inst.Obj(J.Meter) != null ? Js.Or0(inst.Obj(J.Meter)[J.Value]) : Js.Or0(inst[J.Stacks]);

        // ------------------------------------------------------------------ the stance and the Evade of a fighter

        private static JObject ActiveStance(JObject entity)
        {
            var stance = entity.Obj(J.CombatStance);
            if (stance == null || !Js.Truthy(stance[J.Maneuver])) return null;
            if (!Js.Nullish(stance[J.ExpiresOnOwnerCycle]) && Js.Or0(entity[J.CombatOwnerCycle]) >= Js.D(stance[J.ExpiresOnOwnerCycle])) return null;
            return stance;
        }

        /// <summary>combatStanceRow(entity): the stance its last accepted play or telegraph set, as the name and what it means.</summary>
        private static InspectorItem StanceRow(StringTable strings, JObject entity)
        {
            var stance = ActiveStance(entity);
            if (stance == null) return null;
            if (Js.Truthy(stance[J.Staggered]))
                return new InspectorItem
                {
                    Name = strings.Get(S.CombatantStaggeredStance),
                    Detail = string.Join(X.Space, new[]
                    {
                        strings.Get(S.CombatantStaggeredBroke),
                        Js.D(stance[J.StaminaLoss]) > 0 ? Say(strings, S.CombatantStaggeredLoses, X.ArgValue, Num(Js.D(stance[J.StaminaLoss]))) : string.Empty,
                        Js.D(stance[J.VulnerableStacks]) > 0 ? Say(strings, S.CombatantStaggeredGains, X.ArgValue, Num(Js.D(stance[J.VulnerableStacks]))) : string.Empty,
                        strings.Get(S.CombatantStaggeredHolds),
                    }.Where(p => p.Length > 0)),
                };
            var maneuver = stance.Str(J.Maneuver);
            var labelKey = string.Format(CultureInfo.InvariantCulture, X.SigilLabelKey, maneuver);
            var helpKey = string.Format(CultureInfo.InvariantCulture, X.SigilHelpKey, maneuver);
            var label = strings.Has(labelKey) ? strings.Get(labelKey) : TitleCase(maneuver);
            var posture = string.Join(strings.Get(S.CombatantJoin), new[]
            {
                TitleCase(Js.Truthy(stance[J.School]) ? stance.Str(J.School) : stance.Str(J.Camp)),
                Js.Truthy(stance[J.Reach]) ? Say(strings, S.CombatantStanceReach, X.ArgName, TitleCase(stance.Str(J.Reach))) : string.Empty,
            }.Where(p => p.Length > 0));
            return new InspectorItem
            {
                Name = Say(strings, S.CombatantStanceName, X.ArgName, label),
                Detail = string.Join(X.Space, new[]
                {
                    strings.Has(helpKey) ? strings.Get(helpKey) : string.Empty,
                    posture.Length > 0 ? Say(strings, S.CombatantStancePosture, X.ArgValue, posture) : string.Empty,
                    strings.Get(S.CombatantStanceHolds),
                }.Where(p => p.Length > 0)),
            };
        }

        private static double EvadeCharges(JObject entity)
        {
            var prepared = entity.Obj(J.CombatEvade);
            if (!(Js.D(prepared?[J.Charges]) > 0)) return 0;
            if (Js.Truthy(prepared[J.WhileStatus]))
            {
                var status = entity.Obj(J.Statuses)?.Obj(prepared.Str(J.WhileStatus));
                if (!(Js.D(status?[J.Stacks]) > 0) || (Js.IsNum(status[J.Duration]) && Js.D(status[J.Duration]) == 0)) return 0;
            }
            if (!Js.Nullish(prepared[J.ExpiresOnOwnerCycle]) && Js.Or0(entity[J.CombatOwnerCycle]) >= Js.D(prepared[J.ExpiresOnOwnerCycle])) return 0;
            return Math.Floor(Js.D(prepared[J.Charges]));
        }

        /// <summary>preparedEvadeRow(entity): "Evade ×N" with the charge rules.</summary>
        private static InspectorItem EvadeRow(StringTable strings, JObject entity)
        {
            var charges = EvadeCharges(entity);
            if (charges == 0) return null;
            return new InspectorItem
            {
                Name = Say(strings, S.CombatantEvadeName, X.ArgValue, Num(charges)),
                Detail = Say(strings, S.CombatantHelpEvade, X.ArgCount, Num(charges), X.ArgCharges, strings.Get(charges == 1 ? S.CombatantHelpCharge : S.CombatantHelpCharges)),
            };
        }

        // ------------------------------------------------------------------ the pools of a fighter

        private static Pool P(string id, string label, double value, double? max = null) => new Pool { Id = id, Label = label, Value = value, Max = max };

        private static List<Pool> PlayerPools(CombatState c, StringTable strings, JObject e)
        {
            var poise = e.Obj(J.PoiseMeter);
            var pools = new List<Pool>
            {
                P(X.PoolHp, strings.Get(S.CombatantPoolHp), Js.D(e[J.Hp]), Js.D(e[J.MaxHp])),
                P(X.PoolMp, strings.Get(S.CombatantPoolMp), Js.D(e[J.Mana]), Js.D(e[J.MaxMana])),
                P(X.PoolPoise, strings.Get(S.CombatantPoolPoise), Js.Or0(poise?[J.Value]), Js.Or0(poise?[J.Max])),
            };
            var ward = e.Obj(J.WardMeter);
            if (ward != null) pools.Add(P(X.PoolWard, strings.Get(S.CombatantPoolWard), Js.Or0(ward[J.Value]), Js.Truthy(ward[J.Max]) ? Js.D(ward[J.Max]) : Js.D(ward[J.Max])));
            if (Js.D(e[J.PoiseGuard]) > 0) pools.Add(P(X.PoolPoiseGuard, strings.Get(S.CombatantPoolPoiseGuard), Js.D(e[J.PoiseGuard])));
            if (Js.D(e[J.WardGuard]) > 0) pools.Add(P(X.PoolWardGuard, strings.Get(S.CombatantPoolWardGuard), Js.D(e[J.WardGuard])));
            if (Js.Truthy(e[J.Ratings]))
                foreach (var id in new[] { X.RatingAr, X.RatingDr, X.RatingPr })
                    pools.Add(P(id, id.ToUpperInvariant(), CorePools.RatingValue(c, e, id)));
            pools.Add(P(X.PoolBlock, strings.Get(S.CombatantPoolBlock), Js.Or0(e[J.Block])));
            return pools;
        }

        private static List<Pool> EnemyPools(StringTable strings, JObject e)
        {
            var poise = e.Obj(J.PoiseMeter);
            var pools = new List<Pool> { P(X.PoolHp, strings.Get(S.CombatantPoolHp), Js.D(e[J.Hp]), Js.D(e[J.MaxHp])) };
            if (Js.IsFinite(e[J.MaxStamina]))
                pools.Add(P(X.PoolSp, strings.Get(S.CombatantPoolSp), !Js.Nullish(e[J.FixCoreStaminaRemaining]) ? Js.D(e[J.FixCoreStaminaRemaining]) : !Js.Nullish(e[J.Stamina]) ? Js.D(e[J.Stamina]) : 0, Js.D(e[J.MaxStamina])));
            pools.Add(P(X.PoolPoise, strings.Get(S.CombatantPoolPoise), Js.Or0(poise?[J.Value]), Js.Or0(poise?[J.Max])));
            var ward = e.Obj(J.WardMeter);
            if (ward != null) pools.Add(P(X.PoolWard, strings.Get(S.CombatantPoolWard), Js.Or0(ward[J.Value]), Js.D(ward[J.Max])));
            pools.Add(P(X.PoolBlock, strings.Get(S.CombatantPoolBlock), Js.Or0(e[J.Block])));
            return pools;
        }

        // ------------------------------------------------------------------ the intent

        /// <summary>concealKnowledgeIntent(read, serial): what an unread move shows of itself.</summary>
        private static JObject Concealed(JObject enemy)
        {
            var action = enemy.Obj(J.KnowledgeAction);
            var read = action?.Obj(J.Reads)?.Obj(X.Player);
            var clue = read?.Str(J.Visibility) == X.ReadClue;
            var labels = new[] { X.LabelUnknown, X.LabelAttack, X.LabelMagic, X.LabelPreparing };
            return new JObject
            {
                [J.Kind] = X.KindUnknown, [J.Hidden] = true, [J.Revealed] = false, [J.Stance] = X.KindUnknown, [J.MoveId] = JValue.CreateNull(),
                [J.Label] = clue && labels.Contains(read.Str(J.Label)) ? read.Str(J.Label) : X.LabelUnknown,
                [J.KnowledgeRead] = clue ? X.ReadClue : X.KindUnknown, [J.ActionSerial] = action != null && Js.Truthy(action[J.Serial]) ? action[J.Serial].DeepClone() : Js.N(0),
            };
        }

        /// <summary>projectCombatIntent(combat, enemyId) as the door reads it: the move itself once the player has read it, otherwise only what the knowledge gate shows.</summary>
        private static JObject ProjectIntent(CombatState c, JObject enemy) => CoreVisibility.IntentExact(c, enemy) ? CoreIntentProjection.Project(c, enemy) : Concealed(enemy);

        /// <summary>enemyIntentRead(combat, enemy, { preview, inspectedMoves, unknownDetail }): the name and the detail of the move the enemy telegraphs.</summary>
        private static IntentRead ReadIntent(CombatState c, StringTable strings, JObject enemy, JObject preview, List<EnemyMoveCard> cards)
        {
            var hidden = Js.Truthy(preview[J.Hidden]) || (preview[J.Revealed] != null && preview[J.Revealed].Type == JTokenType.Boolean && !preview.Is(J.Revealed));
            var read = new IntentRead { Hidden = hidden, KnowledgeRead = preview.Str(J.KnowledgeRead), Stance = preview.Str(J.Stance) };
            if (hidden)
            {
                read.Name = Js.Truthy(preview[J.Label]) ? preview.Str(J.Label) : X.LabelUnknown;
                read.Detail = strings.Get(S.CombatTargetHudUnknown);
                return read;
            }
            var moveId = preview.Str(J.MoveId);
            if (!Js.Truthy(preview[J.MoveId]))
            {
                read.Name = Core2EnemyMoveCards.Words(Js.Truthy(preview[J.Kind]) ? preview.Str(J.Kind) : strings.Get(S.CombatantUnknownKind));
                read.Detail = read.Name;
                return read;
            }
            var card = cards.FirstOrDefault(m => m.MoveId == moveId);
            read.Name = card?.Name ?? Core2EnemyMoveCards.Words(moveId);
            read.Detail = card?.Detail ?? Core2EnemyMoveCards.Words(Js.Truthy(preview[J.Kind]) ? preview.Str(J.Kind) : strings.Get(S.CombatantUnknownAction));
            return read;
        }

        // ------------------------------------------------------------------ the move of an enemy in a line

        /// <summary>moveDetail(move): what a move did, in a line (the Previous actions rows).</summary>
        private static string MoveDetail(StringTable strings, JObject enemy, JObject move)
        {
            var pieces = new List<string>();
            var damage = CoreIntents.MoveDamage(enemy, move);
            if (!Js.Nullish(damage))
            {
                var hits = Js.D(move?[J.Hits]);
                pieces.Add(Say(strings, S.CombatantHistoryDamage, X.ArgValue, hits > 1 ? Say(strings, S.CombatantMoveTimes, X.ArgValue, Num(Js.D(damage)), X.ArgHits, Num(hits)) : Num(Js.D(damage))));
            }
            if (!Js.Nullish(move?[J.Block])) pieces.Add(Say(strings, S.CombatantHistoryBlock, X.ArgValue, Num(Js.D(move[J.Block]))));
            foreach (var effect in Js.Items(move?[J.Effects]).OfType<JObject>())
            {
                var op = effect.Str(J.Op);
                if (op == X.OpApplyStatus) pieces.Add(Say(strings, S.CombatantHistoryApplies, X.ArgName, TitleWords(effect.Str(J.Status))));
                else if (op == X.OpHeal) pieces.Add(strings.Get(S.CombatantHistoryHeals));
                else if (op == X.OpAddCard) pieces.Add(Say(strings, S.CombatantHistoryAdds, X.ArgName, TitleWords(effect.Str(J.Card))));
                else pieces.Add(TitleWords(op));
            }
            if (pieces.Count > 0) return string.Join(strings.Get(S.CombatantJoin), pieces);
            return TitleWords(move != null && Js.Truthy(move[J.Intent]) ? move.Str(J.Intent) : strings.Get(S.CombatantUnknownAction));
        }

        // ------------------------------------------------------------------ the enemy's deck and the bestiary

        private static List<InspectorCard> AsCards(IEnumerable<EnemyMoveCard> cards) =>
            cards.Select(m => new InspectorCard { Name = m.Name, Detail = m.Detail, Meta = m.Meta, Selected = m.Active }).ToList();

        private sealed class DeckRow
        {
            public EnemyMoveCard Move;
            public string CardId;
            public string Name;
            public string Meta;
            public string Detail;
            public bool Active;
            public bool Last;
            public double Copies;
        }

        /// <summary>enemyDeckInspection(combat, enemy, moves, preview): the authored deck read against the moves the enemy has, and what it is playing and last played; null when the profile authors no such enemy.</summary>
        private static InspectorBlock DeckBlock(CombatState c, StringTable strings, JObject enemy, List<EnemyMoveCard> moves, JObject preview)
        {
            var authored = Js.Items(c.CoreData.Content(J.ContentEnemies)).OfType<JObject>().FirstOrDefault(row => row.Str(J.Id) == enemy.Str(J.EnemyId));
            if (authored == null) return null;
            var definitions = Js.Items(c.CoreData.Content(J.ContentEnemyCards)).OfType<JObject>().Where(card => Js.Truthy(card[J.Id])).GroupBy(card => card.Str(J.Id)).ToDictionary(g => g.Key, g => g.Last());
            var runtime = c.Data.Enemies.Get(enemy.Str(J.EnemyId));
            var performed = Js.Items(enemy[J.PerformedMoves]).Select(Js.Str).ToList();
            var lastMoveId = performed.Count > 0 ? performed[performed.Count - 1] : null;
            var copies = new List<KeyValuePair<string, double>>();
            void Count(string cardId, bool again)
            {
                var at = copies.FindIndex(p => p.Key == cardId);
                if (at >= 0) { if (again) copies[at] = new KeyValuePair<string, double>(cardId, copies[at].Value + 1); }
                else copies.Add(new KeyValuePair<string, double>(cardId, 1));
            }
            foreach (var id in Js.Items(authored[J.DefaultDeck]).Select(Js.Str)) Count(id, true);
            foreach (var id in Js.Items(authored[J.UnlockCards]).Select(Js.Str)) Count(id, false);
            var hidden = preview != null && Js.Truthy(preview[J.Hidden]);
            var rows = new List<DeckRow>();
            foreach (var pair in copies)
            {
                definitions.TryGetValue(pair.Key, out var card);
                var moveId = authored.Obj(J.CardHints)?.Obj(pair.Key)?.Str(J.LegacyMove);
                var move = moves.FirstOrDefault(m => m.MoveId == moveId);
                var authoredMove = moveId != null ? runtime.Obj(J.Moves)?.Obj(moveId) : null;
                if (card == null || move == null || authoredMove == null) continue;
                var cost = CoreIntents.MoveSp(c, authoredMove, enemy, moveId);
                var active = !hidden && preview?.Str(J.MoveId) == moveId;
                var last = moveId == lastMoveId;
                var state = Join(strings, new[] { active ? strings.Get(S.CombatantDeckCurrent) : null, last ? strings.Get(S.CombatantDeckLast) : null, move.Locked ? strings.Get(S.CombatantMoveLocked) : null });
                rows.Add(new DeckRow
                {
                    Move = move, CardId = pair.Key, Name = card.Str(J.Name), Active = active, Last = last, Copies = pair.Value, Detail = move.Detail,
                    Meta = Join(strings, new[]
                    {
                        state.Length > 0 ? state : strings.Get(S.CombatantDeckWord),
                        pair.Value > 1 ? Say(strings, S.CombatantDeckCopies, X.ArgValue, Num(pair.Value)) : string.Empty,
                        Say(strings, S.CombatantDeckCost, X.ArgValue, Num(cost)),
                        Say(strings, S.CombatantDeckBase, X.ArgValue, Num(Js.Or0(card[J.StaminaCost])), X.ArgMana, Num(Js.Or0(card[J.ManaCost]))),
                        move.Meta,
                    }),
                });
            }
            var lastRow = rows.FirstOrDefault(r => r.Last);
            var receipt = c.EventLog.AsEnumerable().Reverse().FirstOrDefault(e => e.Str(J.Type) == X.EventMoveStarted && e.Str(J.SourceId) == enemy.Str(J.Id) && e.Str(J.MoveId) == lastMoveId)?.Obj(J.ActionPreview);
            InspectorCard Card(DeckRow r, string meta = null, string detail = null) => new InspectorCard { Name = r.Name, Detail = detail ?? r.Detail, Meta = meta ?? r.Meta, Selected = r.Active };
            InspectorCard played = null;
            if (lastRow != null)
            {
                if (receipt != null)
                {
                    var spent = !Js.Nullish(receipt[J.TotalSp]) ? receipt[J.TotalSp] : receipt[J.SpCost];
                    var detail = string.Join(strings.Get(S.CombatantJoin), Js.Items(receipt[J.ActionEffects]).OfType<JObject>().Select(effect =>
                        Core2EnemyMoveCards.EffectText(c, strings, effect) + (effect[J.ConditionMet] != null && effect[J.ConditionMet].Type == JTokenType.Boolean && !effect.Is(J.ConditionMet) ? strings.Get(S.CombatantMoveConditionNotMet) : string.Empty)));
                    played = Card(lastRow, Say(strings, S.CombatantDeckPlayed, X.ArgValue, Js.IsNum(spent) ? Num(Js.D(spent)) : string.Empty, X.ArgName, lastRow.CardId), detail.Length > 0 ? detail : lastRow.Detail);
                }
                else played = Card(lastRow);
            }
            var current = hidden ? null : rows.FirstOrDefault(r => r.Active);
            InspectorFold Fold(string label, InspectorCard card, string empty) => new InspectorFold { Summary = Say(strings, S.CombatantDeckSummary, X.ArgLabel, label, X.ArgName, card?.Name ?? empty), Cards = card != null ? new List<InspectorCard> { card } : new List<InspectorCard>() };
            var total = rows.Sum(r => r.Copies > 0 ? r.Copies : 1);
            return new InspectorBlock
            {
                Kind = X.BlockDeck, Title = strings.Get(S.CombatantDeckEyebrow), Note = strings.Get(S.CombatantDeckNote),
                Current = Fold(strings.Get(S.CombatantDeckCurrentAction), current == null ? null : Card(current), hidden ? strings.Get(S.GameProfileCombatUnreadIntent) : strings.Get(S.CombatantDeckNone)),
                Last = Fold(strings.Get(S.CombatantDeckLastPlayed), played, strings.Get(S.CombatantDeckNoneYet)),
                Authored = new InspectorFold { Summary = Say(strings, S.CombatantDeckAuthored, X.ArgValue, Num(total)), Cards = rows.Select(r => Card(r)).ToList() },
            };
        }

        private static InspectorLine L(string kind, string text) => new InspectorLine { Kind = kind, Text = text };

        /// <summary>renderEnemyKnowledge(projectEnemyKnowledge(def, record)) for an enemy no run has learned (a Core 2 run keeps no ledger): the first stage, everything it would unlock locked.</summary>
        private static InspectorBlock KnowledgeBlock(CombatState c, StringTable strings, double perception)
        {
            var target = c.CoreData.EnemyKnowledgeRules.Obj(J.Bestiary).Num(J.EncountersToMaster);
            var progress = CoreKnowledgeLedger.Progress(null, target);
            var thresholds = CoreKnowledgeLedger.Thresholds(progress.Num(J.Target));
            var stage = (int)progress.Num(J.Stage);
            var names = new[] { X.StageEncountered, X.StageStudied, X.StageFamiliar, X.StageUnderstood, X.StageMastered };
            var next = progress.Obj(J.NextStage);
            var lines = new List<InspectorLine>
            {
                L(X.LineEyebrow, strings.Get(S.KnowledgeBestiary)),
                L(X.LineTitle, Say(strings, S.KnowledgeProgress, X.ArgLabel, progress.Str(J.Label), X.ArgPoints, Num(progress.Num(J.Points)), X.ArgTarget, Num(progress.Num(J.Target)))),
                L(X.LineProse, strings.Get(S.KnowledgeLifetime)),
                L(X.LineStatus, next != null ? Say(strings, S.KnowledgeNext, X.ArgLabel, next.Str(J.Label), X.ArgPoints, Num(next.Num(J.Points))) : strings.Get(S.KnowledgeMastered)),
                L(X.LineProse, Say(strings, S.KnowledgePerception, X.ArgLevel, Num(perception))),
            };
            for (var i = 0; i < names.Length; i++)
            {
                var learned = stage >= i + 1;
                lines.Add(new InspectorLine
                {
                    Kind = X.LineStage, Learned = learned,
                    Text = Say(strings, S.KnowledgeStage, X.ArgStage, Num(i + 1), X.ArgLabel, names[i],
                        X.ArgStatus, learned ? strings.Get(S.KnowledgeLearned) : Say(strings, S.KnowledgeUnlock, X.ArgPoints, Num(thresholds[i]))),
                });
            }
            lines.Add(new InspectorLine { Kind = X.LineHairline });
            lines.Add(L(X.LineEyebrow, strings.Get(S.KnowledgeResources)));
            lines.Add(L(X.LineStatus, Say(strings, S.KnowledgeResourcesLocked, X.ArgPoints, Num(thresholds[1]))));
            lines.Add(new InspectorLine { Kind = X.LineHairline });
            lines.Add(L(X.LineEyebrow, strings.Get(S.KnowledgeMoves)));
            lines.Add(L(X.LineStatus, Say(strings, S.KnowledgeMovesLocked, X.ArgPoints, Num(thresholds[(int)N.Two]))));
            lines.Add(new InspectorLine { Kind = X.LineHairline });
            lines.Add(L(X.LineEyebrow, strings.Get(S.KnowledgeLore)));
            lines.Add(L(X.LineStatus, strings.Get(S.KnowledgeLoreLocked)));
            return new InspectorBlock { Kind = X.BlockKnowledge, Stage = stage, Lines = lines };
        }

        /// <summary>knowledgePredictionModel(combat, enemy) through renderIntentPrediction: what the prediction says, and whether the player may make one now.</summary>
        private static InspectorBlock PredictionBlock(CombatState c, StringTable strings, JObject enemy)
        {
            var action = enemy.Obj(J.KnowledgeAction);
            var read = action?.Obj(J.Reads)?.Obj(X.Player);
            var prediction = Js.Truthy(read?[J.Prediction]) ? read.Str(J.Prediction) : null;
            var resolved = Js.Truthy(read?[J.Resolved]);
            var correct = read?[J.Correct];
            var serial = action != null && Js.Truthy(action[J.Serial]) ? Js.D(action[J.Serial]) : 0;
            var lines = new List<InspectorLine>
            {
                L(X.LineEyebrow, strings.Get(S.KnowledgePrediction)),
                L(X.LineStatus, prediction != null
                    ? Say(strings, S.KnowledgePredictionStatus, X.ArgPrediction, prediction, X.ArgStatus,
                        strings.Get(resolved ? correct != null && correct.Type == JTokenType.Boolean ? (correct.Value<bool>() ? S.KnowledgePredictionCorrect : S.KnowledgePredictionIncorrect) : S.KnowledgePredictionCancelled : S.KnowledgePredictionAwaiting))
                    : strings.Get(S.KnowledgePredictionChoose)),
            };
            foreach (var row in Js.Items(c.Fields.Obj(J.EnemyKnowledge)?.Obj(J.Owners)?.Obj(X.Player)?[J.Feedback]).OfType<JObject>().Where(r => r.Str(J.EnemyInstanceId) == enemy.Str(J.Id)))
                lines.Add(L(X.LineStatus, Say(strings, S.KnowledgePredictionFeedback, X.ArgSerial, Num(row.Num(J.ActionSerial)), X.ArgPrediction, row.Str(J.Prediction), X.ArgOutcome,
                    strings.Get(row.Str(J.Outcome) == X.OutcomeCancelled ? S.KnowledgePredictionFeedbackCancelled : row.Is(J.Correct) ? S.KnowledgePredictionFeedbackCorrect : S.KnowledgePredictionFeedbackIncorrect))));
            var player = c.Player;
            var eligible = c.Fields.Obj(J.EnemyKnowledge) != null && c.Phase == X.PhasePlayer && !Js.Truthy(new JValue(c.Result)) && enemy.Is(J.Alive)
                && enemy.Obj(J.Intent)?.Str(J.Kind) != X.KindStaggered && player.Is(J.Alive) && !(action == null || Js.Truthy(action[J.Executed]) || Js.Truthy(action[J.Cancelled]))
                && read != null && read.Str(J.Visibility) == X.ReadUnknown && read[J.Prediction]?.Type == JTokenType.Null && !resolved;
            if (!eligible) return new InspectorBlock { Kind = X.BlockPrediction, Eligible = false, Lines = lines };
            lines.Add(L(X.LineProse, strings.Get(S.KnowledgePredictionHelp)));
            lines.Add(new InspectorLine { Kind = X.LineSelect, Label = strings.Get(S.KnowledgePredictionExpected), Aria = strings.Get(S.KnowledgePredictionAction), Choices = Core2CombatSession.PredictionChoices.ToList() });
            lines.Add(L(X.LineButton, strings.Get(S.KnowledgePredictionCommit)));
            return new InspectorBlock { Kind = X.BlockPrediction, Eligible = true, Serial = serial, Lines = lines };
        }

        // ------------------------------------------------------------------ the subject

        private static Subject PlayerSubject(CombatState c, InspectorContext ctx, JObject e)
        {
            var strings = ctx.Strings;
            var className = c.Data.Classes.Has(e.Str(J.ClassId)) ? c.Data.Classes.Get(e.Str(J.ClassId)).Str(J.Name) : e.Str(J.ClassId);
            var level = Js.IsInt(c.Fields[Ashen.Generated.Core2CombatKeys.CharacterLevel]) && c.Fields.Num(Ashen.Generated.Core2CombatKeys.CharacterLevel) >= 1 ? c.Fields.Num(Ashen.Generated.Core2CombatKeys.CharacterLevel) : 1;
            var abilities = new List<InspectorItem>();
            if (Js.Truthy(e[J.StanceId]) && c.Data.Stances.Has(e.Str(J.StanceId)))
            {
                var stance = c.Data.Stances.Get(e.Str(J.StanceId));
                var text = Core2Readings.StatusText(stance);
                abilities.Add(new InspectorItem { Name = stance.Str(J.Name), Detail = (text.Length > 0 ? text : strings.Get(S.CombatantStanceFallback)) + X.Space + strings.Get(S.CombatantStanceDuration) });
            }
            abilities.AddRange(new[] { StanceRow(strings, e), EvadeRow(strings, e) }.Where(r => r != null));
            foreach (var p in (e.Obj(J.Statuses) ?? new JObject()).Properties())
            {
                if (!(p.Value is JObject inst) || !(StatusAmount(inst) > 0)) continue;
                var read = ReadStatus(c, strings, p.Name, inst);
                if (read == null) continue;
                abilities.Add(new InspectorItem { Name = read.Label, Detail = !string.IsNullOrEmpty(read.Tooltip) ? read.Tooltip : read.Name });
            }
            return new Subject
            {
                Player = true,
                Name = (!string.IsNullOrEmpty(ctx.PlayerName) ? ctx.PlayerName : className).ToUpperInvariant(),
                Subtitle = Say(strings, S.CombatantPlayerSubtitle, X.ArgName, className, X.ArgLevel, Num(level)),
                Pools = PlayerPools(c, strings, e),
                Abilities = abilities,
                SkillLabel = strings.Get(S.CombatantActiveSkills),
            };
        }

        private static Subject EnemySubject(CombatState c, InspectorContext ctx, JObject e)
        {
            var strings = ctx.Strings;
            var def = c.Data.Enemies.Get(e.Str(J.EnemyId));
            var env = new FaceEnv(c, ctx.Text);
            var preview = ProjectIntent(c, e);
            var moves = Core2EnemyMoveCards.Build(c, env, strings, e, preview);
            var read = ReadIntent(c, strings, e, preview, moves);
            var tags = Js.Items(def[J.Tags]).Select(Js.Str).Select(Core2EnemyMoveCards.Words).Where(t => t.Length > 0).ToList();
            var subject = new Subject
            {
                Enemy = true,
                Name = def.Str(J.Name),
                Subtitle = tags.Count > 0 ? string.Join(strings.Get(S.CombatantJoin), tags) : strings.Get(S.CombatLogRoleEnemy),
                Pools = EnemyPools(strings, e),
                Intent = read,
                Perception = Js.Or0(c.Skills.Obj(J.Perception)?[J.Level]),
                SkillLabel = strings.Get(S.CombatantMoveSet),
            };
            if (CoreVisibility.RevealedUntil(c, e, X.Player) != null) subject.Burden = BurdenRead(c, strings, e);
            // The stance an enemy's telegraph set reads only as far as its intent does; a prepared Evade is told either way.
            foreach (var row in new[] { read.Hidden ? null : StanceRow(strings, e), EvadeRow(strings, e) }.Where(r => r != null)) subject.Statuses.Add(new KeyValuePair<string, string>(row.Name, row.Detail));
            foreach (var p in (e.Obj(J.Statuses) ?? new JObject()).Properties())
            {
                if (!(p.Value is JObject inst) || !(StatusAmount(inst) > 0)) continue;
                var status = ReadStatus(c, strings, p.Name, inst);
                if (status != null) subject.Statuses.Add(new KeyValuePair<string, string>(status.Name, status.Tooltip));
            }
            var performed = Js.Items(e[J.PerformedMoves]).Select(Js.Str).ToList();
            subject.History = performed.Select(id => new KeyValuePair<string, string>(TitleWords(id), MoveDetail(strings, e, def.Obj(J.Moves)?.Obj(id)))).ToList();
            subject.Prediction = PredictionBlock(c, strings, e);
            subject.Deck = DeckBlock(c, strings, e, moves, preview);
            return subject;
        }

        /// <summary>projectEnemyBurdenHand(enemy, ...): the cards of the hand a read has opened (each with its type and how many), and the size of the four piles.</summary>
        private static RevealedHand BurdenRead(CombatState c, StringTable strings, JObject enemy)
        {
            var piles = enemy.Obj(J.BurdenPiles) ?? new JObject();
            var kinds = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var instance in Js.Items(piles[J.Hand]).OfType<JObject>())
            {
                var id = instance.Str(J.CardId) ?? string.Empty;
                if (counts.ContainsKey(id)) counts[id]++;
                else { counts[id] = 1; kinds.Add(id); }
            }
            var hand = new RevealedHand();
            foreach (var id in kinds)
            {
                var def = c.Data.Cards.Has(id) ? c.Data.Cards.Get(id) : null;
                hand.Rows.Add(new InspectorRow
                {
                    Label = Say(strings, S.CombatantRevealedCount, X.ArgName, Js.Truthy(def?[J.Name]) ? def.Str(J.Name) : strings.Get(S.CombatantRevealedUnknownCard), X.ArgCount, Num(counts[id])),
                    Status = Js.Truthy(def?[J.Type]) ? def.Str(J.Type) : strings.Get(S.CombatantRevealedUnknownType),
                });
            }
            hand.Counts = string.Join(strings.Get(S.CombatantJoin), new[] { X.ZoneDraw, X.ZoneHand, X.ZoneDiscard, X.ZoneExhaust }.Select(z => Say(strings, S.CombatantRevealedZone, X.ArgName, z, X.ArgCount, Num(Js.Items(piles[z]).Count()))));
            return hand;
        }

        // ------------------------------------------------------------------ the sections

        /// <summary>projectedSection: a section that is known (rows), known to be empty (none) or not revealed (unknown, rows null).</summary>
        private static InspectorBlock Section(string id, string title, List<InspectorRow> rows, StringTable strings)
        {
            var block = new InspectorBlock { Kind = X.BlockSection, Id = id, Title = title };
            if (rows == null) { block.Knowledge = X.KnowledgeUnknown; block.Rows.Add(new InspectorRow { Label = strings.Get(S.CombatantEmptyUnknown), Status = string.Empty, Disabled = true }); return block; }
            if (rows.Count == 0) { block.Knowledge = X.KnowledgeNone; block.Rows.Add(new InspectorRow { Label = strings.Get(S.CombatantEmptyNone), Status = string.Empty, Disabled = true }); return block; }
            block.Knowledge = X.KnowledgeKnown;
            block.Rows.AddRange(rows);
            return block;
        }

        private static InspectorRow Fact(StringTable strings, string label, string value, string detail = null) =>
            new InspectorRow { Label = label, Status = string.Join(strings.Get(S.CombatantJoin), new[] { value, detail }.Where(p => !string.IsNullOrEmpty(p))) };

        private static string PoolValue(StringTable strings, Pool p) => p.Max == null ? Num(p.Value) : Say(strings, S.CombatantPoolValue, X.ArgValue, Num(p.Value), X.ArgMax, Num(p.Max.Value));

        // ------------------------------------------------------------------ the view

        /// <summary>The door of one fighter of the fight: the player (pass <c>c.Player</c>) or one of its enemies.</summary>
        public static CombatantInspectorView Build(CombatState c, JObject entity, InspectorContext ctx)
        {
            if (c == null || entity == null || ctx?.Strings == null) throw new ArgumentNullException(nameof(c));
            var strings = ctx.Strings;
            var subject = entity.Str(J.Kind) == X.Player ? PlayerSubject(c, ctx, entity) : EnemySubject(c, ctx, entity);
            var blocks = new List<InspectorBlock>();
            var hp = subject.Pools.First(p => p.Id == X.PoolHp);
            var block = subject.Pools.FirstOrDefault(p => p.Id == X.PoolBlock);

            // summary
            IntentRead intent = null;
            if (subject.Intent != null)
            {
                var read = subject.Intent;
                if (read.Hidden && !string.IsNullOrEmpty(read.KnowledgeRead))
                    intent = new IntentRead { Name = new[] { X.LabelUnknown, X.LabelAttack, X.LabelMagic, X.LabelPreparing }.Contains(read.Name) ? read.Name : X.LabelUnknown, Detail = strings.Get(S.CombatantIntentUnread) };
                else if (read.Hidden)
                    intent = new IntentRead { Name = Say(strings, S.CombatantIntentHidden, X.ArgName, read.Stance), Detail = strings.Get(S.CombatantIntentUnreadMove) };
                else intent = read;
            }
            var summary = new List<InspectorRow> { Fact(strings, strings.Get(S.CombatantSummaryHp), PoolValue(strings, hp)) };
            foreach (var sp in subject.Pools.Where(p => p.Id == X.PoolSp)) summary.Add(Fact(strings, strings.Get(S.CombatantPoolSp), PoolValue(strings, sp)));
            if (intent != null) summary.Add(Fact(strings, strings.Get(S.CombatantSummaryIntent), intent.Name, intent.Detail ?? string.Empty));
            summary.Add(Fact(strings, strings.Get(S.CombatantSummaryDefense), Say(strings, S.CombatantSummaryBlock, X.ArgAmount, Num(block?.Value ?? 0))));
            blocks.Add(Section(X.SectionSummary, strings.Get(S.CombatantSectionSummary), summary, strings));

            // current state: the pools in play, then the effects (an active list shows its own)
            var state = subject.Pools.Where(p => p.Id != X.PoolHp && p.Id != X.PoolBlock && (p.Max == null ? p.Value > 0 : p.Max > 0)).Select(p => Fact(strings, p.Label, PoolValue(strings, p))).ToList();
            if (subject.Abilities == null) state.AddRange(subject.Statuses.Select(s => Fact(strings, s.Key, string.Empty, s.Value ?? string.Empty)));
            blocks.Add(Section(X.SectionState, strings.Get(S.CombatantSectionState), state, strings));

            var history = subject.History == null ? null : subject.History.AsEnumerable().Reverse().Select(h => Fact(strings, h.Key, string.Empty, h.Value ?? string.Empty)).ToList();
            var abilities = subject.Abilities;

            if (subject.Burden != null)
            {
                var hand = new InspectorBlock { Kind = X.BlockRevealedHand, Title = strings.Get(S.CombatantRevealedTitle), Rows = subject.Burden.Rows, Counts = subject.Burden.Counts };
                hand.Empty = hand.Rows.Count == 0 ? strings.Get(S.CombatantRevealedEmpty) : null;
                blocks.Add(hand);
            }
            if (subject.Enemy)
            {
                blocks.Add(KnowledgeBlock(c, strings, subject.Perception));
                blocks.Add(subject.Prediction);
            }
            blocks.Add(Section(X.SectionHistory, strings.Get(S.CombatantSectionHistory), history, strings));
            if (subject.Deck != null) blocks.Add(subject.Deck);
            if (!subject.Enemy)
            {
                if (abilities.Count > 0)
                    blocks.Add(new InspectorBlock
                    {
                        Kind = X.BlockAbilities, Title = Say(strings, S.CombatantActiveList, X.ArgName, strings.Get(S.CombatantActiveSkills), X.ArgCount, Num(abilities.Count)), Items = abilities,
                    });
                else blocks.Add(Section(X.SectionAbilities, subject.SkillLabel, new List<InspectorRow>(), strings));
            }
            blocks.Add(Section(X.SectionTraits, strings.Get(S.CombatantSectionTraits), null, strings));
            blocks.Add(Section(X.SectionLore, strings.Get(S.CombatantSectionLore), null, strings));

            var view = new CombatantInspectorView { Eyebrow = !string.IsNullOrEmpty(subject.Subtitle) ? subject.Subtitle : strings.Get(S.CombatModalTargetEyebrow), Title = subject.Name, PreviewName = subject.Name, EnemyId = subject.Enemy ? entity.Str(J.Id) : null, Blocks = blocks };
            foreach (var id in new[] { X.PoolHp, X.PoolSp, X.PoolMp, X.PoolPoise, X.PoolWard })
            {
                var pool = subject.Pools.FirstOrDefault(p => p.Id == id);
                if (pool == null || !(id == X.PoolHp || pool.Max > 0)) continue;
                var max = pool.Max ?? pool.Value;
                view.Meters.Add(new InspectorMeter
                {
                    Id = id, Label = pool.Label, Value = PoolValue(strings, pool), Cur = pool.Value, Max = max,
                    Pct = pool.Max > 0 ? Math.Max(0, Math.Min(Ashen.Generated.CombatMath.Percent, pool.Value / pool.Max.Value * Ashen.Generated.CombatMath.Percent)) : (pool.Value > 0 ? Ashen.Generated.CombatMath.Percent : 0),
                });
            }
            return view;
        }
    }
}
