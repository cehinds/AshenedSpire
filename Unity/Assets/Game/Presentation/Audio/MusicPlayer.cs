using System.Globalization;
using Ashen.App.Audio;
using Ashen.App.Run;
using Ashen.App.Settings;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Ashen.Presentation.UI;
using UnityEngine;
using MK = Ashen.Generated.MusicKeys;

namespace Ashen.Presentation.Audio
{
    /// <summary>
    /// Plays the music context the game stands in (US-16.1): each frame it asks <see cref="MusicDirector"/> what should
    /// play (the title, the run's location and fight pool, the run end) and, when that changes, renders the bed loop
    /// (<see cref="BedRenderer"/>) into a looping AudioClip. Silence stops the source. Volume is the master × music
    /// settings (D-150). Generated files would be resolved first; none ship yet, so the beds play (D-156).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MusicPlayer : MonoBehaviour
    {
        private UiHost _host;
        private MusicDirector _director;
        private BedRenderer _renderer;
        private AudioSource _source;
        private MusicChoice _playing;
        private int _sampleRate;

        public void Init(UiHost host, IContentSource source)
        {
            _host = host;
            _director = MusicDirector.From(source);
            var synth = (Newtonsoft.Json.Linq.JObject)JsonContent.Parse(source.ReadText(ContentFiles.AudioSynth));
            _sampleRate = (int)synth[MK.SampleRate];
            _renderer = new BedRenderer((Newtonsoft.Json.Linq.JObject)JsonContent.Parse(source.ReadText(ContentFiles.AudioMusic)),
                (Newtonsoft.Json.Linq.JObject)JsonContent.Parse(source.ReadText(ContentFiles.AudioBeds)), _sampleRate);
            _source = gameObject.AddComponent<AudioSource>();
            _source.loop = true;
            _source.playOnAwake = false;
        }

        private void Update()
        {
            if (_host == null || _director == null) return;
            var ui = _host.Context;
            var session = ui.Session;
            string context;
            if (session == null || _host.Navigator.CurrentId == ScreenIds.Title) context = _director.ContextFor(MK.Title);
            else if (session.RunOver) context = _director.RunEndContext(session.End?.Victory ?? false);
            else if (session.ProloguePending) context = _director.ContextFor(MK.Prologue);
            else
            {
                string pool = null;
                if (session.EncounterId != null && session.Content.Data.Encounters.Has(session.EncounterId))
                    pool = session.Content.Data.Encounters.Get(session.EncounterId).Str(MK.Pool);
                context = _director.ContextFor(session.Location, pool);
            }
            var choice = _director.Choose(context, session?.Act ?? 0);
            if (choice.SameAs(_playing)) return;
            _playing = choice;
            _source.Stop();
            if (choice.Bed == null) return;
            var pcm = _renderer.RenderLoop(choice.Bed, choice.Variant);
            var clip = AudioClip.Create(string.Format(CultureInfo.InvariantCulture, MK.LoopName, choice.Bed, choice.Variant), pcm.Length, 1, _sampleRate, false);
            clip.SetData(pcm, 0);
            _source.clip = clip;
            _source.volume = Volume(ui);
            _source.Play();
        }

        private static float Volume(UiContext ui)
        {
            var settings = new SettingsSession(ui.RunContent, ProfileStore.Load(ui.Saves));
            return (float)(settings.Number(SettingIds.MasterVolume) / UiMath.Percent * settings.Number(SettingIds.MusicVolume) / UiMath.Percent);
        }
    }
}
