using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using K = Ashen.Generated.CombatKeys;
using MK = Ashen.Generated.MapKeys;
using RK = Ashen.Generated.RunKeys;
using SN = Ashen.Generated.StringKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.App.Run
{
    /// <summary>One chip of the stat strip: its key in small caps and its number ("DAMAGE DEALT 214").</summary>
    public sealed class Core2EndChip
    {
        public string Key;
        public string Value;
    }

    /// <summary>A card of the final deck as the web's strip names it (an upgraded one is marked).</summary>
    public sealed class Core2EndCard
    {
        public string Name;
        public bool Upgraded;
    }

    /// <summary>A line of the Earned card: what the run opened ("Twinblade armament", a class).</summary>
    public sealed class Core2EndEarned
    {
        public string Name;
        public string Kind;
    }

    /// <summary>What Run again did: the new climb it began, or the refusal in words.</summary>
    public sealed class Core2RunAgainResult
    {
        public Core2RunSession Session;
        public string Refusal;

        public bool Done => Session != null;
    }

    /// <summary>
    /// The run end screen's logic (VP-12, D-964; web ui/screens/gameover.js mountGameOver over main.js showFinishedRun): a door whose eyebrow says "The climb ends" or "The climb" and whose title says
    /// Defeat or Victory, the heading ("You perished", "Ember restored"), the identity card (who, the floor of the act's floors and the fights won, the seed, and for a death the blow that struck it), the
    /// strip of four numbers (damage dealt and taken from the session's tally, cinders, final HP), the Earned card (the classes the run's close-out opened in the profile's ledger,
    /// <see cref="Core2RunSession.ClassesUnlocked"/>), the final deck and the three ways on: Run again (a new climb with the same class and name and a new seed), Run history and Return to title.
    /// Engine-free; the screen draws these fields and sends presses here.
    /// </summary>
    public sealed class Core2RunEndFlow
    {
        private readonly Core2RunSession _session;
        private readonly UiData _ui;
        private readonly StringTable _strings;

        public Core2RunEndFlow(Core2RunSession session, UiData ui)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _ui = ui;
            _strings = ui.Strings;
            Victory = session.End?.Victory ?? false;
            Abandoned = session.Abandoned;
            var run = session.Run;
            var tally = session.Tally;
            Eyebrow = _strings.Get(Victory ? SN.Core2EndEyebrowVictory : SN.Core2EndEyebrowDefeat);
            Title = _strings.Get(Abandoned ? MetaStringKeys.RunEndAbandoned : Victory ? SN.Core2EndTitleVictory : SN.Core2EndTitleDefeat);
            Heading = _strings.Get(Abandoned ? MetaStringKeys.RunEndAbandoned : Victory ? SN.Core2EndHeadingVictory : SN.Core2EndHeadingDefeat);
            IdentityEyebrow = _strings.Get(SN.Core2EndIdentity);
            Name = session.Name;
            var fights = (int)Js.Or0(run.Obj(RewardsKeys.Stats)?[RewardsKeys.FightsWon]);
            var fightsText = _strings.Format(fights == 1 ? SN.Core2EndFightOne : SN.Core2EndFightMany, new StringArgs().Add(UiPlaceholders.Count, fights));
            var floors = run.Obj(RK.MapGraph)?[MK.Floors];
            Line = floors != null && floors.Type != Newtonsoft.Json.Linq.JTokenType.Null
                ? _strings.Format(SN.Core2EndLine, new StringArgs().Add(UiPlaceholders.Floor, session.Floor).Add(EndPlaceholders.Floors, (int)floors).Add(EndPlaceholders.Fights, fightsText))
                : _strings.Format(SN.Core2EndLineNoFloors, new StringArgs().Add(UiPlaceholders.Floor, session.Floor).Add(EndPlaceholders.Fights, fightsText));
            Meta = _strings.Format(SN.Core2EndSeed, new StringArgs().Add(UiPlaceholders.Seed, session.SeedText));
            Cause = Victory || Abandoned ? null : SlainBy(tally.LastHit);
            var hp = Victory ? run.Num(K.Hp) : 0d;
            Chips = new[]
            {
                Chip(SN.Core2EndDamageDealt, CombatText.Number(tally.DamageDealt)),
                Chip(SN.Core2EndDamageTaken, CombatText.Number(tally.DamageTaken)),
                Chip(SN.Core2EndCinders, CombatText.Number(Math.Floor(run.Num(RK.Cinders)))),
                Chip(SN.Core2EndFinalHp, _strings.Format(SN.Core2EndFinalHpValue, new StringArgs().Add(UiPlaceholders.Hp, CombatText.Number(Math.Floor(hp))).Add(UiPlaceholders.Max, CombatText.Number(Math.Floor(run.Num(K.MaxHp)))))),
            };
            EarnedHeading = _strings.Get(SN.Core2EndEarned);
            var earned = new List<Core2EndEarned>();
            foreach (var id in session.ClassesUnlocked)
                earned.Add(new Core2EndEarned { Name = RunHudView.ClassName(ui, id), Kind = ActMapView.Name(_strings, UiFormats.UnlockKindKey, V.ClassKind) });
            Earned = earned;
            var data = session.CombatData;
            var deck = new List<Core2EndCard>();
            foreach (var card in Js.Items(run[K.Deck]).OfType<Newtonsoft.Json.Linq.JObject>())
            {
                var id = card.Str(K.CardId);
                deck.Add(new Core2EndCard { Name = id != null && data.Cards.Has(id) ? data.Cards.Get(id).Str(K.Name) ?? id : id, Upgraded = card.Is(K.Upgraded) });
            }
            Deck = deck;
            DeckEyebrow = _strings.Get(SN.Core2EndDeck);
            DeckTitle = _strings.Format(deck.Count == 1 ? SN.Core2EndDeckOne : SN.Core2EndDeckMany, new StringArgs().Add(UiPlaceholders.Count, deck.Count));
            AgainLabel = _strings.Get(SN.Core2EndAgain);
            HistoryLabel = _strings.Get(StringKeys.RunEndHistory);
            TitleLabel = _strings.Get(StringKeys.RunEndToTitle);
        }

        public bool Victory { get; }

        public bool Abandoned { get; }

        public string Eyebrow { get; }

        public string Title { get; }

        public string Heading { get; }

        public string IdentityEyebrow { get; }

        public string Name { get; }

        /// <summary>"Floor 4 / 8 · 3 fights won".</summary>
        public string Line { get; }

        /// <summary>"Seed ASHEN1".</summary>
        public string Meta { get; }

        /// <summary>"Slain by Ash Warden · Cleave for 14"; null when the run was won, abandoned, or no blow is known.</summary>
        public string Cause { get; }

        public IReadOnlyList<Core2EndChip> Chips { get; }

        public string EarnedHeading { get; }

        /// <summary>What the run opened (empty: the card is not drawn, as the web's).</summary>
        public IReadOnlyList<Core2EndEarned> Earned { get; }

        public string DeckEyebrow { get; }

        public string DeckTitle { get; }

        public IReadOnlyList<Core2EndCard> Deck { get; }

        public string AgainLabel { get; }

        public string HistoryLabel { get; }

        public string TitleLabel { get; }

        private Core2EndChip Chip(string key, string value) => new Core2EndChip { Key = _strings.Get(key), Value = value };

        /// <summary>slainByLine(lastHit): the move part drops out when the blow had none, the amount when it is not a number; no striker named is "Unknown".</summary>
        private string SlainBy(Core2LastHit hit)
        {
            if (hit == null) return null;
            var args = new StringArgs().Add(UiPlaceholders.Name, hit.SourceName ?? _strings.Get(SN.Core2EndSlainUnknown));
            var move = !string.IsNullOrEmpty(hit.Move);
            var amount = hit.Amount.HasValue;
            if (move) args.Add(EndPlaceholders.Move, hit.Move);
            if (amount) args.Add(UiPlaceholders.Amount, CombatText.Number(hit.Amount.Value));
            return _strings.Format(move ? (amount ? SN.Core2EndSlainMoveAmount : SN.Core2EndSlainMove) : (amount ? SN.Core2EndSlainAmount : SN.Core2EndSlain), args);
        }

        /// <summary>
        /// Run again: a new climb with the same class and name, a new seed, in the slot the finished run held (main.js showFinishedRun: startRunInSlot with the run's beginChoices and a
        /// random seed). The class is the one just played, so it is open in the ledger; a refusal (the class is gone from the profile) is said in words and nothing starts.
        /// </summary>
        public Core2RunAgainResult RunAgain(RunContent content, SaveService saves, uint seed)
        {
            try
            {
                var run = _session.Run;
                var customization = run[LoopKeys.Customization] as Newtonsoft.Json.Linq.JObject;
                var choices = new CreationChoices { Tint = (string)customization?[RunFlowKeys.Tint], Glyph = (string)customization?[RunFlowKeys.Glyph] };
                var fresh = Core2RunSession.New(content, saves, _session.SlotIndex, seed, _session.ClassId, _session.Name, null, choices);
                return new Core2RunAgainResult { Session = fresh };
            }
            catch (ArgumentException)
            {
                return new Core2RunAgainResult { Refusal = _strings.Get(SN.Core2EndAgainRefused) };
            }
        }
    }
}
