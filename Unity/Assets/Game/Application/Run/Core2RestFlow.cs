using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RR = Ashen.Generated.Core2RestKeys;
using RS = Ashen.Generated.Core2RestValues;
using RV = Ashen.Generated.Core2RewardsValues;
using SK = Ashen.Generated.ShopKeys;

namespace Ashen.App.Run
{
    /// <summary>Which modal of the rest screen is open: none, the Smith's item list, the Smith's confirmation of the item chosen, or Level Up.</summary>
    public enum Core2RestModal
    {
        None,
        Smith,
        SmithReview,
        LevelUp,
    }

    /// <summary>One row of the recovery table as the screen draws it: the pool's icon name, its label, the value now and after, and the gain ("+4", or the dash when nothing moves).</summary>
    public sealed class Core2RestLine
    {
        public string Id;
        public string Label;
        public string Before;
        public string After;
        public string Gain;
        public bool Gained;
    }

    /// <summary>One item of the Smith's list: its picture, name, the tier step and how many are owned.</summary>
    public sealed class Core2SmithRow
    {
        public string ItemRef;
        public string Name;
        public string Tier;
        public string Count;
        public string ArtId;
        public string Kind;

        /// <summary>The kind the face helpers know it by (<see cref="Core2RewardsValues"/>), for the icon an item with no picture falls back to.</summary>
        public string FaceKind;

        public bool Selected;
    }

    /// <summary>One before/after row (a stat of the item, a card's figure).</summary>
    public sealed class Core2SmithChange
    {
        public string Label;
        public string Before;
        public string After;
    }

    /// <summary>One attribute requirement the tier moves: "STR 10 → 9" and what the player has.</summary>
    public sealed class Core2SmithRequirement
    {
        public string Label;
        public string Before;
        public string After;
        public string YouHave;
        public bool Met;
    }

    /// <summary>The item under review in the Smith: what it is, its stat rows, its requirements, the deck cards it changes (worn items only) and the recovery the service gives.</summary>
    public sealed class Core2SmithDetail
    {
        public string ItemRef;
        public string Name;
        public string KindLabel;
        public string Tier;
        public string Count;

        /// <summary>"Tier 0 → 1 · ×1 owned".</summary>
        public string Meta;

        public string ArtId;
        public string Kind;
        public string FaceKind;
        public string ChangesHeading;
        public string RequirementsHeading;
        public string CardsHeading;
        public string CostLine;
        public string NoRequirement;
        public IReadOnlyList<Core2SmithChange> Changes = Array.Empty<Core2SmithChange>();
        public IReadOnlyList<Core2SmithRequirement> Requirements = Array.Empty<Core2SmithRequirement>();
        public IReadOnlyList<Core2SmithChange> Cards = Array.Empty<Core2SmithChange>();
        public IReadOnlyList<Core2RestLine> Recovery = Array.Empty<Core2RestLine>();
        public string RecoveryHeading;
    }

    /// <summary>One attribute of Level Up: its name, the value now and after one point, whether a point may go there, and the recovery it would give.</summary>
    public sealed class Core2LevelRow
    {
        public string Id;
        public string Label;
        public string Value;
        public string After;
        public bool Available;
        public bool Selected;
        public string Reason;
    }

    /// <summary>What a press of the rest screen did: a refusal in the web's words (shown at the control; nothing changed), or whether the site has been left (the screen goes where the run stands).</summary>
    public sealed class Core2RestStep
    {
        public string Refusal;
        public bool Left;
        public bool Done => Refusal == null;
    }

    /// <summary>
    /// The rest screen's logic (VP-8, D-950; web ui/components/gameProfileRest.js, smithUpgradeModal.js and statAllocationCard.js over main.js showRest): the site's status, arrival line and
    /// recovery preview; the services (Smith, Level Up) as cards that open their modal, or refuse at the control when they cannot be taken; the Smith's list of items with the one under review
    /// (stat rows, requirements, the deck cards it changes before and after, the recovery) and its confirmation; Level Up's attribute rows and confirmation; Leave. Every sentence is the web's
    /// (strings/app.en.json core2.rest.*) and every rule is <see cref="Core2RunSession"/>'s rest API, which saves at once. Engine-free; the screen only draws it and sends presses here.
    /// </summary>
    public sealed class Core2RestFlow
    {
        private readonly Core2RunSession _session;
        private readonly UiData _ui;
        private readonly StringTable _strings;
        private readonly Dictionary<string, IReadOnlyList<JObject>> _cardRows = new Dictionary<string, IReadOnlyList<JObject>>(StringComparer.Ordinal);
        private string _smithItem;
        private string _levelAttribute;

