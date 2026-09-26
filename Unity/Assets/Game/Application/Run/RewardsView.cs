using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using LV = Ashen.Generated.LoopValues;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;
using WV = Ashen.Generated.RewardsValues;

namespace Ashen.App.Run
{
    /// <summary>One cost pip of an offered card: its kind (ui/components.json card.costKinds) and number.</summary>
    public sealed class RewardCost
    {
        public string Kind;
        public string Text;
    }

    /// <summary>One pick of a card offer or draft: a card (CardView data) or a class-tree node (a tile).</summary>
    public sealed class RewardPickView
    {
        public string Id;
        public bool IsNode;
        public string Name;
        public string Text;
        public string TypeLine;
        public string Rarity;
        public bool Upgraded;
        public List<RewardCost> Costs = new List<RewardCost>();
    }

    /// <summary>One W-08 row: its claim state, what it says, its icon, and — for a choice — the picks it offers.</summary>
    public sealed class RewardRowView
    {
        public string Key;
        public string Kind;

        /// <summary>taken | skipped | blocked | available (rewardClaimStatus).</summary>
        public string State;

        public string BlockedBy;
        public bool Choice;
        public string Title;
        public string Body;
        public string StateText;

        /// <summary>The registry id of the row's icon (null when the kind has no art), and the glyph string key shown without it.</summary>
        public string ArtId;

        public string GlyphKey;

        /// <summary>The picks a card offer or a draft opens (empty for the other kinds).</summary>
        public List<RewardPickView> Picks = new List<RewardPickView>();

        /// <summary>The chooser's heading (a draft's "Longsword draft · level 2"; the card offer's "Choose a card").</summary>
        public string PickTitle;

        public bool Resolved => State == WV.Taken || State == LV.Skipped;
        public bool Blocked => State == RunFlowValues.StateBlocked;
        public bool Pending => State == RunFlowValues.StateAvailable;
    }

    /// <summary>The whole W-08 door as the screen draws it.</summary>
    public sealed class RewardsViewState
    {
        public string Title;
        public string StatusText;
        public int Claimed;
        public int Total;
        public bool AllResolved;
        public string RequiredText;
        public string HintText;
        public string Mode;
        public JObject Status;
        public List<RewardRowView> Rows = new List<RewardRowView>();
    }

    /// <summary>
    /// W-08 rewards as display data (US-11.1 to US-11.3; shipped model/rewardplan.js rewardClaimStatus and reward.js
    /// rowBody): the open door's rows with their states and sentences from strings/app.en.json, the head's
    /// "{claimed} of {total} claimed", the one choice still waiting, and what Continue will do under the rewardCollect
    /// dial. Also the refusals a claim can meet before or after the door is asked. Engine-free.
    /// </summary>
    public static class RewardsView
    {
        // ------------------------------------------------------------------ claim status (rewardplan.js rewardClaimStatus)

        /// <summary>A row's state: its claim state, else blocked, else available.</summary>
        public static string StateOf(JObject row, JObject states) =>
            Js.Str(states?[row.Str(LK.Key)]) ?? (Js.Truthy(row[LK.BlockedBy]) ? RunFlowValues.StateBlocked : RunFlowValues.StateAvailable);

        /// <summary>
        /// rewardClaimStatus(plan, states): per-row state, the counts the head prints (taken, skipped, blocked, available)
        /// and the first choice still waiting, in row order.
        /// </summary>
        public static JObject Status(JArray rows, JObject states)
        {
            var list = rows.OfType<JObject>().ToList();
            var entries = new JArray(list.Select(row => Js.Obj(K.Kind, row[K.Kind]?.DeepClone(), LK.Key, row[LK.Key]?.DeepClone(), RunFlowKeys.State, StateOf(row, states))));
            double Count(string state) => entries.Count(e => ((JObject)e).Str(RunFlowKeys.State) == state);
            var choice = list.FirstOrDefault(row => row.Is(LK.Choice) && !Js.Truthy(states?[row.Str(LK.Key)]));
            return Js.Obj(K.Total, (double)entries.Count, RunFlowKeys.Claimed, Count(WV.Taken), RunFlowKeys.Skipped, Count(LV.Skipped),
                K.Blocked, Count(RunFlowValues.StateBlocked), RunFlowKeys.Available, Count(RunFlowValues.StateAvailable),
                RunFlowKeys.RequiredChoice, choice == null ? Js.Null() : Js.Obj(K.Kind, choice[K.Kind]?.DeepClone(), LK.Key, choice[LK.Key]?.DeepClone(), K.Count, (double)PickIds(choice).Count),
                RunFlowKeys.Rows, entries);
        }

