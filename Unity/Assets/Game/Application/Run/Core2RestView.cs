using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Loop;
using Ashen.Domain.Rewards;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RR = Ashen.Generated.Core2RestKeys;
using RS = Ashen.Generated.Core2RestValues;
using SK = Ashen.Generated.ShopKeys;
using SN = Ashen.Generated.StringKeys;

namespace Ashen.App.Run
{
    /// <summary>One row of the recovery ledger: what a pool holds now and what it holds once the service is taken.</summary>
    public sealed class Core2RecoveryRowView
    {
        public string Id;
        public string Label;
        public double Before;
        public double After;

        /// <summary>The gain, or null when nothing moves.</summary>
        public double? Gain => After > Before ? After - Before : (double?)null;
    }

    /// <summary>One service of the rest site (Smith, Level Up) as the screen's card shows it.</summary>
    public sealed class Core2RestServiceView
    {
        public string Id;
        public string Title;

        /// <summary>The card's line: what the service does, or "Done at this site." once it is used.</summary>
        public string Description;

        public bool Available;
        public bool Used;

        /// <summary><c>used</c>, <c>available</c> or <c>unavailable</c> and the word for it.</summary>
        public string State;

        public string StateText;

        /// <summary>Why it cannot be taken (the web's sentence or its ui line); empty when it can.</summary>
        public string Reason;
    }

    /// <summary>An item the Smith could upgrade: the plan's candidate (name, tiers, stat rows, requirements) and whether taking it now would succeed.</summary>
    public sealed class Core2SmithItemView
    {
        public string ItemRef;
        public string Name;
        public string Kind;
        public double CurrentLevel;
        public double NextLevel;

        /// <summary>The plan's candidate as the web states it (itemRef, itemKind, itemId, itemName, levels, cost, changes, requirements, authoredChanges, previewCards).</summary>
        public JObject Plan;

        public bool Available;
        public string Reason = string.Empty;
        public IReadOnlyList<Core2RecoveryRowView> Recovery;
    }

    /// <summary>An attribute a point could go to.</summary>
    public sealed class Core2AttributeView
    {
        public string Id;
        public string Label;
        public string ShortLabel;
        public double Value;
        public bool Available;
        public string Reason = string.Empty;
        public IReadOnlyList<Core2RecoveryRowView> Recovery;
    }

    /// <summary>The whole rest site of a Core 2 run as the (future) screen draws it.</summary>
    public sealed class Core2RestViewState
    {
        public string VisitId;

        /// <summary>"1 of 2 available" and the arrival line ("Healed +4 HP, +0 MP, +2 SP. Flasks refilled."), the line under the choices, the recovery preview.</summary>
        public string Status;

        public string Healed;
        public string Line;
        public string RecoveryHeading;
        public IReadOnlyList<Core2RecoveryRowView> Recovery = Array.Empty<Core2RecoveryRowView>();
        public IReadOnlyList<Core2RestServiceView> Services = Array.Empty<Core2RestServiceView>();
        public string ContinueText;

        /// <summary>The Smith's side: what it costs (nothing here), the stones carried and the items it could work on.</summary>
        public bool SmithFree;

        public double Stones;
        public IReadOnlyList<Core2SmithItemView> SmithItems = Array.Empty<Core2SmithItemView>();

        /// <summary>The Level Up side: one row per attribute.</summary>
        public IReadOnlyList<Core2AttributeView> Attributes = Array.Empty<Core2AttributeView>();

        /// <summary>The consequence lines of the Smith and Level Up modals once a service has been used or not (the web's smith/level notes).</summary>
        public string SmithConsequence;

        public string SmithStays;
        public string SmithDecision;
        public string LevelNote;
        public string LevelConfirm;
    }

