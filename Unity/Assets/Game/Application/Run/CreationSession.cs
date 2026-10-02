using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using CV = Ashen.Generated.CreationValues;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using MU = Ashen.Generated.MetaUiKeys;
using P = Ashen.Generated.MetaPlaceholders;
using RK = Ashen.Generated.RunKeys;
using S = Ashen.Generated.MetaStringKeys;

namespace Ashen.App.Run
{
    /// <summary>A W-04 refusal: the reason (resolved text) and the pane and control focus moves to.</summary>
    public sealed class CreationRefusal
    {
        public string Text;
        public string Pane;
        public string Focus;
    }

    /// <summary>One choice row of a W-04 pane (a class, attribute mode, keepsake, tint, sigil or a piece of equipment).</summary>
    public sealed class CreationOption
    {
        public string Id;
        public string Label;
        public string Detail;
        public bool Selected;
        public bool Locked;
        public string LockReason;
    }

    /// <summary>One attribute of the Character pane, with whether −/+ would be taken.</summary>
    public sealed class CreationAttribute
    {
        public string Id;
        public string Label;
        public int Value;
        public bool CanLower;
        public bool CanRaise;
    }

    /// <summary>What the chosen loadout would start with (createRunState's own document; null when it cannot begin).</summary>
    public sealed class CreationPreview
    {
        public string Derived;
        public string Flasks;
        public string Deck;
        public readonly List<string> DeckCards = new List<string>();
        public string Problem;
    }

    /// <summary>
    /// W-04 creation, all four panes (US-2.1-2.6, US-2.8) engine-free: the class, then the name, stat mode (Standard is
    /// the class preset; Assign starts every attribute at the mode's baseline with its bonus pool), keepsake, tint and
    /// sigil, then armour, hands (one item never in both: choosing the other hand's item moves it), relic and kit, then
    /// the journey, seed and slot. Next always answers: it moves on or refuses with the reason and the control to focus.
    /// Every choice is checked by <see cref="RunState.Create"/>, the same door the run is made through, so the preview
    /// and Begin cannot disagree. Back keeps every choice.
    /// </summary>
    public sealed class CreationSession
    {
        private readonly RunContent _content;
        private readonly SaveService _saves;
        private readonly JObject _profile;
        private readonly UiData _ui;
        private readonly JObject _rules;
        private readonly Func<uint> _random;

        public CreationSession(RunContent content, SaveService saves, JObject profile, UiData ui, int slotIndex, Func<uint> random)
        {
            _content = content;
            _saves = saves;
            _profile = profile ?? new JObject();
            _ui = ui;
            _rules = ui.Meta?.Obj(MU.Creation) ?? new JObject();
            _random = random;
            SlotIndex = slotIndex;
            Name = ui.Strings.Get(content.Flow.NameKey);
            Tint = content.Flow.DefaultTint;
            Glyph = Js.Str(content.Flow.Customization[RunFlowKeys.Glyph]);
            Seed = random();
            Panes = Js.Items(_rules[MU.Panes]).Select(Js.Str).Where(s => s != null).ToList();
        }

        public IReadOnlyList<string> Panes { get; }
        public int PaneIndex { get; private set; }
        public string Pane => Panes.Count == 0 ? null : Panes[PaneIndex];
        public bool OnReview => PaneIndex == Panes.Count - 1;

        public string ClassId { get; private set; }
        public string Name { get; private set; }
        public bool Assign { get; private set; }
        public JObject Attributes { get; private set; }
        public string KeepsakeId { get; private set; }
        public string Tint { get; private set; }
        public string Glyph { get; private set; }
        public string ArmourId { get; private set; }
        public string RightHand { get; private set; }
        public string LeftHand { get; private set; }
        public string RelicId { get; private set; }
        public string KitId { get; private set; }
        public uint Seed { get; private set; }
        public int SlotIndex { get; }

