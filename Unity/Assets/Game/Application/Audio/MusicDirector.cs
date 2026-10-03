using System;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using MK = Ashen.Generated.MusicKeys;

namespace Ashen.App.Audio
{
    /// <summary>What should play: a context, its generated file (when its slot has one), else its procedural bed and variant, or silence.</summary>
    public sealed class MusicChoice
    {
        public string Context;
        public string File;
        public string Bed;
        public int Variant;
        public bool Silence;

        public bool SameAs(MusicChoice other) => other != null && other.Context == Context && other.Variant == Variant;
    }

    /// <summary>
    /// Music selection (US-16.1): where the run stands → an audio context (audio/beds.json locations; a fight's
    /// encounter pool may override it; the run end by outcome), then the context's file slot, else its bed in
    /// audio/music.json, else silence. The bed variant is picked by the act so a climb's acts sound apart. Stingers
    /// are named by event. Engine-free: the Unity player streams what <see cref="BedRenderer"/> renders.
    /// </summary>
    public sealed class MusicDirector
    {
        private readonly JObject _beds;
        private readonly JObject _contexts;
        private readonly JObject _music;
        private readonly string _silence;

        public MusicDirector(JObject beds, JObject contexts, JObject music, string silenceWord)
        {
            _beds = beds ?? new JObject();
            _contexts = contexts ?? new JObject();
            _music = music ?? new JObject();
            _silence = silenceWord;
        }

        public static MusicDirector From(IContentSource source)
        {
            JObject Read(string path) => (JObject)JsonContent.Parse(source.ReadText(path));
            return new MusicDirector(Read(ContentFiles.AudioBeds), Read(ContentFiles.AudioContexts), Read(ContentFiles.AudioMusic),
                (string)Read(ContentFiles.AudioSynth)[SynthKeys.SilenceWord]);
        }

        /// <summary>The context for a run location (and the fight's encounter pool), or a named screen such as the title.</summary>
        public string ContextFor(string location, string encounterPool = null)
        {
            var pool = encounterPool == null ? null : (string)_beds.Obj(MK.Pools)?[encounterPool];
            if (location == RunFlowValues.LocationCombat && pool != null) return pool;
            return location == null ? null : (string)_beds.Obj(MK.Locations)?[location];
        }

        /// <summary>The run end's context: the victory bed, or (in defeat) the quiet one the stinger plays over.</summary>
        public string RunEndContext(bool victory) => (string)_beds.Obj(MK.RunEnd)?[victory ? MK.Victory : MK.Defeat];

        /// <summary>A stinger's slot (audio/contexts.json stingers) by event name: its file, else its synth recipe id.</summary>
        public (string File, string Recipe) Stinger(string eventName)
        {
            var id = (string)_beds.Obj(MK.Stingers)?[eventName ?? string.Empty];
            var slot = id == null ? null : _contexts.Obj(MK.Stingers)?.Obj(id);
            return slot == null ? (null, null) : ((string)slot[MK.File], (string)slot[MK.Recipe]);
        }

        /// <summary>The context's file, else its bed and a variant (by <paramref name="salt"/>, e.g. the act), else silence.</summary>
        public MusicChoice Choose(string context, int salt = 0)
        {
            var choice = new MusicChoice { Context = context };
            var slot = context == null ? null : _contexts.Obj(MK.Contexts)?.Obj(context);
            if (slot == null)
            {
                choice.Silence = true;
                return choice;
            }
            var file = (string)slot[MK.File];
            if (!string.IsNullOrEmpty(file))
            {
                choice.File = file;
                return choice;
            }
            var bedId = (string)slot[MK.Bed];
            var bed = bedId == null || bedId == _silence ? null : _music.Obj(MK.Beds)?[bedId] as JObject;
            if (bed == null)
            {
                choice.Silence = true;
                return choice;
            }
            choice.Bed = bedId;
            var variants = bed.Arr(MK.Variants)?.Count ?? 0;
            choice.Variant = variants == 0 ? 0 : ((salt % variants) + variants) % variants;
            return choice;
        }
    }

    /// <summary>
    /// Renders one loop of a procedural bed (audio/music.json BEDS × SCALES, shaped by audio/beds.json render) to mono
    /// float PCM: melody notes on the variant's cadence (a seeded walk over the scale), an optional root drone an
    /// octave down and an optional pulse tick on every beat, all scaled by the bed's gain. Deterministic. Engine-free.
    /// </summary>
    public sealed class BedRenderer
    {
        private readonly JObject _music;
        private readonly JObject _render;
        private readonly int _sampleRate;