    /// <summary>
    /// The rest site of a Core 2 run as display data (WP-16; the seam the rest screen reads, web ui/components/gameProfileRest.js and ui/models/GameProfileRestModel.js without the DOM):
    /// the arrival heal, the services with their state and reasons, the Smith's candidates and the attributes, each with the recovery it would give. Nothing here grants anything. The
    /// Smith's live card rows run real fights, so they are asked for apart (<see cref="Core2RunSession.SmithCardRows"/>).
    /// </summary>
    public static class Core2RestView
    {
        private static string Say(StringTable strings, string key, params (string name, string value)[] args)
        {
            var named = new StringArgs();
            foreach (var a in args) named.Add(a.name, a.value);
            return strings.Format(key, named);
        }

        private static string Key(string format, string id) => string.Format(System.Globalization.CultureInfo.InvariantCulture, format, id);

        private static IReadOnlyList<Core2RecoveryRowView> Rows(Core2RewardsData d, JObject run, JObject after, StringTable strings)
        {
            var rows = new List<Core2RecoveryRowView>();
            var charges = run.Obj(K.FlaskCharges);
            foreach (var row in Js.Items(d.RestRules[RR.RecoveryRows]).OfType<JObject>())
            {
                var flask = row.Str(RR.Kind) == RS.RecoveryFlask;
                if (flask && charges == null) continue;
                var field = row.Str(RR.Pool);
                rows.Add(new Core2RecoveryRowView
                {
                    Id = row.Str(RR.Id), Label = strings.Get(Key(Ashen.Generated.Core2RestValues.PoolKeyFormat, row.Str(RR.Id))),
                    Before = flask ? charges.Num(field) : run.Num(field), After = after.Num(field),
                });
            }
            return rows;
        }

        private static string ReasonText(StringTable strings, string reason, string reasonKey) =>
            reasonKey != null ? strings.Get(Key(RS.StringKeyFormat, reasonKey)) : reason ?? string.Empty;

