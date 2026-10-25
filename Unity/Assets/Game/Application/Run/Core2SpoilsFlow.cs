using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using DK = Ashen.Generated.Core2DeckKeys;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RV = Ashen.Generated.Core2RewardsValues;

namespace Ashen.App.Run
{
    /// <summary>Where the spoils screen stands (web gameProfileOffers.js and main.js showGameProfileReward): the level notices, the offer to pick from, or the one picked option under review.</summary>
    public enum Core2SpoilsStage
    {
        Notices,
        Offer,
        Review,
    }

    /// <summary>The chip an option wears: NEW while it can still be taken, TAKEN once it has been (the web's reward door, docs/qa/web-screens/reward-full-1920x1080.jpg).</summary>
    public enum Core2SpoilsChip
    {
        None,
        New,
        Taken,
    }

    /// <summary>One level the fight crossed as the notice stage prints it: the web's sentence parts (gameProfileLevelUp.js) and the numbers.</summary>
    public sealed class Core2SpoilsNotice
    {
        public string Label;
        public string Before;
        public string After;

        /// <summary>The web's sentence: "Blade · Level 1 → 2", a weapon group as "Axe, Mace (Blade group) · Level 1 → 2".</summary>
        public string Sentence;
    }

    /// <summary>One option of the offer as the screen draws it: a card (its face data) or another thing (art, name, one line of what it is).</summary>
    public sealed class Core2SpoilsOption
    {
        public int Index;
        public string Kind;
        public string Id;
        public string Name;

        /// <summary>"Card", "Relic", "Flask", "Armament", "Armour", "Weapon art".</summary>
        public string KindLabel;

        /// <summary>What it does or is, in the web's words (the card's text, a relic's or flask's sentence, an armament's blurb).</summary>
        public string Effect;

        public bool IsCard;
        public Core2SpoilsChip Chip;
        public string ChipText;
        public bool Taken;
        public bool Claimable;

        /// <summary>The registry art id of a collectible's picture (relic, flask, armament), or null for a card or a thing with no art.</summary>
        public string ArtId;

        public RewardPickView Card;
    }

    /// <summary>What a step of the spoils screen did: a refusal in the web's words (shown at the control; nothing changed), or whether the door has been left (the screen goes where the run stands).</summary>
    public sealed class Core2SpoilsResult
    {
        public string Refusal;

        /// <summary>The run no longer stands at this offer: the climb goes on (the map, the next act, the run end), or a level offer replaced it.</summary>
        public bool Left;

        public bool Done => Refusal == null;

        /// <summary>Edit deck claimed the card; the screen opens the deck editor with <see cref="EditorFocusKey"/> ready, and calls <see cref="Core2SpoilsFlow.Proceed"/> when it closes.</summary>
        public bool OpenEditor;

        /// <summary>The collection tile key of the card the reward handed over (the editor starts with it selected).</summary>
        public string EditorFocusKey;

        /// <summary>Equip now claimed the item; the screen opens the Armoury on its equipment, and calls <see cref="Core2SpoilsFlow.Proceed"/> when it closes.</summary>
        public bool OpenEquipment;
    }

    /// <summary>
    /// The spoils screen's logic (VP-7, D-850; web main.js showGameProfileReward 3905-3940, components/gameProfileOffers.js and gameProfileLevelUp.js): the level notices are read first
    /// (Continue), then the offer shows its options and the player picks one (Confirm), the picked option is reviewed (a card: Add to deck, which obeys the deck rules and shows the refusal
    /// when it cannot, or Keep in collection; anything else: Continue), Back returns to the pick, Skip leaves. A spent offer leaves by itself, as the web's proceed() does. The words are the
    /// web's strings (strings/app.en.json spoils.*); the rules are <see cref="Core2RunSession"/>'s reward API. Engine-free; the screen only draws it and sends presses here.
    /// </summary>
    public sealed class Core2SpoilsFlow
    {
        private readonly Core2RunSession _session;
        private readonly UiData _ui;
        private readonly StringTable _strings;
        private string _offerId;
        private int? _selected;
        private bool _reviewing;
        private double _cindersTotal;
        private bool _proceedPending;

