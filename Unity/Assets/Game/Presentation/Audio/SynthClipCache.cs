using System.Collections.Generic;
using Ashen.App.Audio;
using UnityEngine;

namespace Ashen.Presentation.Audio
{
    /// <summary>
    /// Wraps engine-free synth PCM in Unity AudioClips, cached by resolved recipe id (PF-12). Rendering can run off
    /// the main thread (SynthRenderer is pure); only AudioClip.Create/SetData must run here on the main thread.
    /// </summary>
    public sealed class SynthClipCache
    {
        private readonly SynthRenderer _renderer;
        private readonly int _sampleRate;
        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();

        public SynthClipCache(SynthRenderer renderer, int sampleRate)
        {
            _renderer = renderer;
            _sampleRate = sampleRate;
        }

        public AudioClip Get(string recipeId)
        {
            var id = _renderer.Resolve(recipeId);
            if (_clips.TryGetValue(id, out var clip)) return clip;
            var pcm = _renderer.Render(id);
            clip = AudioClip.Create(id, pcm.Length, 1, _sampleRate, false);
            clip.SetData(pcm, 0);
            return _clips[id] = clip;
        }
    }
}
