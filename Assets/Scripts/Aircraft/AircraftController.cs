using System.Collections.Generic;
using Turnaround.Core;
using Turnaround.Interaction;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Aircraft
{
    public enum AircraftState { Approaching, Parked, Servicing, ReadyForPush, Pushback, Departing, Gone }

    // Host drives the state machine and the waypoint taxiing; clients just see the replicated state and transform.
    // Paths are nose-gear positions (the root sits on the nose gear). During pushback the tug owns the nose gear
    // and the aircraft follows it like a trailer.
    public class AircraftController : NetworkBehaviour
    {
        public AircraftDefinition definition;
        public FlightDefinition flight;
        public float pushLimitZ = 74f; // shortcut: one gate, so "on the taxiway" is a world Z line
        public float gateX = 12f; // the stand's centreline; routes come from AirportRoutes

        readonly NetworkVariable<AircraftState> state = new();
        readonly NetworkVariable<int> flightNumber = new();
        readonly NetworkVariable<FixedString32Bytes> destination = new();
        readonly NetworkVariable<int> chocksPlaced = new(); // counted toward zone requirements
        readonly NetworkVariable<int> chocksPresent = new(); // any chock in a zone, for the "remove chocks" checks
        readonly NetworkVariable<int> conesPlaced = new();
        readonly NetworkVariable<int> conesPresent = new();

        public AircraftState State => state.Value;
        public int FlightNumber => flightNumber.Value;
        public string Destination => destination.Value.ToString();
        public bool IsStationary => State is AircraftState.Parked or AircraftState.Servicing or AircraftState.ReadyForPush;
        public bool PastPushLimit => transform.position.z > pushLimitZ;
        public int ChocksPlaced => chocksPlaced.Value;
        public int ChocksPresent => chocksPresent.Value;
        public int ConesPlaced => conesPlaced.Value;
        public int ConesPresent => conesPresent.Value;
        public int ChocksRequired { get; private set; }
        public int ConesRequired { get; private set; }

        public AircraftDoor[] Doors { get; private set; }
        public AircraftServicePoint[] Services { get; private set; }
        public CargoHold[] Holds { get; private set; }
        public AircraftZone[] Zones { get; private set; }

        // Set by the tug while its towbar is on the nose gear during pushback (host only).
        public Transform TowedBy { get; set; }

        // Host only: keep it waiting out on the taxiway (e.g. no contract to fly for).
        public bool HoldArrival { get; set; }

        readonly HashSet<CarryableObject> scan = new();
        Renderer[] renderers;
        Collider[] colliders;
        readonly PathFollower path = new();
        Waypoint[] route;
        float stateTime;

        void Awake()
        {
            Doors = GetComponentsInChildren<AircraftDoor>();
            Services = GetComponentsInChildren<AircraftServicePoint>();
            Holds = GetComponentsInChildren<CargoHold>();
            Zones = GetComponentsInChildren<AircraftZone>();
            renderers = GetComponentsInChildren<Renderer>(true);
            colliders = GetComponentsInChildren<Collider>(true);
            foreach (var z in Zones)
            {
                if (z.kind == CarryKind.Chock) ChocksRequired += z.required;
                if (z.kind == CarryKind.Cone) ConesRequired += z.required;
            }
        }

        public override void OnNetworkSpawn()
        {
            state.OnValueChanged += OnStateChanged;
            ApplyVisibility();
            if (!IsServer) return;
            PathFollower.Movers.Add(transform);
            ServerAssignFlight(flight.flightNumber, flight.outboundDestination);
            PlaceAtArrivalStart();
            InvokeRepeating(nameof(ScanZones), 1f, 0.5f);
        }

        void PlaceAtArrivalStart()
        {
            Runway.Release(this);
            route = AirportRoutes.Arrival(gateX);
            transform.SetPositionAndRotation(route[0].position, Quaternion.LookRotation(route[1].position - route[0].position));
            path.Begin(transform, route[0].speed);
        }

        public void ServerAssignFlight(int number, string dest)
        {
            flightNumber.Value = number;
            destination.Value = new FixedString32Bytes(dest);
        }

        // Same airframe, next flight: back to the start of the taxi route with everything closed and unplugged.
        public void ServerResetForNextFlight()
        {
            TowedBy = null;
            foreach (var d in Doors) d.ServerClose();
            foreach (var s in Services) s.ServerReset();
            foreach (var h in Holds) h.ServerReset();
            PlaceAtArrivalStart();
            SetState(AircraftState.Approaching);
            ApplyVisibility();
        }

        public override void OnNetworkDespawn()
        {
            state.OnValueChanged -= OnStateChanged;
            CancelInvoke();
            PathFollower.Movers.Remove(transform);
            Runway.Release(this);
        }

        void OnStateChanged(AircraftState previous, AircraftState current)
        {
            ApplyVisibility();
            Debug.Log($"[TURNAROUND] Flight {FlightNumber}: {current}{(IsServer ? "" : " (client)")}");
        }

        void ApplyVisibility()
        {
            bool visible = State != AircraftState.Gone;
            foreach (var r in renderers) r.enabled = visible;
            foreach (var c in colliders) c.enabled = visible;
            foreach (var z in Zones) z.marker.enabled = visible && IsStationary;
        }

        void SetState(AircraftState next)
        {
            state.Value = next;
            stateTime = 0f;
            if (next == AircraftState.Departing)
            {
                route = AirportRoutes.Departure(transform.position.x);
                path.Begin(transform, 0f);
            }
        }

        void Update()
        {
            if (!IsServer || !IsSpawned) return;
            stateTime += Time.deltaTime;
            switch (State)
            {
                case AircraftState.Approaching:
                    if (HoldArrival) stateTime = 0f;
                    else if (stateTime > definition.arrivalDelay && path.Step(transform, route, this, Time.deltaTime)) SetState(AircraftState.Parked);
                    break;
                case AircraftState.Parked:
                    if (AnyOpenOrConnected()) SetState(AircraftState.Servicing);
                    break;
                case AircraftState.Servicing:
                    if (!AnyOpenOrConnected() && RequiredServicesDone()) SetState(AircraftState.ReadyForPush);
                    break;
                case AircraftState.ReadyForPush:
                    if (AnyOpenOrConnected()) SetState(AircraftState.Servicing);
                    break;
                case AircraftState.Pushback:
                    if (TowedBy != null) FollowTug(TowedBy.position);
                    else if (TugIsClear()) SetState(AircraftState.Departing);
                    break;
                case AircraftState.Departing:
                    if (path.Step(transform, route, this, Time.deltaTime)) SetState(AircraftState.Gone);
                    break;
            }
        }

        bool AnyOpenOrConnected()
        {
            foreach (var d in Doors) if (d.IsOpen) return true;
            foreach (var s in Services) if (s.IsConnected) return true;
            return false;
        }

        bool RequiredServicesDone()
        {
            foreach (var s in Services) if (s.requiredForDeparture && !s.IsComplete) return false;
            return true;
        }

        void ScanZones()
        {
            int chocks = 0, chocksAny = 0, cones = 0, conesAny = 0;
            foreach (var zone in Zones)
            {
                BoxScan.Collect(zone.Volume, scan);
                int n = 0;
                foreach (var item in scan) if (item.kind == zone.kind && !item.IsHeld) n++;
                if (zone.kind == CarryKind.Chock) { chocks += Mathf.Min(n, zone.required); chocksAny += n; }
                if (zone.kind == CarryKind.Cone) { cones += Mathf.Min(n, zone.required); conesAny += n; }
            }
            if (chocksPlaced.Value != chocks) chocksPlaced.Value = chocks;
            if (chocksPresent.Value != chocksAny) chocksPresent.Value = chocksAny;
            if (conesPlaced.Value != cones) conesPlaced.Value = cones;
            if (conesPresent.Value != conesAny) conesPresent.Value = conesAny;
        }

        // Called by the tug once the captain agrees. Whatever is in the holds is locked in for the flight.
        public bool ServerBeginPushback(Transform towPoint)
        {
            if (State != AircraftState.ReadyForPush || ChocksPresent > 0) return false;
            Events.SafetyViolationManager.Instance?.ServerPushbackChecks(this);
            foreach (var hold in Holds) hold.ServerDepartContents();
            TowedBy = towPoint;
            SetState(AircraftState.Pushback);
            ApplyVisibility();
            return true;
        }

        bool TugIsClear()
        {
            var tug = Vehicles.PushbackTug.Instance;
            return tug == null || Vector3.Distance(tug.transform.position, transform.position) > 15f;
        }

        // Kinematic trailer: the nose gear sits on the tow point and the main gear drags behind it.
        void FollowTug(Vector3 towPosition)
        {
            towPosition.y = 0f;
            Vector3 mainGear = transform.position - transform.forward * definition.wheelbase;
            Vector3 heading = towPosition - mainGear;
            heading.y = 0f;
            if (heading.sqrMagnitude < 0.01f) return;
            transform.SetPositionAndRotation(towPosition, Quaternion.LookRotation(heading));
        }
    }
}
