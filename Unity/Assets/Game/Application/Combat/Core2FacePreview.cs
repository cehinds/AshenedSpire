using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using T = Ashen.Generated.CardTextKeys;

namespace Ashen.App.Combat
{
    /// <summary>
    /// What the hand's card face reads of a card in play: the web's previewCard result (engine/combat.js previewCard) for the figures the face prints. The card is priced on a detached
    /// copy of the fight as the player sees it (the enemies it has not read show nothing), already in the stance it would enter, with the charges other cards left for it; each effect gets
    /// a values row (its figure, and for damage its hits, the damage of each hit on the first enemy, the face's whole number and the split of it), the tokens the card's template fills,
    /// a Counter's return against the revealed attack, and the Precision the stance entry would grant.
    /// </summary>
    public sealed class Core2FacePreview
    {
        private sealed class Reply
        {
            public double Damage;
            public List<double> HitDamages = new List<double>();
            public double Total;
            public JObject Return;
        }

        private static TOut Try<TOut>(Func<TOut> read) where TOut : class
        {
            try { return read(); }
            catch (InvalidOperationException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        private static bool Plain(string op) =>
            op == T.Buildup || op == T.LoseHp || op == T.Draw || op == T.Discard || op == T.RemoveStatus || op == T.GainEnergy || op == T.RestoreMana || op == T.RestoreStamina
            || op == T.PoiseDamage || op == T.AddCinders || op == T.WardDamage;

        private readonly CombatState _live;
        private readonly CombatState _priced;
        private readonly JObject _player;
        private readonly JObject _instance;
        private readonly JObject _def;
        private readonly JObject _carrier;
        private readonly JObject _paid;
        private readonly JObject _charges;
        private readonly List<JObject> _living;
        private readonly bool _counterCard;
        private readonly bool _coreCounter;
        private readonly CombatAction _action;
        private readonly List<JObject> _charged;
        private JObject _projection;
        private bool _projected;
        private JObject _read;
        private bool _readDone;
        private readonly Dictionary<string, List<Reply>> _replies = new Dictionary<string, List<Reply>>(StringComparer.Ordinal);

        private Core2FacePreview(CombatState live, JObject instance, JObject def, CombatState priced, JObject carrier, JObject paid, JObject charges, List<JObject> charged, CombatAction action)
        {
            _live = live;
            _instance = instance;
            _def = def;
            _priced = priced;
            _player = priced.Player;
            _carrier = carrier;
            _paid = paid;
            _charges = charges;
            _charged = charged;
            _action = action;
            _living = priced.Enemies.Where(e => e.Is(T.Alive)).ToList();
            _counterCard = carrier.Obj(T.CombatProfile)?.Str(T.Maneuver) == T.Counter;
            _coreCounter = _counterCard && CoreFixCore.Enabled(priced);
        }

        /// <summary>previewCard(combat, instanceId) with no target: the preview the hand prints a card from.</summary>
        public static JObject Build(CombatState c, JObject instance)
        {
            var id = instance.Str(T.InstanceId);
            var printed = CoreCosts.Resolve(c, instance);
            var paid = CoreCosts.CardPlayCosts(c, id);
            var def = CoreVariants.Select(c, printed, paid, null);
            var priced = CorePreparing.Preview(CoreVisibility.VisibleCopy(c), instance, printed, null);
            var carrier = CoreCardPlay.Carrier(priced, instance, def);
            CoreStances.SetProfileStance(priced, priced.Player, carrier);
            var authored = Js.Items(def[T.Effects]).OfType<JObject>().ToList();
            var living = priced.Enemies.Where(e => e.Is(T.Alive)).ToList();
            var charges = CoreRiders.Matching(priced.Player, carrier, living.Count > 0);
            var charged = CoreRiders.ChargedEffects(priced, authored, charges, living.Count > 0);
            var needsEnemy = authored.Any(e => e.Str(T.Target) == T.Enemy) || !ReferenceEquals(charged, authored);
            var action = new CombatAction
            {
                Source = priced.Player, Owner = priced.Player, Target = needsEnemy ? living.FirstOrDefault() : null, Card = carrier,
                Meta = new ActionMeta { EnergySpent = paid.Num(T.Stamina), ManaSpent = paid.Num(T.Mana), StaminaSpent = paid.Num(T.Stamina) },
            };
            return new Core2FacePreview(c, instance, def, priced, carrier, paid, charges, charged, action).Run(authored, id);
        }

        private ActionMeta Meta() => new ActionMeta { EnergySpent = _paid.Num(T.Stamina), ManaSpent = _paid.Num(T.Mana), StaminaSpent = _paid.Num(T.Stamina) };

        private double? Price(JToken value, JObject on)
        {
            if (Js.IsNum(value)) return Math.Floor(Js.D(value));
            if (Js.Nullish(value)) return 0;
            var read = Try(() => (JToken)Js.N(CombatPreview.EvalPreview(_priced, _action, value, on)));
            return read?.Value<double>();
        }

        // ------------------------------------------------------------------ a Counter's return

        /// <summary>The player once the Counter's immediate defensive effects (its Block, the stance entry's grants) have resolved, on a detached copy through the queue the live play drains.</summary>
        private JObject Projection()
        {
            if (_projected) return _projection;
            _projected = true;
            var sim = _priced.Detach();
            foreach (var effect in _charged)
            {
                if (effect.Str(T.Op) == T.Damage || effect.Str(T.Target) != T.Self) continue;
                sim.Enqueue(new CombatAction { Effect = effect, Source = sim.Player, Owner = sim.Player, Target = sim.Player, Card = _carrier, Meta = new ActionMeta { EnergySpent = _paid.Num(T.Stamina) } });
            }
            CoreActions.Drain(sim);
            return _projection = sim.Player;
        }

        /// <summary>coreCounterReturnRead: the Counter's return against the first revealed attack (or { uncovered: true }), once.</summary>
        private JObject CounterRead()
        {
            if (_readDone) return _read;
            _readDone = true;
            var enemy = _action.Target != null && _action.Target.Str(T.Kind) == T.Enemy ? _action.Target : _living.FirstOrDefault();
            return _read = enemy == null ? null : CoreCounterReturn.PreviewAgainst(_priced, enemy, _carrier, Projection());
        }

        private JObject CounterReturn() { var read = CounterRead(); return read != null && read.Is(T.Uncovered) ? null : read; }

        private bool CounterUncovered() => CounterRead()?.Is(T.Uncovered) == true;

        private static JObject ReturnRow(JObject read) => new JObject
        {
            [T.Incoming] = read[T.Incoming]?.DeepClone(), [T.Hp] = read[T.Hp]?.DeepClone(), [T.Poise] = read[T.Poise]?.DeepClone(), [T.Block] = read[T.Block]?.DeepClone(), [T.Advantaged] = read[T.Advantaged]?.DeepClone(),
        };

        /// <summary>previewCounter(resolvedTarget): the reply of each effect of a Counter against one enemy (a damage effect carries the one computed return).</summary>
        private List<Reply> PreviewCounter(JObject target)
        {
            var key = target?.Str(T.Id) ?? string.Empty;
            if (_replies.TryGetValue(key, out var cached)) return cached;
            var meta = new ActionMeta { EnergySpent = _paid.Num(T.Stamina) };
            var eligible = _charged.Select(effect => effect.Str(T.Op) == T.Damage ? Try(() => (object)CoreCounterReturn.ReplyPreview(_priced, _player, target, _carrier, effect, meta)) as (double value, double hits, double total)? : null).ToList();
            var replies = _charged.Select(effect => effect.Str(T.Op) == T.Damage ? CoreCounterReturn.ReplyPreview(_priced, _player, target, _carrier, effect, meta, true) : null).ToList();
            var bonusIndex = eligible.FindIndex(reply => reply.HasValue && reply.Value.hits > 0);
            if (bonusIndex < 0) bonusIndex = _charged.FindIndex(effect => effect.Str(T.Op) == T.Damage);
            var bonus = Math.Max(0, _charges.Or0(T.Damage) + _charges.Or0(T.DamageEffect));
            JObject coreRead = null;
            var result = new List<Reply>();
            var returnCarrier = Js.Spread(_carrier);
            returnCarrier[T.CombatReaction] = true;
            returnCarrier[T.CounterReturnAttack] = true;
            if (CoreFixCore.Enabled(_priced)) returnCarrier[T.SkipRatingBonus] = true;
            for (var effectIndex = 0; effectIndex < _charged.Count; effectIndex++)
            {
                var reply = replies[effectIndex];
                if (coreRead == null && reply.HasValue && target != null && target.Str(T.Kind) == T.Enemy) coreRead = CoreCounterReturn.PreviewAgainst(_priced, target, _carrier, Projection());
                var read = effectIndex == bonusIndex ? coreRead : null;
                // Live replaces the card's whole HP payload with the one computed return, so every other damage row of a Counter with a known read deals nothing here.
                if (effectIndex != bonusIndex && reply.HasValue && coreRead != null) { result.Add(new Reply()); continue; }
                // A revealed attack the Counter does not answer deals no return; a hidden one keeps the printed amount.
                if (read != null && read.Is(T.Uncovered)) { result.Add(new Reply()); continue; }
                var amount = read != null ? read.Num(T.Hp) : (reply?.value ?? 0) + (effectIndex == bonusIndex ? bonus : 0);
                var count = read != null ? 1 : reply?.hits ?? 0;
                var copy = target == null ? null : (JObject)target.DeepClone();
                var receipts = copy == null ? new List<JObject>() : CoreDamage.PreviewHits(_priced, _player, copy, amount, returnCarrier, count);
                var row = new Reply();
                if (read != null)
                {
                    row.Damage = read.Num(T.Hp);
                    row.HitDamages = new List<double> { read.Num(T.Hp) };
                    row.Total = read.Num(T.Hp);
                    row.Return = ReturnRow(read);
                }
                else
                {
                    row.HitDamages = receipts.Select(r => r.Num(T.HpLoss)).ToList();
                    row.Damage = row.HitDamages.Count > 0 ? row.HitDamages[0] : 0;
                    row.Total = row.HitDamages.Sum();
                }
                result.Add(row);
            }
            _replies[key] = result;
            return result;
        }

        // ------------------------------------------------------------------ the preview

        private JObject Run(List<JObject> authored, string id)
        {
            var bindings = CombatPreview.TokenBindings(_live.Data, _def[T.Effects]);
            var prepared = Prepared(authored);
            var values = new JArray();
            var tokens = new JObject();
            var sequences = new JArray();
            var applied = new HashSet<string>();
            var poiseRowUsed = false;
            foreach (var effect in prepared)
            {
                var op = effect.Str(T.Op);
                if (!Js.IsStr(effect[T.Op])) continue;
                var chargedAt = _charged.FindIndex(x => ReferenceEquals(x, effect));
                var authoredIndex = authored.IndexOf(chargedAt >= 0 ? _charged[chargedAt] : effect);
                var entry = new JObject { [T.Op] = op, [T.Target] = Js.Truthy(effect[T.Target]) ? effect[T.Target].DeepClone() : Js.Null() };
                _action.Effect = effect;
                _action.Meta = Meta();
                CoreRiders.Attach(_priced, _action, _charges, applied);
                var charge = _action.Meta.Charge;
                var primary = Try(() => CombatPreview.FirstResolvedTarget(_priced, _action, effect));
                var field = op == T.ApplyStatus ? T.Stacks : op == T.LoseMaxHpPct ? T.Pct : T.Amount;
                switch (op)
                {
                    case var _ when op == T.Damage:
                        Damage(effect, chargedAt, primary, charge, entry);
                        break;
                    case var _ when op == T.Block || op == T.GainBarrier:
                    {
                        var amount = Price(effect[T.Amount], primary);
                        entry[T.DefenseSplit] = Js.Null();
                        if (amount.HasValue)
                        {
                            var @base = amount.Value + (charge?.Or0(T.Block) ?? 0);
                            var gain = Try(() => (JToken)Js.N(CoreActions.ComputeBlockGain(_priced, _player, @base, _carrier)));
                            if (gain != null) entry[T.Value] = gain;
                        }
                        break;
                    }
                    case var _ when op == T.GainPoise || op == T.GainWard:
                    {
                        var amount = Price(effect[T.Amount], primary);
                        var gain = amount == null ? null : Try(() => (JToken)Js.N(CoreActions.ComputeMeterGuardGain(_priced, _player, amount.Value, _carrier)));
                        if (gain != null) entry[T.Value] = gain;
                        break;
                    }
                    case var _ when op == T.ApplyStatus:
                    {
                        entry[T.Status] = effect[T.Status]?.DeepClone();
                        var stacks = Price(Js.Nullish(effect[T.Stacks]) ? Js.N(1) : effect[T.Stacks], primary);
                        if (stacks.HasValue) entry[T.Value] = Js.N(stacks.Value + (charge?.Or0(T.Buildup) ?? 0));
                        break;
                    }
                    case var _ when op == T.Heal:
                    {
                        var amount = Price(effect[T.Amount], primary);
                        var healed = amount == null ? null : Try(() => (JToken)Js.N(Math.Floor((amount.Value + Ratings.CardBonus(_priced, _player, _carrier, T.Heal, amount.Value) + (charge?.Or0(T.Heal) ?? 0)) * CoreActions.StanceEffectiveness(_priced, _player, _carrier))));
                        if (healed != null) entry[T.Value] = healed;
                        break;
                    }
                    case var _ when op == T.GrantCardCharge:
                        foreach (var rider in new[] { T.Damage, T.ManaDiscount, T.Block, T.Heal, T.Break, T.Buildup })
                            if (authoredIndex >= 0 && bindings.TryGetValue(authoredIndex.ToString(CultureInfo.InvariantCulture) + Ashen.Generated.CombatValues.KeySeparator + rider, out var riderToken))
                                tokens[riderToken] = Js.N(Math.Max(0, Price(effect[rider], primary) ?? 0));
                        entry[T.Value] = Js.Null();
                        break;
                    case var _ when Plain(op):
                    {
                        var amount = Price(op == T.LoseMaxHpPct ? (Js.Nullish(effect[T.Pct]) ? Js.N(0) : effect[T.Pct]) : Js.Nullish(effect[T.Amount]) ? Js.N(1) : effect[T.Amount], primary);
                        if (amount.HasValue) entry[T.Value] = Js.N(amount.Value);
                        // The face shows the Poise the return really deals (SPEC 17), once; the authored rows and their conditions do not stack.
                        if (op == T.PoiseDamage && _coreCounter)
                        {
                            var read = CounterReturn();
                            if (read != null) { entry[T.Value] = Js.N(poiseRowUsed ? 0 : read.Num(T.Poise)); poiseRowUsed = true; }
                            else if (CounterUncovered()) entry[T.Value] = Js.N(0);
                        }
                        break;
                    }
                    default:
                        entry[T.Value] = Js.Null();
                        break;
                }
                if (op == T.LoseMaxHpPct) field = T.Pct;
                if (!Js.Nullish(entry[T.Value]) && authoredIndex >= 0)
                {
                    var key = authoredIndex.ToString(CultureInfo.InvariantCulture) + Ashen.Generated.CombatValues.KeySeparator + field;
                    if (bindings.TryGetValue(key, out var token))
                    {
                        entry[T.Token] = token;
                        tokens[token] = !Js.Nullish(entry[T.FaceValue]) ? entry[T.FaceValue].DeepClone() : entry[T.Value].DeepClone();
                        if (op == T.Damage && entry[T.HitDamages] is JArray hitDamages && hitDamages.Count > 1 && hitDamages.Any(x => Js.D(x) != Js.D(hitDamages[0]))
                            && bindings.TryGetValue(authoredIndex.ToString(CultureInfo.InvariantCulture) + Ashen.Generated.CombatValues.KeySeparator + T.Hits, out var sequenceHits))
                            sequences.Add(new JObject { [T.AmountToken] = token, [T.HitsToken] = sequenceHits, [T.HitDamages] = hitDamages.DeepClone(), [T.TotalDamage] = entry[T.TotalDamage].DeepClone() });
                    }
                    if (bindings.TryGetValue(authoredIndex.ToString(CultureInfo.InvariantCulture) + Ashen.Generated.CombatValues.KeySeparator + T.Hits, out var hitsToken) && !Js.Nullish(entry[T.Hits]))
                        tokens[hitsToken] = entry[T.Hits].DeepClone();
                }
                values.Add(entry);
            }
            var preview = new JObject
            {
                [T.ResolvedDefinition] = _def,
                [T.ChargedEffects] = new JArray(prepared),
                [T.CombatExpansionVersion] = Js.N(_live.CoreData.ExpandedVersion),
                [T.Values] = values,
                [T.Tokens] = tokens,
                [T.DamageSequences] = sequences,
            };
            // The one computed return (SPEC 17) for any Core Counter, with or without an authored damage opcode: the face reads it for the HP, Poise and Block rows its effects do not carry.
            if (_coreCounter)
            {
                var read = CounterReturn();
                if (read != null) preview[T.CounterReturn] = ReturnRow(read);
                else if (CounterUncovered()) preview[T.CounterReturn] = new JObject { [T.Uncovered] = true };
            }
            var costRead = Try(() => CoreCosts.PreviewCosts(_live, id));
            if (costRead != null && Js.IsNum(costRead[T.EntryPrecision])) preview[T.EntryPrecision] = costRead[T.EntryPrecision].DeepClone();
            return preview;
        }

        /// <summary>previewTacticalEffects: the effects the play will run, a Counter keeping its reply rows where they stand.</summary>
        private List<JObject> Prepared(List<JObject> authored)
        {
            var target = _action.Target;
            if (!_counterCard)
                return CoreCardTraits.CounterstrikeEffects(_priced, _player, target, _carrier, CoreReactions.SweepReactionEffects(_priced, target, _carrier, _charged), false);
            var payloadOps = new[] { T.Damage, T.PoiseDamage, T.WardDamage };
            var support = _charged.Where(e => !payloadOps.Contains(e.Str(T.Op))).ToList();
            var strike = CoreCardTraits.CounterstrikeEffects(_priced, _player, target, _carrier, support, false);
            var index = 0;
            return _charged.Select(e => payloadOps.Contains(e.Str(T.Op)) ? e : strike[index++]).ToList();
        }

        private void Damage(JObject effect, int chargedAt, JObject primary, JObject charge, JObject entry)
        {
            var chargeAmount = (charge?.Or0(T.Damage) ?? 0) + (charge?.Or0(T.DamageEffect) ?? 0);
            if (_counterCard)
            {
                // The reply the card arms: one computed return in one row, the rest zeroed (previewCounter).
                var at = Math.Max(0, chargedAt);
                entry[T.Hits] = Js.N(PreviewCounter(primary)[at].HitDamages.Count);
                foreach (var enemy in _living)
                {
                    var result = PreviewCounter(enemy)[at];
                    if (primary == null || primary.Str(T.Id) != enemy.Str(T.Id)) continue;
                    if (result.Return != null) entry[T.CounterReturn] = result.Return.DeepClone();
                    entry[T.Value] = Js.N(result.Damage);
                    entry[T.HitDamages] = new JArray(result.HitDamages.Select(Js.N));
                    entry[T.TotalDamage] = Js.N(result.Total);
                }
                if (Js.Nullish(entry[T.Value]))
                {
                    var result = PreviewCounter(primary)[at];
                    entry[T.Value] = Js.N(result.Damage);
                    entry[T.HitDamages] = new JArray(result.HitDamages.Select(Js.N));
                    entry[T.TotalDamage] = Js.N(result.Total);
                }
                return;
            }
            var hitsRead = Price(Js.Nullish(effect[T.Hits]) ? Js.N(1) : effect[T.Hits], primary);
            if (hitsRead.HasValue) entry[T.Hits] = Js.N(hitsRead.Value);
            foreach (var enemy in _living)
            {
                var amount = Price(effect[T.Amount], enemy);
                if (amount == null || !hitsRead.HasValue) continue;
                var receipts = Try(() => CoreDamage.PreviewHits(_priced, _player, enemy, amount.Value + chargeAmount, _carrier, hitsRead.Value));
                if (receipts == null || primary == null || primary.Str(T.Id) != enemy.Str(T.Id)) continue;
                var damages = receipts.Select(r => r.Num(T.HpLoss)).ToList();
                entry[T.Value] = Js.N(damages.Count > 0 ? damages[0] : 0);
                entry[T.HitDamages] = new JArray(damages.Select(Js.N));
                entry[T.TotalDamage] = Js.N(damages.Sum());
            }
            // An un-aimed face states the card's offensive amount (combat.js:2698).
            var printedAmount = Price(effect[T.Amount], null);
            if (printedAmount == null) return;
            var printedBase = printedAmount.Value + chargeAmount;
            var raw = Try(() => (JToken)Js.N(CoreDamage.ProfileDamageReceipt(_priced, _player, null, printedBase, _carrier).Num(T.Raw)));
            if (raw != null) entry[T.FaceValue] = Js.N(Math.Floor(raw.Value<double>()));
            entry[T.SplitBase] = Js.N(Math.Max(0, Math.Floor(printedAmount.Value)));
            entry[T.RatingBonus] = Js.N(_carrier.Is(T.SkipRatingBonus) ? 0 : Math.Max(0, Math.Floor(Ratings.CardBonus(_priced, _player, _carrier, T.Damage, printedBase))));
            entry[T.RatingPart] = CoreExpansion.RatingPart(_priced, _carrier);
            if (Js.Truthy(_carrier[T.PhysicalDamageType])) entry[T.PhysicalDamageType] = _carrier[T.PhysicalDamageType].DeepClone();
            if (Js.Nullish(entry[T.Value]))
            {
                // combat.js:2690: no previewed target gave a figure; the card's own is the value, once for each hit.
                var own = Math.Floor(entry[T.FaceValue] != null ? Js.D(entry[T.FaceValue]) : 0);
                entry[T.Value] = Js.N(own);
                var count = Math.Max(0, Math.Floor(Js.Or0(entry[T.Hits])));
                entry[T.HitDamages] = new JArray(Enumerable.Range(0, (int)count).Select(_ => (JToken)Js.N(own)));
                entry[T.TotalDamage] = Js.N(own * count);
            }
        }
    }
}
