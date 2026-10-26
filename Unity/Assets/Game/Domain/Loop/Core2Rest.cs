using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Ashen.Domain.Shop;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using MI = Ashen.Generated.Core2ModuleIds;
using MK = Ashen.Generated.MapKeys;
using PK = Ashen.Generated.Core2ProfileKeys;
using RK = Ashen.Generated.RunKeys;
using RM = Ashen.Generated.Core2RestMessages;
using RR = Ashen.Generated.Core2RestKeys;
using RS = Ashen.Generated.Core2RestValues;
using RV = Ashen.Generated.RunValues;
using SK = Ashen.Generated.Core2RunSnapshotKeys;
using VK = Ashen.Generated.Core2VersionKeys;

namespace Ashen.Domain.Loop
{
    /// <summary>One service the rest site offers, as the screen's model reads it (web ui/models/GameProfileRestModel.js).</summary>
    public sealed class Core2RestService
    {
        public string Id;
        public bool Available;

        /// <summary>The web's sentence for why it cannot be taken (an error message), or null.</summary>
        public string Reason;

        /// <summary>Or the key of a ui sentence naming why (<see cref="RS"/> ReasonSpent, ReasonNoSmith, ReasonNoLevel), or null.</summary>
        public string ReasonKey;

        /// <summary>What the pools would be once taken (the recovery ledger's after column); null when it cannot be taken.</summary>
        public JObject Recovery;
    }

    /// <summary>One item the Smith can work on and whether taking it now would succeed.</summary>
    public sealed class Core2SmithChoice
    {
        public string ItemRef;
        public bool Available;
        public string Reason;
        public string ReasonKey;
        public JObject Recovery;
    }

    /// <summary>One attribute a point could go to.</summary>
    public sealed class Core2AttributeChoice
    {
        public string Id;
        public string Label;
        public string ShortLabel;
        public double Value;
        public bool Available;
        public string Reason;
        public string ReasonKey;
        public JObject Recovery;
    }

    /// <summary>The whole rest site as the screen reads it: the services (Smith, Level Up), the Smith's candidates and what each would do, the attributes.</summary>
    public sealed class Core2RestModel
    {
        public List<Core2RestService> Services = new List<Core2RestService>();

        /// <summary>The Smith's plan, priced as the visit prices it (free where the profile says so).</summary>
        public JObject Smith;

        public List<Core2SmithChoice> SmithChoices = new List<Core2SmithChoice>();
        public List<Core2AttributeChoice> Attributes = new List<Core2AttributeChoice>();
        public bool SmithFree;
    }

    /// <summary>
    /// The rest site of a Core 2 run (web model/gameProfileRest.js, restRevisit.js and main.js showRest for a configured profile, WP-16): arriving at a rest node heals the player once
    /// (HP, Mana, Stamina and the flask charges, the visit remembers what it gave), then each service the profile lists — the Smith, which upgrades one owned item one tier for
    /// nothing, and Level Up, which spends an earned attribute point — can be taken once, in any order, and every one leaves the pools full; only Continue leaves. A refusal changes
    /// nothing. A left site stays enterable until the player travels elsewhere (the re-entry record). Engine-free.
    /// </summary>
    public static class Core2Rest
    {
        // ------------------------------------------------------------------ the profile's rules

        private static JObject Rests(Core2RewardsData d, JObject run) => d.Content(run, RR.Rests) as JObject;

        /// <summary>restServicesAll(run): the visit heals on arrival and every listed service is usable once (servicesPerVisit "all").</summary>
        public static bool ServicesAll(Core2RewardsData d, JObject run) => Rests(d, run)?.Str(RR.ServicesPerVisit) == RS.ServicesAll;

        /// <summary>restSmithFree(run): the Smith costs nothing.</summary>
        public static bool SmithFree(Core2RewardsData d, JObject run)
        {
            var free = Rests(d, run)?.Obj(RR.Smith)?[RR.Free];
            return free != null && free.Type == JTokenType.Boolean && free.Value<bool>();
        }

