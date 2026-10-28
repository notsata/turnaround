using UnityEngine;

namespace Turnaround.Core
{
    // Sounds made from maths so there are no audio assets to ship.
    public static class ProceduralAudio
    {
        static AudioClip clang, thud;

        // A struck sheet of metal: a noise crack, then inharmonic ringing partials that die off at different rates.
        public static AudioClip Clang()
        {
            if (clang != null) return clang;
            const int rate = 22050, samples = rate * 9 / 10;
            float[] partials = { 523f, 1287f, 2141f, 3379f, 4817f };
            float[] decay = { 3.5f, 5f, 7f, 10f, 14f };
            var data = new float[samples];
            var rng = new System.Random(5);
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)rate;
                float v = t < 0.012f ? (float)(rng.NextDouble() * 2 - 1) * (1f - t / 0.012f) : 0f;
                for (int p = 0; p < partials.Length; p++) v += Mathf.Sin(2 * Mathf.PI * partials[p] * t) * Mathf.Exp(-decay[p] * t) * (0.5f / (p + 1));
                data[i] = Mathf.Clamp(v * 0.9f, -1f, 1f);
            }
            clang = AudioClip.Create("Clang", samples, 1, rate, false);
            clang.SetData(data, 0);
            return clang;
        }

        // Duller hit for whacking equipment and luggage.
        public static AudioClip Thud()
        {
            if (thud != null) return thud;
            const int rate = 22050, samples = rate / 4;
            var data = new float[samples];
            var rng = new System.Random(9);
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)rate;
                data[i] = (Mathf.Sin(2 * Mathf.PI * 95f * t) * 0.8f + (float)(rng.NextDouble() * 2 - 1) * 0.3f) * Mathf.Exp(-18f * t);
            }
            thud = AudioClip.Create("Thud", samples, 1, rate, false);
            thud.SetData(data, 0);
            return thud;
        }

        // Loud, 3D, fire-and-forget.
        public static void PlayAt(AudioClip clip, Vector3 position, float volume = 1f)
        {
            var go = new GameObject("OneShot_" + clip.name);
            go.transform.position = position;
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.volume = volume;
            src.spatialBlend = 1f;
            src.minDistance = 4f;
            src.maxDistance = 60f;
            src.pitch = Random.Range(0.92f, 1.08f);
            src.Play();
            Object.Destroy(go, clip.length + 0.1f);
        }
    }
}
