using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Aircraft
{
    // Someone else's flight: lands, taxis to its stand, sits there a while, pushes back on its own, taxis out, takes
    // off and despawns. Nobody services it. Host only; clients see the NetworkTransform.
    public class TrafficAircraft : NetworkBehaviour
    {
        enum Phase { Arriving, Parked, Pushback, Departing }

        public float gateX { get; private set; }
        public bool Done { get; private set; }

        readonly PathFollower path = new();
        Waypoint[] route;
        Phase phase;
        float parkedUntil;

        public void ServerBegin(float gate, float parkSeconds)
        {
            gateX = gate;
            parkedUntil = parkSeconds;
            route = AirportRoutes.Arrival(gate);
            transform.SetPositionAndRotation(route[0].position, Quaternion.LookRotation(route[1].position - route[0].position));
            path.Begin(transform, route[0].speed);
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer) PathFollower.Movers.Add(transform);
        }

        public override void OnNetworkDespawn()
        {
            PathFollower.Movers.Remove(transform);
            Runway.Release(this);
        }

        void Update()
        {
            if (!IsServer || !IsSpawned || route == null || Done) return;
            float dt = Time.deltaTime;
            switch (phase)
            {
                case Phase.Arriving:
                    if (path.Step(transform, route, this, dt)) { phase = Phase.Parked; parkedUntil += Time.time; Debug.Log($"[TURNAROUND] Traffic parked at {transform.position}"); }
                    break;
                case Phase.Parked:
                    if (Time.time > parkedUntil) Next(Phase.Pushback, AirportRoutes.Pushback(gateX));
                    break;
                case Phase.Pushback:
                    if (path.Step(transform, route, this, dt, reverse: true)) Next(Phase.Departing, AirportRoutes.Departure(transform.position.x));
                    break;
                case Phase.Departing:
                    if (path.Step(transform, route, this, dt)) { Done = true; Debug.Log("[TURNAROUND] Traffic gone"); }
                    break;
            }
        }

        void Next(Phase p, Waypoint[] r)
        {
            Debug.Log($"[TURNAROUND] Traffic at gate x={gateX}: {p} from {transform.position}");
            phase = p;
            route = r;
            path.Begin(transform, 0f);
        }
    }
}
