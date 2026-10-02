using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using MK = Ashen.Generated.MapKeys;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;

namespace Ashen.App.Run
{
    /// <summary>The RUN_HUD band (04 §1 RunHud) as display data: identity, trail, the three pools, cinders, stones, flask charges and the deck.</summary>
    public sealed class RunHudState
    {
        public string Portrait;
        public string Identity;
        public string Trail;
        public int Hp;
        public int MaxHp;
        public int Mana;
        public int MaxMana;
        public int Stamina;
        public int MaxStamina;
        public string Cinders;
        public string Stones;
        public string Flasks;
        public string Deck;

        /// <summary>The XP strip (W-22 WGH5): null unless ui/meta.json runHud.showXp is on (an open owner decision; off).</summary>
        public string Xp;

        /// <summary>The strip's fill, 0..1 (0 while it is hidden or the level is at its cap).</summary>
        public double XpFraction;
    }

    public static class RunHudView
    {
        private static int Int(double v) => (int)Math.Floor(v);

        public static RunHudState Build(RunSession session, UiData ui)
        {
            var strings = ui.Strings;
            var run = session.Run;
            var charges = run.Obj(K.FlaskCharges) ?? new JObject();
            var seat = session.SeatId;
            var level = Int(run.Obj(K.Level)?.Num(K.Level) ?? 0);
            var hud = new RunHudState
            {
                Portrait = session.Portrait,
                Identity = strings.Format(StringKeys.HudIdentity, new StringArgs().Add(UiPlaceholders.Name, session.Name)
                    .Add(UiPlaceholders.Class, ClassName(ui, session.ClassId)).Add(UiPlaceholders.Level, level)),
                Trail = strings.Format(StringKeys.HudTrail, new StringArgs().Add(UiPlaceholders.Act, session.Act)
                    .Add(UiPlaceholders.Region, SeatName(ui, seat)).Add(UiPlaceholders.Floor, session.Floor)),
                Hp = Int(run.Num(K.Hp)),
                MaxHp = Int(run.Num(K.MaxHp)),
                Mana = Int(run.Num(K.Mana)),
                MaxMana = Int(run.Num(K.MaxMana)),
                Stamina = Int(run.Num(K.Stamina)),
                MaxStamina = Int(run.Num(K.MaxStamina)),
                Cinders = strings.Format(StringKeys.HudCinders, new StringArgs().Add(UiPlaceholders.Amount, Int(run.Num(RK.Cinders)))),
                Stones = strings.Format(StringKeys.HudStones, new StringArgs().Add(UiPlaceholders.Amount, Int(run.Num(RK.SmithingStones)))),
                Flasks = strings.Format(StringKeys.HudFlasks, new StringArgs()
                    .Add(UiPlaceholders.Hp, Int(charges.Num(K.Hp + V.CurrentSuffix))).Add(UiPlaceholders.Mp, Int(charges.Num(K.Mana + V.CurrentSuffix)))
                    .Add(UiPlaceholders.Count, run.Arr(K.Flasks)?.Count ?? 0)),
                Deck = strings.Format(StringKeys.HudDeck, new StringArgs().Add(UiPlaceholders.Count, run.Arr(K.Deck)?.Count ?? 0)),
            };
            if (ui.Meta?.Obj(MetaUiKeys.RunHud)?.Is(MetaUiKeys.ShowXp) == true)
            {
                var row = run.Obj(K.Level) ?? Ashen.Domain.Rewards.LevelUp.EmptyLevel();
                var next = Ashen.Domain.Rewards.LevelUp.XpToNext(session.Content.Loop.Rewards, row[K.Level]);
                var capped = double.IsInfinity(next) || double.IsNaN(next) || next <= 0;
                hud.Xp = capped ? strings.Get(MetaStringKeys.HudXpMax)
                    : strings.Format(MetaStringKeys.HudXp, new StringArgs().Add(MetaPlaceholders.Xp, Int(row.Num(K.Xp))).Add(MetaPlaceholders.Next, Int(next)));
                hud.XpFraction = capped ? 0 : Math.Max(0, Math.Min(1, row.Num(K.Xp) / next));
            }
            return hud;
        }