        private RunData Data => _content.Data;
        private string ModeId => _rules.Str(MU.AttributeMode) ?? CreationStats.DefaultCreationModeId(Data);
        private JObject Mode => CreationStats.CreationMode(Data, ModeId);

        // ------------------------------------------------------------------ the rail and footer

        /// <summary>The rail: each pane with its receipt (US-2.6, US-2.8).</summary>
        public List<KeyValuePair<string, string>> Rail()
        {
            var strings = _ui.Strings;
            var rail = new List<KeyValuePair<string, string>>();
            foreach (var pane in Panes)
            {
                string receipt;
                switch (pane)
                {
                    case CV.PaneClass:
                        receipt = ClassId == null ? strings.Get(S.CreationReceiptNone) : ClassName(ClassId);
                        break;
                    case CV.PaneCharacter:
                        receipt = strings.Format(S.CreationReceiptCharacter, new StringArgs().Add(P.Name, Name)
                            .Add(P.Mode, strings.Get(Assign ? S.CreationStatModeAssign : S.CreationStatModeStandard)));
                        break;
                    case CV.PaneEquipment:
                        receipt = ClassId == null ? strings.Get(S.CreationReceiptNone) : strings.Format(S.CreationReceiptEquipment,
                            new StringArgs().Add(P.Armour, ArmourName(ResolvedArmour())).Add(P.Hands, HandsText()));
                        break;
                    default:
                        receipt = strings.Format(S.CreationReceiptReview, new StringArgs().Add(P.Seed, SeedText).Add(P.Slot, SlotIndex));
                        break;
                }
                rail.Add(new KeyValuePair<string, string>(pane, receipt));
            }
            return rail;
        }

        /// <summary>Next (Begin on Review is <see cref="Begin"/>): the next pane, or the refusal with its focus.</summary>
        public CreationRefusal Next()
        {
            var refusal = PaneRefusal(Pane);
            if (refusal != null) return refusal;
            if (!OnReview) PaneIndex++;
            return null;
        }

        public void Back()
        {
            if (PaneIndex > 0) PaneIndex--;
        }

        /// <summary>A rail jump: any pane up to the first one that still refuses.</summary>
        public CreationRefusal Open(string pane)
        {
            var target = Panes.ToList().IndexOf(pane);
            if (target < 0) return Refuse(S.CreationRefusalUnknown, Pane, null);
            for (var i = 0; i < target; i++)
            {
                var refusal = PaneRefusal(Panes[i]);
                if (refusal != null) return refusal;
            }
            PaneIndex = target;
            return null;
        }

        private CreationRefusal PaneRefusal(string pane)
        {
            switch (pane)
            {
                case CV.PaneClass:
                    if (ClassId == null) return new CreationRefusal { Text = _ui.Strings.Get(StringKeys.CreationRefusalNoClass), Pane = CV.PaneClass, Focus = CV.PaneClass };
                    var problem = _content.ClassProblem(ClassId);
                    return problem == null ? null : new CreationRefusal { Text = _ui.Strings.Get(StringKeys.CreationProblem), Pane = CV.PaneClass, Focus = CV.PaneClass };
                case CV.PaneCharacter:
                    if (string.IsNullOrWhiteSpace(Name)) return Refuse(S.CreationRefusalNameEmpty, CV.PaneCharacter, K.Name);
                    var left = PointsLeft;
                    if (Assign && left > 0)
                        return new CreationRefusal { Text = _ui.Strings.Format(S.CreationRefusalPointsLeft, new StringArgs().Add(P.N, left)), Pane = CV.PaneCharacter, Focus = K.Attributes };
                    return null;
                case CV.PaneEquipment:
                    var preview = Preview();
                    return preview.Problem == null ? null : new CreationRefusal { Text = preview.Problem, Pane = CV.PaneEquipment, Focus = CV.ChoiceArmour };
                default:
                    return null;
            }
        }

        private CreationRefusal Refuse(string key, string pane, string focus) => new CreationRefusal { Text = _ui.Strings.Get(key), Pane = pane, Focus = focus };

