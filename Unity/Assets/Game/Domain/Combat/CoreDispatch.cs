using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using EV = Ashen.Generated.Core2EnemyValues;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using RK = Ashen.Generated.Core2ReactionKeys;
using RV = Ashen.Generated.Core2ReactionValues;
using RM = Ashen.Generated.Core2ReactionMessages;
using RV2 = Ashen.Generated.Core2RemainingValues;
using SM = Ashen.Generated.Core2StatusMessages;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The intents of a Core 2 fight (web engine/combat.js dispatch): the player's cards, their reaction answers, the switch, the end of the turn and the
    /// choices a card leaves open. Every intent is the one door the web has: a fight paused on a reaction takes only its answer; a card choice pending
    /// takes only its answer; an answer resumes the enemy phase where it paused.
    /// </summary>
    public static class CoreDispatch
    {
        private static IReadOnlyList<string> Ids(JToken token) => Js.Items(token).Select(Js.Str).ToList();

        /// <summary>dispatch(combat, intent): the intent is applied and the queue drained before this returns, or the fight is paused on a reaction offer.</summary>
        public static void Dispatch(CombatState c, JObject intent)
        {
            // The web runs a Core fight's intent on a candidate copy and adopts it when it succeeded: a refusal, however deep, leaves the fight as it was.
            var transaction = CoreTransaction.Take(c);
            try { Run(c, intent); }
            catch { transaction.Restore(c); throw; }
        }

        private static void Run(CombatState c, JObject intent)
        {
            if (c.Result != null) throw new InvalidOperationException(M.CombatOver);
            var type = intent.Str(K.Type);
            if (c.Fields[Ashen.Generated.Core2DamageKeys.PendingReaction] != null && type != RV.IntentChooseReaction) throw new InvalidOperationException(RM.AnswerPendingFirst);
            if (type != RV.IntentChooseDiscard && type != RV.IntentChooseCards && CoreCardZones.HasChoice(c)) throw new InvalidOperationException(SM.ChooseBeforeAction);
            switch (type)
            {
                case RV.IntentSetReactions:
                    CoreReactions.SetReactions(c, intent[RK.Enabled]);
                    break;
                case RV.IntentChooseReaction:
                    CoreReactions.Answer(c, intent.Str(RK.OfferId), intent.Str(RK.OptionId));
                    CoreTurns.Resume(c);
                    break;
                case RV.IntentRecoverControl:
                    CoreCardPlay.RecoverControl(c, intent.Arr(RK.Selections));
                    break;
                case RV.IntentChooseCards:
                    CoreCardPlay.ChooseCards(c, Ids(intent[RK.CardInstanceIds]));
                    if (CoreReactions.RestoreReactionQueue(c) || c.Fields[Ashen.Generated.Core2DamageKeys.ReactionCursor] != null) CoreTurns.Resume(c);
                    break;
                case RV.IntentChooseDiscard:
                    CoreCardPlay.ChooseDiscard(c, Ids(intent[RK.CardInstanceIds]));
                    if (CoreReactions.RestoreReactionQueue(c) || c.Fields[Ashen.Generated.Core2DamageKeys.ReactionCursor] != null) CoreTurns.Resume(c);
                    break;
                case EV.IntentPredictIntent:
                    CoreKnowledge.Predict(c, Ashen.Generated.CombatValues.Player, intent.Str(Ashen.Generated.Core2DamageKeys.EnemyInstanceId), intent[Ashen.Generated.Core2DamageKeys.ActionSerial], intent.Str(Ashen.Generated.Core2CombatKeys.Maneuver));
                    break;
                case RV.IntentPlayCard:
                    CoreCardPlay.Play(c, intent.Str(RK.CardInstanceId), intent.Str(RK.TargetId), intent[RK.Choice], intent.Str(RK.RangeRoute));
                    break;
                case Ashen.Generated.Core2RangeValues.IntentMoveCharacter:
                    CoreFormation.Move(c, intent.Str(Ashen.Generated.Core2RangeKeys.Cell), intent.Obj(Ashen.Generated.Core2RangeKeys.Settings));
                    break;
                case V.CommandUseFlask:
                    CoreFlasks.Use(c, intent);
                    break;
                case V.CommandSwapArmament:
                    CoreEquipment.Swap(c, intent.Str(K.SlotId), (int)intent.Num(K.SetIndex));
                    break;
                case V.CommandChangeEquipment:
                    CoreEquipment.Change(c, intent.Str(K.SlotId), (int)intent.Num(K.SetIndex), intent.Str(K.PieceId));
                    break;
                case RV2.IntentPass:
                    CoreTurns.EndTurn(c, new string[0]);
                    break;
                case RV2.IntentBasicAttack:
                    // A Core 2 fight plays cards: the fallback attack is for a profile without them.
                    throw new InvalidOperationException(Ashen.Generated.Core2RemainingMessages.BasicAttackUnavailable);
                case RV2.IntentUnqueueCard:
                    CoreRefusals.UnqueueCard(c);
                    break;
                case RV2.IntentChooseBlightFeat:
                    CoreRefusals.ChooseBlightFeat(c, intent);
                    break;
                case RV.IntentEndTurn:
                    CoreTurns.EndTurn(c, Ids(intent[RK.DiscardIds]));
                    break;
                default:
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnknownIntent, type));
            }
        }
    }
}