        /// <summary>coreSkillOnlyProgression(subject): a run of the Core author's profile levels by skill alone.</summary>
        private static bool SkillOnly(Core2RewardsData d, JObject run)
        {
            var subject = d.Deck.Run.SubjectOf(run);
            return subject?.Obj(PK.Foundation)?.Str(RR.Mode) == RS.ExpectedFoundation && subject.Obj(PK.Author)?.Str(RR.Core) == d.RestRules.Str(RR.CoreAuthor);
        }

        /// <summary>configuredCharacterLevels(subject): characters level and earn attribute points (never under the Core author's profile).</summary>
        public static bool CharacterLevels(Core2RewardsData d, JObject run) =>
            !SkillOnly(d, run) && d.Module(run, MI.Progression)?[RR.CharacterLevels] is JToken levels && levels.Type == JTokenType.Boolean && levels.Value<bool>();

        private static bool PointsWaiting(JObject run) => run.Obj(RR.Level)?[RR.UnspentPoints] is JToken points && Js.IsNum(points) && Js.D(points) > 0;

        // ------------------------------------------------------------------ the visit

        /// <summary>restContext(run): the visit id of the node the run stands at, <c>&lt;act&gt;:&lt;node&gt;</c>; throws unless that node is a rest site.</summary>
        public static string Context(Core2RewardsData d, JObject run)
        {
            var at = run[MK.MapNodeId];
            var node = Js.Truthy(at) ? run.Obj(RK.MapGraph)?.Obj(MK.Nodes)?.Obj(Js.Str(at) ?? RunJs.Key(at)) : null;
            if (node == null || (!Js.Items(d.RestRules[RR.RestNodeTypes]).Any(t => Js.Str(t) == node.Str(K.Type)) && node.Str(RR.ActualType) != d.RestRules.Str(RR.RestActualType)))
                throw new InvalidOperationException(RM.NeedRestNode);
            return RunJs.Fmt(RS.VisitIdFormat, RunJs.Key(run[RK.ActNumber]), RunJs.Key(at));
        }

        private static JObject VisitOf(JObject run, string id = null)
        {
            id = id ?? Js.Str(run[RR.GameProfileRestVisitId]);
            return id == null ? null : run.Obj(RR.GameProfileRestVisits)?.Obj(id);
        }

        /// <summary>The visit the run stands in (<c>gameProfileRestVisitId</c>), or null.</summary>
        public static JObject Visit(JObject run) => VisitOf(run);

        /// <summary>restServiceUsed(visit, service): saves from before <c>used</c> existed carry only <c>service</c>; that one counts as used.</summary>
        public static bool ServiceUsed(JObject visit, string service) => visit.Obj(RR.Used) is JObject used ? Js.Truthy(used[service]) : visit.Str(RR.Service) == service;

        /// <summary>restServiceSpent(run, visit, service).</summary>
        public static bool ServiceSpent(Core2RewardsData d, JObject run, JObject visit, string service) => ServicesAll(d, run) ? ServiceUsed(visit, service) : Js.Truthy(visit[RR.Service]);

        private static void HealFull(JObject run)
        {
            run[K.Hp] = run[K.MaxHp]?.DeepClone();
            run[K.Mana] = run[K.MaxMana]?.DeepClone();
            run[K.Stamina] = run[K.MaxStamina]?.DeepClone();
            if (run.Obj(K.FlaskCharges) is JObject charges)
            {
                charges[RR.HpCurrent] = charges[RR.Hp]?.DeepClone();
                charges[RR.ManaCurrent] = charges[RR.Mana]?.DeepClone();
            }
        }

