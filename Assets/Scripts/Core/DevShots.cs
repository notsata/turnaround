using System.Collections;
using System.IO;
using UnityEngine;

namespace Turnaround.Core
{
    // Dev/QA only, "-shots <folder>": renders a few fixed viewpoints to PNGs and quits, so the look of the airport can
    // be checked without anyone flying a camera around. Needs a real (windowed) run; batch mode has no GPU.
    public class DevShots : MonoBehaviour
    {
        static readonly (string name, Vector3 pos, Vector3 lookAt)[] Views =
        {
            ("overview", new Vector3(-180f, 140f, -120f), new Vector3(20f, 0f, 90f)),
            ("ramp", new Vector3(-6f, 2.2f, 10f), new Vector3(30f, 2f, 60f)),
            ("runway", new Vector3(-380f, 25f, 110f), new Vector3(100f, 0f, 165f)),
            ("tower", new Vector3(-60f, 8f, 25f), new Vector3(-108f, 25f, -14f)),
            ("aircraft", new Vector3(-55f, 6f, 70f), new Vector3(-70f, 2.5f, 85f)),
            ("gate", new Vector3(32f, 4f, 40f), new Vector3(12f, 2f, 20f)),
            ("lobby", new Vector3(17.5f, 2.2f, -20.5f), new Vector3(27f, 1.2f, -7f)),
            ("lobbyout", new Vector3(24f, 1.7f, -12f), new Vector3(24f, 3f, 30f)),
            ("conveyor", new Vector3(-22f, 3f, 16f), new Vector3(-28f, 0.8f, 2f)),
        };

        public string folder;

        IEnumerator Start()
        {
            Directory.CreateDirectory(folder);
            yield return new WaitForSeconds(1.5f); // let the scene settle (fonts, physics)
            var go = new GameObject("ShotCamera");
            var cam = go.AddComponent<Camera>();
            var main = Object.FindFirstObjectByType<LowResRenderer>()?.GetComponent<Camera>();
            if (main != null) cam.CopyFrom(main);
            cam.targetTexture = null;
            var rt = new RenderTexture(1280, 720, 24);
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            foreach (var (name, pos, lookAt) in Views)
            {
                go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(lookAt - pos));
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(folder, name + ".png"), tex.EncodeToPNG());
                Debug.Log("[SHOTS] " + name);
                yield return null;
            }
            Application.Quit();
        }
    }
}