        // ------------------------------------------------------------------ class (US-2.1)

        public List<CreationOption> Classes() => _content.ClassIds.Select(id =>
        {
            var problem = _content.ClassProblem(id);
            return new CreationOption
            {
                Id = id,
                Label = ClassName(id),
                Selected = id == ClassId,
                Locked = problem != null,
                LockReason = problem != null ? _ui.Strings.Get(StringKeys.CreationProblem) : null,
            };
        }).ToList();

        /// <summary>Choosing a class resets the class-bound choices (attributes, equipment) to that class's defaults.</summary>
        public CreationRefusal ChooseClass(string classId)
        {
            if (classId == null || !Data.Classes.Has(classId)) return Refuse(S.CreationRefusalUnknown, CV.PaneClass, CV.PaneClass);
            if (classId == ClassId) return null;
            ClassId = classId;
            Attributes = Assign ? AssignStart() : null;
            ArmourId = null;
            RelicId = null;
            KitId = null;
            var kit = SafeKit(null);
            RightHand = kit?.Str(RK.RightHand);
            LeftHand = kit?.Str(RK.LeftHand);
            return null;
        }

        // ------------------------------------------------------------------ character (US-2.2, US-2.3)

        public CreationRefusal SetName(string name)
        {
            var max = (int)Js.Or0(_rules[MU.NameMaxLength]);
            var trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0) return Refuse(S.CreationRefusalNameEmpty, CV.PaneCharacter, K.Name);
            if (max > 0 && trimmed.Length > max)
                return new CreationRefusal { Text = _ui.Strings.Format(S.CreationRefusalNameLong, new StringArgs().Add(P.N, max)), Pane = CV.PaneCharacter, Focus = K.Name };
            Name = trimmed;
            return null;
        }

        /// <summary>Standard (the class preset) or Assign (the mode's baseline in every cell and the bonus pool to spend).</summary>
        public void SetAssign(bool assign)
        {
            Assign = assign;
            Attributes = assign && ClassId != null ? AssignStart() : null;
        }

        private JObject AssignStart()
        {
            var cells = new JObject();
            foreach (var a in CreationStats.OrderedAttributes(Data)) cells[a.Str(K.Id)] = Mode.Num(K.Baseline);
            return cells;
        }

        /// <summary>The cells the run would start with: the class preset (Standard) or the assigned cells.</summary>
        public JObject CurrentAttributes() => ClassId == null ? new JObject() : Attributes ?? CreationStats.ClassAttributePreset(Data, ClassId, ModeId);

        public int PointsLeft
        {
            get
            {
                if (!Assign || Attributes == null) return 0;
                var ids = CreationStats.OrderedAttributes(Data).Select(a => a.Str(K.Id)).ToList();
                var expected = Mode.Num(K.Baseline) * ids.Count + Mode.Num(RK.BonusPool);
                return (int)(expected - ids.Sum(id => Attributes.Num(id)));
            }
        }

        public List<CreationAttribute> AttributeRows()
        {
            var cells = CurrentAttributes();
            var floor = Mode.Num(K.Minimum);
            var cap = Mode.Num(K.Maximum);
            return CreationStats.OrderedAttributes(Data).Select(a =>
            {
                var id = a.Str(K.Id);
                var v = (int)cells.Num(id);
                return new CreationAttribute
                {
                    Id = id,
                    Label = Format(CV.AttributeLabelKey, id),
                    Value = v,
                    CanLower = Assign && v > floor,
                    CanRaise = Assign && v < cap && PointsLeft > 0,
                };
            }).ToList();
        }