        /// <summary>A card offer and the two drafts open the chooser (select, then Confirm).</summary>
        public static bool IsChoiceKind(string kind) => kind == K.Card || kind == LV.SkillDraft || kind == LV.ClassDraft;

        /// <summary>pickIds(row): a class draft's nodes, else the row's cards.</summary>
        public static List<string> PickIds(JObject row) =>
            Js.Items(row?[WK.NodeIds] is JArray ? row[WK.NodeIds] : row?[WK.CardIds]).Select(Js.Str).Where(s => s != null).ToList();

        // ------------------------------------------------------------------ refusals (the screen only offers what can land)

        /// <summary>Why a claim is refused before the door is asked: no such row, resolved, blocked, or a choice without an offered pick.</summary>
        public static string PreRefusal(JObject row, JObject states, string pickId)
        {
            if (row == null) return RunFlowValues.RefusalNoSuchRow;
            if (Js.Truthy(states?[row.Str(LK.Key)])) return RunFlowValues.RefusalClaimed;
            if (Js.Truthy(row[LK.BlockedBy])) return RunFlowValues.RefusalBlocked;
            if (!IsChoiceKind(row.Str(K.Kind))) return pickId == null ? null : RunFlowValues.RefusalNotOffered;
            if (pickId == null) return RunFlowValues.RefusalNoPick;
            return PickIds(row).Contains(pickId) ? null : RunFlowValues.RefusalNotOffered;
        }

        /// <summary>Why a row the door was asked for landed nothing: a spent draft, a full or duplicate bag, else a plain refusal.</summary>
        public static string ApplyRefusal(JObject row)
        {
            var kind = row?.Str(K.Kind);
            if (kind == LV.SkillDraft || kind == LV.ClassDraft) return RunFlowValues.RefusalSpent;
            if (kind == LV.ArmamentKind) return RunFlowValues.RefusalNoRoom;
            return RunFlowValues.RefusalRefused;
        }

        // ------------------------------------------------------------------ the view

        /// <summary>The open door as display data (null when the run holds no pending reward).</summary>
        public static RewardsViewState Build(RunSession session, UiData ui)
        {
            var door = session.Door;
            if (door == null) return null;
            var strings = ui.Strings;
            var run = session.Run;
            var checkpoint = run.Obj(WK.PendingReward) ?? new JObject();
            var rewards = checkpoint.Obj(WK.Rewards) ?? new JObject();
            var states = door.States;
            var status = Status(door.Rows, states);
            var view = new RewardsViewState
            {
                Status = status,
                Claimed = (int)status.Num(RunFlowKeys.Claimed),
                Total = (int)status.Num(K.Total),
                Mode = session.RewardCollectMode,
            };
            var titleKey = string.Format(CultureInfo.InvariantCulture, UiFormats.RewardTitleKey, checkpoint.Str(RK.Source));
            var titleArgs = new StringArgs().Add(UiPlaceholders.Title, rewards.Str(WK.Title) ?? string.Empty);
            view.Title = strings.Format(strings.Has(titleKey) ? titleKey : StringKeys.RewardsTitleFallback, titleArgs);
            view.StatusText = strings.Format(StringKeys.RewardsStatus, new StringArgs().Add(UiPlaceholders.Claimed, view.Claimed).Add(UiPlaceholders.Total, view.Total));
            var drafts = door.Rows.OfType<JObject>().Count(r => r.Str(K.Kind) == LV.SkillDraft || r.Str(K.Kind) == LV.ClassDraft);
            var draftOrdinal = 0;
            foreach (var row in door.Rows.OfType<JObject>())
            {
                var kind = row.Str(K.Kind);
                var isDraft = kind == LV.SkillDraft || kind == LV.ClassDraft;
                if (isDraft) draftOrdinal++;
                view.Rows.Add(Row(session, ui, run, checkpoint, row, StateOf(row, states), isDraft ? draftOrdinal : 0, drafts));
            }
            view.AllResolved = view.Rows.All(r => r.Resolved);
            var required = status.Obj(RunFlowKeys.RequiredChoice);
            var requiredRow = required == null ? null : view.Rows.FirstOrDefault(r => r.Key == required.Str(LK.Key));
            view.RequiredText = requiredRow == null
                ? strings.Get(StringKeys.RewardsClaimNone)
                : strings.Format(StringKeys.RewardsClaimRequired, new StringArgs().Add(UiPlaceholders.Kind, requiredRow.Title));
            view.HintText = strings.Get(view.AllResolved ? StringKeys.RewardsHintDone : view.Mode == LV.CollectAuto ? StringKeys.RewardsHintAuto : StringKeys.RewardsHintManual);
            return view;
        }