        /// <summary>
        /// beginGameProfileRest(run, visitId): the visit for the rest node the run stands at, made on the first arrival (a reload or a remount finds it); under servicesPerVisit "all" the
        /// arrival heals once and the visit remembers what that gave. Returns the visit on the run.
        /// </summary>
        public static JObject Begin(Core2RewardsData d, JObject run, string visitId = null)
        {
            if (d.Module(run, MI.Rest)?.Str(RR.Mode) != Ashen.Generated.Core2RunValues.Configured) throw new InvalidOperationException(RM.RestUnavailable);
            var context = Context(d, run);
            visitId = visitId ?? context;
            if (visitId != context) throw new InvalidOperationException(RM.VisitIdMismatch);
            if (!(run[RR.GameProfileRestVisits] is JObject visits)) run[RR.GameProfileRestVisits] = visits = new JObject();
            if (!Js.Truthy(visits[visitId])) visits[visitId] = new JObject { [RR.Id] = visitId, [RR.Service] = Js.Null() };
            run[RR.GameProfileRestVisitId] = visitId;
            var visit = (JObject)visits[visitId];
            if (ServicesAll(d, run) && !Js.Truthy(visit[RR.Healed]))
            {
                var charges = run.Obj(K.FlaskCharges);
                var hp = run.Num(K.Hp);
                var mana = run.Num(K.Mana);
                var stamina = run.Num(K.Stamina);
                var flaskHp = Js.Nullish(charges?[RR.HpCurrent]) ? 0 : charges.Num(RR.HpCurrent);
                var flaskMana = Js.Nullish(charges?[RR.ManaCurrent]) ? 0 : charges.Num(RR.ManaCurrent);
                HealFull(run);
                var after = run.Obj(K.FlaskCharges);
                visit[RR.Healed] = new JObject
                {
                    [RR.Hp] = Js.N(run.Num(K.Hp) - hp), [RR.Mana] = Js.N(run.Num(K.Mana) - mana), [RR.Stamina] = Js.N(run.Num(K.Stamina) - stamina),
                    [RR.FlaskHp] = Js.N((Js.Nullish(after?[RR.HpCurrent]) ? 0 : after.Num(RR.HpCurrent)) - flaskHp),
                    [RR.FlaskMana] = Js.N((Js.Nullish(after?[RR.ManaCurrent]) ? 0 : after.Num(RR.ManaCurrent)) - flaskMana),
                };
                RunState.SyncZones(d.RunData, run);
            }
            return visit;
        }

        // ------------------------------------------------------------------ the re-entry record (restRevisit.js)

        private static bool MatchesPlace(JObject run, JObject record) =>
            record != null && Js.IsNum(record[RR.Act]) && Js.D(record[RR.Act]) == run.Num(RK.ActNumber) && JToken.DeepEquals(record[RR.NodeId] ?? Js.Null(), run[MK.MapNodeId] ?? Js.Null());

        /// <summary>openRestRecord(run, { multiUse }): the record for the place the run stands at, made when none matches it.</summary>
        public static JObject OpenRecord(JObject run, bool multiUse = false)
        {
            if (MatchesPlace(run, run.Obj(RR.RestRevisit))) return run.Obj(RR.RestRevisit);
            run[RR.RestRevisit] = new JObject
            {
                [RR.Act] = run[RK.ActNumber]?.DeepClone(), [RR.NodeId] = run[MK.MapNodeId]?.DeepClone() ?? Js.Null(), [RR.Rested] = false, [RR.Spent] = false, [RR.Left] = false, [RR.Refilled] = false, [RR.MultiUse] = multiUse,
            };
            return run.Obj(RR.RestRevisit);
        }

        /// <summary>recordRestConsumed(run, kind): a consumed choice at this place; <c>rested</c> only for a rest, <c>spent</c> for every kind the record follows.</summary>
        public static void RecordConsumed(Core2RewardsData d, JObject run, string kind)
        {
            var record = run.Obj(RR.RestRevisit);
            if (!MatchesPlace(run, record)) return;
            if (Js.Items(d.RestRules[RR.RestedKinds]).Any(k => Js.Str(k) == kind)) record[RR.Rested] = true;
            if (Js.Items(d.RestRules[RR.ConsumedKinds]).Any(k => Js.Str(k) == kind)) record[RR.Spent] = true;
        }

        /// <summary>markRestLeft(run): the player has walked out of the visit.</summary>
        public static void MarkLeft(JObject run)
        {
            if (MatchesPlace(run, run.Obj(RR.RestRevisit))) run.Obj(RR.RestRevisit)[RR.Left] = true;
        }

