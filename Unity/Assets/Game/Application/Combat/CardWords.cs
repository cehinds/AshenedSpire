using System;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using T = Ashen.Generated.CardTextKeys;

namespace Ashen.App.Combat
{
    /// <summary>
    /// The words of a card's face (P-1): the rules text the web writes with <c>cardFaceLines</c>, for a card in a fight (read from the preview the hand prints from) and for a card
    /// outside one (a reward, a shop shelf, the deck, a pile: the profile's card with the run's rules). One per screen; it reads the fight it is given or, outside a fight, a state
    /// that holds only the profile's data.
    /// </summary>
    public sealed class CardWords
    {
        private readonly FaceEnv _env;

        /// <summary>The words read this fight (the preview a hand card is priced on is a copy of it).</summary>
        public CardWords(CombatState state, CardTextData text)
        {
            _env = new FaceEnv(state, text);
        }

        /// <summary>The words outside a fight: the profile's data alone.</summary>
        public static CardWords Outside(CoreCombatData data, CardTextData text) => new CardWords(FaceEnv.Bare(data), text);

        public CardTextData Text => _env.Text;

        /// <summary>The rules of a card the view built: from its preview when it has one, else from its definition (cached on the card).</summary>
        public FaceRules For(Core2CardView card)
        {
            if (card == null) return null;
            if (card.Words != null) return card.Words;
            JObject def = card.FaceDef;
            if (card.FacePreview == null && card.Instance != null) def = Project(card.Instance) ?? def;
            return card.Words = def == null ? null : CardFaceRules.Render(_env, def, card.FacePreview);
        }

        /// <summary>The rules of a card named by its id: the profile's card as the shops and rewards show it.</summary>
        public FaceRules For(string cardId, bool upgraded)
        {
            var def = Project(new JObject { [T.InstanceId] = cardId, [T.CardId] = cardId, [T.Upgraded] = upgraded });
            return def == null ? null : CardFaceRules.Render(_env, def, null);
        }

        private JObject Project(JObject instance)
        {
            try { return CoreCards.Project(_env.State, instance); }
            catch (Exception e) when (e is InvalidOperationException || e is NotSupportedException) { return null; }
        }
    }
}