        public Core2RestFlow(Core2RunSession session, UiData ui)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _ui = ui;
            _strings = ui.Strings;
            Refresh();
        }

        public Core2RestViewState View { get; private set; }

        public Core2RestModal Modal { get; private set; }

        public string Eyebrow => _strings.Get(StringKeys.Core2RestEyebrow);

        public string Title => _strings.Get(StringKeys.Core2RestTitle);

        public string Status => View?.Status ?? string.Empty;

        /// <summary>The arrival line ("Healed +29 HP, +1 MP, +3 SP. Flasks refilled."), then the line under it ("Use any service here, all or none, then Continue.").</summary>
        public string Healed => View?.Healed ?? string.Empty;

        public string Line => View?.Line ?? string.Empty;

        public string RecoveryHeading => View?.RecoveryHeading ?? string.Empty;

        public IReadOnlyList<Core2RestLine> Recovery { get; private set; } = Array.Empty<Core2RestLine>();

        public IReadOnlyList<Core2RestServiceView> Services => View?.Services ?? Array.Empty<Core2RestServiceView>();

        public string ContinueText => View?.ContinueText ?? string.Empty;

        // ------------------------------------------------------------------ the modals' own words

        public string ModalTitle { get; private set; } = string.Empty;

        public string ModalStatus { get; private set; } = string.Empty;

        /// <summary>The sentence under the Smith's pane ("Review the upgrade before confirming...") or Level Up's note.</summary>
        public string ModalNote { get; private set; } = string.Empty;

        public string Instruction { get; private set; } = string.Empty;

        public string BackLabel { get; private set; } = string.Empty;

        public string ConfirmLabel { get; private set; } = string.Empty;

        /// <summary>The Smith's confirmation: the question, the message, the badge and its Back.</summary>
        public string ReviewQuestion { get; private set; } = string.Empty;

        public string ReviewMessage { get; private set; } = string.Empty;

        public string ReviewBadge { get; private set; } = string.Empty;

        public string ReviewBack => _strings.Get(StringKeys.Core2RestSmithConfirmBack);

        /// <summary>The Smith's eyebrow over the item under review ("Selected item").</summary>
        public string SelectedEyebrow => _strings.Get(StringKeys.Core2RestSmithSelected);

        public IReadOnlyList<Core2SmithRow> SmithRows { get; private set; } = Array.Empty<Core2SmithRow>();

        public Core2SmithDetail SmithDetail { get; private set; }

        public string SmithSelected => _smithItem;

        public IReadOnlyList<Core2LevelRow> LevelRows { get; private set; } = Array.Empty<Core2LevelRow>();

        public string LevelSelected => _levelAttribute;

        public IReadOnlyList<Core2RestLine> ModalRecovery { get; private set; } = Array.Empty<Core2RestLine>();

        // ------------------------------------------------------------------ the view

        /// <summary>Reads the site again from the run and rebuilds every field the open modal shows.</summary>
        public void Refresh()
        {
            View = _session.RestView(_strings);
            if (View == null) return;
            Recovery = Lines(View.Recovery);
            if (Modal != Core2RestModal.None) FillModal();
        }

        private string Num(double value) => CombatText.Number(value);

        private static string Fmt(JToken token) => token == null ? string.Empty : Js.IsNum(token) ? CombatText.Number(Js.D(token)) : Js.Str(token);

        private IReadOnlyList<Core2RestLine> Lines(IReadOnlyList<Core2RecoveryRowView> rows)
        {
            if (rows == null) return Array.Empty<Core2RestLine>();
            return rows.Select(r => new Core2RestLine
            {
                Id = r.Id,
                Label = r.Label,
                Before = Num(r.Before),
                After = Num(r.After),
                Gained = r.Gain.HasValue,
                Gain = r.Gain.HasValue ? _strings.Format(StringKeys.Core2RestRecoveryGain, new StringArgs().Add(UiPlaceholders.Amount, Num(r.Gain.Value))) : _strings.Get(StringKeys.Core2RestRecoveryNone),
            }).ToList();
        }

