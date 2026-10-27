using UnityEngine;

namespace Turnaround.Core
{
    // Renders the camera into a tiny point-filtered RenderTexture and stretches it over the screen.
    // UI is drawn afterwards (OnGUI with a lower depth) at native resolution so text stays readable.
    [RequireComponent(typeof(Camera))]
    public class LowResRenderer : MonoBehaviour
    {
        public int width = 640;
        public int height = 360;

        static Camera clearCamera;
        Camera cam;
        RenderTexture rt;

        void OnEnable()
        {
            cam = GetComponent<Camera>();
            rt = new RenderTexture(width, height, 24) { filterMode = FilterMode.Point };
            cam.targetTexture = rt;
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
            Destroy(rt);
        }

        void OnGUI()
        {
            GUI.depth = 100;
            if (cam.enabled && Event.current.type == EventType.Repaint)
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), rt, ScaleMode.ScaleAndCrop, false);
        }
    }
}
