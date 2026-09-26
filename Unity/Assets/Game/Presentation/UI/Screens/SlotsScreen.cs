using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>Which W-03 variant to show: W1l New or W1m Load (UiValues.SlotModeNew / SlotModeLoad).</summary>
    public sealed class SlotsArgs
    {
        public string Mode;
    }

    public sealed class SlotsViewModel : ViewModel
    {
        private string _mode;
        private int _selected = -1;
        private bool _newerKept;
        private bool _emptyState;
        private IReadOnlyList<SlotRowData> _rows = Array.Empty<SlotRowData>();

        public string Mode { get => _mode; set => Set(ref _mode, value); }
        public int Selected { get => _selected; set => Set(ref _selected, value); }
        public bool NewerKept { get => _newerKept; set => Set(ref _newerKept, value); }
        public bool EmptyState { get => _emptyState; set => Set(ref _emptyState, value); }
        public IReadOnlyList<SlotRowData> Rows { get => _rows; set { _rows = value; Notify(); } }
        public bool IsLoad => Mode != UiValues.SlotModeNew;
    }

    /// <summary>
    /// W-03 slots (US-1.4): the three run slots from the SaveService, each with portrait, class, act/floor/HP,
    /// saved time and seed; an empty slot says so; a newer build's save is kept and noted. Selecting a slot and
    /// pressing the primary opens the pre-review door (Load slot n? / Start in slot n?); [✕] opens the destructive
    /// hold-to-confirm delete. Load mode with no saves shows the empty state and a full-width [Create character].
    /// </summary>
    public sealed class SlotsScreen : ScreenView
    {
        private readonly List<SlotRow> _rows = new List<SlotRow>();
        private IReadOnlyList<SlotSummary> _slots = Array.Empty<SlotSummary>();
        private Shell _shell;

        public SlotsViewModel ViewModel { get; } = new SlotsViewModel();
        public IReadOnlyList<SlotRow> Rows => _rows;

        protected override void OnBind(object args)
        {
            ViewModel.Mode = (args as SlotsArgs)?.Mode ?? UiValues.SlotModeLoad;
            _shell = Root.Q<Shell>(UiNames.Shell);
            _shell.Exit += () => Nav.Pop();
            _shell.Back += () => Nav.Pop();
            _shell.Primary += () => OnPrimary(_shell.PrimaryButton);
            var list = Root.Q(UiNames.SlotList);
            for (var i = 0; i < Ui.Saves.Rules.RunSlots; i++)
            {
                var row = new SlotRow();
                row.Selected += r => ViewModel.Selected = _rows.IndexOf(r);
                row.Activated += r => { ViewModel.Selected = _rows.IndexOf(r); OnPrimary(r); };
                row.DeleteRequested += OnDelete;
                list.Add(row);
                _rows.Add(row);
            }
            ViewModel.Observe(Render);
            Refresh();
            Context.Instance.LastFocused = ViewModel.Selected >= 0 && ViewModel.Selected < _rows.Count ? (VisualElement)_rows[ViewModel.Selected] : _shell.PrimaryButton;
        }

        public void Refresh()
        {
            _slots = SlotSummaries.Read(Ui.Saves);
            ViewModel.NewerKept = _slots.Any(s => s.Status == SlotStatus.Newer);
            ViewModel.EmptyState = ViewModel.IsLoad && _slots.All(s => s.Status == SlotStatus.Empty);
            ViewModel.Rows = _slots.Select(RowData).ToList();
            if (ViewModel.EmptyState) ViewModel.Selected = -1;
            else if (ViewModel.Selected < 0 || ViewModel.Selected >= _slots.Count) ViewModel.Selected = DefaultSelection();
        }

        private int DefaultSelection()
        {
            for (var i = 0; i < _slots.Count; i++)
                if (ViewModel.IsLoad ? _slots[i].IsReady : _slots[i].Status == SlotStatus.Empty) return i;
            return 0;
        }

        private SlotRowData RowData(SlotSummary s)
        {
            var strings = Ui.Data.Strings;
            var slotArgs = new StringArgs().Add(UiPlaceholders.Slot, s.Index);
            var data = new SlotRowData { Index = s.Index, Label = strings.Format(StringKeys.SlotsLabel, slotArgs), DeleteName = strings.Get(StringKeys.SlotsDelete) };
            switch (s.Status)
            {
                case SlotStatus.Empty:
                    data.Empty = true;
                    data.Facts = strings.Get(StringKeys.SlotsEmpty);
                    break;
                case SlotStatus.Newer:
                    data.Unreadable = true;
                    data.Facts = strings.Get(StringKeys.SlotsNewerRow);
                    break;
                case SlotStatus.Unreadable:
                    data.Unreadable = true;
                    data.Deletable = true;
                    data.Facts = strings.Get(StringKeys.SlotsCorrupt);
                    break;
                default:
                    data.Deletable = true;
                    data.Portrait = Ui.Texture(s.Portrait);
                    data.Facts = strings.Format(StringKeys.SlotsFacts, new StringArgs()
                        .Add(UiPlaceholders.Name, s.Name).Add(UiPlaceholders.Class, ClassName(s.ClassId)).Add(UiPlaceholders.Act, s.Act).Add(UiPlaceholders.Floor, s.Floor)
                        .Add(UiPlaceholders.Hp, s.Hp).Add(UiPlaceholders.HpMax, s.HpMax));
                    data.Saved = strings.Format(StringKeys.SlotsSaved, new StringArgs().Add(UiPlaceholders.When, When(s.SavedAt)).Add(UiPlaceholders.Seed, s.Seed));
                    break;
            }
            return data;
        }

        private string ClassName(string classId) =>
            classId == null ? string.Empty : Ui.Data.Strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.ClassNameKey, classId));

        /// <summary>"Saved {when}": the time of day today, "yesterday", else the date (04 W-03).</summary>
        private string When(string iso)
        {
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)) return iso ?? string.Empty;
            var local = t.ToLocalTime();
            var today = DateTime.Now.Date;
            if (local.Date == today) return local.ToString(UiFormats.TimeOfDay, CultureInfo.InvariantCulture);
            if (local.Date == today.AddDays(-1)) return Ui.Data.Strings.Get(StringKeys.SlotsWhenYesterday);
            return local.ToString(UiFormats.Date, CultureInfo.InvariantCulture);
        }

        private void Render()
        {
            var load = ViewModel.IsLoad;
            _shell.titleKey = load ? StringKeys.SlotsTitleLoad : StringKeys.SlotsTitleNew;
            if (ViewModel.EmptyState) _shell.SetFooter(null, StringKeys.SlotsPrimaryNew);
            else _shell.SetFooter(StringKeys.SlotsBack, load ? StringKeys.SlotsPrimaryLoad : StringKeys.SlotsPrimaryNew);
            UiDom.Show(Root.Q(UiNames.EmptyState), ViewModel.EmptyState);
            _shell.PrimaryButton?.EnableInClassList(UiClasses.ButtonReady, PrimaryReady());
            UiDom.Show(Root.Q(UiNames.Notice), ViewModel.NewerKept);
            for (var i = 0; i < _rows.Count && i < ViewModel.Rows.Count; i++)
            {
                _rows[i].Bind(ViewModel.Rows[i]);
                _rows[i].SetSelected(i == ViewModel.Selected);
            }
        }

        /// <summary>A ready primary is green (04 §0): Create character always, Load only on a loadable slot.</summary>
        private bool PrimaryReady()
        {
            if (ViewModel.EmptyState || !ViewModel.IsLoad) return true;
            return ViewModel.Selected >= 0 && ViewModel.Selected < _slots.Count && _slots[ViewModel.Selected].IsReady;
        }

        /// <summary>The primary action on the selected slot; a refusal anchors to (and returns focus to) the control that asked.</summary>
        private void OnPrimary(VisualElement anchor)
        {
            if (ViewModel.EmptyState)
            {
                ViewModel.Mode = UiValues.SlotModeNew;
                Refresh();
                ViewModel.Selected = DefaultSelection();
                Nav.FocusTop(_rows[ViewModel.Selected]);
                return;
            }
            if (ViewModel.Selected < 0) return;
            var slot = _slots[ViewModel.Selected];
            var args = new StringArgs().Add(UiPlaceholders.Slot, slot.Index);
            var target = ViewModel.Rows[ViewModel.Selected].Facts;
            if (ViewModel.IsLoad)
            {
                if (slot.Status == SlotStatus.Empty) { Refuse(anchor, StringKeys.SlotsRefusalEmpty); return; }
                if (!slot.IsReady) { Refuse(anchor, StringKeys.SlotsRefusalUnreadable); return; }
                ConfirmRequest.Open(Nav, new ConfirmRequest { ConfirmId = ConfirmIds.LoadSlot, Args = args, Target = target, OnConfirm = () => Refuse(anchor, StringKeys.SlotsRefusalLater) });
                return;
            }
            ConfirmRequest.Open(Nav, new ConfirmRequest
            {
                ConfirmId = ConfirmIds.StartSlot,
                Args = args,
                Target = slot.Status == SlotStatus.Empty ? null : target,
                Occupied = slot.Status != SlotStatus.Empty,
                OnConfirm = () => Refuse(anchor, StringKeys.SlotsRefusalLater),
            });
        }

        private void OnDelete(SlotRow row)
        {
            var index = _rows.IndexOf(row);
            var slot = _slots[index];
            ConfirmRequest.Open(Nav, new ConfirmRequest
            {
                ConfirmId = ConfirmIds.DeleteSlot,
                Args = new StringArgs().Add(UiPlaceholders.Slot, slot.Index),
                Target = Ui.Data.Strings.Format(StringKeys.SlotsTarget, new StringArgs().Add(UiPlaceholders.Label, ViewModel.Rows[index].Label).Add(UiPlaceholders.Facts, ViewModel.Rows[index].Facts)),
                OnConfirm = () =>
                {
                    Ui.Saves.Delete(slot.SlotName);
                    Refresh();
                },
            });
        }

        private void Refuse(VisualElement anchor, string key) =>
            Refusal.Show(anchor, Context.Overlay, Ui.Data.Strings.Get(key), Ui.Data.Tokens.Duration(TokenKeys.Refusal));

        public override void OnReturned() => Refresh();
    }
}