        private Core2RestServiceView Service(string id) => Services.FirstOrDefault(s => s.Id == id);

        private void FillModal()
        {
            switch (Modal)
            {
                case Core2RestModal.Smith:
                case Core2RestModal.SmithReview:
                    FillSmith();
                    break;
                case Core2RestModal.LevelUp:
                    FillLevel();
                    break;
            }
        }

        private string KindText(string kind) => _strings.Get(string.Format(CultureInfo.InvariantCulture, RestShopFormats.SmithKindKey, kind));

        private static string FaceKind(string kind) =>
            kind == RS.KindArmament ? RV.KindWeapons : kind == RS.KindArmor ? RV.KindArmor : RV.KindRelics;

        private string Tier(Core2SmithItemView item) =>
            _strings.Format(StringKeys.Core2RestSmithTier, new StringArgs().Add(UiPlaceholders.Before, Num(item.CurrentLevel)).Add(UiPlaceholders.After, Num(item.NextLevel)));

        private void FillSmith()
        {
            var amount = View.Stones;
            ModalTitle = _strings.Get(StringKeys.Core2RestSmithTitle);
            ModalStatus = _strings.Format(amount == 1 ? StringKeys.Core2RestSmithPurseOne : StringKeys.Core2RestSmithPurseMany, new StringArgs().Add(UiPlaceholders.Amount, Num(amount)));
            Instruction = _strings.Get(StringKeys.Core2RestSmithInstruction);
            ModalNote = View.SmithConsequence;
            BackLabel = _strings.Get(StringKeys.Core2RestSmithBack);
            SmithRows = View.SmithItems.Select(i =>
            {
                var face = Core2ItemFaces.Build(_session, _ui, FaceKind(i.Kind), Js.Str(i.Plan[SK.ItemId]), i.Name);
                return new Core2SmithRow
                {
                    ItemRef = i.ItemRef, Name = i.Name, Kind = i.Kind, FaceKind = FaceKind(i.Kind), Tier = Tier(i), ArtId = face.ArtId, Selected = i.ItemRef == _smithItem,
                    Count = _strings.Format(StringKeys.Core2RestSmithOwned, new StringArgs().Add(UiPlaceholders.Count, Num(i.Plan.Num(SK.InventoryCount)))),
                };
            }).ToList();
            var chosen = View.SmithItems.FirstOrDefault(i => i.ItemRef == _smithItem);
            if (chosen == null)
            {
                _smithItem = null;
                SmithDetail = null;
                ModalRecovery = Array.Empty<Core2RestLine>();
                ConfirmLabel = _strings.Get(StringKeys.Core2RestSmithSelect);
                return;
            }
            SmithDetail = Detail(chosen);
            ModalRecovery = SmithDetail.Recovery;
            ConfirmLabel = _strings.Format(StringKeys.Core2RestSmithUpgrade, new StringArgs().Add(UiPlaceholders.Cost, Num(chosen.Plan.Num(CK.Cost))));
            ReviewQuestion = _strings.Format(StringKeys.Core2RestSmithConfirmTitle, new StringArgs().Add(UiPlaceholders.Name, chosen.Name));
            ReviewMessage = _strings.Format(StringKeys.Core2RestSmithConfirmMessage,
                new StringArgs().Add(UiPlaceholders.Before, Num(chosen.CurrentLevel)).Add(UiPlaceholders.After, Num(chosen.NextLevel)).Add(UiPlaceholders.Label, View.SmithDecision));
            ReviewBadge = _strings.Get(StringKeys.Core2RestSmithPermanent);
        }