        public Core2SpoilsFlow(Core2RunSession session, UiData ui)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _ui = ui;
            _strings = ui.Strings;
            Refresh();
        }

        public Core2RewardsViewState View { get; private set; }

        public Core2SpoilsStage Stage { get; private set; }

        public string Eyebrow { get; private set; } = string.Empty;

        public string Title { get; private set; } = string.Empty;

        /// <summary>"1 of 1 claimed" on the offer; empty on the notices.</summary>
        public string Status { get; private set; } = string.Empty;

        /// <summary>The web's line above the options (Choose 1. Add the card to your deck or keep it in your collection.).</summary>
        public string Note { get; private set; } = string.Empty;

        public string ProgressionHeading => _strings.Get(StringKeys.SpoilsProgression);

        public IReadOnlyList<Core2SpoilsNotice> Notices { get; private set; } = Array.Empty<Core2SpoilsNotice>();

        /// <summary>The options to draw: every one on the offer, only the picked one under review.</summary>
        public IReadOnlyList<Core2SpoilsOption> Options { get; private set; } = Array.Empty<Core2SpoilsOption>();

        /// <summary>The cinders the fight paid and what the purse holds now, or null when it paid none.</summary>
        public string CindersTitle { get; private set; }

        public string CindersBody { get; private set; }

        public int? Selected => _selected;

        public Core2SpoilsOption SelectedOption => _selected.HasValue ? Options.FirstOrDefault(o => o.Index == _selected.Value) : null;

        /// <summary>The primary button's words: Continue (notices, a non-card under review), Confirm (the offer), Add to deck (a card under review).</summary>
        public string ConfirmLabel { get; private set; } = string.Empty;

        /// <summary>The secondary button's words: Skip on the offer, Back under review; empty on the notices.</summary>
        public string BackLabel { get; private set; } = string.Empty;

        /// <summary>"Keep in collection" while a card is under review; empty otherwise.</summary>
        public string KeepLabel { get; private set; } = string.Empty;

        /// <summary>The primary button cannot act now (nothing picked; a card that may not join the deck): the words that say why, shown at the control and in the status line. Empty when it can.</summary>
        public string ConfirmRefusal { get; private set; } = string.Empty;

        /// <summary>"Edit deck" while a card is under review and the deck editor may open (the web hides it otherwise, and Keep in collection takes its place); empty otherwise.</summary>
        public string EditDeckLabel { get; private set; } = string.Empty;

        /// <summary>"Equip now" while an armament or armour is under review; empty otherwise.</summary>
        public string EquipLabel { get; private set; } = string.Empty;

        /// <summary>A claim opened a door (the deck editor, the Armoury) and the door has not yet been left: <see cref="Proceed"/> goes on.</summary>
        public bool ProceedPending => _proceedPending;

        /// <summary>The web's status line under the options (the deck refusal, or what the last press refused).</summary>
        public string StatusLine { get; private set; } = string.Empty;

        /// <summary>The door's offer stands under review (one option shown).</summary>
        public bool Reviewing => _reviewing;

        // ------------------------------------------------------------------ the view

