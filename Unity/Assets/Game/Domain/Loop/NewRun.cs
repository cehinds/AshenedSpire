using System;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Events;
using Ashen.Domain.Map;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using LM = Ashen.Generated.LoopMessages;
using RK = Ashen.Generated.RunKeys;
using WK = Ashen.Generated.RewardsKeys;

namespace Ashen.Domain.Loop
{
    /// <summary>
    /// What a new climb is made from (the shipped main.js newRun arguments), with the settings the caller resolves as
    /// the settings screen resolves them: advancedConfigSnapshot(settings), shouldPlayPrologue(settings, seen) and
    /// derivedStatDialOptions(settings) (in <see cref="Creation"/>). The seed's display string is the caller's
    /// (<c>SeedCodec.Format</c>, rules/rng.json).
    /// </summary>
    public sealed class NewRunOptions
    {
        public string ClassId;
        public uint Seed;
        public string SeedString;

        /// <summary>The Custom Climb rules ({ ascension, mods, deckMode, firstSeat?, mapShape? }); null is the Classic default (rules/loopEngine.json newRun.custom).</summary>
        public JObject Custom;

        public string KeepsakeId;

        /// <summary>{ name, glyph, tint }; null is the default (rules/loopEngine.json newRun.customization).</summary>
        public JObject Customization;

        /// <summary>createRunState's creation choices (kit, hands, armour, relic, attribute mode, derived-stat options); the profile is the context's.</summary>
        public RunOptions Creation;

        /// <summary>advancedConfigSnapshot(settings): { schemaVersion, ratingsVersion, overrides }.</summary>
        public JObject AdvancedConfigSnapshot;

        /// <summary>shouldPlayPrologue(settings, settings.prologueSeen === true).</summary>
        public bool Prologue;
    }

    public static partial class RunLoop
    {
        /// <summary>
        /// newRun + startClimb (shipped main.js, the run-writing half; D-102i): createRunState over the profile, the
        /// config snapshot, the seed string, customization and Custom Climb rules, the run's travel ledgers, a fresh RNG,
        /// the seat order drawn once on the <c>seats</c> stream (a pinned first seat rotates it), the keepsake's effects
        /// through the run-level door (<see cref="RunEffects.Execute"/>), the Custom Climb start (Cursed Start's card,
        /// Hoarder's cinders), then the first act's map and the prologue's pending state. Returns the loop's context.
        /// Sealed and Draft decks are refused by name (the Sealed deck is a shipped defect, D-099; the draft screen is
        /// not ported); World Journeys are deferred (D-091).
        /// </summary>
        public static LoopContext NewRun(LoopData d, JObject profile, LoopSettings settings, NewRunOptions o)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            if (o == null) throw new ArgumentNullException(nameof(o));
            if (o.SeedString == null) throw new ArgumentException(LM.NewRunNeedsSeedString, nameof(o));
            if (o.AdvancedConfigSnapshot == null) throw new ArgumentException(LM.NewRunNeedsConfigSnapshot, nameof(o));
            profile ??= new JObject();
            var rules = d.Engine.Obj(LK.NewRun) ?? throw new InvalidOperationException(LM.NewRunRulesMissing);
            var creation = o.Creation ?? new RunOptions();
            creation.ProfileMeta = profile;
            var run = RunState.Create(d.Run, o.Seed, o.ClassId, creation);
            run[LK.AdvancedConfigSnapshot] = o.AdvancedConfigSnapshot.DeepClone();
            run[RK.SeedString] = o.SeedString;
            run[LK.Customization] = (o.Customization ?? rules.Obj(LK.Customization)).DeepClone();
            run[RK.Custom] = (o.Custom ?? rules.Obj(RK.Custom)).DeepClone();
            run[WK.Stats] = rules.Obj(WK.Stats).DeepClone();
            run[LK.Path] = new JArray();
            run[LK.SeenEvents] = new JArray();
            run[LK.LastEncounters] = new JArray();
            var rng = new Rng(o.Seed);
            var ctx = new LoopContext(d, run, rng, profile, settings);
            var firstSeat = run.Obj(RK.Custom)[LK.FirstSeat];
            run[RK.SeatOrder] = new JArray(ActMap.DrawSeatOrder(d.Map, rng, Js.Truthy(firstSeat) ? Js.Str(firstSeat) : null).Select(s => (JToken)s));

            var keepsake = Js.Items(d.Run.CharacterCreation[LK.Keepsakes]).OfType<JObject>().FirstOrDefault(k => k.Str(K.Id) == o.KeepsakeId);
            if (keepsake != null && keepsake.Arr(K.Effects).Count > 0) RunEffects.Execute(d.Events, run, rng, keepsake.Arr(K.Effects));

            var deckMode = Js.Truthy(run.Obj(RK.Custom)[LK.DeckMode]) ? run.Obj(RK.Custom).Str(LK.DeckMode) : rules.Str(LK.DefaultDeckMode);
            if (d.RuleList(LK.NewRun, LK.DeferredDeckModes).Contains(deckMode)) throw new NotSupportedException(RunJs.Fmt(LM.DeckModeDeferred, deckMode));
            var cursed = rules.Obj(LK.CursedStart);
            if (ctx.ModOn(cursed.Str(LK.Mod)))
            {
                double n = 0;
                foreach (var cardId in Js.Items(cursed[K.Cards]).Select(Js.Str))
                {
                    n += 1;
                    run.Arr(K.Deck).Add(Js.Obj(K.InstanceId, cursed.Str(LK.IdPrefix) + RunJs.NumStr(n), K.CardId, cardId, K.Upgraded, false));
                }
            }
            if (ctx.ModOn(rules.Str(LK.HoarderMod))) run.Put(RK.Cinders, run.Num(RK.Cinders) + d.Balance.Obj(LK.CustomMods).Num(LK.HoarderCinders));

            // startClimb
            run[RK.MapGraph] = BuildMap(ctx);
            if (o.Prologue) run[LK.Prologue] = rules.Obj(LK.Prologue).DeepClone();
            return ctx;
        }
    }
}
