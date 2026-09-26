using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Domain.Shop;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using GraceRefill = Ashen.Domain.Loop.GraceRefill;
using K = Ashen.Generated.CombatKeys;
using LevelPoints = Ashen.Domain.Loop.LevelPoints;
using LK = Ashen.Generated.LoopKeys;
using LV = Ashen.Generated.LoopValues;
using RK = Ashen.Generated.RunKeys;
using SK = Ashen.Generated.ShopKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;

namespace Ashen.App.Run
{
    /// <summary>One W-10 option card and its availability line.</summary>
    public sealed class RestOptionView
    {
        /// <summary>rest | smith | extract | install | flasks | level (RestOptions).</summary>
        public string Id;

        public string Title;
        public string Detail;
        public bool Available;
        public string StateText;

        /// <summary>Why it cannot be taken now (the visit's code), or null.</summary>
        public string Refusal;
    }

    /// <summary>One item the smith can promote: tier before → after, the price, and what changes on its lent cards or values.</summary>
    public sealed class RestSmithItem
    {
        public string ItemRef;
        public string Name;
        public string Tier;
        public string Cost;
        public bool Affordable;
        public string Commit;
        public readonly List<string> Changes = new List<string>();
    }

    /// <summary>One mount move (extract: a card out of a mount; install: a deck card into an open mount) with its receipt line.</summary>
    public sealed class RestMountRow
    {
        public string ItemRef;
        public string MountKey;
        public string InstanceId;
        public string Title;
        public string Receipt;
        public string Cost;
        public bool Affordable;
    }

    public sealed class RestFlaskRow
    {
        public string Kind;
        public string Name;
        public int Count;
        public bool CanAdd;
        public bool CanSub;
    }

    public sealed class RestAttributeRow
    {
        public string Id;
        public string Name;
        public int Value;
    }

    /// <summary>
    /// W-10 rest (W1s; AF-06, US-8.1 to US-8.6) as display data: the place, its tags, the option cards the place offers
    /// with their availability (Rest with its heal preview before and after, Smith with the items and tier previews,
    /// Extract and Install with their mount receipts, the flask split within the shared pool, the banked attribute points),
    /// and whether the footer's Continue shows (a multi-use place, or a relic that forbids Rest).
    /// </summary>
    public sealed class RestViewState
    {
        public string Place;
        public string Title;
        public string Header;
        public string Tags;
        public string Refill;
        public bool MultiUse;
        public bool ContinueShown;
        public string RestDetail;
        public string RestReview;
        public string Stones;
        public string Pool;
        public int Points;
        public readonly List<RestOptionView> Options = new List<RestOptionView>();
        public readonly List<RestSmithItem> Smith = new List<RestSmithItem>();
        public readonly List<RestMountRow> Extract = new List<RestMountRow>();
        public readonly List<RestMountRow> Install = new List<RestMountRow>();
        public readonly List<RestFlaskRow> Flasks = new List<RestFlaskRow>();
        public readonly List<RestAttributeRow> Attributes = new List<RestAttributeRow>();

        public RestOptionView Option(string id) => Options.FirstOrDefault(o => o.Id == id);
    }

    public static class RestView
    {
        private static int Int(double v) => (int)Math.Floor(v);