        /// <summary>Reads the door again from the run and rebuilds every field. A new offer (a level offer after the fight's) starts with nothing picked.</summary>
        public void Refresh()
        {
            View = Core2RewardsView.Build(_session);
            if (View == null) return;
            if (View.OfferId != _offerId)
            {
                _offerId = View.OfferId;
                _selected = null;
                _reviewing = false;
                _cindersTotal = _session.Run.Num(RK.Cinders);
            }
            var strings = _strings;
            Notices = View.Notices.Select(Notice).ToList();
            CindersTitle = null;
            CindersBody = null;
            StatusLine = string.Empty;
            ConfirmRefusal = string.Empty;
            KeepLabel = string.Empty;
            EditDeckLabel = string.Empty;
            EquipLabel = string.Empty;
            BackLabel = string.Empty;
            if (View.ShowNotices)
            {
                Stage = Core2SpoilsStage.Notices;
                Eyebrow = strings.Get(StringKeys.SpoilsLevelEyebrow);
                Title = strings.Get(StringKeys.SpoilsLevelTitle);
                Status = string.Empty;
                Note = string.Empty;
                Options = Array.Empty<Core2SpoilsOption>();
                ConfirmLabel = strings.Get(StringKeys.SpoilsContinue);
                return;
            }
            var all = View.Options.Select(Option).ToList();
            if (_selected.HasValue && all.FirstOrDefault(o => o.Index == _selected.Value)?.Claimable != true) { _selected = null; _reviewing = false; }
            Stage = _reviewing ? Core2SpoilsStage.Review : Core2SpoilsStage.Offer;
            Eyebrow = strings.Get(StringKeys.SpoilsEyebrow);
            Title = strings.Get(View.Source == RV.SourceLevel ? StringKeys.SpoilsTitleLevel : StringKeys.SpoilsTitleVictory);
            Status = strings.Format(StringKeys.SpoilsStatus, new StringArgs().Add(UiPlaceholders.Claimed, View.TakenCount).Add(UiPlaceholders.Count, Math.Max(View.TakeCount, 1)));
            Note = strings.Format(EditorOpens ? StringKeys.SpoilsNoteArmoury : StringKeys.SpoilsNote, new StringArgs().Add(UiPlaceholders.Count, View.TakeCount));
            if (View.Cinders > 0 && View.Source == RV.SourceCombat)
            {
                CindersTitle = strings.Format(StringKeys.SpoilsCindersTitle, new StringArgs().Add(UiPlaceholders.Amount, CombatText.Number(View.Cinders)));
                CindersBody = strings.Format(StringKeys.SpoilsCindersBody, new StringArgs().Add(UiPlaceholders.Total, CombatText.Number(_cindersTotal)));
            }
            Options = _reviewing ? all.Where(o => o.Index == _selected).ToList() : all;
            var picked = SelectedOption;
            if (_reviewing && picked != null)
            {
                BackLabel = strings.Get(StringKeys.SpoilsBack);
                if (picked.IsCard)
                {
                    ConfirmLabel = strings.Get(StringKeys.SpoilsAddToDeck);
                    // The web: with a deck door open, Edit deck takes Keep in collection's place (the card is kept either way).
                    if (_session.DeckEditorRefusal() == null) EditDeckLabel = strings.Get(StringKeys.SpoilsEditDeck);
                    else KeepLabel = strings.Get(StringKeys.SpoilsKeep);
                    var code = View.Options.First(o => o.Index == picked.Index).DeckRefusal;
                    ConfirmRefusal = DeckRefusalText(code);
                    StatusLine = ConfirmRefusal;
                }
                else
                {
                    ConfirmLabel = strings.Get(StringKeys.SpoilsContinue);
                    if (picked.Kind == RV.KindWeapons || picked.Kind == RV.KindArmor) EquipLabel = strings.Get(StringKeys.SpoilsEquipNow);
                }
                return;
            }
            ConfirmLabel = strings.Get(StringKeys.SpoilsConfirm);
            BackLabel = strings.Get(StringKeys.SpoilsSkip);
            ConfirmRefusal = _selected.HasValue ? string.Empty : strings.Get(StringKeys.RewardsRefusalNoPick);
        }

        /// <summary>The Armoury opens the deck editor (the web's armouryOpensDeckEditor): the note then promises the editor.</summary>
        private bool EditorOpens => DeckRules.ArmouryOpensEditor(_session.Core.Deck, _session.Run, _session.Profile.Settings);

        private Core2SpoilsNotice Notice(Core2RewardNoticeView n)
        {
            var strings = _strings;
            var label = n.Weapons.Count > 0
                ? strings.Format(StringKeys.SpoilsLevelGroup, new StringArgs().Add(UiPlaceholders.Name, string.Join(RV.ListJoiner, n.Weapons)).Add(UiPlaceholders.Label, n.Label))
                : n.Label;
            var before = CombatText.Number(n.Before);
            var after = CombatText.Number(n.After);
            return new Core2SpoilsNotice
            {
                Label = label,
                Before = before,
                After = after,
                Sentence = strings.Format(StringKeys.SpoilsLevelRow, new StringArgs().Add(UiPlaceholders.Label, label).Add(UiPlaceholders.Before, before).Add(UiPlaceholders.After, after)),
            };
        }

