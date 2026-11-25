using UnityEngine;

namespace SpatialSoundHunt
{
    /// <summary>
    /// Generates all audio clips at runtime so the project has no external audio dependency.
    /// The beacon is a short harmonic tone burst mixed with low-passed noise: broadband content
    /// is easier to localise than a pure sine.
    /// </summary>
    public static class ProceduralAudio
    {
        public const int SampleRate = 44100;

        /// <summary>Looping, pulsing localisation beacon (silent gap after each burst).</summary>
        public static AudioClip CreateBeacon(string clipName, float frequency, float noiseMix, int seed, float periodSeconds = 0.85f)
        {
            int total = Mathf.CeilToInt(periodSeconds * SampleRate);
            int burst = Mathf.Min(total, Mathf.CeilToInt(0.24f * SampleRate));
            float[] data = new float[total];
            System.Random rng = new System.Random(seed);
            float lowpass = 0f;
            noiseMix = Mathf.Clamp01(noiseMix);

            for (int i = 0; i < burst; i++)
            {
                float u = (float)i / burst;
                float window = Mathf.Sin(Mathf.PI * u);
                window *= window; // sin^2 window -> starts and ends at exactly 0 (click-free loop)

                float t = (float)i / SampleRate;
                float tone = Mathf.Sin(2f * Mathf.PI * frequency * t)
                             + 0.5f * Mathf.Sin(2f * Mathf.PI * frequency * 2f * t)
                             + 0.25f * Mathf.Sin(2f * Mathf.PI * frequency * 3f * t);
                tone /= 1.75f;

                float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                lowpass += 0.45f * (white - lowpass);

                data[i] = window * ((1f - noiseMix) * tone + noiseMix * lowpass * 1.5f);
            }

            Normalize(data, 0.85f);
            return Build(clipName, data);
        }

        /// <summary>Ascending arpeggio played when the correct target is found.</summary>
        public static AudioClip CreateSuccess()
        {
            float[] data = new float[Mathf.CeilToInt(0.9f * SampleRate)];
            AddTone(data, 0.00f, 0.35f, 523.25f, 0.45f);
            AddTone(data, 0.09f, 0.35f, 659.25f, 0.45f);
            AddTone(data, 0.18f, 0.35f, 783.99f, 0.45f);
            AddTone(data, 0.27f, 0.55f, 1046.50f, 0.45f);
            Normalize(data, 0.8f);
            return Build("Success", data);
        }

        /// <summary>Low dissonant buzz for a wrong selection.</summary>
        public static AudioClip CreateError()
        {
            float[] data = new float[Mathf.CeilToInt(0.35f * SampleRate)];
            AddTone(data, 0.00f, 0.30f, 140f, 0.5f, 0.9f);
            AddTone(data, 0.00f, 0.30f, 147f, 0.5f, 0.9f);
            Normalize(data, 0.7f);
            return Build("Error", data);
        }

        /// <summary>Two rising notes when the experiment starts.</summary>
        public static AudioClip CreateStart()
        {
            float[] data = new float[Mathf.CeilToInt(0.55f * SampleRate)];
            AddTone(data, 0.00f, 0.25f, 440f, 0.5f);
            AddTone(data, 0.15f, 0.35f, 660f, 0.5f);
            Normalize(data, 0.7f);
            return Build("Start", data);
        }

        /// <summary>Longer fanfare when all targets have been found.</summary>
        public static AudioClip CreateComplete()
        {
            float[] data = new float[Mathf.CeilToInt(1.6f * SampleRate)];
            AddTone(data, 0.00f, 0.50f, 523.25f, 0.35f);
            AddTone(data, 0.15f, 0.50f, 659.25f, 0.35f);
            AddTone(data, 0.30f, 0.50f, 783.99f, 0.35f);
            AddTone(data, 0.45f, 0.90f, 1046.50f, 0.35f);
            AddTone(data, 0.45f, 1.00f, 659.25f, 0.25f);
            AddTone(data, 0.45f, 1.10f, 783.99f, 0.25f);
            Normalize(data, 0.8f);
            return Build("Complete", data);
        }

        /// <summary>Very short tick played once when the participant comes close to the active target.</summary>
        public static AudioClip CreateProximityTick()
        {
            float[] data = new float[Mathf.CeilToInt(0.12f * SampleRate)];
            AddTone(data, 0.00f, 0.10f, 1320f, 0.6f, 0.2f);
            Normalize(data, 0.7f);
            return Build("ProximityTick", data);
        }

        static void AddTone(float[] data, float startSeconds, float durationSeconds, float frequency, float amplitude, float secondHarmonic = 0.3f)
        {
            int start = Mathf.RoundToInt(startSeconds * SampleRate);
            int count = Mathf.RoundToInt(durationSeconds * SampleRate);
            for (int i = 0; i < count && start + i < data.Length; i++)
            {
                float t = (float)i / SampleRate;
                float u = (float)i / count;
                float attack = Mathf.Clamp01(t / 0.008f);
                float decay = Mathf.Exp(-4f * u);
                float release = Mathf.Clamp01((1f - u) / 0.1f);
                float envelope = attack * decay * release;
                float sample = Mathf.Sin(2f * Mathf.PI * frequency * t)
                               + secondHarmonic * Mathf.Sin(4f * Mathf.PI * frequency * t);
                data[start + i] += amplitude * envelope * sample;
            }
        }

        static void Normalize(float[] data, float peak)
        {
            float max = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float a = Mathf.Abs(data[i]);
                if (a > max) max = a;
            }

            if (max < 1e-5f) return;
            float scale = peak / max;
            for (int i = 0; i < data.Length; i++)
                data[i] *= scale;
        }

        static AudioClip Build(string clipName, float[] data)
        {
            AudioClip clip = AudioClip.Create(clipName, data.Length, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
