using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;
using EK = Ashen.Generated.EventKeys;
using EV = Ashen.Generated.EventValues;

namespace Ashen.Domain.Events
{
    /// <summary>
    /// An event's choices as the screens read them (shipped content/events.js eventChoicesWithHistory, engine/quests.js
    /// choiceAffordable and model/consequence.js bindingReasons): each authored choice with its durable id and history
    /// requirement, whether the purse pays its price, and whether it binds — fail closed: every op not positively ruled
    /// safe binds, and addCardToDeck binds when it names a curse or status (or names nothing).
    /// </summary>
    public static class EventChoices
    {
        /// <summary>
        /// eventChoicesWithHistory(event): every authored choice spread with its stable id and history requirement;
        /// empty when the id list is missing or does not match the choices one for one.
        /// </summary>
        public static List<JObject> WithHistory(EventsData d, JObject def)
        {
            var ids = def != null ? d.EventChoiceIds[def.Str(K.Id) ?? V.Undefined] as JArray : null;
            var choices = def?[RK.Choices] as JArray;
            if (def == null || choices == null || ids == null || ids.Count != choices.Count) return new List<JObject>();
            var requirements = d.ChoiceRequirements[def.Str(K.Id)] as JArray ?? new JArray();
            var out_ = new List<JObject>();
            for (var index = 0; index < choices.Count; index++)
            {
                var choice = choices[index] is JObject o ? Js.Spread(o) : new JObject();
                choice[K.Id] = ids[index].DeepClone();
                var requirement = index < requirements.Count ? requirements[index] : null;
                if (!Js.Nullish(requirement)) choice[EK.RequiresHistory] = requirement.DeepClone();
                out_.Add(choice);
            }
            return out_;
        }

        /// <summary>choiceAffordable(choice, run): a choice's cinder price, read the one way both event screens read it.</summary>
        public static bool Affordable(JObject choice, JObject run)
        {
            var requires = choice?[K.Requires];
            if (!Js.Truthy(requires)) return true;
            var cinders = Js.Get(requires, RK.Cinders);
            if (Js.IsNum(cinders) && !(run.Num(RK.Cinders) >= Js.D(cinders))) return false;
            return true;
        }

        /// <summary>
        /// bindingReasons(choice, registries): empty means a tap is enough; otherwise why the choice binds (an
        /// instrument's and a log's words, never shown raw to a player).
        /// </summary>
        public static List<string> BindingReasons(EventsData d, JToken choice)
        {
            if (!(choice is JObject || choice is JArray)) return new List<string> { EV.ReasonMalformedChoice };
            if (!(Js.Get(choice, K.Effects) is JArray effects)) return new List<string> { EV.ReasonMalformedEffects };
            var safe = d.List(EK.SafeOps);
            var bindingTypes = d.List(EK.BindingCardTypes);
            var out_ = new List<string>();
            foreach (var eff in effects)
            {
                if (!(eff is JObject e) || !Js.IsStr(e[K.Op]))
                {
                    out_.Add(EV.ReasonMalformedEffect);
                    continue;
                }
                var op = e.Str(K.Op);
                if (op == EV.AddCardToDeck)
                {
                    if (Js.Truthy(e[K.Random]) || Js.Nullish(e[K.Card]))
                    {
                        out_.Add(RunJs.Fmt(EV.ReasonAddCardFormat, EV.Unnamed));
                        continue;
                    }
                    var def = d.Run.Cards.Get(RunJs.Key(e[K.Card]));
                    var type = def != null ? Cards.Kind(d.Combat, def) : null;
                    if (type == null || bindingTypes.Contains(type)) out_.Add(RunJs.Fmt(EV.ReasonAddCardFormat, string.IsNullOrEmpty(type) ? EV.UnknownType : type));
                    continue;
                }
                if (!safe.Contains(op)) out_.Add(RunJs.Fmt(EV.ReasonUnrecognisedOpFormat, op));
            }
            return out_;
        }
    }
}