        public static string ClassName(UiData ui, string classId) =>
            classId == null ? string.Empty : ui.Strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.ClassNameKey, classId));

        public static string SeatName(UiData ui, string seat) =>
            seat == null ? string.Empty : ui.Strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.SeatNameKey, seat));
    }

    /// <summary>One node of the act map as W-06 draws it.</summary>
    public sealed class MapNodeView
    {
        public string Id;
        public int Floor;
        public int Col;

        /// <summary>The node's authored type (monster, elite, boss, event, shrine, merchant, treasure); an Unknown stays 'event' (path mode).</summary>
        public string Kind;

        public string GlyphKey;
        public string Label;
        public string Hint;

        /// <summary>A boss node's destination ("location · enemy"), composed by the map port (D-051).</summary>
        public string BossLabel;

        public bool Reachable;
        public bool Current;
        public bool Travelled;

        /// <summary>Path and fog modes: the node can no longer be reached from where the run stands (drawn dimmed).</summary>
        public bool Dimmed;

        /// <summary>Fog mode: the node is beyond the revealed floors; its kind, glyph and label read as unseen.</summary>
        public bool Hidden;

        /// <summary>The accessible name: kind, floor and the non-colour reachable cue (›).</summary>
        public string AccessibleName;
    }

    public sealed class MapEdgeView
    {
        public string From;
        public string To;
        public bool Travelled;

        /// <summary>Either end is dimmed (path and fog modes).</summary>
        public bool Dimmed;
    }

    public sealed class MapLegendEntry
    {
        public string GlyphKey;
        public string Label;
        public string Kind;
    }

    /// <summary>
    /// The act map (W-06) as display data: the seat and floor header, every node (position, kind, glyph, label, boss
    /// destination) and edge, where the run stands, the travelled trail, what is reachable, and the legend. The settings
    /// mapMode picks the reveal (D-151): 'full' draws the whole act (D-128); 'path' dims what can no longer be reached;
    /// 'fog' also hides the kinds beyond ui/meta.json's floors ahead, the boss destinations kept.
    /// </summary>
    public sealed class ActMapViewState
    {
        public string Title;
        public string SeatText;
        public string FloorText;
        public int Floors;
        public int Columns;
        public string CurrentId;
        public string Mode;
        public readonly List<MapNodeView> Nodes = new List<MapNodeView>();
        public readonly List<MapEdgeView> Edges = new List<MapEdgeView>();
        public readonly List<MapLegendEntry> Legend = new List<MapLegendEntry>();

        public MapNodeView Node(string id) => Nodes.FirstOrDefault(n => n.Id == id);
    }

    public static class ActMapView
    {
        public static ActMapViewState Build(RunSession session, UiData ui)
        {
            var strings = ui.Strings;
            var run = session.Run;
            var graph = run.Obj(RK.MapGraph) ?? new JObject();
            var nodes = graph.Obj(MK.Nodes) ?? new JObject();
            var path = Js.Items(run[LK.Path]).Select(Js.Str).ToList();
            var at = Js.Str(run[MK.MapNodeId]);
            var reachable = new HashSet<string>(session.ReachableNodes(), StringComparer.Ordinal);
            var seatOrder = Js.Items(run[RK.SeatOrder]).Select(Js.Str).ToList();
            var seat = session.SeatId;
            var view = new ActMapViewState
            {
                CurrentId = at,
                Columns = (int)Js.Or0(graph[MK.Columns]),
                Title = strings.Format(StringKeys.MapTitle, new StringArgs().Add(UiPlaceholders.Act, session.Act).Add(UiPlaceholders.Region, RunHudView.SeatName(ui, seat))),
                SeatText = strings.Format(StringKeys.MapSeat, new StringArgs().Add(UiPlaceholders.Count, seatOrder.IndexOf(seat) + 1).Add(UiPlaceholders.Total, seatOrder.Count)),
            };
            foreach (var p in nodes.Properties())
            {
                var n = (JObject)p.Value;
                var kind = n.Str(K.Type);
                var floor = (int)Js.Or0(n[MK.Floor]);
                var label = Name(strings, UiFormats.MapNodeKey, kind);
                var node = new MapNodeView
                {
                    Id = p.Name,
                    Floor = floor,
                    Col = (int)Js.Or0(n[MK.Col]),
                    Kind = kind,
                    GlyphKey = string.Format(CultureInfo.InvariantCulture, UiFormats.MapGlyphKey, kind),
                    Label = label,
                    Hint = Name(strings, UiFormats.MapNodeHintKey, kind),
                    BossLabel = n.Str(MK.DestinationLabel),
                    Reachable = reachable.Contains(p.Name),
                    Current = p.Name == at,
                    Travelled = path.Contains(p.Name),
                };
                node.AccessibleName = strings.Format(node.Reachable ? StringKeys.MapNodeReachable : StringKeys.MapNodeName,
                    new StringArgs().Add(UiPlaceholders.Kind, label).Add(UiPlaceholders.Floor, floor));
                view.Nodes.Add(node);
                foreach (var next in Js.Items(n[MK.Next]).Select(Js.Str))
                {
                    var i = path.IndexOf(p.Name);
                    view.Edges.Add(new MapEdgeView { From = p.Name, To = next, Travelled = i >= 0 && i + 1 < path.Count && path[i + 1] == next });
                }
            }
            Reveal(session, ui, view, nodes, reachable);
            view.Floors = view.Nodes.Count == 0 ? 0 : view.Nodes.Max(n => n.Floor);
            view.FloorText = strings.Format(StringKeys.MapFloor, new StringArgs().Add(UiPlaceholders.Floor, session.Floor).Add(UiPlaceholders.Total, view.Floors));
            foreach (var kind in view.Nodes.Select(n => n.Kind).Distinct())
                view.Legend.Add(new MapLegendEntry { Kind = kind, GlyphKey = string.Format(CultureInfo.InvariantCulture, UiFormats.MapGlyphKey, kind), Label = Name(strings, UiFormats.MapNodeKey, kind) });
            return view;
        }

        internal static string Name(StringTable strings, string format, string id)
        {
            var key = string.Format(CultureInfo.InvariantCulture, format, id);
            return strings.Has(key) ? strings.Get(key) : id ?? string.Empty;
        }

        /// <summary>The mapMode reveal (D-151): dim what is behind or beside the run's way on; in fog, hide the far floors.</summary>
        private static void Reveal(RunSession session, UiData ui, ActMapViewState view, JObject nodes, HashSet<string> reachable)
        {
            view.Mode = session.PlayerSettings.Text(SettingIds.MapMode) ?? SettingIds.MapFull;
            if (view.Mode == SettingIds.MapFull) return;
            // Every node still ahead: the forward closure of the reachable row (the start row before the first step).
            var ahead = new HashSet<string>(reachable, StringComparer.Ordinal);
            var queue = new Queue<string>(ahead);
            while (queue.Count > 0)
                foreach (var next in Js.Items(nodes.Obj(queue.Dequeue())?[MK.Next]).Select(Js.Str))
                    if (next != null && ahead.Add(next)) queue.Enqueue(next);
            foreach (var n in view.Nodes) n.Dimmed = !ahead.Contains(n.Id) && !n.Travelled && !n.Current;
            var dimmed = new HashSet<string>(view.Nodes.Where(n => n.Dimmed).Select(n => n.Id), StringComparer.Ordinal);
            foreach (var e in view.Edges) e.Dimmed = dimmed.Contains(e.From) || dimmed.Contains(e.To);
            if (view.Mode != SettingIds.MapFog) return;

            var rules = ui.Meta?.Obj(MetaUiKeys.MapReveal) ?? new JObject();
            var keep = new HashSet<string>(Js.Items(rules[MetaUiKeys.FogKeepsKinds]).Select(Js.Str).Where(s => s != null), StringComparer.Ordinal);
            var fogKind = rules.Str(MetaUiKeys.FogKind);
            // The reachable row (else where the run stands) and the floors ahead of it are revealed.
            var row = view.Nodes.Where(n => n.Reachable).Select(n => n.Floor).DefaultIfEmpty(view.Node(view.CurrentId)?.Floor ?? 0).Min();
            var seen = row + (int)Js.Or0(rules[MetaUiKeys.FogFloorsAhead]);
            var strings = ui.Strings;
            foreach (var n in view.Nodes)
            {
                if (n.Floor <= seen || n.Travelled || n.Current || keep.Contains(n.Kind)) continue;
                n.Hidden = true;
                n.Kind = fogKind;
                n.BossLabel = null;
                n.GlyphKey = string.Format(CultureInfo.InvariantCulture, UiFormats.MapGlyphKey, fogKind);
                n.Label = Name(strings, UiFormats.MapNodeKey, fogKind);
                n.Hint = Name(strings, UiFormats.MapNodeHintKey, fogKind);
                n.AccessibleName = strings.Format(n.Reachable ? StringKeys.MapNodeReachable : StringKeys.MapNodeName,
                    new StringArgs().Add(UiPlaceholders.Kind, n.Label).Add(UiPlaceholders.Floor, n.Floor));
            }
        }
    }

    /// <summary>
    /// The act map's planned door (D-126/D-127): the place a node led to whose screen is not built yet (the merchant, an
    /// event, a legacy dungeon), with the one legal action that keeps the run moving.
    /// </summary>
    public sealed class PlannedDoorState
    {
        public string Location;
        public string Title;
        public string Body;

        /// <summary>The event's response text once a response is taken (then the action is Continue).</summary>
        public string Result;

        public string ActionText;
    }

    public static class PlannedDoorView
    {
        public static PlannedDoorState Build(RunSession session, UiData ui)
        {
            var strings = ui.Strings;
            var location = session.Location;
            var state = new PlannedDoorState
            {
                Location = location,
                Body = strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.PlannedBodyKey, location)),
            };
            if (location == RunFlowValues.LocationMerchant)
            {
                state.Title = strings.Get(StringKeys.MapPlannedMerchantTitle);
                state.ActionText = strings.Get(StringKeys.MapPlannedLeave);
            }
            else if (location == RunFlowValues.LocationEvent)
            {
                var view = session.EventView();
                state.Title = view?.Str(WK.Title) ?? string.Empty;
                if (session.EventDone)
                {
                    state.Result = LastResult(session);
                    state.ActionText = strings.Get(StringKeys.MapPlannedContinue);
                }
                else
                {
                    var id = session.EventStandInChoice();
                    var choice = Js.Items(view?[RunKeys.Choices]).OfType<JObject>().FirstOrDefault(c => c.Str(MK.ChoiceId) == id);
                    state.ActionText = strings.Format(StringKeys.MapPlannedChoose, new StringArgs().Add(UiPlaceholders.Label, choice?.Str(K.Label) ?? id));
                }
            }
            else if (location == RunFlowValues.LocationDungeon)
            {
                var run = session.Run;
                var dungeon = Ashen.Domain.Loop.LegacyDungeons.Definition(session.Content.Loop, run);
                var room = Ashen.Domain.Loop.LegacyDungeons.Node(session.Content.Loop, run);
                state.Title = strings.Format(StringKeys.MapPlannedDungeonTitle, new StringArgs()
                    .Add(UiPlaceholders.Name, dungeon?.Str(K.Name) ?? session.DungeonId).Add(UiPlaceholders.Label, room?.Str(K.Name) ?? string.Empty));
                var pending = run.Obj(RK.LegacyDungeon)?.Obj(K.Pending);
                if (pending != null) state.Result = pending.Str(LK.Text);
                state.ActionText = strings.Get(StringKeys.MapPlannedAdvance);
            }
            return state;
        }

        /// <summary>The response text of the event's last history row (the choice just taken).</summary>
        private static string LastResult(RunSession session)
        {
            var view = session.EventView();
            var history = session.Run.Arr(MK.History);
            var last = history?.Count > 0 ? history[history.Count - 1] as JObject : null;
            var choiceId = last?.Str(MK.ChoiceId);
            var def = session.Content.Loop.Events.Events.Get(session.EventId);
            var choice = Js.Items(def?[RunKeys.Choices]).OfType<JObject>().FirstOrDefault(c => c.Str(K.Id) == choiceId);
            return choice?.Str(EventKeys.ResultText) ?? view?.Str(EventKeys.Text) ?? string.Empty;
        }
    }

    /// <summary>
    /// W-15 run end (AF-11, US-4.7, US-4.9) as display data: victory or death, who and where, the seed, class, act and
    /// floor, what ended it, how long it took, the final deck and what was newly unlocked (finishRun's receipt). The killer
    /// and the duration are this build's (the shipped run record keeps neither; D-129).
    /// </summary>
    public sealed class RunEndViewState
    {
        public bool Victory;
        public string Title;
        public string Detail;
        public string Where;
        public string Region;
        public string Portrait;
        public readonly List<string> Stats = new List<string>();
        public readonly List<string> Deck = new List<string>();
        public readonly List<string> Unlocks = new List<string>();
        public string DeckTitle;
    }

    public static class RunEndView
    {
        public static RunEndViewState Build(RunSession session, UiData ui)
        {
            var strings = ui.Strings;
            var run = session.Run;
            var result = session.End?.Result ?? new JObject();
            var victory = session.End?.Victory ?? false;
            var data = session.Content.Combat;
            var view = new RunEndViewState
            {
                Victory = victory,
                Region = session.Region,
                Portrait = session.Portrait,
                Title = strings.Get(session.Abandoned ? MetaStringKeys.RunEndAbandoned : victory ? StringKeys.RunEndVictory : StringKeys.RunEndDefeat),
                Detail = strings.Format(StringKeys.RunEndDetail, new StringArgs().Add(UiPlaceholders.Name, session.Name)
                    .Add(UiPlaceholders.Class, result.Str(LK.ClassName) ?? RunHudView.ClassName(ui, session.ClassId))),
                Where = strings.Format(StringKeys.RunEndWhere, new StringArgs().Add(UiPlaceholders.Act, session.Act).Add(UiPlaceholders.Floor, session.Floor)
                    .Add(UiPlaceholders.Region, RunHudView.SeatName(ui, session.SeatId))),
            };
            view.Stats.Add(strings.Format(StringKeys.RunEndSeed, new StringArgs().Add(UiPlaceholders.Seed, session.SeedText)));
            view.Stats.Add(strings.Format(StringKeys.RunEndClass, new StringArgs().Add(UiPlaceholders.Class, RunHudView.ClassName(ui, session.ClassId))));
            view.Stats.Add(strings.Format(StringKeys.RunEndFloor, new StringArgs().Add(UiPlaceholders.Act, session.Act).Add(UiPlaceholders.Floor, session.Floor)));
            var foe = session.Abandoned ? null : Killer(session);
            if (foe != null) view.Stats.Add(strings.Format(victory ? StringKeys.RunEndFelled : StringKeys.RunEndKiller, new StringArgs().Add(UiPlaceholders.Name, foe)));
            view.Stats.Add(strings.Format(StringKeys.RunEndDuration, new StringArgs()
                .Add(UiPlaceholders.Time, TimeSpan.FromSeconds(session.PlaytimeSeconds).ToString(UiFormats.Duration, CultureInfo.InvariantCulture))));
            view.Stats.Add(strings.Format(StringKeys.RunEndCinders, new StringArgs().Add(UiPlaceholders.Amount, (int)run.Num(RK.Cinders))));
            view.Stats.Add(strings.Format(StringKeys.RunEndFights, new StringArgs().Add(UiPlaceholders.Count, (int)Js.Or0(run.Obj(WK.Stats)?[WK.FightsWon]))));
            foreach (var card in Js.Items(run[K.Deck]).OfType<JObject>())
            {
                var id = card.Str(K.CardId);
                var name = id != null && data.Cards.Has(id) ? data.Cards.Get(id).Str(K.Name) ?? id : id;
                view.Deck.Add(strings.Format(card.Is(K.Upgraded) ? StringKeys.RunEndCardUpgraded : StringKeys.RunEndCard, new StringArgs().Add(UiPlaceholders.Name, name)));
            }
            view.DeckTitle = strings.Format(StringKeys.RunEndDeck, new StringArgs().Add(UiPlaceholders.Count, view.Deck.Count));
            foreach (var unlock in session.End?.Earned ?? new List<JObject>())
            {
                var key = string.Format(CultureInfo.InvariantCulture, UiFormats.UnlockNameKey, unlock.Str(K.Id));
                var name = strings.Has(key) ? strings.Get(key) : unlock.Str(RunFlowKeys.Ref) ?? unlock.Str(K.Id);
                view.Unlocks.Add(strings.Format(StringKeys.RunEndUnlock, new StringArgs().Add(UiPlaceholders.Kind, ActMapView.Name(strings, UiFormats.UnlockKindKey, unlock.Str(K.Kind)))
                    .Add(UiPlaceholders.Name, name)));
            }
            return view;
        }

        /// <summary>The last fight's foes by name (the enemies of its encounter), or null when the run did not end in a fight.</summary>
        private static string Killer(RunSession session)
        {
            if (session.LastOutcome == null || session.EncounterId == null) return null;
            var data = session.Content.Data;
            if (!data.Encounters.Has(session.EncounterId)) return null;
            var names = Js.Items(data.Encounters.Get(session.EncounterId)[K.Enemies]).Select(Js.Str)
                .Select(id => session.Content.Combat.Enemies.Has(id) ? session.Content.Combat.Enemies.Get(id).Str(K.Name) ?? id : id).Distinct().ToList();
            return names.Count == 0 ? null : string.Join(session.Content.Loop.Map.Rules.EnemySeparator, names);
        }
    }
}