        /// <summary>restRevisitNodeId(run): the node the map keeps enterable for re-entry, or null.</summary>
        public static string RevisitNodeId(JObject run) => MatchesPlace(run, run.Obj(RR.RestRevisit)) ? Js.Str(run[MK.MapNodeId]) : null;

        /// <summary>clearRestRevisitOnTravel(run, nodeId): travelling to a node other than the rest site ends the re-entry.</summary>
        public static void ClearRevisitOnTravel(JObject run, string nodeId)
        {
            var record = run.Obj(RR.RestRevisit);
            if (record != null && nodeId != Js.Str(record[RR.NodeId])) run.Remove(RR.RestRevisit);
        }

        // ------------------------------------------------------------------ the services

        /// <summary>
        /// The run as a service works on it: every field copied but the pinned profile (megabytes, never edited), which the draft shares by reference — the zones projection reads it to
        /// know it stands in a Core 2 run (its collection holds the sideboard too).
        /// </summary>
        private static JObject Draft(JObject run)
        {
            var draft = new JObject();
            foreach (var p in run.Properties())
                draft[p.Name] = p.Name == SK.GameProfile ? p.Value : p.Value.DeepClone();
            return draft;
        }

        /// <summary>The five figures a recovery ledger compares: the pools and the flask charges a run holds.</summary>
        public static JObject Recovery(JObject run)
        {
            var charges = run.Obj(K.FlaskCharges);
            return new JObject
            {
                [RR.Hp] = run[K.Hp]?.DeepClone(), [RR.Mana] = run[K.Mana]?.DeepClone(), [RR.Stamina] = run[K.Stamina]?.DeepClone(),
                [RR.HpCurrent] = charges?[RR.HpCurrent]?.DeepClone(), [RR.ManaCurrent] = charges?[RR.ManaCurrent]?.DeepClone(),
            };
        }

        /// <summary>
        /// rederivePools(registries, run): Stamina-fed Actions and Hand from the run's own snapshot at its attributes and level, then every pool through the pool reconciliation
        /// (deficits carried). Returns how many maxima moved.
        /// </summary>
        public static double RederivePools(Core2RewardsData d, JObject run)
        {
            var data = d.Deck.Run;
            if (run.Obj(K.DerivedStatRuleSnapshot)?.Obj(K.Rules) == null) return 0;
            var rules = run.Obj(K.DerivedStatRuleSnapshot).Obj(K.Rules);
            var classDef = data.Run.Classes.Get(run.Str(RK.Class));
            var level = RunState.CharacterLevelOf(run);
            var before = new Dictionary<string, JToken>();
            foreach (var key in new[] { K.MaxHp, K.MaxMana, K.MaxStamina }) before[key] = run[key]?.DeepClone();
            var priorEnergy = run[K.EnergyMax]?.DeepClone();
            var attributes = CoreGear.EffectiveAttributes(data, run, CoreRunRules.EffectiveBlightAttributes(data, run));
            var expanded = run.Num(SK.CombatExpansionVersion) == data.Versions.Num(VK.CombatExpansionVersion);
            double moved = 0;
            foreach (var (key, statId) in new[] { (K.EnergyMax, RV.StatEnergy), (K.DrawPerTurn, RV.StatDraw) })
            {
                if (run[key] == null) continue;
                if (expanded && key == K.EnergyMax) continue;
                var next = CoreDerivedStats.Derive(data, rules, statId, attributes, classDef, level).Value;
                if (!(Js.IsNum(run[key]) && Js.D(run[key]) == next)) moved += 1;
                run.Put(key, next);
            }
            CoreRunRules.ReconcilePools(data, run, false);
            if (expanded && !JToken.DeepEquals(priorEnergy ?? Js.Null(), run[K.EnergyMax] ?? Js.Null())) moved += 1;
            foreach (var pair in before)
                if (!JToken.DeepEquals(pair.Value ?? Js.Null(), run[pair.Key] ?? Js.Null())) moved += 1;
            return moved;
        }