        public static RestViewState Build(RunSession session, UiData ui)
        {
            var visit = session.Visit;
            if (visit == null) return null;
            var strings = ui.Strings;
            var d = session.Content.Loop;
            var run = session.Run;
            var place = visit.LocationId;
            var view = new RestViewState
            {
                Place = place,
                Title = ActMapView.Name(strings, UiFormats.LocationNameKey, place),
                MultiUse = visit.MultiUse,
                Tags = string.Join(strings.Get(StringKeys.RestTagJoin), visit.TagIds.Select(t => string.Format(CultureInfo.InvariantCulture, UiFormats.RestTagKey, t)).Where(strings.Has).Select(strings.Get)),
                Refill = visit.Refill != null ? strings.Get(StringKeys.RestRefill) : null,
                Stones = strings.Format(StringKeys.RestSmithPurse, new StringArgs().Add(UiPlaceholders.Amount, Int(run.Num(RK.SmithingStones)))),
            };

            // Rest (previewRest: the heal before and after, no write).
            var restRefusal = visit.RestDenied != null ? LV.RelicRefusal : visit.MultiUse && session.StayRested ? LV.RestedRefusal : null;
            var preview = visit.Preview();
            if (preview != null)
            {
                var args = new StringArgs().Add(UiPlaceholders.Heal, Int(preview.Num(LK.Heal))).Add(UiPlaceholders.Before, Int(run.Num(K.Hp)))
                    .Add(UiPlaceholders.After, Int(preview.Num(K.Hp))).Add(UiPlaceholders.HpMax, Int(preview.Num(K.MaxHp)))
                    .Add(UiPlaceholders.Mp, Int(run.Num(K.Mana))).Add(UiPlaceholders.Value, Int(preview.Num(LK.ManaAfter))).Add(UiPlaceholders.Max, Int(run.Num(K.MaxMana)));
                var mana = preview.Num(K.Mana) > 0;
                view.RestDetail = strings.Format(mana ? StringKeys.RestDetailRest : StringKeys.RestDetailRestHp, args);
                view.RestReview = strings.Format(visit.MultiUse ? (mana ? StringKeys.RestReviewStay : StringKeys.RestReviewStayHp) : (mana ? StringKeys.RestReviewLeave : StringKeys.RestReviewLeaveHp), args);
            }
            var relicName = visit.RestDenied != null && session.Content.Combat.Relics.Has(visit.RestDenied) ? session.Content.Combat.Relics.Get(visit.RestDenied).Str(K.Name) : visit.RestDenied;
            view.Options.Add(Option(strings, RunFlowValues.OptionRest, view.RestDetail ?? string.Empty, restRefusal,
                restRefusal == LV.RestedRefusal ? StringKeys.RestStateUsed : null, new StringArgs().Add(UiPlaceholders.Name, relicName)));

            var offered = visit.OfferedServices();
            if (visit.Services.Is(LK.Smith) && offered.Contains(LV.UpgradeService))
            {
                var refusal = visit.SmithRefusal();
                foreach (var c in Js.Items(ItemSmithing.Plan(d.Shop, run)[SK.Candidates]).OfType<JObject>()) view.Smith.Add(SmithItem(strings, c));
                view.Options.Add(Option(strings, RunFlowValues.OptionSmith, strings.Format(StringKeys.RestDetailSmith, new StringArgs()
                    .Add(UiPlaceholders.Amount, Int(run.Num(RK.SmithingStones))).Add(UiPlaceholders.Count, view.Smith.Count)), refusal, null, null));
            }
            foreach (var service in new[] { LV.ExtractService, LV.InstallService })
            {
                if (!offered.Contains(service)) continue;
                var extract = service == LV.ExtractService;
                var plan = extract ? CardExtraction.ExtractionPlan(d.Shop, run) : CardExtraction.InstallPlan(d.Shop, run);
                var rows = extract ? view.Extract : view.Install;
                foreach (var c in Js.Items(plan[SK.Candidates]).OfType<JObject>())
                    foreach (var m in Js.Items(c[RK.Mounts]).OfType<JObject>())
                    {
                        if (extract) rows.Add(MountRow(strings, c, m, null, true));
                        else foreach (var card in Js.Items(m[K.Cards]).OfType<JObject>()) rows.Add(MountRow(strings, c, m, card, false));
                    }
                view.Options.Add(Option(strings, extract ? RunFlowValues.OptionExtract : RunFlowValues.OptionInstall,
                    strings.Format(extract ? StringKeys.RestDetailExtract : StringKeys.RestDetailInstall, new StringArgs().Add(UiPlaceholders.Count, rows.Count)),
                    visit.CardServiceRefusal(service), null, null));
            }
            if (visit.Services.Is(K.Flasks))
            {
                var charges = run.Obj(K.FlaskCharges);
                if (charges != null)
                {
                    foreach (var row in GraceRefill.Plan(d, charges))
                        view.Flasks.Add(new RestFlaskRow
                        {
                            Kind = row.Kind,
                            Name = ActMapView.Name(strings, UiFormats.FlaskKindKey, row.Kind),
                            Count = Int(row.Count),
                            CanAdd = row.CanAdd,
                            CanSub = row.CanSub,
                        });
                    view.Pool = strings.Format(StringKeys.RestFlasksPool, new StringArgs().Add(UiPlaceholders.Count, Int(charges.Num(K.Capacity))));
                }
                view.Options.Add(Option(strings, RunFlowValues.OptionFlasks, FlaskLine(strings, view.Flasks), null, null, null));
            }
            if (visit.Services.Is(WK.LevelUp))
            {
                view.Points = Int(LevelPoints.Waiting(run));
                var attributes = run.Obj(K.Attributes) ?? new JObject();
                foreach (var a in CreationStats.OrderedAttributes(d.Run))
                {
                    var id = a.Str(K.Id);
                    view.Attributes.Add(new RestAttributeRow { Id = id, Name = ActMapView.Name(strings, UiFormats.AttributeShortKey, id), Value = Int(attributes.Num(id)) });
                }
                view.Options.Add(Option(strings, RunFlowValues.OptionLevel, strings.Format(StringKeys.RestDetailLevel, new StringArgs().Add(UiPlaceholders.Count, view.Points)),
                    view.Points > 0 ? null : LV.OfferRefusal, null, null));
            }
            view.ContinueShown = visit.MultiUse || visit.RestDenied != null;
            view.Header = strings.Format(StringKeys.RestHeader, new StringArgs().Add(UiPlaceholders.Name, view.Title)
                .Add(UiPlaceholders.Count, view.Options.Count(o => o.Available)).Add(UiPlaceholders.Total, view.Options.Count));
            return view;
        }