        private Core2SpoilsOption Option(Core2RewardOptionView o)
        {
            var strings = _strings;
            var chip = o.Taken ? Core2SpoilsChip.Taken : o.Claimable ? Core2SpoilsChip.New : Core2SpoilsChip.None;
            var view = new Core2SpoilsOption
            {
                Index = o.Index,
                Kind = o.Kind,
                Id = o.Id,
                Name = o.Name,
                KindLabel = strings.Get(string.Format(CultureInfo.InvariantCulture, SpoilsFormats.KindKey, o.Kind)),
                IsCard = o.IsCard,
                Taken = o.Taken,
                Claimable = o.Claimable,
                Chip = chip,
                ChipText = chip == Core2SpoilsChip.Taken ? strings.Get(StringKeys.SpoilsChipTaken) : chip == Core2SpoilsChip.New ? strings.Get(StringKeys.SpoilsChipNew) : string.Empty,
            };
            var face = Core2ItemFaces.Build(_session, _ui, o.Kind, o.Id, o.Name);
            view.Card = face.Card;
            view.Effect = face.Effect;
            view.ArtId = face.ArtId;
            return view;
        }

        /// <summary>The web's words for an Add to deck refusal code (empty when legal): reward.configured.deckFull and deckLimit; a non-card never reaches this.</summary>
        private string DeckRefusalText(string code)
        {
            if (string.IsNullOrEmpty(code)) return string.Empty;
            if (code == RV.CodeFull)
            {
                var max = DeckRules.EditBounds(_session.Core.Deck, _session.Profile.Settings).Max;
                return _strings.Format(StringKeys.SpoilsRefusalFull, new StringArgs().Add(UiPlaceholders.Max, CombatText.Number(max)));
            }
            return _strings.Get(StringKeys.SpoilsRefusalLimit);
        }

        // ------------------------------------------------------------------ presses

        /// <summary>Notices: Continue reads them (the session's AcknowledgeProgression); a death or the summit then ends the climb.</summary>
        public Core2SpoilsResult Acknowledge()
        {
            if (Stage != Core2SpoilsStage.Notices) return new Core2SpoilsResult();
            var result = _session.AcknowledgeProgression();
            return Settle(result);
        }

        /// <summary>Picks an option on the offer (the web's select: the radio and the face). A taken option refuses with "Already taken."</summary>
        public Core2SpoilsResult Select(int index)
        {
            if (Stage != Core2SpoilsStage.Offer) return new Core2SpoilsResult();
            var option = Options.FirstOrDefault(o => o.Index == index);
            if (option == null) return new Core2SpoilsResult { Refusal = _strings.Get(StringKeys.RewardsRefusalClaimed) };
            if (!option.Claimable) return new Core2SpoilsResult { Refusal = _strings.Get(StringKeys.RewardsRefusalClaimed) };
            _selected = index;
            Refresh();
            return new Core2SpoilsResult();
        }

        /// <summary>Offer: Confirm opens the review of the picked option (nothing picked refuses). Review: Add to deck for a card (the deck rules may refuse), Continue for anything else.</summary>
        public Core2SpoilsResult Confirm()
        {
            switch (Stage)
            {
                case Core2SpoilsStage.Notices:
                    return Acknowledge();
                case Core2SpoilsStage.Offer:
                    if (!_selected.HasValue) return new Core2SpoilsResult { Refusal = _strings.Get(StringKeys.RewardsRefusalNoPick) };
                    _reviewing = true;
                    Refresh();
                    return new Core2SpoilsResult();
                default:
                {
                    var option = SelectedOption;
                    if (option == null) return new Core2SpoilsResult { Refusal = _strings.Get(StringKeys.RewardsRefusalNoPick) };
                    if (!option.IsCard) return Claim(_session.TakeReward(option.Index));
                    if (ConfirmRefusal.Length > 0) return new Core2SpoilsResult { Refusal = ConfirmRefusal };
                    var added = _session.AddRewardToDeck(option.Index);
                    return Claim(added, added.Code);
                }
            }
        }

        /// <summary>Review of a card: take it into the collection without adding it to the deck.</summary>
        public Core2SpoilsResult Keep()
        {
            var option = SelectedOption;
            if (Stage != Core2SpoilsStage.Review || option == null || !option.IsCard) return new Core2SpoilsResult();
            return Claim(_session.TakeReward(option.Index));
        }