        /// <summary>xpToNext(registries, level): the Core 2 profile curve (balance.level.xp: base, growth or linear, rounded), at least one unit.</summary>
        public static double XpToNext(Core2RewardsData d, double level)
        {
            var curve = d.RunData.Balance.Obj(K.Level)?.Obj(K.Xp) ?? new JObject();
            var defaults = d.RestRules.Obj(RR.LevelCurveDefaults);
            var b = Js.IsFinite(curve[K.Base]) && curve.Num(K.Base) > 0 ? curve.Num(K.Base) : defaults.Num(RR.Base);
            var g = Js.IsFinite(curve[K.Growth]) && curve.Num(K.Growth) > 0 ? curve.Num(K.Growth) : defaults.Num(RR.Growth);
            var step = Math.Floor(level) == level && level > 1 ? level - 1 : 0;
            var linear = curve[Ashen.Generated.Core2RewardsKeys.Linear] is JToken l && l.Type == JTokenType.Boolean && l.Value<bool>();
            var raw = linear ? b + step * b * curve.Num(Ashen.Generated.Core2RewardsKeys.MultScaler) : b * Math.Pow(g, step);
            var unit = Js.IsInt(curve[Ashen.Generated.RewardsKeys.RoundTo]) && curve.Num(Ashen.Generated.RewardsKeys.RoundTo) > 0 ? curve.Num(Ashen.Generated.RewardsKeys.RoundTo) : 1;
            return Math.Max(unit, RunJs.Round(raw / unit + d.Rules.Num(Ashen.Generated.Core2RewardsKeys.LinearEpsilon)) * unit);
        }

        /// <summary>applyLevelUp(registries, run, attributeId): one earned point to one attribute, the pools re-derived from the run's own snapshot (a level-up is not a rest).</summary>
        private static void ApplyLevelUp(Core2RewardsData d, JObject run, JToken attributeId)
        {
            var row = run.Obj(RR.Level) ?? Ashen.Domain.Rewards.LevelUp.EmptyLevel();
            var points = Js.IsInt(row[RR.UnspentPoints]) && Js.D(row[RR.UnspentPoints]) > 0 ? Js.D(row[RR.UnspentPoints]) : 0;
            var ids = CreationStats.OrderedAttributes(d.RunData).Select(a => a.Str(K.Id)).ToList();
            if (!(Js.IsStr(attributeId) && ids.Contains(Js.Str(attributeId)))) throw new InvalidOperationException(RunJs.Fmt(RM.NotAnAttribute, RunJs.Key(attributeId), string.Join(RM.IdsJoiner, ids)));
            if (!(points > 0)) throw new InvalidOperationException(RunJs.Fmt(RM.NoPointWaiting, RunJs.Key(row[RR.Level]), RunJs.Key(row[RR.Xp]), RunJs.NumStr(XpToNext(d, Js.D(row[RR.Level])))));
            if (run.Obj(K.DerivedStatRuleSnapshot)?.Obj(K.Rules) == null) throw new InvalidOperationException(RM.NoSnapshot);
            var id = Js.Str(attributeId);
            var attributes = run.Obj(K.Attributes);
            attributes.Put(id, attributes.Num(id) + 1);
            row.Put(RR.UnspentPoints, row.Num(RR.UnspentPoints) - 1);
            run.Put(RK.LevelUps, (Js.IsInt(run[RK.LevelUps]) ? run.Num(RK.LevelUps) : 0) + 1);
            run.Put(RK.LevelPoints, (Js.IsInt(run[RK.LevelPoints]) ? run.Num(RK.LevelPoints) : 0) + 1);
            RederivePools(d, run);
        }

