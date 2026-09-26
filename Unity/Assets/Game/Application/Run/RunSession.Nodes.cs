using System;
using Ashen.App.Combat;
using Ashen.App.Nodes;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using LK = Ashen.Generated.LoopKeys;
using LoopContext = Ashen.Domain.Loop.LoopContext;
using NodeOutcome = Ashen.Domain.Loop.NodeOutcome;
using RunEndReceipt = Ashen.Domain.Loop.RunEndReceipt;
using RK = Ashen.Generated.RunKeys;

namespace Ashen.App.Run
{
    /// <summary>
    /// The node screens' side of the run session (W-09 merchant, W-11 event and dialogue, W-13 legacy dungeon; D-141n).
    /// The session is their <see cref="INodeHost"/>: it resolves the shopSell setting, saves the run at the screen with its
    /// resume entry (payload 'node', location = the screen id) or at the act map, writes the profile when a node step
    /// changed it, starts the fight a node hands over on the run's own RNG, and closes the run out when leaving a cleared
    /// dungeon ended the climb. The router opens a node screen with <see cref="NodeContext"/> and
    /// <see cref="NodeScreens.For"/>; a loaded run resumes on <see cref="ResumeNode"/>.
    /// </summary>
    public sealed partial class RunSession : INodeHost
    {
        private NodeEntry _node;
        private string _profileSaved;
        private string _portraitClass;

        /// <summary>The node screen entry saved with the run (null outside the node screens).</summary>
        public NodeEntry Node => _node;

        /// <summary>The node screen a loaded or returning run resumes on (a fight and a pending reward come first), or null.</summary>
        public NodeEntry ResumeNode => IsInCombat || HasPendingReward || RunOver ? null : NodeScreens.Resume(_run, _node);

        /// <summary>
        /// The loop context a node session plays on: this run (by reference), its RNG, the profile and the settings. Hold one
        /// per screen; every node step writes through it.
        /// </summary>
        public LoopContext NodeContext()
        {
            var ctx = Context();
            _profileSaved ??= ContentSet.Canonical(Profile.Doc);
            _portraitClass ??= ClassId;
            return ctx;
        }

        /// <summary>settingOn(settings, 'shopSell'): the profile's stored setting, else the preset's player setting, else on (D-081).</summary>
        public bool SellOn
        {
            get
            {
                var setting = Profile.Settings[NodeKeys.ShopSell] ?? Content.Snapshot.PlayerSettings?[NodeKeys.ShopSell];
                return setting == null || setting.Type == JTokenType.Null || Js.Truthy(setting);
            }
        }

        /// <summary>Save the run at a node screen (the entry) or, with null, at the act map; the profile too when a step changed it.</summary>
        public void Checkpoint(NodeEntry entry)
        {
            if (RunOver) return;
            _node = entry;
            _location = entry?.Screen ?? RunFlowValues.LocationMap;
            if (_portraitClass != null && ClassId != _portraitClass)
            {
                Portrait = Content.Portrait(ClassId, _run.Obj(LK.Customization)?.Str(RunFlowKeys.Tint));
                _portraitClass = ClassId;
            }
            var profile = ContentSet.Canonical(Profile.Doc);
            if (profile != _profileSaved)
            {
                Profile.Save(Content.ContentHash);
                _profileSaved = profile;
            }
            Save();
        }

        /// <summary>A node closed the run out (a cleared dungeon left at the summit): the profile is written and the slot cleared (permadeath's door).</summary>
        public void RunEnded(RunEndReceipt end)
        {
            RunOver = true;
            _node = null;
            Profile.Save(Content.ContentHash);
            Saves.Delete(Slot);
        }

        /// <summary>
        /// Start the fight a node handed over (an event's startCombat, a dungeon room or response): the loop already entered it
        /// (RunLoop.EnterCombat on this run), so the fight begins from its createCombat arguments on the run's RNG and is
        /// checkpointed at once (location combat).
        /// </summary>
        public CombatSession StartFight(NodeOutcome fight)
        {
            if (fight?.Args == null) throw new ArgumentException(NodeMessages.NoFightArgs);
            EncounterId = fight.EncounterId;
            _rng ??= RunRng();
            Combat = CombatSession.Start(Content.Combat, _rng, fight.Args);
            FightFinished = false;
            LastOutcome = null;
            _node = null;
            Save();
            return Combat;
        }

        /// <summary>The region of the seat the run climbs now (the node screens' backgrounds), or null.</summary>
        public string Region()
        {
            if (!(_run[RK.SeatOrder] is JArray)) return EncounterId != null ? Content.RegionOf(EncounterId) : null;
            var seat = Ashen.Domain.Loop.RunLoop.CurrentSeat(Context());
            return seat != null && Content.Data.Seats.Has(seat) ? Content.Data.Seats.Get(seat).Str(RunFlowKeys.RegionId) : null;
        }
    }
}
