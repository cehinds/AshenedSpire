using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;

namespace Ashen.App.Run
{
    /// <summary>
    /// The classes W-04 offers a New Game under a ruleset (docs/design/11 WP-C1b, D-312): the 405 content's four classes, or the Core 2 profile's, where a class is open only once
    /// the profile's ledger says so (the first run opens the first class, every finished run opens the next; <see cref="CoreRunRules.ClassAvailable"/>). Previews are the run each class
    /// would start with, read from its own creation rules.
    /// </summary>
    public sealed class CreationClasses
    {
        private readonly RunContent _content;
        private readonly JObject _profile;
        private readonly Dictionary<string, ClassPreview> _previews = new Dictionary<string, ClassPreview>(StringComparer.Ordinal);

        private CreationClasses(RunContent content, bool core2, JObject profile)
        {
            _content = content;
            IsCore2 = core2;
            _profile = profile ?? new JObject();
        }

        public bool IsCore2 { get; }

        /// <summary>The ruleset's classes in its own order.</summary>
        public IReadOnlyList<string> Ids => IsCore2 ? _content.Core2.ClassIds : _content.ClassIds;

        public static CreationClasses For(RunContent content, string ruleset, JObject profile) =>
            new CreationClasses(content, ruleset == RunFlowValues.RulesetCore2, profile);

        /// <summary>A Core 2 class still locked in the profile's ledger (the 405 run has no gate).</summary>
        public bool Locked(string classId) => IsCore2 && !CoreRunRules.ClassAvailable(_content.Core2.Combat.Run, _profile, classId);

        /// <summary>How many more finished runs open a locked class (the web's "Play 1 game to unlock."); 0 for an open class.</summary>
        public int GamesToUnlock(string classId) => IsCore2 ? CoreRunRules.GamesToUnlock(_content.Core2.Combat.Run, _profile, classId) : 0;

        /// <summary>Why a class cannot begin a run (null when it can): the rules' refusal of its creation, or its lock.</summary>
        public string Problem(string classId)
        {
            if (!IsCore2) return _content.ClassProblem(classId);
            if (classId == null || !_content.Core2.Combat.Run.Run.Classes.Has(classId)) return string.Format(System.Globalization.CultureInfo.InvariantCulture, RunFlowMessages.UnknownClass, classId);
            if (Locked(classId)) return string.Format(System.Globalization.CultureInfo.InvariantCulture, RunFlowMessages.ClassLocked, classId);
            return Create(classId, out _);
        }

        /// <summary>The class a New Game preselects: the data's default when it can begin, else the first that can.</summary>
        public string Default() =>
            Problem(_content.Flow.DefaultClass) == null ? _content.Flow.DefaultClass : Ids.FirstOrDefault(id => Problem(id) == null);

        /// <summary>The attributes, pools and flask split a class starts with, or null when its creation is refused.</summary>
        public ClassPreview Preview(string classId)
        {
            if (!IsCore2) return _content.Preview(classId);
            if (_previews.TryGetValue(classId ?? string.Empty, out var known)) return known;
            ClassPreview preview = null;
            if (classId != null && Create(classId, out var run) == null)
            {
                var attributes = run.Obj(K.Attributes) ?? new JObject();
                var charges = run.Obj(K.FlaskCharges) ?? new JObject();
                preview = new ClassPreview
                {
                    Attributes = attributes.Properties().Select(p => new KeyValuePair<string, double>(p.Name, p.Value.Value<double>())).ToList(),
                    MaxHp = run.Num(K.MaxHp),
                    MaxMana = run.Num(K.MaxMana),
                    MaxStamina = run.Num(K.MaxStamina),
                    Actions = run.Num(K.EnergyMax),
                    HpFlasks = charges.Num(K.Hp),
                    ManaFlasks = charges.Num(K.Mana),
                };
            }
            _previews[classId ?? string.Empty] = preview;
            return preview;
        }

        /// <summary>The run a class would start with, for the preview: created with every class open, so a locked class still shows what it holds.</summary>
        private string Create(string classId, out JObject run)
        {
            run = null;
            var core = _content.Core2;
            var open = new JObject
            {
                [Core2RunKeys.GameProfileUnlocks] = new JObject
                {
                    [core.ProfileId] = new JObject { [Core2RunKeys.CompletedRunIds] = new JArray(core.ClassIds.Select(id => (object)id).ToArray()) },
                },
            };
            try { run = CoreRunRules.Create(core.Combat.Run, 1u, classId, CoreRunOptions.FromJson(null, open)); }
            catch (InvalidOperationException e) { return e.Message; }
            catch (NotSupportedException e) { return e.Message; }
            return null;
        }
    }
}