        private Core2SmithDetail Detail(Core2SmithItemView item)
        {
            var plan = item.Plan;
            var face = Core2ItemFaces.Build(_session, _ui, FaceKind(item.Kind), Js.Str(plan[SK.ItemId]), item.Name);
            var detail = new Core2SmithDetail
            {
                ItemRef = item.ItemRef, Name = item.Name, Kind = item.Kind, FaceKind = FaceKind(item.Kind), KindLabel = KindText(item.Kind), Tier = Tier(item), ArtId = face.ArtId,
                Count = _strings.Format(StringKeys.Core2RestSmithOwned, new StringArgs().Add(UiPlaceholders.Count, Num(plan.Num(SK.InventoryCount)))),
                ChangesHeading = _strings.Get(StringKeys.Core2RestSmithChanges),
                RequirementsHeading = _strings.Get(StringKeys.Core2RestSmithRequirements),
                CardsHeading = _strings.Get(StringKeys.Core2RestSmithCards),
                NoRequirement = _strings.Get(StringKeys.Core2RestSmithNoRequirement),
                RecoveryHeading = View.RecoveryHeading,
                CostLine = _strings.Format(StringKeys.Core2RestSmithCost, new StringArgs().Add(UiPlaceholders.Cost, Num(plan.Num(CK.Cost))).Add(UiPlaceholders.Amount, Num(plan.Num(SK.Stones)))),
                Recovery = Lines(item.Recovery),
            };
            detail.Meta = _strings.Format(StringKeys.Core2RestSmithMeta, new StringArgs().Add(UiPlaceholders.Label, detail.Tier).Add(UiPlaceholders.Count, detail.Count));
            detail.Changes = Js.Items(plan[SK.Changes]).OfType<JObject>().Where(c => c[RR.Before]?.ToString() != c[RR.After]?.ToString())
                .Select(c => new Core2SmithChange { Label = c.Str(RR.Label), Before = Fmt(c[RR.Before]), After = Fmt(c[RR.After]) }).ToList();
            detail.Requirements = Js.Items(plan[RK.Requirements]).OfType<JObject>().Select(r => new Core2SmithRequirement
            {
                Label = r.Str(CK.Label), Before = Fmt(r[SK.CurrentRequired]), After = Fmt(r[SK.NextRequired]), Met = Js.Truthy(r[SK.MetAfter]),
                YouHave = Js.IsFinite(r[RK.Actual]) ? _strings.Format(StringKeys.Core2RestSmithYouHave, new StringArgs().Add(UiPlaceholders.Amount, Fmt(r[RK.Actual]))) : _strings.Get(StringKeys.Core2RestSmithYouHaveUnknown),
            }).ToList();
            if (!_cardRows.TryGetValue(item.ItemRef, out var rows))
            {
                rows = _session.SmithCardRows(item.ItemRef);
                _cardRows[item.ItemRef] = rows;
            }
            detail.Cards = rows.Select(c => new Core2SmithChange
            {
                Label = _strings.Format(StringKeys.Core2RestSmithCardRow, new StringArgs().Add(UiPlaceholders.Label, c.Str(RR.Label)).Add(UiPlaceholders.Count, Fmt(c[RR.Copies]))),
                Before = Fmt(c[RR.Before]),
                After = Fmt(c[RR.After]),
            }).ToList();
            return detail;
        }

        private void FillLevel()
        {
            ModalTitle = Service(RS.ServiceLevelUp)?.Title ?? string.Empty;
            ModalStatus = string.Empty;
            ModalNote = View.LevelNote;
            BackLabel = _strings.Get(StringKeys.Core2RestLevelCancel);
            ConfirmLabel = View.LevelConfirm;
            LevelRows = View.Attributes.Select(a => new Core2LevelRow
            {
                Id = a.Id, Label = a.Label, Value = Num(a.Value), After = Num(a.Value + (a.Id == _levelAttribute ? 1 : 0)), Available = a.Available, Reason = a.Reason, Selected = a.Id == _levelAttribute,
            }).ToList();
            var chosen = View.Attributes.FirstOrDefault(a => a.Id == _levelAttribute);
            ModalRecovery = chosen?.Recovery == null || !chosen.Available ? Array.Empty<Core2RestLine>() : Lines(chosen.Recovery);
        }

        // ------------------------------------------------------------------ presses

        private Core2RestStep Refuse(string text) => new Core2RestStep { Refusal = string.IsNullOrEmpty(text) ? _strings.Get(StringKeys.RewardsRefusalRefused) : text };

