using UnityEngine;

namespace Turnaround.Interaction
{
    // Host-side: something heavy that a hard hit can nudge (vehicles, carts). Impulse in N*s, so mass matters.
    public interface IShovable
    {
        void ServerShove(Vector3 impulse);
    }
}