        /// <summary>applyService(registries, draft, service, payload): the web's checks in its order, then the service, the full recovery and the visit's record.</summary>
        private static JObject ApplyService(Core2RewardsData d, JObject draft, string service, JObject payload)
        {
            var rules = Rests(d, draft);
            if (Js.Str(draft[RR.GameProfileRestVisitId]) != Context(d, draft)) throw new InvalidOperationException(RM.VisitStale);
            var visit = VisitOf(draft);
            if (d.Module(draft, MI.Rest)?.Str(RR.Mode) != Ashen.Generated.Core2RunValues.Configured || visit == null) throw new InvalidOperationException(RM.VisitSpent);
            if (!Js.Items(rules[RR.Services]).Any(s => Js.Str(s) == service)) throw new InvalidOperationException(RunJs.Fmt(RM.UnknownService, service));
            if (ServiceSpent(d, draft, visit, service)) throw new InvalidOperationException(RM.VisitSpent);
            if (service == RS.ServiceLevelUp && !CharacterLevels(d, draft) && !PointsWaiting(draft)) throw new InvalidOperationException(RM.LevelingDisabled);
            if (service == RS.ServiceLevelUp) ApplyLevelUp(d, draft, payload?[RR.AttributeId]);
            if (service == RS.ServiceSmith)
            {
                var tiers = rules.Obj(RR.Smith)?[RR.TiersPerService];
                if (!(Js.IsNum(tiers) && Js.D(tiers) == 1)) throw new InvalidOperationException(RM.SmithPolicy);
                Core2Smith.Commit(d, draft, Js.Str(payload?[RR.ItemRef]), SmithFree(d, draft), false);
                RederivePools(d, draft);
            }
            HealFull(draft);
            if (ServicesAll(d, draft))
            {
                var used = visit.Obj(RR.Used) != null ? (JObject)visit.Obj(RR.Used).DeepClone() : Js.Truthy(visit[RR.Service]) ? new JObject { [Js.Str(visit[RR.Service])] = true } : new JObject();
                used[service] = true;
                visit[RR.Used] = used;
            }
            visit[RR.Service] = service;
            RunState.SyncZones(d.RunData, draft);
            return new JObject { [RR.VisitId] = visit[RR.Id]?.DeepClone(), [RR.Service] = service, [RR.Hp] = draft[K.Hp]?.DeepClone(), [RR.Mana] = draft[K.Mana]?.DeepClone(), [RR.Stamina] = draft[K.Stamina]?.DeepClone() };
        }

        /// <summary>
        /// previewGameProfileRest(registries, run, service, payload): the run as the service would leave it (a copy, the run itself untouched) and its receipt. Throws the web's sentence
        /// for a refusal. The Smith's card rows (two real fights) are not read for a preview.
        /// </summary>
        public static JObject Preview(Core2RewardsData d, JObject run, string service, JObject payload, out JObject receipt)
        {
            var draft = Draft(run);
            receipt = ApplyService(d, draft, service, payload);
            return draft;
        }

        /// <summary>
        /// commitGameProfileRest(registries, run, service, payload) → the receipt: the service is applied to a copy, a Smith's receipt gains the live card rows (read off the run as it
        /// stood), and the copy is written back field by field (as Object.assign does: a field the copy dropped stays on the run). A refusal changes nothing.
        /// </summary>
        public static JObject Commit(Core2RewardsData d, JObject run, string service, JObject payload)
        {
            var draft = Preview(d, run, service, payload, out var receipt);
            if (service == RS.ServiceSmith && draft.Obj(Ashen.Generated.ShopKeys.LastSmithingReceipt) is JObject last && Core2Smith.Active(d, run))
            {
                var cards = Core2Smith.CardRows(d, run, last.Str(RR.ItemRef), last.Num(RR.BeforeLevel), last.Num(RR.AfterLevel));
                var changes = (JArray)last[RR.Changes];
                foreach (var row in cards) changes.Add(row.DeepClone());
                last[RR.AffectedCards] = Core2Smith.AffectedCards(cards);
            }
            foreach (var p in draft.Properties()) run[p.Name] = p.Value;
            return receipt;
        }

        // ------------------------------------------------------------------ the model the screen reads

        private sealed class Attempt
        {
            public bool Available;
            public string Reason;
            public string ReasonKey;
            public JObject Recovery;
        }