        /// <summary>A service card was pressed: Smith and Level Up open their modal; one that cannot be taken refuses with its reason (the web leaves such a card locked).</summary>
        public Core2RestStep OpenService(string id)
        {
            if (Modal != Core2RestModal.None || View == null) return new Core2RestStep();
            var service = Service(id);
            if (service == null) return Refuse(null);
            if (!service.Available) return Refuse(service.Reason);
            _smithItem = null;
            _levelAttribute = null;
            Modal = id == RS.ServiceSmith ? Core2RestModal.Smith : Core2RestModal.LevelUp;
            FillModal();
            return new Core2RestStep();
        }

        /// <summary>An item of the Smith's list was chosen: its rows (and the deck cards it changes) show.</summary>
        public Core2RestStep SelectSmith(string itemRef)
        {
            if (Modal != Core2RestModal.Smith) return new Core2RestStep();
            var item = View.SmithItems.FirstOrDefault(i => i.ItemRef == itemRef);
            if (item == null) return Refuse(null);
            _smithItem = itemRef;
            FillModal();
            return new Core2RestStep();
        }

        /// <summary>The Smith's Upgrade button: nothing chosen, or an item that cannot be taken now, refuses; otherwise the confirmation opens.</summary>
        public Core2RestStep RequestUpgrade()
        {
            if (Modal != Core2RestModal.Smith) return new Core2RestStep();
            var item = View.SmithItems.FirstOrDefault(i => i.ItemRef == _smithItem);
            if (item == null) return new Core2RestStep { Refusal = _strings.Get(StringKeys.Core2RestSmithSelect) };
            if (!item.Available) return Refuse(item.Reason);
            Modal = Core2RestModal.SmithReview;
            return new Core2RestStep();
        }

        /// <summary>The confirmation's Back: to the Smith's pane.</summary>
        public Core2RestStep BackToSmith()
        {
            if (Modal != Core2RestModal.SmithReview) return new Core2RestStep();
            Modal = Core2RestModal.Smith;
            return new Core2RestStep();
        }

        /// <summary>The confirmation's Upgrade: the item goes up one tier (saved at once) and the site stays open.</summary>
        public Core2RestStep CommitUpgrade()
        {
            if (Modal != Core2RestModal.SmithReview || _smithItem == null) return new Core2RestStep();
            var result = _session.UpgradeItem(_smithItem);
            if (!result.Done) return Refuse(result.Refusal);
            _cardRows.Clear();
            _smithItem = null;
            Modal = Core2RestModal.None;
            Refresh();
            return new Core2RestStep();
        }

        /// <summary>An attribute of Level Up was chosen (pressing the chosen one again takes the point back).</summary>
        public Core2RestStep SelectAttribute(string id)
        {
            if (Modal != Core2RestModal.LevelUp) return new Core2RestStep();
            var row = View.Attributes.FirstOrDefault(a => a.Id == id);
            if (row == null) return Refuse(null);
            if (!row.Available) return Refuse(row.Reason);
            _levelAttribute = _levelAttribute == id ? null : id;
            FillModal();
            return new Core2RestStep();
        }

        /// <summary>Level Up's confirm: nothing chosen refuses; otherwise the point is spent (saved at once) and the site stays open.</summary>
        public Core2RestStep ConfirmLevel()
        {
            if (Modal != Core2RestModal.LevelUp) return new Core2RestStep();
            if (_levelAttribute == null) return new Core2RestStep { Refusal = _strings.Get(StringKeys.RewardsRefusalNoPick) };
            var result = _session.SpendPoint(_levelAttribute);
            if (!result.Done) return Refuse(result.Refusal);
            _levelAttribute = null;
            Modal = Core2RestModal.None;
            Refresh();
            return new Core2RestStep();
        }

        /// <summary>Back, the close glyph or Escape: the review goes back to the Smith's pane, any other modal closes (nothing is taken).</summary>
        public Core2RestStep CloseModal()
        {
            if (Modal == Core2RestModal.SmithReview) return BackToSmith();
            Modal = Core2RestModal.None;
            _smithItem = null;
            _levelAttribute = null;
            return new Core2RestStep();
        }

        /// <summary>Continue: the visit is over (the site stays enterable from the map until a new node is chosen) and the run is back at the map.</summary>
        public Core2RestStep Leave()
        {
            if (Modal != Core2RestModal.None) return new Core2RestStep();
            var result = _session.LeaveRest();
            return result.Done ? new Core2RestStep { Left = true } : Refuse(result.Refusal);
        }
    }
}
