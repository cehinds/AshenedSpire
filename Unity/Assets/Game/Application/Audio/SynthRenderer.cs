using System;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Audio
{
    /// <summary>audio/synth.json: engine parameters for the procedural synth (US-16.2, PF-12).</summary>
    public sealed class SynthParams
    {
        public int SampleRate;
        public string ToneDefaultType;
        public double TonePeak, AttackMax, AttackFraction, ToneFloor, MinFrequency;
        public double NoisePeak, NoiseHighPass, NoiseLowPass, NoiseFloor;
        public string DefaultRecipe, FamilySeparator, SilenceWord;

        public static SynthParams From(JObject j)
        {
            var tone = (JObject)j[SynthKeys.Tone];
            var noise = (JObject)j[SynthKeys.Noise];
            var recipe = (JObject)j[SynthKeys.Recipe];
            return new SynthParams
            {
                SampleRate = (int)j[SynthKeys.SampleRate],
                ToneDefaultType = (string)tone[SynthKeys.DefaultType],
                TonePeak = (double)tone[SynthKeys.DefaultPeak],
                AttackMax = (double)tone[SynthKeys.AttackMax],
                AttackFraction = (double)tone[SynthKeys.AttackFraction],
                ToneFloor = (double)tone[SynthKeys.FloorGain],
                MinFrequency = (double)tone[SynthKeys.MinFrequency],
                NoisePeak = (double)noise[SynthKeys.DefaultPeak],
                NoiseHighPass = (double)noise[SynthKeys.DefaultHighPass],
                NoiseLowPass = (double)noise[SynthKeys.DefaultLowPass],
                NoiseFloor = (double)noise[SynthKeys.FloorGain],
                DefaultRecipe = (string)recipe[SynthKeys.DefaultId],
                FamilySeparator = (string)recipe[SynthKeys.FamilySeparator],
                SilenceWord = (string)j[SynthKeys.SilenceWord],
            };
        }
    }

    /// <summary>
    /// Renders sfx recipes (lists of tone/noise layers, audio/sfx.json) to mono float PCM, reproducing the shipped
    /// Web Audio shapes: tones glide exponentially freq→to and ramp their gain exponentially floor→peak (attack =
    /// min(attackMax, dur·attackFraction)) then peak→floor; noise is high-/low-pass filtered and ramps peak→floor.
    /// Noise uses its own seeded xorshift (never a domain RNG stream), so a recipe always renders the same samples.
    /// Engine-free: the Unity player only wraps the samples in an AudioClip.
    /// </summary>
    public sealed class SynthRenderer
    {
        private readonly SynthParams _p;
        private readonly JObject _recipes;

        public SynthRenderer(SynthParams parameters, JObject recipes)
        {
            _p = parameters;
            _recipes = recipes;
        }

        /// <summary>exact id → family (text before the separator) → default. Always returns an audible recipe id.</summary>
        public string Resolve(string id)
        {
            if (id != null && _recipes[id] != null) return id;
            var cut = id?.IndexOf(_p.FamilySeparator, StringComparison.Ordinal) ?? -1;
            if (cut > 0 && _recipes[id.Substring(0, cut)] != null) return id.Substring(0, cut);
            return _p.DefaultRecipe;
        }

        public float[] Render(string id)
        {
            var resolved = Resolve(id);
            var layers = (JArray)_recipes[resolved];
            var length = layers.OfType<JObject>().Max(l => Num(l, SynthKeys.T0, 0) + (double)l[SynthKeys.Dur]);
            var buffer = new float[(int)Math.Ceiling(length * _p.SampleRate)];
            var seed = Hash(resolved);
            foreach (var layer in layers.OfType<JObject>())
            {
                if ((string)layer[SynthKeys.Kind] == Waveforms.NoiseKind) RenderNoise(layer, buffer, ref seed);
                else RenderTone(layer, buffer);
            }
            return buffer;
        }

        private void RenderTone(JObject l, float[] buf)
        {
            var type = (string)l[SynthKeys.Type] ?? _p.ToneDefaultType;
            var f0 = Math.Max(_p.MinFrequency, (double)l[SynthKeys.Freq]);
            var f1 = Math.Max(_p.MinFrequency, Num(l, SynthKeys.To, f0));
            var dur = (double)l[SynthKeys.Dur];
            var peak = Num(l, SynthKeys.Peak, _p.TonePeak);
            var start = (int)(Num(l, SynthKeys.T0, 0) * _p.SampleRate);
            var n = (int)(dur * _p.SampleRate);
            var attack = Math.Min(_p.AttackMax, dur * _p.AttackFraction);
            var phase = 0.0;
            for (var i = 0; i < n && start + i < buf.Length; i++)
            {
                var t = (double)i / _p.SampleRate;
                var freq = f0 * Math.Pow(f1 / f0, t / dur);
                phase += freq / _p.SampleRate;
                phase -= Math.Floor(phase);
                buf[start + i] += (float)(Oscillator(type, phase) * Envelope(t, attack, dur, peak, _p.ToneFloor));
            }
        }

        private void RenderNoise(JObject l, float[] buf, ref uint seed)
        {
            var dur = (double)l[SynthKeys.Dur];
            var peak = Num(l, SynthKeys.Peak, _p.NoisePeak);
            var hp = Num(l, SynthKeys.HighPass, _p.NoiseHighPass);
            var lp = Num(l, SynthKeys.LowPass, _p.NoiseLowPass);
            var start = (int)(Num(l, SynthKeys.T0, 0) * _p.SampleRate);
            var n = (int)(dur * _p.SampleRate);
            var dt = 1.0 / _p.SampleRate;
            var twoPi = Math.PI * DspConstants.Two;
            var aLp = dt / (dt + 1.0 / (twoPi * lp));
            var rcHp = 1.0 / (twoPi * hp);
            var aHp = rcHp / (rcHp + dt);
            double lpOut = 0, hpOut = 0, prevIn = 0;
            for (var i = 0; i < n && start + i < buf.Length; i++)
            {
                seed ^= seed << (int)NoiseAlgorithm.ShiftA;
                seed ^= seed >> (int)NoiseAlgorithm.ShiftB;
                seed ^= seed << (int)NoiseAlgorithm.ShiftC;
                var white = (double)seed / uint.MaxValue * DspConstants.Two - 1.0;
                hpOut = aHp * (hpOut + white - prevIn);
                prevIn = white;
                lpOut += aLp * (hpOut - lpOut);
                var t = (double)i / _p.SampleRate;
                var gain = peak * Math.Pow(_p.NoiseFloor / peak, t / dur);
                buf[start + i] += (float)(lpOut * gain);
            }
        }

        /// <summary>Exponential floor→peak over the attack, then peak→floor until dur (Web Audio exponentialRamp).</summary>
        private static double Envelope(double t, double attack, double dur, double peak, double floor)
        {
            if (t < attack) return floor * Math.Pow(peak / floor, t / attack);
            return peak * Math.Pow(floor / peak, (t - attack) / Math.Max(dur - attack, double.Epsilon));
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

        private static double Num(JObject l, string key, double fallback) => l[key] == null || l[key].Type == JTokenType.Null ? fallback : (double)l[key];

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