        private static Attempt Try(Core2RewardsData d, JObject run, JObject visit, string service, JObject payload)
        {
            if (ServiceSpent(d, run, visit, service)) return new Attempt { ReasonKey = RS.ReasonSpent };
            try
            {
                var draft = Preview(d, run, service, payload, out _);
                return new Attempt { Available = true, Recovery = Recovery(draft) };
            }
            catch (InvalidOperationException e) { return new Attempt { Reason = e.Message }; }
        }

        /// <summary>gameProfileRestModel(registries, run, visit): every available choice has successfully previewed the same transaction that confirmation will commit. Grants nothing.</summary>
        public static Core2RestModel Model(Core2RewardsData d, JObject run, JObject visit)
        {
            var model = new Core2RestModel { SmithFree = SmithFree(d, run) };
            model.Smith = Core2Smith.Plan(d, run, model.SmithFree);
            foreach (var candidate in Js.Items(model.Smith[Ashen.Generated.ShopKeys.Candidates]).OfType<JObject>())
            {
                var attempt = Try(d, run, visit, RS.ServiceSmith, new JObject { [RR.ItemRef] = candidate[K.ItemRef]?.DeepClone() });
                model.SmithChoices.Add(new Core2SmithChoice { ItemRef = candidate.Str(K.ItemRef), Available = attempt.Available, Reason = attempt.Reason, ReasonKey = attempt.ReasonKey, Recovery = attempt.Recovery });
            }
            foreach (var p in (run.Obj(K.Attributes) ?? new JObject()).Properties())
            {
                var def = d.RunData.Attributes.Get(p.Name);
                var attempt = Try(d, run, visit, RS.ServiceLevelUp, new JObject { [RR.AttributeId] = p.Name });
                model.Attributes.Add(new Core2AttributeChoice
                {
                    Id = p.Name, Label = Js.Truthy(def[K.Label]) ? def.Str(K.Label) : Js.Truthy(def[K.Name]) ? def.Str(K.Name) : p.Name,
                    ShortLabel = Js.Truthy(def[RK.ShortLabel]) ? def.Str(RK.ShortLabel) : p.Name, Value = Js.D(p.Value),
                    Available = attempt.Available, Reason = attempt.Reason, ReasonKey = attempt.ReasonKey, Recovery = attempt.Recovery,
                });
            }
            foreach (var id in Js.Items(Rests(d, run)[RR.Services]).Select(Js.Str))
            {
                if (id == RS.ServiceLevelUp && !CharacterLevels(d, run) && !PointsWaiting(run)) continue;
                var service = new Core2RestService { Id = id };
                if (id == RS.ServiceSmith)
                {
                    var pick = model.SmithChoices.FirstOrDefault(c => c.Available) ?? model.SmithChoices.FirstOrDefault();
                    if (pick == null) service.ReasonKey = RS.ReasonNoSmith;
                    else Fill(service, pick.Available, pick.Reason, pick.ReasonKey, pick.Recovery);
                }
                else if (id == RS.ServiceLevelUp)
                {
                    var pick = model.Attributes.FirstOrDefault(c => c.Available) ?? model.Attributes.FirstOrDefault();
                    if (pick == null) service.ReasonKey = RS.ReasonNoLevel;
                    else Fill(service, pick.Available, pick.Reason, pick.ReasonKey, pick.Recovery);
                    if (!ServiceSpent(d, run, visit, id) && !service.Available && !PointsWaiting(run)) { service.Reason = null; service.ReasonKey = RS.ReasonNoLevel; }
                }
                else
                {
                    var attempt = Try(d, run, visit, id, new JObject());
                    Fill(service, attempt.Available, attempt.Reason, attempt.ReasonKey, attempt.Recovery);
                }
                model.Services.Add(service);
            }
            return model;
        }

        private static void Fill(Core2RestService service, bool available, string reason, string reasonKey, JObject recovery)
        {
            service.Available = available;
            service.Reason = reason;
            service.ReasonKey = reasonKey;
            service.Recovery = recovery;
        }
    }
}
