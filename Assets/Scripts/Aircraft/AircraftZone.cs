using Turnaround.Interaction;
using UnityEngine;

namespace Turnaround.Aircraft
{
    // A spot on the ramp, relative to the parked aircraft, where a chock or cone belongs. The marker
    // is only shown while the aircraft is on stand, so it doesn't slide across the taxiway with it.
    [RequireComponent(typeof(BoxCollider))]
    public class AircraftZone : MonoBehaviour
    {
        public CarryKind kind = CarryKind.Cone;
        public int required = 1;
        public Renderer marker;

        public BoxCollider Volume { get; private set; }

        void Awake()
        {
            Volume = GetComponent<BoxCollider>();
            Volume.isTrigger = true;
        }
    }
}
