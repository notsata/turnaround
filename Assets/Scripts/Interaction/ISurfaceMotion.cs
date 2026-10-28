using UnityEngine;

namespace Turnaround.Interaction
{
    // A surface that carries whatever stands on it (belt loaders, the bag room conveyor).
    public interface ISurfaceMotion
    {
        Vector3 SurfaceVelocity { get; }
    }
}