        /// <summary>One point into (+1) or out of (−1) an attribute, within the mode's floor and cap and the pool.</summary>
        public CreationRefusal Adjust(string attributeId, int step)
        {
            if (!Assign || Attributes == null) return Refuse(S.CreationRefusalStandardMode, CV.PaneCharacter, K.Attributes);
            if (Attributes[attributeId] == null) return Refuse(S.CreationRefusalUnknown, CV.PaneCharacter, K.Attributes);
            var label = Format(CV.AttributeLabelKey, attributeId);
            var next = Attributes.Num(attributeId) + Math.Sign(step);
            if (next < Mode.Num(K.Minimum))
                return new CreationRefusal { Text = _ui.Strings.Format(S.CreationRefusalAttributeFloor, new StringArgs().Add(P.Name, label)), Pane = CV.PaneCharacter, Focus = attributeId };
            if (next > Mode.Num(K.Maximum))
                return new CreationRefusal { Text = _ui.Strings.Format(S.CreationRefusalAttributeCap, new StringArgs().Add(P.Name, label)), Pane = CV.PaneCharacter, Focus = attributeId };
            if (step > 0 && PointsLeft <= 0) return Refuse(S.CreationRefusalNoPoints, CV.PaneCharacter, attributeId);
            Attributes[attributeId] = next;
            return null;
        }

        public List<CreationOption> Keepsakes() => Js.Items(Data.CharacterCreation[LK.Keepsakes]).OfType<JObject>().Select(k => new CreationOption
        {
            Id = k.Str(K.Id),
            Label = k.Str(K.Name),
            Detail = k.Str(MetaKeys.Desc),
            Selected = k.Str(K.Id) == (KeepsakeId ?? FirstKeepsake()),
        }).ToList();

        private string FirstKeepsake() => Js.Items(Data.CharacterCreation[LK.Keepsakes]).OfType<JObject>().Select(k => k.Str(K.Id)).FirstOrDefault();

        public CreationRefusal ChooseKeepsake(string id) => Choose(Keepsakes(), id, CV.PaneCharacter, CV.ChoiceKeepsake, () => KeepsakeId = id);

        /// <summary>The tints with a portrait frame set for the class (the tint picks frames; no recolour).</summary>
        public List<CreationOption> Tints() => Js.Items(_rules[MU.Tints]).Select(Js.Str)
            .Where(t => ClassId == null || _content.HasAsset(_content.Portrait(ClassId, t)))
            .Select(t => new CreationOption { Id = t, Label = t, Detail = ClassId == null ? null : _content.Portrait(ClassId, t), Selected = t == Tint }).ToList();

        public CreationRefusal ChooseTint(string tint) => Choose(Tints(), tint, CV.PaneCharacter, RunFlowKeys.Tint, () => Tint = tint);

        public List<CreationOption> Glyphs() => Js.Items(_rules[MU.Glyphs]).Select(Js.Str)
            .Select(g => new CreationOption { Id = g, Label = g, Selected = g == Glyph }).ToList();

        public CreationRefusal ChooseGlyph(string glyph) => Choose(Glyphs(), glyph, CV.PaneCharacter, RunFlowKeys.Glyph, () => Glyph = glyph);

        private CreationRefusal Choose(List<CreationOption> options, string id, string pane, string focus, Action take)
        {
            var option = options.FirstOrDefault(o => o.Id == id);
            if (option == null) return Refuse(S.CreationRefusalUnknown, pane, focus);
            if (option.Locked) return new CreationRefusal { Text = _ui.Strings.Format(S.CreationRefusalLocked, new StringArgs().Add(P.Hint, option.LockReason)), Pane = pane, Focus = focus };
            take();
            return null;
        }

        // ------------------------------------------------------------------ equipment (US-2.4)

        private JObject Config() => ClassId == null ? new JObject() : Data.CharacterCreation?.Obj(K.Classes)?.Obj(ClassId) ?? new JObject();