        /// <summary>
        /// Review of a card, Edit deck (web gameProfileOffers.js editDeck): the card is claimed into the collection, then the deck editor opens with it ready to place. The offer is not left
        /// until the editor closes (<see cref="Proceed"/>), so a reload mid-editor lands back on the spoils with the card already taken.
        /// </summary>
        public Core2SpoilsResult EditDeck()
        {
            var option = SelectedOption;
            if (Stage != Core2SpoilsStage.Review || option == null || !option.IsCard || EditDeckLabel.Length == 0) return new Core2SpoilsResult();
            var claim = _session.TakeReward(option.Index);
            if (!claim.Done) return new Core2SpoilsResult { Refusal = claim.Refusal };
            var instanceId = Core2Rewards.InstanceId(View.OfferId, option.Index);
            var card = Js.Items(_session.Run[DK.Sideboard]).OfType<JObject>().FirstOrDefault(c => c.Str(DK.InstanceId) == instanceId);
            return Opened(new Core2SpoilsResult { OpenEditor = true, EditorFocusKey = card == null ? null : Ashen.Generated.Core2DeckEditorValues.KindCard + Ashen.Generated.Core2DeckEditorValues.KeySeparator + DeckEditor.VariantKey(card) });
        }

        /// <summary>Review of an armament or armour, Equip now (web equip): the item is claimed, then the Armoury opens so it can be worn.</summary>
        public Core2SpoilsResult EquipNow()
        {
            var option = SelectedOption;
            if (Stage != Core2SpoilsStage.Review || option == null || EquipLabel.Length == 0) return new Core2SpoilsResult();
            var claim = _session.TakeReward(option.Index);
            if (!claim.Done) return new Core2SpoilsResult { Refusal = claim.Refusal };
            return Opened(new Core2SpoilsResult { OpenEquipment = true });
        }

        private Core2SpoilsResult Opened(Core2SpoilsResult result)
        {
            _proceedPending = true;
            _reviewing = false;
            _selected = null;
            Refresh();
            return result;
        }

        /// <summary>The door a claim opened was left (the editor or the Armoury closed): a spent offer is left by itself, else the offer stands for the next pick (the web's proceed()).</summary>
        public Core2SpoilsResult Proceed()
        {
            if (!_proceedPending) return new Core2SpoilsResult { Left = !_session.HasPendingReward || _session.RunOver };
            _proceedPending = false;
            return Claim(new Core2RewardResult { Option = new JObject() });
        }

        /// <summary>Review: back to the pick (the web's Back).</summary>
        public Core2SpoilsResult Back()
        {
            if (Stage != Core2SpoilsStage.Review) return new Core2SpoilsResult();
            _reviewing = false;
            Refresh();
            return new Core2SpoilsResult();
        }

        /// <summary>Offer: Skip leaves the spoils where they lie (LeaveRewards).</summary>
        public Core2SpoilsResult Skip()
        {
            if (Stage != Core2SpoilsStage.Offer) return new Core2SpoilsResult();
            return Settle(_session.LeaveRewards());
        }

        /// <summary>A claim landed (or was refused): a spent offer is left by itself (the web's proceed()), else the offer stands for the next pick.</summary>
        private Core2SpoilsResult Claim(Core2RewardResult claim, string code = null)
        {
            if (!claim.Done)
            {
                var text = code == RV.CodeFull || code == RV.CodeLimit ? DeckRefusalText(code) : claim.Refusal;
                return new Core2SpoilsResult { Refusal = text };
            }
            _reviewing = false;
            _selected = null;
            var view = Core2RewardsView.Build(_session);
            var spent = view == null || view.Options.All(o => !o.Claimable) || view.TakenCount >= Math.Min(view.TakeCount, view.Options.Count);
            if (spent && _session.HasPendingReward) return Settle(_session.LeaveRewards());
            return Settle(claim);
        }

        private Core2SpoilsResult Settle(Core2RewardResult step)
        {
            if (!step.Done) return new Core2SpoilsResult { Refusal = step.Refusal };
            Refresh();
            var left = !_session.HasPendingReward || _session.RunOver;
            return new Core2SpoilsResult { Left = left };
        }
    }
}