        private static RestOptionView Option(StringTable strings, string id, string detail, string refusal, string stateKey, StringArgs refusalArgs)
        {
            var refusalKey = refusal == null ? null : string.Format(CultureInfo.InvariantCulture, UiFormats.RestRefusalKey, refusal);
            return new RestOptionView
            {
                Id = id,
                Title = strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.RestOptionKey, id)),
                Detail = detail,
                Available = refusal == null,
                Refusal = refusal,
                StateText = refusal == null ? strings.Get(StringKeys.RestStateAvailable)
                    : strings.Format(stateKey ?? (strings.Has(refusalKey) ? refusalKey : StringKeys.RestStateUnavailable), refusalArgs ?? new StringArgs()),
            };
        }

        public static string FlaskLine(StringTable strings, IReadOnlyList<RestFlaskRow> rows) =>
            string.Join(strings.Get(StringKeys.RestTagJoin), rows.Select(r => strings.Format(StringKeys.RestFlasksRow, new StringArgs().Add(UiPlaceholders.Name, r.Name).Add(UiPlaceholders.Count, r.Count))));

        private static RestSmithItem SmithItem(StringTable strings, JObject c)
        {
            var item = new RestSmithItem
            {
                ItemRef = c.Str(K.ItemRef),
                Name = c.Str(SK.ItemName) ?? c.Str(K.ItemRef),
                Tier = strings.Format(StringKeys.RestSmithTier, new StringArgs().Add(UiPlaceholders.Before, Int(Js.Or0(c[SK.CurrentLevel]))).Add(UiPlaceholders.After, Int(Js.Or0(c[SK.NextLevel])))),
                Affordable = c.Is(SK.Affordable),
                Cost = strings.Format(c.Is(SK.Affordable) ? StringKeys.RestSmithCost : StringKeys.RestSmithShortfall, new StringArgs()
                    .Add(UiPlaceholders.Amount, Int(c.Num(K.Cost))).Add(UiPlaceholders.Count, Int(c.Num(SK.Shortfall)))),
                Commit = strings.Format(StringKeys.RestSmithUpgrade, new StringArgs().Add(UiPlaceholders.Amount, Int(c.Num(K.Cost)))),
            };
            foreach (var change in Js.Items(c[SK.Changes]).OfType<JObject>())
                AddOnce(item.Changes, strings.Format(StringKeys.RestSmithChange, new StringArgs().Add(UiPlaceholders.Label, change.Str(K.Label))
                    .Add(UiPlaceholders.Before, Show(change[WK.Before])).Add(UiPlaceholders.After, Show(change[WK.After]))));
            return item;
        }

        /// <summary>A change line once: several lent copies of one card change alike (the smith upgrades them all).</summary>
        private static void AddOnce(List<string> lines, string line)
        {
            if (!lines.Contains(line)) lines.Add(line);
        }

        private static string Show(JToken value) => value == null || value.Type == JTokenType.Null ? string.Empty
            : Js.IsNum(value) ? Js.D(value).ToString(CultureInfo.InvariantCulture) : value.Type == JTokenType.String ? (string)value : value.ToString(Newtonsoft.Json.Formatting.None);

        private static RestMountRow MountRow(StringTable strings, JObject candidate, JObject mount, JObject card, bool extract)
        {
            var item = candidate.Str(SK.ItemName) ?? candidate.Str(K.ItemRef);
            var cardName = extract ? mount.Str(SK.CardName) ?? mount.Str(K.CardId) : card.Str(SK.CardName) ?? card.Str(K.CardId);
            var args = new StringArgs().Add(UiPlaceholders.Name, cardName).Add(UiPlaceholders.Label, item)
                .Add(UiPlaceholders.Amount, Int(candidate.Num(K.Cost))).Add(UiPlaceholders.Kind, mount.Str(K.Kind) ?? string.Empty);
            return new RestMountRow
            {
                ItemRef = candidate.Str(K.ItemRef),
                MountKey = mount.Str(SK.MountKey),
                InstanceId = card?.Str(K.InstanceId),
                Title = strings.Format(extract ? StringKeys.RestCardsExtractRow : StringKeys.RestCardsInstallRow, args),
                Receipt = strings.Format(extract ? StringKeys.RestCardsReceiptExtract : StringKeys.RestCardsReceiptInstall, args),
                Cost = strings.Format(StringKeys.RestCardsCost, args),
                Affordable = candidate.Is(SK.Affordable),
            };
        }

        /// <summary>
        /// The level card's live receipt: the derived pools before and after spending <paramref name="assigned"/> points
        /// ({ attributeId: n }), computed on a copy through the same applyLevelUp the stay commits (nothing is written).
        /// </summary>
        public static List<string> LevelPreview(RunSession session, UiData ui, JObject assigned)
        {
            var strings = ui.Strings;
            var d = session.Content.Loop;
            var before = session.Run;
            var after = (JObject)before.DeepClone();
            foreach (var p in (assigned ?? new JObject()).Properties())
                for (var i = 0; i < Js.D(p.Value); i++) LevelPoints.Apply(d, after, p.Name);
            var rows = new List<string>();
            foreach (var pair in new[] { (K.MaxHp, StringKeys.RestLevelHp), (K.MaxMana, StringKeys.RestLevelMana), (K.MaxStamina, StringKeys.RestLevelStamina), (K.EnergyMax, StringKeys.RestLevelActions) })
                rows.Add(strings.Format(StringKeys.RestLevelReceipt, new StringArgs().Add(UiPlaceholders.Label, strings.Get(pair.Item2))
                    .Add(UiPlaceholders.Before, Int(before.Num(pair.Item1))).Add(UiPlaceholders.After, Int(after.Num(pair.Item1)))));
            return rows;
        }
    }
}
