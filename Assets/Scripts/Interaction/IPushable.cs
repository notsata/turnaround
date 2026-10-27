using UnityEngine;

namespace Turnaround.Interaction
{
    // Things a walking player shoves by bumping into them. Implementations forward to the host.
    public interface IPushable
    {
        void Push(Vector3 velocity);
    }
}