        /// <summary>The class's armour (shared sets aside); a locked one names its unlock.</summary>
        public List<CreationOption> Armour()
        {
            if (ClassId == null) return new List<CreationOption>();
            var chosen = ResolvedArmour();
            return Data.EquipmentRows(K.Armour).Where(r => r.Str(K.ClassId) == ClassId && !Js.Truthy(r[RK.SharedSet])).Select(r =>
            {
                var eligible = Creation.ArmourIsStartingEligible(Data, r, _profile, ClassId);
                return new CreationOption
                {
                    Id = r.Str(K.Id),
                    Label = ArmourName(r.Str(K.Id)),
                    Selected = r.Str(K.Id) == chosen,
                    Locked = !eligible,
                    LockReason = eligible ? null : UnlockHint(r.Str(RK.Unlock)),
                };
            }).ToList();
        }

        public CreationRefusal ChooseArmour(string id) => Choose(Armour(), id, CV.PaneEquipment, CV.ChoiceArmour, () => ArmourId = id);

        private string ResolvedArmour()
        {
            if (ArmourId != null || ClassId == null) return ArmourId;
            try { return Creation.ResolveStartingArmour(Data, ClassId, null, _profile).Str(K.Id); }
            catch (InvalidOperationException) { return null; }
        }

        /// <summary>The class's creation hand list for a slot (the off hand may be empty).</summary>
        public List<CreationOption> Hands(string slot)
        {
            var held = slot == RK.RightHand ? RightHand : LeftHand;
            var options = RunJs.Strs(Config()[RK.HandIds]).Select(id => new CreationOption { Id = id, Label = Format(MetaFormats.ArmamentName, id), Selected = id == held }).ToList();
            if (slot == RK.LeftHand) options.Insert(0, new CreationOption { Id = string.Empty, Label = _ui.Strings.Get(S.CreationHandEmpty), Selected = string.IsNullOrEmpty(held) });
            return options;
        }

        /// <summary>
        /// Puts an armament in a hand. An item held in the other hand moves (never duplicated) and the receipt says so;
        /// the off hand can be emptied. Grip legality is the loadout's (the preview and Next refuse an illegal pair).
        /// </summary>
        public CreationRefusal ChooseHand(string slot, string id, out string receipt)
        {
            receipt = null;
            if (slot != RK.RightHand && slot != RK.LeftHand) return Refuse(S.CreationRefusalUnknown, CV.PaneEquipment, slot);
            if (Hands(slot).All(o => o.Id != (id ?? string.Empty))) return Refuse(S.CreationRefusalUnknown, CV.PaneEquipment, slot);
            var other = slot == RK.RightHand ? LeftHand : RightHand;
            if (!string.IsNullOrEmpty(id) && id == other)
            {
                if (slot == RK.RightHand) LeftHand = null; else RightHand = null;
                receipt = _ui.Strings.Format(S.CreationHandMoved, new StringArgs().Add(P.Name, Format(MetaFormats.ArmamentName, id))
                    .Add(P.Slot, _ui.Strings.Get(slot == RK.RightHand ? S.CreationSlotRightHand : S.CreationSlotLeftHand)));
            }
            if (slot == RK.RightHand) RightHand = string.IsNullOrEmpty(id) ? null : id;
            else LeftHand = string.IsNullOrEmpty(id) ? null : id;
            return null;
        }

        public List<CreationOption> Relics()
        {
            var chosen = RelicId ?? (ClassId == null ? null : Data.Classes.Get(ClassId).Str(RK.StartingRelic));
            return RunJs.Strs(Config()[K.RelicIds]).Select(id => new CreationOption { Id = id, Label = Format(CV.RelicNameKey, id), Selected = id == chosen }).ToList();
        }

        public CreationRefusal ChooseRelic(string id) => Choose(Relics(), id, CV.PaneEquipment, CV.ChoiceRelic, () => RelicId = id);

