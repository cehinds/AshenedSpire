using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RV = Ashen.Generated.RunValues;

namespace Ashen.Domain.Shop
{
    /// <summary>
    /// Permanent deck removal (shipped model/cardRemoval.js): an item-granted card cannot be burned; a basic attack
    /// retires its attack slot (validated against the birth allocation), not its current weapon face.
    /// </summary>
    public static class CardRemoval
    {
        /// <summary>canRemoveDeckCard(card): a card the run owns (not granted by an item).</summary>
        public static bool CanRemove(JObject card) => card != null && !Js.Truthy(card[K.GrantedBy]);

        /// <summary>
        /// removeDeckCard(run, instanceId, { keepOne }): false (nothing changed) for a missing or granted card, the last
        /// card when keepOne, or an attack slot already retired; otherwise the card leaves the deck.
        /// </summary>
        public static bool RemoveDeckCard(ShopData d, JObject run, string instanceId, bool keepOne = false)
        {
            var deck = run.Arr(K.Deck);
            var index = deck.OfType<JObject>().ToList().FindIndex(card => card.Str(K.InstanceId) == instanceId);
            var card = index >= 0 ? deck[index] as JObject : null;
            if (!CanRemove(card) || (keepOne && deck.Count <= 1)) return false;
            if (Js.Truthy(card[K.EquipmentAttackSlotId]))
            {
                var count = Js.Nullish(run[K.EquipmentAttackSlotCount])
                    ? deck.OfType<JObject>().Count(c => c.Str(K.EquipmentRole) == RV.RoleAttack)
                    : run.Num(K.EquipmentAttackSlotCount);
                WeaponCards.RetiredAttackSlots(count, run[K.RemovedAttackSlotIds]);
                WeaponCards.RetiredAttackSlots(count, new JArray(card[K.EquipmentAttackSlotId].DeepClone()));
                var retired = Js.Items(run[K.RemovedAttackSlotIds]).Select(Js.Str).ToList();
                var slot = card.Str(K.EquipmentAttackSlotId);
                if (retired.Contains(slot)) return false;
                retired.Add(slot);
                run.Put(K.EquipmentAttackSlotCount, count);
                run[K.RemovedAttackSlotIds] = new JArray(retired);
            }
            deck.RemoveAt(index);
            return true;
        }
    }
}