        /// <summary>The rest site the run stands in, or null when it is not at one.</summary>
        public static Core2RestViewState Build(Core2RewardsData d, JObject run, StringTable strings)
        {
            var visit = Core2Rest.Visit(run);
            if (visit == null) return null;
            var model = Core2Rest.Model(d, run, visit);
            var all = Core2Rest.ServicesAll(d, run);
            bool UsedOf(string id) => all ? Core2Rest.ServiceUsed(visit, id) : visit.Str(RR.Service) == id;
            var state = new Core2RestViewState { VisitId = visit.Str(RR.Id) };
            var available = model.Services.Count(s => !UsedOf(s.Id) && s.Available);
            state.Status = Say(strings, SN.Core2RestStatusAvailable, (RS.PlaceholderAvailable, Ashen.Domain.Run.RunJs.NumStr(available)), (RS.PlaceholderTotal, Ashen.Domain.Run.RunJs.NumStr(model.Services.Count)));
            var healed = visit.Obj(RR.Healed);
            var healedAny = healed != null && (Js.Truthy(healed[RR.Hp]) || Js.Truthy(healed[RR.Mana]) || Js.Truthy(healed[RR.Stamina]) || Js.Truthy(healed[RR.FlaskHp]) || Js.Truthy(healed[RR.FlaskMana]));
            if (all)
            {
                state.Healed = healedAny
                    ? Say(strings, SN.Core2RestHealed, (RS.PlaceholderHp, Ashen.Domain.Run.RunJs.Key(healed[RR.Hp])), (RS.PlaceholderMp, Ashen.Domain.Run.RunJs.Key(healed[RR.Mana])), (RS.PlaceholderSp, Ashen.Domain.Run.RunJs.Key(healed[RR.Stamina])))
                    : strings.Get(SN.Core2RestHealedFull);
                if (Js.Truthy(visit[RR.Service]))
                {
                    var completion = !model.Services.Any(s => UsedOf(s.Id)) ? RS.ChooseAll : model.Services.Any(s => !UsedOf(s.Id) && s.Available) ? RS.CompleteAll : RS.CompleteDoneAll;
                    state.Line = Say(strings, Key(RS.CompletionKeyFormat, completion), (RS.PlaceholderService, strings.Get(Key(RS.ServiceTitleFormat, visit.Str(RR.Service)))));
                }
                else
                {
                    var ids = new HashSet<string>(model.Services.Select(s => s.Id), StringComparer.Ordinal);
                    var both = model.SmithFree && ids.SetEquals(Js.Items(d.RestRules[RR.IntroServices]).Select(Js.Str));
                    state.Line = strings.Get(Key(RS.CompletionKeyFormat, both ? RS.ChooseAll : RS.ChooseAllAny));
                }
            }
            var preview = model.Services.FirstOrDefault(s => s.Available)?.Recovery ?? Core2Rest.Recovery(run);
            state.RecoveryHeading = strings.Get(SN.Core2RestRecoveryHeading);
            state.Recovery = Rows(d, run, preview, strings);
            state.Services = model.Services.Select(s =>
            {
                var used = UsedOf(s.Id);
                var stateName = used ? RS.StateUsed : s.Available ? RS.StateAvailable : RS.StateUnavailable;
                var description = Key(RS.DescriptionKeyFormat, s.Id == RS.ServiceSmith && !model.SmithFree ? RS.DescriptionSmithPaid : s.Id + RS.DescriptionAllSuffix);
                return new Core2RestServiceView
                {
                    Id = s.Id, Title = strings.Get(Key(RS.ServiceTitleFormat, s.Id)), Available = s.Available, Used = used, State = stateName,
                    StateText = strings.Get(Key(Ashen.Generated.Core2RestValues.StateKeyFormat, stateName)),
                    Description = used ? strings.Get(SN.Core2RestUsedService) : strings.Get(description),
                    Reason = s.Available ? string.Empty : used ? strings.Get(SN.Core2RestUsedService) : ReasonText(strings, s.Reason, s.ReasonKey),
                };
            }).ToList();
            state.ContinueText = strings.Get(SN.Core2RestContinue);
            state.SmithFree = model.SmithFree;
            state.Stones = model.Smith.Num(SK.Stones);
            var candidates = Js.Items(model.Smith[SK.Candidates]).OfType<JObject>().ToList();
            state.SmithItems = candidates.Select(c =>
            {
                var choice = model.SmithChoices.First(x => x.ItemRef == c.Str(K.ItemRef));
                return new Core2SmithItemView
                {
                    ItemRef = c.Str(K.ItemRef), Name = c.Str(SK.ItemName), Kind = c.Str(SK.ItemKind), CurrentLevel = c.Num(SK.CurrentLevel), NextLevel = c.Num(SK.NextLevel), Plan = (JObject)c.DeepClone(),
                    Available = choice.Available, Reason = choice.Available ? string.Empty : ReasonText(strings, choice.Reason, choice.ReasonKey),
                    Recovery = choice.Recovery == null ? null : Rows(d, run, choice.Recovery, strings),
                };
            }).ToList();
            state.Attributes = model.Attributes.Select(a => new Core2AttributeView
            {
                Id = a.Id, Label = a.Label, ShortLabel = a.ShortLabel, Value = a.Value, Available = a.Available,
                Reason = a.Available ? string.Empty : ReasonText(strings, a.Reason, a.ReasonKey), Recovery = a.Recovery == null ? null : Rows(d, run, a.Recovery, strings),
            }).ToList();
            var levelUpOffered = model.Services.Any(s => s.Id == RS.ServiceLevelUp);
            state.SmithConsequence = strings.Get(levelUpOffered ? SN.Core2RestSmithConsequenceAll : SN.Core2RestSmithConsequenceAny);
            state.SmithStays = strings.Get(SN.Core2RestStaysVisit);
            state.SmithDecision = strings.Get(SN.Core2RestSmithDecisionAll);
            state.LevelNote = strings.Get(SN.Core2RestLevelNoteAll);
            state.LevelConfirm = strings.Get(SN.Core2RestLevelConfirmAll);
            return state;
        }
    }
}