        /// <summary>The class's starting kits; one not yet discovered (a piece never found) is locked.</summary>
        public List<CreationOption> Kits()
        {
            if (ClassId == null) return new List<CreationOption>();
            var eligible = RunJs.Strs(Data.Classes.Get(ClassId)[RK.EligibleStartingKitIds]);
            var chosen = SafeKit(KitId)?.Str(K.Id);
            return Data.EquipmentRows(RK.StartingKits).Where(k => k.Str(K.ClassId) == ClassId && eligible.Contains(k.Str(K.Id))).Select(k =>
            {
                var found = Creation.KitIsDiscovered(k, _profile);
                return new CreationOption { Id = k.Str(K.Id), Label = Format(CV.KitNameKey, k.Str(K.Id)), Selected = k.Str(K.Id) == chosen, Locked = !found, LockReason = found ? null : _ui.Strings.Get(S.CreationReceiptNone) };
            }).ToList();
        }

        /// <summary>A kit sets both hands to its pieces.</summary>
        public CreationRefusal ChooseKit(string id) => Choose(Kits(), id, CV.PaneEquipment, CV.ChoiceKit, () =>
        {
            KitId = id;
            var kit = SafeKit(id);
            RightHand = kit?.Str(RK.RightHand);
            LeftHand = kit?.Str(RK.LeftHand);
        });

        private JObject SafeKit(string id)
        {
            if (ClassId == null) return null;
            try { return Creation.ResolveStartingKit(Data, ClassId, id, _profile); }
            catch (InvalidOperationException) { return null; }
        }

        // ------------------------------------------------------------------ review (US-2.5)

        public string SeedText => _content.Seeds.Format(Seed);
        public string Journey => _ui.Strings.Get(S.CreationJourneyClassic);
        public bool SlotOccupied => _saves.Exists(_saves.Rules.RunSlotName(SlotIndex));
        public string SlotNote => SlotOccupied ? _ui.Strings.Format(S.CreationSlotOccupied, new StringArgs().Add(P.N, SlotIndex)) : _ui.Strings.Format(S.CreationSlot, new StringArgs().Add(P.N, SlotIndex));

        public void RandomSeed() => Seed = _random();