        public BedRenderer(JObject music, JObject beds, int sampleRate)
        {
            _music = music;
            _render = beds.Obj(MK.Render) ?? new JObject();
            _sampleRate = sampleRate;
        }

        private double R(string key) => (double)_render[key];

        public float[] RenderLoop(string bedId, int variant)
        {
            var bed = (JObject)_music.Obj(MK.Beds)[bedId];
            var v = (JObject)bed.Arr(MK.Variants)[variant];
            var scale = _music.Obj(MK.Scales).Arr((string)v[MK.Scale]).Select(t => (double)t).ToArray();
            var root = (double)v[MK.Root];
            var beat = (double)v[MK.Cadence] / UiMath.MillisPerSecond;
            var notes = (int)R(MK.NotesPerLoop);
            var lift = Math.Max(1, Math.Min(scale.Length, (int)(double)v[MK.Lift]));
            var gain = (double)bed[MK.Gain];
            var wave = (string)v[MK.Wave];
            var octave = R(MK.SemitonesPerOctave);
            var beatSamples = (int)(beat * _sampleRate);
            var buf = new float[beatSamples * notes];

            var seed = Hash(bedId) ^ (uint)(variant + 1);
            var melodyRoot = root * Math.Pow(DspConstants.Two, R(MK.MelodyOctaveUp));
            for (var n = 0; n < notes; n++)
            {
                seed ^= seed << (int)NoiseAlgorithm.ShiftA;
                seed ^= seed >> (int)NoiseAlgorithm.ShiftB;
                seed ^= seed << (int)NoiseAlgorithm.ShiftC;
                var degree = scale[(int)(seed % (uint)lift)];
                var freq = melodyRoot * Math.Pow(DspConstants.Two, degree / octave);
                Tone(buf, n * beatSamples, (int)(beatSamples * R(MK.NoteLengthFraction)), freq, wave, gain * R(MK.NoteGain));
                if (bed.Is(MK.Pulse)) Tone(buf, n * beatSamples, (int)(R(MK.PulseSeconds) * _sampleRate), R(MK.PulseFrequency), wave, gain * R(MK.PulseGain));
            }
            if (bed.Is(MK.Drone))
            {
                var droneFreq = root / Math.Pow(DspConstants.Two, R(MK.DroneOctaveDown));
                var phase = 0.0;
                for (var i = 0; i < buf.Length; i++)
                {
                    phase += droneFreq / _sampleRate;
                    phase -= Math.Floor(phase);
                    buf[i] += (float)(Math.Sin(Math.PI * DspConstants.Two * phase) * gain * R(MK.DroneGain));
                }
            }
            return buf;
        }

        /// <summary>A note with a linear attack and release (so the loop's seams stay clickless).</summary>
        private void Tone(float[] buf, int start, int length, double freq, string wave, double peak)
        {
            var attack = Math.Max(1, (int)(R(MK.NoteAttackSeconds) * _sampleRate));
            var phase = 0.0;
            for (var i = 0; i < length && start + i < buf.Length; i++)
            {
                phase += freq / _sampleRate;
                phase -= Math.Floor(phase);
                var env = Math.Min(1.0, Math.Min((double)i / attack, (double)(length - i) / attack));
                buf[start + i] += (float)(Oscillator(wave, phase) * env * peak);
            }
        }

        private static double Oscillator(string type, double phase)
        {
            switch (type)
            {
                case Waveforms.Square: return phase < DspConstants.Half ? 1.0 : -1.0;
                case Waveforms.Sawtooth: return DspConstants.Two * phase - 1.0;
                case Waveforms.Triangle: return 1.0 - DspConstants.Two * Math.Abs(DspConstants.Two * phase - 1.0);
                default: return Math.Sin(Math.PI * DspConstants.Two * phase);
            }
        }

        private static uint Hash(string text)
        {
            unchecked
            {
                var h = RngAlgorithm.FnvOffset;
                foreach (var c in text) { h ^= c; h *= RngAlgorithm.FnvPrime; }
                return h == 0 ? NoiseAlgorithm.Fallback : h;
            }
        }
    }
}
