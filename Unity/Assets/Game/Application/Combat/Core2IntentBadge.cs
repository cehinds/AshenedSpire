using System;
using System.Collections.Generic;
using System.Globalization;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using IK = Ashen.Generated.IntentKeys;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.App.Combat
{
    /// <summary>One symbol of an intent badge: its icon, its number (null for the bare symbol) and the line that says what the number counts ('Total 0–16 HP').</summary>
    public sealed class Core2IntentPart
    {
        public string Icon;
        public string Value;
        public string Summary;
    }

    /// <summary>
    /// The badge an enemy's read move wears (P-1, D-1320..): the web's <c>combatIntentPresentation</c> (ui/models/CombatIntentModel.js) of the projected intent. Its title is the stance
    /// family ('Attacking'), its parts the symbols with the HP the move can take ('attack 0–16' with 'Total 0–16 HP'), its damage class the tint (physical or magic) and its tone green
    /// when the player's stance lowers the range shown. Engine-free; built from the projection <see cref="CoreIntentProjection.Project"/> makes.
    /// </summary>
    public sealed class Core2IntentBadge
    {
        public string Visibility;
        public string Title;
        public string Stance;
        public string DamageClass;
        public string Tone;
        public List<Core2IntentPart> Parts = new List<Core2IntentPart>();

        /// <summary>The line under the number (the first part that has one), or null.</summary>
        public string Summary
        {
            get
            {
                foreach (var part in Parts) if (part.Summary != null) return part.Summary;
                return null;
            }
        }

        /// <summary>The number the badge shows beside its symbol (the first part that has one), or null.</summary>
        public string Value
        {
            get
            {
                foreach (var part in Parts) if (part.Value != null) return part.Value;
                return null;
            }
        }

        private static string Title_(StringTable strings, string family)
        {
            switch (family)
            {
                case "attacking": return strings.Get(StringKeys.CombatIntentTitleAttacking);
                case "smashing": return strings.Get(StringKeys.CombatIntentTitleSmashing);
                case "defending": return strings.Get(StringKeys.CombatIntentTitleDefending);
                case "countering": return strings.Get(StringKeys.CombatIntentTitleCountering);
                case "evading": return strings.Get(StringKeys.CombatIntentTitleEvading);
                case "casting": return strings.Get(StringKeys.CombatIntentTitleCasting);
                case "buffing": return strings.Get(StringKeys.CombatIntentTitleBuffing);
                default: return strings.Get(StringKeys.CombatIntentTitlePreparing);
            }
        }

        private static string Part(JToken value) => Js.Nullish(value) ? null : Js.IsNum(value) ? CoreIntentProjection.Number(Js.D(value)) : Js.Str(value);

        private static bool Spell(string camp) => camp == IK.CampSpell || camp == IK.CampSpells || camp == IK.CampMagic;

        /// <summary>combatIntentPresentation(intent) for a move the player has read; the unknown mark for one that carries no kind.</summary>
        public static Core2IntentBadge Of(JObject intent, StringTable strings)
        {
            if (intent == null) return null;
            var kind = intent.Str(K.Kind);
            var profile = intent.Obj(IK.Profile);
            var stance = profile?.Str(CK.Maneuver) == IK.ManeuverEvade && !intent.Is(IK.Hidden) ? IK.StanceEvading : CoreIntentProjection.Stance(intent, profile ?? new JObject());
            var unknown = (string.IsNullOrEmpty(kind) || kind == V.IntentUnknown || intent[K.MoveId]?.Type == JTokenType.Null) && kind != V.IntentStaggered;
            if (unknown) return new Core2IntentBadge { Visibility = IK.StanceUnknown, Title = IK.UnknownMark, Stance = IK.StanceUnknown };
            // Interrupted turns retain their status in the tooltip; their badge simply reports that the enemy is preparing, rather than claiming an attack.
            if (kind == V.IntentStaggered)
                return new Core2IntentBadge
                {
                    Visibility = IK.StancePartial, Title = strings.Format(StringKeys.CombatIntentPartial, new StringArgs().Add(IK.Stance, Title_(strings, IK.StancePreparing))),
                    Stance = IK.StancePreparing, Parts = { new Core2IntentPart { Icon = IK.IconPrepare } },
                };
            var family = kind == IK.KindBuff ? IK.StanceBuffing : stance == IK.StanceRanged || stance == IK.StanceSweeping ? IK.StanceAttacking
                : stance == IK.StancePreparing ? (kind == IK.KindBuff ? IK.StanceBuffing : IK.StanceCasting) : stance;
            var read = CoreIntentProjection.Read(intent);
            var damage = read?.Label ?? (Js.Nullish(intent[K.Damage]) ? null : Part(intent[K.Damage]));
            var counterDamage = intent.Or0(IK.CounterDamage);
            var counterPoise = intent.Or0(IK.CounterPoiseDamage);
            var parts = new List<Core2IntentPart>();
            if (family == IK.StanceCountering)
            {
                parts.Add(new Core2IntentPart { Icon = IK.IconCounter, Value = Part(intent[K.Block]) });
                if (!(counterPoise > 0) || counterDamage > 0) parts.Add(new Core2IntentPart { Icon = IK.IconAttack, Value = Part(intent[IK.CounterDamage]) });
                if (counterPoise > 0) parts.Add(new Core2IntentPart { Icon = IK.IconSmash, Value = Part(intent[IK.CounterPoiseDamage]) });
            }
            else if (family == IK.StanceEvading) parts.Add(new Core2IntentPart { Icon = IK.IconEvade, Value = Part(!Js.Nullish(intent[IK.DodgeCharges]) ? intent[IK.DodgeCharges] : intent[IK.EvadeCharges]) });
            else if (family == IK.StanceDefending) parts.Add(new Core2IntentPart { Icon = IK.IconDefend, Value = Part(intent[K.Block]) });
            else if (family == IK.StanceSmashing) parts.Add(new Core2IntentPart { Icon = IK.IconSmash, Value = damage });
            else if (family == IK.StanceAttacking) parts.Add(new Core2IntentPart { Icon = IK.IconAttack, Value = damage });
            else parts.Add(new Core2IntentPart { Icon = family == IK.StanceBuffing ? IK.IconBuff : IK.IconCast, Value = family == IK.StanceBuffing ? null : damage });
            string summary = null;
            if (read != null)
            {
                var basis = strings.Get(read.Hp ? StringKeys.CombatIntentDamageHp : StringKeys.CombatIntentDamageBeforeBlock);
                summary = read.ContactIndex > 0
                    ? strings.Format(StringKeys.CombatIntentDamageContact, new StringArgs().Add(IK.Index, read.ContactIndex).Add(IK.Count, read.ContactCount).Add(IK.Amount, read.Amount).Add(IK.Basis, basis))
                    : strings.Format(StringKeys.CombatIntentDamageTotal, new StringArgs().Add(IK.Amount, read.Amount).Add(IK.Basis, basis));
                foreach (var part in parts) if (part.Value == damage) part.Summary = summary;
            }
            return new Core2IntentBadge
            {
                Visibility = IK.StanceKnown, Title = Title_(strings, family), Stance = family, Parts = parts,
                DamageClass = Spell(profile?.Str(CK.Camp)) ? IK.ClassMagic : family == IK.StanceAttacking || family == IK.StanceSmashing ? IK.ClassPhysical : null,
                Tone = intent.Obj(IK.ProjectedDamage)?.Str(IK.Tone),
            };
        }
    }
}
