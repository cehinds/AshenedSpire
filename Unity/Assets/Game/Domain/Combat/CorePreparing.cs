using Newtonsoft.Json.Linq;
using DK = Ashen.Generated.Core2DamageKeys;
using K = Ashen.Generated.CombatKeys;
using SV = Ashen.Generated.Core2StatusValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The price of a card as its play will pay it, when something listens for the card being prepared (web engine/combat.js preparedPlayCosts and preparingPreview, WP-C0 D-293):
    /// a detached copy of the fight prepares the card (its <c>cardPreparing</c> event is emitted and the queue drained, so a discount or a surcharge the listener grants shows) and the
    /// price is read from that copy. The live fight is never touched.
    /// </summary>
    public static class CorePreparing
    {
        /// <summary>preparingPreview(combat, inst, def, targetId): the fight copy the card was prepared on.</summary>
        public static CombatState Preview(CombatState live, JObject instance, JObject def, string targetId)
        {
            var clone = live.Detach();
            if (CoreCardZones.HasChoice(clone))
            {
                clone.Queue.Clear();
                return clone;
            }
            clone.Fields.Remove(DK.PendingReaction);
            var player = clone.Player;
            var inst = instance;
            var target = targetId != null ? Triggers.FindEntity(clone, targetId) : null;
            if (targetId == null) target = clone.Enemies.Find(e => e.Is(K.Alive));
            var probe = CoreCardPlay.Carrier(clone, inst, def);
            var before = CoreRiders.Before(clone, player);
            before[SV.CardTargetsAllEnemies] = CoreRiders.TargetsAllEnemies(def);
            var payload = new JObject
            {
                [DK.CardInstanceId] = inst[K.InstanceId], [K.CardId] = inst[K.CardId], [DK.CardType] = Cards.Kind(clone.Data, def), [DK.CardTags] = probe[DK.AuthoredTags],
                [DK.AbilityKind] = probe[DK.AbilityKind], [K.SourceId] = player[K.Id], [K.TargetId] = target != null ? target[K.Id] : Js.Null(),
            };
            foreach (var p in before.Properties()) if (payload[p.Name] == null) payload[p.Name] = p.Value.DeepClone();
            clone.Emit(DK.CardPreparing, payload);
            CoreActions.Drain(clone);
            return clone;
        }
    }
}
