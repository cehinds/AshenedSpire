using System.Collections.Generic;
using Ashen.App.Settings;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Run
{
    /// <summary>
    /// What a climb screen reads of a run, whichever ruleset plays it (docs/design/11 WP-C1b, D-310): the 405 <see cref="RunSession"/> and the Core 2 <see cref="Core2RunSession"/>
    /// both answer it, so the run HUD, the act map, the pause menu and the run end are built once. Display facts and the few controls those screens press; nothing of a fight.
    /// </summary>
    public interface IClimb
    {
        RunContent Content { get; }

        /// <summary>The run document (a copy).</summary>
        JObject Run { get; }

        /// <summary>The registries the run's cards, statuses and enemies are read from (a Core 2 run reads its profile's, not the 405 content's).</summary>
        Ashen.Domain.Combat.CombatData CombatData { get; }

        string Name { get; }
        string Portrait { get; }
        string ClassId { get; }
        string SeedText { get; }
        int SlotIndex { get; }
        int Act { get; }
        int Floor { get; }

        /// <summary>The seat climbed at the current tier, and its region (backgrounds are chosen by region).</summary>
        string SeatId { get; }
        string Region { get; }

        /// <summary>Where the run stands: 'combat', 'map', ... 'runEnd' (rules/runFlow.json screens).</summary>
        string Location { get; }

        bool IsInCombat { get; }
        bool RunOver { get; }
        bool Abandoned { get; }
        long PlaytimeSeconds { get; }

        /// <summary>The close-out of a finished run (the result record and what it earned), or null.</summary>
        Ashen.Domain.Loop.RunEndReceipt End { get; }

        /// <summary>The player's settings over the run's profile (settings/defaults.json).</summary>
        SettingsSession PlayerSettings { get; }

        /// <summary>The classes the run's close-out opened (a Core 2 profile opens one per completed run); empty on the 405 run.</summary>
        IReadOnlyList<string> ClassesUnlocked { get; }

        /// <summary>The prologue is queued before the first step (the 405 run's; a Core 2 run has none).</summary>
        bool ProloguePending { get; }

        /// <summary>The pool ('normal', 'elite', 'boss') of the encounter fought last or now, or null (the music follows it).</summary>
        string EncounterPool { get; }

        /// <summary>The map reveal the run's rules pin (fog or path), or null when the player's Map reveal setting rules: a Core 2 profile with configured routes pins its own.</summary>
        string PinnedMapMode { get; }

        IReadOnlyList<string> ReachableNodes();

        /// <summary>The names of the last fight's foes (the run end names the killer), empty when the run did not end in a fight.</summary>
        IReadOnlyList<string> LastFoes();

        void Save();

        Ashen.Domain.Loop.RunEndReceipt Abandon();
    }
}
