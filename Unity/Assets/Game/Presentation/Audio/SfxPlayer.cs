using Ashen.App.Audio;
using Ashen.App.Run;
using Ashen.App.Settings;
using Ashen.Content;
using Ashen.Generated;
using Ashen.Presentation.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Ashen.Presentation.Audio
{
    /// <summary>
    /// Plays the sound effects of each committed combat command (US-16.2): it follows the UI's run session, asks
    /// <see cref="SfxDirector"/> which recipes the command's events call for, and plays each through the synth clip cache
    /// at master × effects volume (D-163). Written without the editor (D-156).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SfxPlayer : MonoBehaviour
    {
        private UiHost _host;
        private SfxDirector _director;
        private SynthClipCache _clips;
        private AudioSource _source;
        private RunSession _watched;
        private Core2RunSession _watchedCore2;
        private float _volume = 1f;

        public void Init(UiHost host, IContentSource source)
        {
            _host = host;
            _director = SfxDirector.From(source);
            var synth = (JObject)JsonContent.Parse(source.ReadText(ContentFiles.AudioSynth));
            var recipes = (JObject)((JObject)JsonContent.Parse(source.ReadText(ContentFiles.AudioSfx)))[SynthKeys.Recipes];
            _clips = new SynthClipCache(new SynthRenderer(SynthParams.From(synth), recipes), (int)synth[MusicKeys.SampleRate]);
            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            SettingsSession.Changed += OnSettingsChanged;
        }

        private void Update()
        {
            var session = _host?.Context.Session;
            var core2 = _host?.Context.Core2Session;
            if (session == _watched && core2 == _watchedCore2) return;
            if (_watched != null) _watched.CommandCommitted -= OnCommand;
            if (_watchedCore2 != null) _watchedCore2.CommandCommitted -= OnCore2Command;
            _watched = session;
            _watchedCore2 = core2;
            if (_watched != null)
            {
                _watched.CommandCommitted += OnCommand;
                _volume = Volume(_watched.PlayerSettings);
            }
            if (_watchedCore2 != null)
            {
                _watchedCore2.CommandCommitted += OnCore2Command;
                _volume = Volume(_watchedCore2.PlayerSettings);
            }
        }

        private void OnDestroy()
        {
            SettingsSession.Changed -= OnSettingsChanged;
            if (_watched != null) _watched.CommandCommitted -= OnCommand;
            if (_watchedCore2 != null) _watchedCore2.CommandCommitted -= OnCore2Command;
        }

        private void OnSettingsChanged(SettingsSession settings, string key)
        {
            if (key == null || key == SettingIds.MuteAudio || key == SettingIds.SfxVolume) _volume = Volume(settings);
        }

        private void OnCommand(Ashen.App.Combat.CombatOutcome outcome)
        {
            if (outcome == null || !outcome.Accepted) return;
            foreach (var id in _director.For(outcome.Events)) _source.PlayOneShot(_clips.Get(id), _volume);
        }

        private void OnCore2Command(Ashen.App.Combat.Core2Outcome outcome)
        {
            if (outcome == null || !outcome.Accepted) return;
            foreach (var id in _director.For(outcome.Events)) _source.PlayOneShot(_clips.Get(id), _volume);
        }

        private static float Volume(SettingsSession settings) =>
            (float)settings.SfxLevel;
    }
}
