using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Run
{
    /// <summary>One W-05 scene as the screen stages it: art for both layouts, the class item layer, the line and the controls.</summary>
    public sealed class PrologueViewState
    {
        public string SceneId;
        public string ArtWide;
        public string ArtNarrow;
        public string Item;
        public string Line;
        public string Continue;
        public bool IsLast;
        public string Skip;
        public string Pause;
        public string Progress;

        /// <summary>One dot per playable scene; true up to and including this one.</summary>
        public readonly List<bool> Dots = new List<bool>();

        public double HoldMs;
        public string Music;
        public bool Banner;
        public JObject Stage;
        public JObject Presentation;

        /// <summary>The scene's name ("Remembered warmth") and who speaks ("Hamlet witness"), or empty.</summary>
        public string Title;
        public string Speaker;
    }

}
