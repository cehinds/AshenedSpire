using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;

namespace Ashen.App.Run
{
    /// <summary>One attribute row of the allocation dialog (web progressionCards.js openProgressionAllocation spec rows): its card at the allocation held now, and whether a point can be taken back or added.</summary>
    public sealed class AllocationRow
    {
        public string Id;
        public string Label;
        public double Value;
        public AttributeCardView Card;
        public bool CanDecrease;
        public bool CanIncrease;
    }

    /// <summary>
    /// The Progression hub's attribute allocation dialog (web ui/components/progressionCards.js openProgressionAllocation over statAllocationCard.js): the points the character holds, the ones put on each attribute so far,
    /// and the cards that show what the allocation would give. Nothing is written until Done; the web's Done spends each point through <c>applyLevelUp</c>, which in the configured profile refuses outside a Rest visit's
    /// Level up service (model/levelup.js:414), so here Done spends nothing either (<see cref="Commit"/>). Engine-free.
    /// </summary>
    public sealed class AttributeAllocation
    {
        private readonly Core2RunSession _session;
        private readonly UiData _ui;
        private readonly List<string> _ids;
        private readonly Dictionary<string, int> _pending;
        private readonly JObject _attributes;

        public AttributeAllocation(Core2RunSession session, UiData ui)
        {
            _session = session;
            _ui = ui;
            var run = session.Run;
            var level = run.Obj(K.Level);
            Points = Js.IsInt(level?[RunKeys.UnspentPoints]) && level.Num(RunKeys.UnspentPoints) > 0 ? (int)level.Num(RunKeys.UnspentPoints) : 0;
            _attributes = run.Obj(K.Attributes) ?? new JObject();
            _ids = Ashen.Domain.Run.CreationStats.OrderedAttributes(session.Core.Combat.Run.Run).Select(a => a.Str(K.Id)).ToList();
            _pending = _ids.ToDictionary(id => id, id => 0);
        }

        /// <summary>The points the character holds (levelUpPlan.points).</summary>
        public int Points { get; }

        /// <summary>The points put on attributes so far.</summary>
        public int Spent => _pending.Values.Sum();

        public int Remaining => Points - Spent;

        /// <summary>Done is shut until a point is put somewhere.</summary>
        public bool DoneDisabled => Spent == 0;

        public int PendingOn(string id) => _pending.TryGetValue(id, out var n) ? n : 0;

        public bool Increase(string id)
        {
            if (!_pending.ContainsKey(id) || Spent >= Points) return false;
            _pending[id]++;
            return true;
        }

        public bool Decrease(string id)
        {
            if (!_pending.ContainsKey(id) || _pending[id] == 0) return false;
            _pending[id]--;
            return true;
        }

        /// <summary>The attributes with the pending points added (what the cards are drawn over).</summary>
        public JObject Preview()
        {
            var shown = new JObject();
            foreach (var id in _ids) shown[id] = _attributes.Num(id) + _pending[id];
            return shown;
        }

        /// <summary>spec().rows: each attribute's card over the allocation held now.</summary>
        public IReadOnlyList<AllocationRow> Rows()
        {
            var preview = Preview();
            var cards = ProgressionStats.For(_session, _ui, preview).Attributes;
            return _ids.Select(id => new AllocationRow
            {
                Id = id, Label = cards.First(c => c.Id == id).Title, Value = preview.Num(id), Card = cards.First(c => c.Id == id),
                CanDecrease = _pending[id] > 0, CanIncrease = Spent < Points,
            }).ToList();
        }

        /// <summary>
        /// onDone: nothing held, or more than the character has, does nothing; otherwise the web spends each point with <c>applyLevelUp</c>, which throws in the configured profile unless the Rest visit's
        /// Level up service is the one asking (model/levelup.js:414). The allocation stays as it was, and so does the dialog. Always false: points are spent at a Rest site.
        /// </summary>
        public bool Commit() => false;
    }
}