        private static RewardRowView Row(RunSession session, UiData ui, JObject run, JObject checkpoint, JObject row, string state, int draftOrdinal, int drafts)
        {
            var strings = ui.Strings;
            var data = session.Content.Combat;
            var rewards = session.Content.Loop.Rewards;
            var kind = row.Str(K.Kind);
            var key = row.Str(LK.Key);
            var view = new RewardRowView
            {
                Key = key,
                Kind = kind,
                State = state,
                BlockedBy = row.Str(LK.BlockedBy),
                Choice = row.Is(LK.Choice),
                GlyphKey = string.Format(CultureInfo.InvariantCulture, UiFormats.GlyphKey, kind),
            };
            view.StateText = strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.RewardStateKey,
                state == RunFlowValues.StateAvailable && view.Choice ? RunFlowValues.StateChoice : state));
            var taken = state == WV.Taken;
            string Fmt(string k, string name) => strings.Format(k, new StringArgs().Add(UiPlaceholders.Name, name));
            switch (kind)
            {
                case RK.Cinders:
                    view.Title = strings.Format(StringKeys.RewardsRowCinders, new StringArgs().Add(UiPlaceholders.Amount, CombatText.Number(row.Num(K.Amount))));
                    view.Body = strings.Format(StringKeys.RewardsBodyCinders, new StringArgs().Add(UiPlaceholders.Total, CombatText.Number(run.Num(RK.Cinders))));
                    break;
                case WK.SmithingStone:
                    view.Title = strings.Format(StringKeys.RewardsRowStone, new StringArgs().Add(UiPlaceholders.Amount, CombatText.Number(row.Num(K.Amount))));
                    view.Body = strings.Format(StringKeys.RewardsBodyStone, new StringArgs().Add(UiPlaceholders.Total, CombatText.Number(row.Num(RunFlowKeys.StoneBalanceAfter))));
                    break;
                case K.Card:
                {
                    view.Title = strings.Get(view.Choice ? StringKeys.RewardsRowCard : StringKeys.RewardsRowCardOne);
                    view.PickTitle = strings.Get(StringKeys.RewardsPickCard);
                    view.Picks = PickIds(row).Select(id => CardPick(data, strings, ui.Components.CostKinds, id, false)).ToList();
                    var chosen = checkpoint.Str(WK.ChosenCardId);
                    view.Body = taken && chosen != null ? Fmt(StringKeys.RewardsBodyJoins, CardName(data, chosen)) : ChooseBody(strings, view);
                    break;
                }
                case LV.SkillDraft:
                {
                    var skillId = row.Str(WK.SkillId);
                    view.Title = strings.Format(StringKeys.RewardsRowSkillDraft, new StringArgs().Add(UiPlaceholders.Name, TrackName(session, skillId)).Add(UiPlaceholders.Level, CombatText.Number(row.Num(K.Level))));
                    var upgraded = Skills.UpgradesCards(rewards, Skills.LevelOf(run, skillId));
                    view.Picks = PickIds(row).Select(id => CardPick(data, strings, ui.Components.CostKinds, id, upgraded)).ToList();
                    var chosen = checkpoint.Obj(WK.ChosenDraftCardIds)?.Str(key);
                    view.Body = taken && chosen != null ? Fmt(StringKeys.RewardsBodyJoins, CardName(data, chosen)) : ChooseBody(strings, view);
                    view.PickTitle = PickTitle(strings, view.Title, draftOrdinal, drafts);
                    break;
                }
                case LV.ClassDraft:
                {
                    var classId = row.Str(K.ClassId);
                    var className = data.Classes.Has(classId) ? data.Classes.Get(classId).Str(K.Name) : classId;
                    view.Title = strings.Format(StringKeys.RewardsRowClassDraft, new StringArgs().Add(UiPlaceholders.Name, className).Add(UiPlaceholders.Level, CombatText.Number(row.Num(K.Level))));
                    view.Picks = PickIds(row).Select(id => NodePick(session, id)).ToList();
                    var chosen = checkpoint.Obj(WK.ChosenDraftNodeIds)?.Str(key);
                    view.Body = taken && chosen != null ? Fmt(StringKeys.RewardsBodyNodeJoins, NodeName(session, chosen)) : ChooseBody(strings, view);
                    view.PickTitle = PickTitle(strings, view.Title, draftOrdinal, drafts);
                    break;
                }
                case LV.FlaskKind:
                {
                    var def = data.Flasks.Has(row.Str(K.FlaskId)) ? data.Flasks.Get(row.Str(K.FlaskId)) : new JObject();
                    var name = def.Str(K.Name) ?? row.Str(K.FlaskId);
                    view.Title = Fmt(StringKeys.RewardsRowFlask, name);
                    view.Body = view.BlockedBy != null ? Fmt(StringKeys.RewardsBodyFlaskBlocked, name) : Sentence(data, def.Str(K.TextTemplate), EffectTokens(data, def[K.Effects]));
                    view.ArtId = ArtFromKey(ui.Components.RewardFlaskArt, def.Str(RunFlowKeys.ArtKey), ui.Components.RewardFlaskArtPrefix);
                    break;
                }
                case LV.ArmamentKind:
                {
                    var id = row.Str(WK.ArmamentId);
                    var def = session.Content.Data.EquipmentRows(K.Armaments).FirstOrDefault(a => a.Str(K.Id) == id) ?? new JObject();
                    var name = def.Str(K.Name) ?? id;
                    var found = session.Profile.Doc[LK.Found];
                    view.Title = Fmt(StringKeys.RewardsRowArmament, name);
                    view.Body = view.BlockedBy != null ? Fmt(StringKeys.RewardsBodyArmamentBlocked, name)
                        : taken ? Fmt(StringKeys.RewardsBodyArmamentCarried, name)
                        : !Js.Includes(found, id) ? Fmt(StringKeys.RewardsBodyArmamentNew, name) : Fmt(StringKeys.RewardsBodyPlain, name);
                    view.ArtId = ArtFromKey(ui.Components.RewardArmamentArt, def.Str(RunFlowKeys.ArtKey) ?? id, null);
                    break;
                }
                case V.RelicKind:
                {
                    var id = row.Str(K.RelicId);
                    var def = data.Relics.Has(id) ? data.Relics.Get(id) : new JObject();
                    view.Title = Fmt(StringKeys.RewardsRowRelic, def.Str(K.Name) ?? id);
                    view.Body = Sentence(data, def.Str(K.TextTemplate), RelicTokens(data, def, ui.Components));
                    view.ArtId = StringTable.Fill(ui.Components.RewardRelicArt, new StringArgs().Add(UiPlaceholders.Id, id));
                    break;
                }
                default:
                    view.Title = kind;
                    view.Body = string.Empty;
                    break;
            }
            return view;
        }

        private static string ChooseBody(StringTable strings, RewardRowView view) =>
            view.Choice ? strings.Format(StringKeys.RewardsBodyChoose, new StringArgs().Add(UiPlaceholders.Count, view.Picks.Count)) : strings.Get(StringKeys.RewardsBodyOffered);

        private static string PickTitle(StringTable strings, string title, int ordinal, int drafts) =>
            drafts > 1
                ? strings.Format(StringKeys.RewardsPickRound, new StringArgs().Add(UiPlaceholders.Round, ordinal).Add(UiPlaceholders.Rounds, drafts)) + strings.Get(StringKeys.CreationStatJoin) + title
                : strings.Format(StringKeys.RewardsPickDraft, new StringArgs().Add(UiPlaceholders.Title, title));

        /// <summary>A template-with-{art} registry id from an artKey (a flask's "flask-crimson" loses its prefix first), or null.</summary>
        private static string ArtFromKey(string template, string artKey, string prefix)
        {
            if (string.IsNullOrEmpty(template) || string.IsNullOrEmpty(artKey)) return null;
            if (!string.IsNullOrEmpty(prefix) && artKey.StartsWith(prefix, StringComparison.Ordinal)) artKey = artKey.Substring(prefix.Length);
            return StringTable.Fill(template, new StringArgs().Add(UiPlaceholders.Art, artKey));
        }

        // ------------------------------------------------------------------ cards and nodes

        private static string CardName(CombatData data, string cardId) => data.Cards.Has(cardId) ? data.Cards.Get(cardId).Str(K.Name) : cardId;

        /// <summary>A card as its CardView reads out of a fight: name, kind, rarity, authored costs and its text with the authored numbers.</summary>
        public static RewardPickView CardPick(CombatData data, StringTable strings, IReadOnlyList<string> costKinds, string cardId, bool upgraded)
        {
            if (!data.Cards.Has(cardId)) return new RewardPickView { Id = cardId, Name = cardId, Text = string.Empty };
            var def = Cards.Resolve(data, Js.Obj(K.CardId, cardId, K.Upgraded, upgraded));
            var pick = new RewardPickView
            {
                Id = cardId,
                Name = def.Str(K.Name),
                TypeLine = CombatText.CardKind(strings, Cards.Kind(data, def)),
                Rarity = def.Str(RK.Rarity),
                Upgraded = upgraded,
                Text = Sentence(data, def.Str(K.TextTemplate), EffectTokens(data, def[K.Effects])),
            };
            // Pips in ui/components.json card.costKinds order (actions, stamina, mana): Actions always, the pools when the card costs them.
            var amounts = new[] { def.Num(K.Cost), def.Num(K.StaminaCost), def.Num(K.ManaCost) };
            for (var i = 0; i < costKinds.Count && i < amounts.Length; i++)
                if (i == 0 || amounts[i] > 0) pick.Costs.Add(new RewardCost { Kind = costKinds[i], Text = CombatText.Number(amounts[i]) });
            return pick;
        }

        private static JObject Node(RunSession session, string nodeId) =>
            session.Content.Loop.Rewards.Nodes.OfType<JObject>().FirstOrDefault(n => n.Str(K.Id) == nodeId);

        private static string NodeName(RunSession session, string nodeId) => Node(session, nodeId)?.Str(RunFlowKeys.Label) ?? nodeId;

        /// <summary>A class-tree node as a tile: its label and its rule's sentence (numbers bound by op), else its blurb.</summary>
        private static RewardPickView NodePick(RunSession session, string nodeId)
        {
            var data = session.Content.Combat;
            var node = Node(session, nodeId) ?? new JObject();
            var rule = data.PropertyRules.Has(nodeId) ? data.PropertyRules.Get(nodeId) : null;
            var sentence = rule == null ? null : Sentence(data, rule.Str(K.TextTemplate), EffectTokens(data, TriggerOps(rule)));
            return new RewardPickView
            {
                Id = nodeId,
                IsNode = true,
                Name = node.Str(RunFlowKeys.Label) ?? nodeId,
                Text = string.IsNullOrEmpty(sentence) ? node.Str(RunFlowKeys.Blurb) ?? string.Empty : sentence,
            };
        }

        /// <summary>A skill track's name: its tree node's label, or the class's name for a class track.</summary>
        private static string TrackName(RunSession session, string skillId)
        {
            var node = Node(session, skillId);
            if (node != null) return node.Str(RunFlowKeys.Label) ?? skillId;
            var classes = session.Content.Combat.Classes;
            foreach (var cls in classes.All)
                if (Skills.ClassSkillId(cls.Str(K.Id)) == skillId) return cls.Str(K.Name);
            return skillId;
        }

        // ------------------------------------------------------------------ sentences (card.js staticTokens, validate.js relicTokens)

        /// <summary>A text template with its tokens filled from the numbers; an unresolved token keeps its braces (shipped honest degrade).</summary>
        private static string Sentence(CombatData data, string template, JObject tokens) =>
            CombatViewModel.ResolveText(template ?? string.Empty, tokens);

        /// <summary>computeTokenBindings over an effect list: each tokenizable literal number under its token.</summary>
        private static JObject EffectTokens(CombatData data, JToken effects)
        {
            var tokens = new JObject();
            var list = Js.Items(effects).ToList();
            foreach (var binding in CombatPreview.TokenBindings(data, effects))
            {
                var parts = binding.Key.Split(new[] { V.KeySeparator }, StringSplitOptions.None);
                if (parts.Length <= 1 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index >= list.Count) continue;
                var value = (list[index] as JObject)?[parts[1]];
                if (Js.IsNum(value)) tokens[binding.Value] = value.DeepClone();
            }
            return tokens;
        }

        /// <summary>A rule's or relic's trigger ops flattened (triggers[].do, then effects, then do).</summary>
        private static JArray TriggerOps(JObject def)
        {
            var ops = new JArray();
            foreach (var t in Js.Items(def?[K.Triggers]).OfType<JObject>()) foreach (var op in Js.Items(t[K.Do])) ops.Add(op.DeepClone());
            foreach (var op in Js.Items(def?[K.Effects])) ops.Add(op.DeepClone());
            foreach (var op in Js.Items(def?[K.Do])) ops.Add(op.DeepClone());
            return ops;
        }

        /// <summary>
        /// relicTokens(def, rules): the relic's own ops bound by position, its passive modifiers by the
        /// ui/components.json rewards.modifierTokens rule, and each carried property rule's ops (a rule's numbers are
        /// bound by op here; the shipped build binds them by variable name, which agrees wherever the variable is named
        /// after its op).
        /// </summary>
        private static JObject RelicTokens(CombatData data, JObject def, ComponentDefaults components)
        {
            var tokens = EffectTokens(data, TriggerOps(def));
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in Js.Items(def.Obj(K.Passives)?[K.Modifiers]).OfType<JObject>())
            {
                if (!(components.RewardModifierTokens?[row.Str(RunFlowKeys.Tag) ?? string.Empty] is JObject rule)) continue;
                var baseName = row.Str(rule.Str(RunFlowKeys.From)) + rule.Str(RunFlowKeys.Suffix);
                counts[baseName] = counts.TryGetValue(baseName, out var n) ? n + 1 : 1;
                var token = counts[baseName] == 1 ? baseName : baseName + V.TokenRepeatSeparator + counts[baseName].ToString(CultureInfo.InvariantCulture);
                var value = row[rule.Str(RunFlowKeys.Value)];
                if (Js.IsNum(value)) tokens[token] = value.DeepClone();
            }
            foreach (var tag in Js.Items(def[K.PropertyTags]).Select(Js.Str).Where(t => t != null && data.PropertyRules.Has(t)))
                foreach (var p in EffectTokens(data, TriggerOps(data.PropertyRules.Get(tag))).Properties())
                    if (tokens[p.Name] == null) tokens[p.Name] = p.Value.DeepClone();
            return tokens;
        }
    }
}
