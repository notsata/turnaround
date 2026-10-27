using UnityEngine;

namespace Turnaround.Core
{
    // World-text materials copy the font texture; dynamic fonts rebuild it at runtime, so keep them pointed at it.
    public static class WorldTextFix
    {
        [RuntimeInitializeOnLoadMethod]
        static void Hook()
        {
            Font.textureRebuilt += font =>
            {
                foreach (var tm in Object.FindObjectsByType<TextMesh>(FindObjectsSortMode.None))
                    if (tm.font == font) tm.GetComponent<Renderer>().sharedMaterial.mainTexture = font.material.mainTexture;
            };
        }
    }
}
