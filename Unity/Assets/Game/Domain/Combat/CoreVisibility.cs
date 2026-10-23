using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using K = Ashen.Generated.CombatKeys;
using SK = Ashen.Generated.Core2StatusKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// What the player can see of an enemy's turn (web engine/enemyKnowledge.js knowledgeIntentProjection and the visible copy previewCard plans on, WP-C0 D-295): an enemy whose intent
    /// the player has not read exactly shows no move, no stance, no Counter and no move it is charging in a card's preview. <see cref="IntentExact"/> is the web's test; <see cref="VisibleCopy"/>
    /// is the copy of the fight with the unread enemies stripped of what they have not shown.
    /// </summary>
    public static class CoreVisibility
    {
        /// <summary>proposalIntentRevealUntil(enemy, ownerId, turn, { legacySolo: true }): the turn an intent-revealing card keeps the move open to this observer until, or null.</summary>
        private static double? RevealedUntil(CombatState c, JObject enemy, string ownerId)
        {
            var scoped = enemy.Obj(SK.ProposalIntentRevealedUntilByOwner);
            if (scoped != null && Js.IsFinite(scoped[ownerId]) && scoped.Num(ownerId) > c.Turn) return scoped.Num(ownerId);
            return null;
        }

        /// <summary>knowledgeIntentProjection(combat, enemy, ownerId)?.exact === true.</summary>
        public static bool IntentExact(CombatState c, JObject enemy, string ownerId = V.Player)
        {
            if (RevealedUntil(c, enemy, ownerId) != null) return true;
            if (c.Fields.Obj(CK.EnemyKnowledge) == null) return false;
            if (enemy.Is(CK.ActorIntentRevealed) || enemy.Obj(K.Intent)?.Str(K.Kind) == V.IntentStaggered) return true;
            return enemy.Obj(CK.KnowledgeAction)?.Obj(CK.Reads)?.Obj(ownerId)?.Str(CK.Visibility) == CV.VisibilityExact;
        }

        /// <summary>previewCard's visible copy: a detached fight (the fight itself when it keeps no enemy knowledge) where every enemy the player has not read shows an unknown intent and no stance, Counter or pending move.</summary>
        public static CombatState VisibleCopy(CombatState live)
        {
            var visible = live.Detach();
            if (visible.Fields.Obj(CK.EnemyKnowledge) == null) return visible;
            foreach (var enemy in visible.Enemies.Where(e => !IntentExact(visible, e)))
            {
                enemy[K.Intent] = new JObject { [K.Kind] = V.IntentUnknown, [K.MoveId] = Js.Null() };
                enemy.Remove(CK.CombatStance);
                enemy.Remove(CK.CombatCounter);
                enemy.Remove(K.PendingMove);
            }
            return visible;
        }
    }
}