        public CreationRefusal SetSeed(string text)
        {
            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0) return Refuse(S.CreationRefusalSeedEmpty, CV.PaneReview, RK.Seed);
            if (trimmed.Length > _content.Seeds.MaxLength)
                return new CreationRefusal { Text = _ui.Strings.Format(S.CreationRefusalSeedLong, new StringArgs().Add(P.N, _content.Seeds.MaxLength)), Pane = CV.PaneReview, Focus = RK.Seed };
            var bad = _content.Seeds.Problem(trimmed);
            if (bad.HasValue)
                return new CreationRefusal { Text = _ui.Strings.Format(S.CreationRefusalSeed, new StringArgs().Add(P.Name, bad.Value.ToString())), Pane = CV.PaneReview, Focus = RK.Seed };
            Seed = _content.Seeds.Parse(trimmed);
            return null;
        }

        // ------------------------------------------------------------------ preview and Begin

        /// <summary>createRunState's options for the current choices.</summary>
        public RunOptions Options() => new RunOptions
        {
            AttributeMode = ModeId,
            Attributes = Assign ? (JObject)Attributes?.DeepClone() : null,
            StartingKitId = KitId,
            StartingHands = ClassId == null ? null : Js.Obj(RK.RightHand, RightHand == null ? Js.Null() : (JToken)RightHand, RK.LeftHand, LeftHand == null ? Js.Null() : (JToken)LeftHand),
            StartingArmourId = ArmourId,
            StartingRelicId = RelicId,
            ProfileMeta = _profile,
        };

        /// <summary>The run the choices would make: pools, flasks and the starting deck, or the reason it cannot begin.</summary>
        public CreationPreview Preview()
        {
            var strings = _ui.Strings;
            var preview = new CreationPreview();
            if (ClassId == null) return preview;
            JObject run;
            try { run = RunState.Create(Data, 1u, ClassId, Options()); }
            catch (Exception e) when (e is InvalidOperationException || e is NotSupportedException)
            {
                preview.Problem = strings.Format(S.CreationRefusalLoadout, new StringArgs().Add(P.Reason, e.Message));
                return preview;
            }
            var charges = run.Obj(K.FlaskCharges) ?? new JObject();
            preview.Derived = strings.Format(StringKeys.CreationDerived, new StringArgs()
                .Add(UiPlaceholders.Hp, (int)run.Num(K.MaxHp)).Add(UiPlaceholders.Mp, (int)run.Num(K.MaxMana))
                .Add(UiPlaceholders.Sp, (int)run.Num(K.MaxStamina)).Add(UiPlaceholders.Actions, (int)run.Num(K.EnergyMax)));
            preview.Flasks = strings.Format(StringKeys.CreationFlasks, new StringArgs().Add(UiPlaceholders.Hp, (int)charges.Num(K.Hp)).Add(UiPlaceholders.Mp, (int)charges.Num(K.Mana)));
            foreach (var card in Js.Items(run[K.Deck]).OfType<JObject>())
            {
                var id = card.Str(K.CardId);
                preview.DeckCards.Add(id != null && Data.Cards.Has(id) ? Data.Cards.Get(id).Str(K.Name) ?? id : id);
            }
            preview.Deck = strings.Format(S.CreationDeck, new StringArgs().Add(P.Count, preview.DeckCards.Count));
            return preview;
        }

        /// <summary>
        /// Begin the Climb: every pane's checks, then the run through <see cref="RunSession.New"/> with these choices.
        /// An occupied slot needs <paramref name="replaceConfirmed"/> (W2c Replace); without it nothing is written.
        /// </summary>
        public RunSession Begin(bool replaceConfirmed, out CreationRefusal refusal, Func<DateTime> clock = null, JObject custom = null)
        {
            refusal = null;
            foreach (var pane in Panes)
            {
                refusal = PaneRefusal(pane);
                if (refusal != null)
                {
                    PaneIndex = Panes.ToList().IndexOf(pane);
                    return null;
                }
            }
            if (SlotOccupied && !replaceConfirmed)
            {
                refusal = new CreationRefusal { Text = SlotNote, Pane = CV.PaneReview, Focus = SaveKeys.RunSlots };
                return null;
            }
            return RunSession.New(_content, _saves, SlotIndex, Seed, ClassId, Name, clock, custom, new CreationChoices
            {
                Creation = Options(),
                KeepsakeId = KeepsakeId,
                Tint = Tint,
                Glyph = Glyph,
            });
        }

        // ------------------------------------------------------------------ names

        private string ClassName(string classId) => RunHudView.ClassName(_ui, classId);

        private string ArmourName(string id) => id == null ? _ui.Strings.Get(S.CreationReceiptNone)
            : Lookup(string.Format(CultureInfo.InvariantCulture, CV.ArmourNameKey, ClassId, id), id);

        private string HandsText()
        {
            var hands = new[] { RightHand, LeftHand }.Where(h => !string.IsNullOrEmpty(h)).Select(h => Format(MetaFormats.ArmamentName, h)).ToList();
            return hands.Count == 0 ? _ui.Strings.Get(S.CreationHandEmpty) : string.Join(_ui.Strings.Get(S.CreationHandsJoin), hands);
        }

        private string UnlockHint(string unlockId)
        {
            var row = Js.Items(_content.Loop.Unlocks).OfType<JObject>().FirstOrDefault(u => u.Str(K.Id) == unlockId || u.Str(RunFlowKeys.Ref) == unlockId);
            var key = string.Format(CultureInfo.InvariantCulture, CV.UnlockHintKey, row?.Str(K.Id) ?? unlockId);
            return _ui.Strings.Has(key) ? _ui.Strings.Get(key) : unlockId;
        }

        private string Format(string format, string id) => Lookup(string.Format(CultureInfo.InvariantCulture, format, id), id);

        private string Lookup(string key, string fallback) => _ui.Strings.Has(key) ? _ui.Strings.Get(key) : fallback;
    }
}
