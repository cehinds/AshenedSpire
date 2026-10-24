using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using TK = Ashen.Generated.Core2TallyKeys;
using SV = Ashen.Generated.EventScreenValues;
using TV = Ashen.Generated.Core2TallyValues;
using V = Ashen.Generated.CombatValues;

namespace Ashen.App.Run
{
    /// <summary>The blow that last cost the player hit points, as the web's game over screen names it ("Slain by Ash Warden · Cleave for 14").</summary>
    public sealed class Core2LastHit
    {
        /// <summary>The striker's name, or the cause in words when no enemy struck (a status tick); null when neither is known.</summary>
        public string SourceName;

        /// <summary>The move the striker had started, in words; null for a blow without one.</summary>
        public string Move;

        public double? Amount;
    }

    /// <summary>
    /// The tallies a fight keeps on the run in the web (ui/screens/combat.js trackStats: run.stats.damageDealt, damageTaken and lastHit), which the run end shows and the web writes to the
    /// record. The configured run's own <c>stats</c> stay as the web's loop oracle holds them (no tally), so the session keeps these beside the run in the save (D-962). A new fight
    /// forgets the last blow (the line on the defeat screen comes from this fight or names no striker). Engine-free.
    /// </summary>
    public sealed class Core2RunTally
    {
        private readonly Dictionary<string, string> _moves = new Dictionary<string, string>(StringComparer.Ordinal);
        private string _lastStrike;

        public double DamageDealt { get; private set; }

        public double DamageTaken { get; private set; }

        public Core2LastHit LastHit { get; private set; }

        /// <summary>A fight begins: the last blow, the moves started and the last striker are the new fight's.</summary>
        public void BeginFight()
        {
            LastHit = null;
            _moves.Clear();
            _lastStrike = null;
        }

        /// <summary>trackStats(events): reads the events a command produced, in order. <paramref name="enemies"/> names the strikers.</summary>
        public void Observe(IEnumerable<JObject> events, IReadOnlyList<JObject> enemies)
        {
            foreach (var e in events)
            {
                var type = e.Str(K.Type);
                var source = e.Str(K.SourceId);
                var target = e.Str(K.TargetId);
                if (type == E.DamageDealt && source == V.Player) DamageDealt += e.Num(K.Amount);
                if (type == E.EnemyMoveStarted && !string.IsNullOrEmpty(source)) _moves[source] = e.Str(K.MoveId);
                if (type == E.DamageDealt && target == V.Player && !string.IsNullOrEmpty(source) && source != V.Player) _lastStrike = source;
                if (type != E.HpLost || target != V.Player) continue;
                var amount = e.Num(K.Amount);
                DamageTaken += amount;
                var cause = e.Str(K.Cause) ?? string.Empty;
                var strikerId = cause == TV.CauseAttack ? _lastStrike : null;
                var striker = strikerId == null ? null : FindEnemy(enemies, strikerId);
                string moveId = null;
                if (strikerId != null) _moves.TryGetValue(strikerId, out moveId);
                if (cause.StartsWith(TV.ProcPrefix, StringComparison.Ordinal)) cause = cause.Substring(TV.ProcPrefix.Length);
                LastHit = new Core2LastHit
                {
                    SourceName = striker?.Str(K.Name) ?? (strikerId != null ? Words(strikerId) : cause.Length > 0 && cause != TV.CauseEffect ? Words(cause) : null),
                    Move = string.IsNullOrEmpty(moveId) ? null : Words(moveId),
                    Amount = amount,
                };
            }
        }

        private static JObject FindEnemy(IReadOnlyList<JObject> enemies, string id)
        {
            if (enemies == null) return null;
            for (var i = 0; i < enemies.Count; i++) if (enemies[i].Str(K.Id) == id) return enemies[i];
            return null;
        }

        /// <summary>The web's words(): an id in camelCase or with separators as spaced words with the first letter capitalised ("ashStrike" is "Ash Strike", "cinder_fall" is "Cinder fall").</summary>
        public static string Words(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var s = Regex.Replace(Regex.Replace(value, SV.CamelPattern, SV.CamelReplacement), SV.SeparatorPattern, SV.Space);
            return char.ToUpper(s[0], CultureInfo.InvariantCulture) + s.Substring(1);
        }

        public JObject ToJson() => new JObject
        {
            [TK.DamageDealt] = DamageDealt,
            [TK.DamageTaken] = DamageTaken,
            [TK.LastHit] = LastHit == null ? JValue.CreateNull() : new JObject { [TK.SourceName] = LastHit.SourceName, [TK.Move] = LastHit.Move, [TK.Amount] = LastHit.Amount },
        };

        public static Core2RunTally FromJson(JToken json)
        {
            var tally = new Core2RunTally();
            if (!(json is JObject o)) return tally;
            tally.DamageDealt = (double?)o[TK.DamageDealt] ?? 0d;
            tally.DamageTaken = (double?)o[TK.DamageTaken] ?? 0d;
            if (o[TK.LastHit] is JObject hit)
                tally.LastHit = new Core2LastHit { SourceName = (string)hit[TK.SourceName], Move = (string)hit[TK.Move], Amount = (double?)hit[TK.Amount] };
            return tally;
        }
    }
}
