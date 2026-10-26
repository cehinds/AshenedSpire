using System;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The combatant's target card on hover, focus or tap (VP-6, D-800; the web's hover card, docs/qa/web-screens/combat-target-hover-* and combat-intent-tooltip-*): the name, HP bar, chips and intent of
    /// a fighter in a small panel beside it, with a round "i" that opens the full reading. A tap or click on a fighter that is not being aimed at pins the card (touch has no hover); another tap, the
    /// pointer leaving, Escape or an overlay opening dismisses it. It only reads the fighter's <see cref="FighterData"/>.
    /// </summary>
    internal sealed class Core2TargetInspector
    {
        private readonly VisualElement _root;
        private readonly Func<bool> _blocked;
        private readonly Func<bool> _aiming;
        private readonly Action<FighterData, VisualElement> _openDetail;
        private readonly long _hideDelayMs;
        private readonly TargetCard _card = new TargetCard();
        private IVisualElementScheduledItem _hide;
        private Fighter _shown;
        private bool _pinned;
        private bool _overCard;
        private string _selectedId;

        public Core2TargetInspector(VisualElement root, Func<bool> blocked, Func<bool> aiming, Action<FighterData, VisualElement> openDetail, int hideDelayMs)
        {
            _hideDelayMs = hideDelayMs;
            _root = root;
            _blocked = blocked;
            _aiming = aiming;
            _openDetail = openDetail;
            _card.pickingMode = PickingMode.Position;
            UiDom.Show(_card, false);
            _root.Add(_card);
            // web combat.js:603-606: a press on a blank part of the board ends the contextual selection (the fighters and their cards keep it).
            _root.RegisterCallback<PointerUpEvent>(e =>
            {
                if (e.target is VisualElement hit && hit.GetFirstAncestorOfType<Fighter>() == null && !(hit is Fighter) && hit.GetFirstAncestorOfType<TargetCard>() == null && hit != _card) _selectedId = null;
            });
            _card.RegisterCallback<PointerEnterEvent>(_ =>
            {
                _overCard = true;
                _hide?.Pause();
            });
            _card.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                _overCard = false;
                HideSoon();
            });
            _card.InfoPressed += () =>
            {
                var fighter = _shown;
                if (fighter?.Data == null) return;
                Hide();
                _openDetail(fighter.Data, fighter);
            };
        }

        public bool IsShown => _shown != null;

        /// <summary>The card was pinned by a tap (it stays until tapped again, Escape or an overlay).</summary>
        public bool IsPinned => _shown != null && _pinned;

        /// <summary>
        /// The fighter the player has selected for reading (web <c>selectedCombatantId</c>, combat.js:545 and 591-596: "contextual reading selection; never combat targeting"). A tap on a fighter and the focus landing on one
        /// select it; a blank press, Escape, a card or flask being armed (combat.js:1530) and its death clear it. The Inspect key opens this fighter's door, else the player's.
        /// </summary>
        public string SelectedId => _selectedId;

        public void ClearSelection() => _selectedId = null;

        private void Select(Fighter fighter)
        {
            if (fighter?.Data == null || !fighter.Data.Alive) return;
            _selectedId = fighter.Data.Id;
        }

        /// <summary>Wires a fighter: hover and focus show its card, leaving hides it, a tap pins or unpins it.</summary>
        public void Attach(Fighter fighter)
        {
            fighter.RegisterCallback<PointerEnterEvent>(_ => Show(fighter));
            fighter.RegisterCallback<PointerLeaveEvent>(_ => HideSoon());
            fighter.RegisterCallback<FocusInEvent>(_ =>
            {
                if (!_aiming()) Select(fighter);
                Show(fighter);
            });
            fighter.RegisterCallback<FocusOutEvent>(_ => HideSoon());
            fighter.RegisterCallback<PointerUpEvent>(_ =>
            {
                if (_aiming() || _blocked()) return;
                Select(fighter);
                if (_shown == fighter && _pinned) Hide();
                else
                {
                    Show(fighter);
                    _pinned = true;
                }
            });
        }

        /// <summary>The fighter changed (a redraw): the open card follows its data, or goes when the fighter is gone or an overlay took the screen.</summary>
        public void Refresh()
        {
            if (_aiming()) _selectedId = null;
            if (_shown == null) return;
            if (_blocked() || _shown.Data == null || !_shown.Data.Alive) Hide();
            else _card.Bind(_shown.Data);
        }

        public void Hide()
        {
            _hide?.Pause();
            _shown = null;
            _pinned = false;
            _overCard = false;
            UiDom.Show(_card, false);
        }

        private void HideSoon()
        {
            if (_pinned) return;
            _hide?.Pause();
            _hide = _card.schedule.Execute(() =>
            {
                if (!_overCard && !_pinned) Hide();
            }).StartingIn(_hideDelayMs);
        }

        private void Show(Fighter fighter)
        {
            if (fighter?.Data == null || !fighter.Data.Alive || _blocked()) return;
            _hide?.Pause();
            if (_shown != fighter) _pinned = false;
            _shown = fighter;
            _card.Bind(fighter.Data);
            UiDom.Show(_card, true);
            Place(fighter);
        }

        /// <summary>Beside the fighter: to its right when the card fits there, else to its left; level with the top of the figure.</summary>
        private void Place(Fighter fighter)
        {
            var bound = fighter.worldBound;
            if (float.IsNaN(bound.width) || bound.width <= 0f) return;
            var topLeft = _root.WorldToLocal(new Vector2(bound.xMin, bound.yMin));
            var bottomRight = _root.WorldToLocal(new Vector2(bound.xMax, bound.yMax));
            var room = _root.resolvedStyle.width - bottomRight.x;
            var cardWidth = _card.resolvedStyle.width;
            var needed = float.IsNaN(cardWidth) ? 0f : cardWidth;
            if (room >= needed)
            {
                _card.style.left = bottomRight.x;
                _card.style.right = StyleKeyword.Auto;
            }
            else
            {
                _card.style.right = _root.resolvedStyle.width - topLeft.x;
                _card.style.left = StyleKeyword.Auto;
            }
            _card.style.top = Mathf.Max(0f, topLeft.y);
        }
    }
}
