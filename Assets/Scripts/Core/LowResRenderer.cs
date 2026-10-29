using UnityEngine;

namespace Turnaround.Core
{
    // Renders the camera into a tiny point-filtered RenderTexture and stretches it over the screen.
    // UI is drawn afterwards (OnGUI with a lower depth) at native resolution so text stays readable.
    [RequireComponent(typeof(Camera))]
    public class LowResRenderer : MonoBehaviour
    {
        public int width = 426;
        public int height = 240;

        static Camera clearCamera;
        Camera cam;
        RenderTexture rt;

        void OnEnable()
        {
            cam = GetComponent<Camera>();
            rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Point,
                antiAliasing = 1,
                useMipMap = false,
                name = $"PS1 Frame {width}x{height}"
            };
            rt.Create();
            cam.targetTexture = rt;
            cam.aspect = (float)width / height;
            // Something must render to the backbuffer, otherwise it never gets cleared.
            if (clearCamera == null)
            {
                clearCamera = new GameObject("ScreenClearCamera").AddComponent<Camera>();
                clearCamera.cullingMask = 0;
                clearCamera.clearFlags = CameraClearFlags.SolidColor;
                clearCamera.backgroundColor = Color.black;
                clearCamera.depth = -100;
            }
        }

        void OnDisable()
        {
            cam.targetTexture = null;
            if (rt != null)
            {
                rt.Release();
                Destroy(rt);
                rt = null;
            }
        }

        void OnGUI()
        {
            GUI.depth = 100;
            if (!cam.enabled || rt == null || Event.current.type != EventType.Repaint) return;
            float scale = Mathf.Min((float)Screen.width / width, (float)Screen.height / height);
            float drawWidth = Mathf.Round(width * scale);
            float drawHeight = Mathf.Round(height * scale);
            var rect = new Rect(Mathf.Floor((Screen.width - drawWidth) * 0.5f),
                Mathf.Floor((Screen.height - drawHeight) * 0.5f), drawWidth, drawHeight);
            GUI.DrawTexture(rect, rt, ScaleMode.StretchToFill, false);
        }
    }
}
