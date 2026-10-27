using System.Collections.Generic;
using System.Linq;
using Turnaround.Aircraft;
using Turnaround.Baggage;
using Turnaround.Equipment;
using Turnaround.Player;
using Turnaround.Vehicles;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Events
{
    public enum ViolationKind
    {
        ThrowingLuggage, RunningInVehicleLane, StandingOnBelt, Speeding, HitCoworker, DrivingUnderWing,
        VehicleLeftAtAircraft, RidingCart, ConesLeftOut, HitAircraft,
    }

    // Nothing unsafe is ever blocked; it's just written down (on the host) and announced.
    // Each (kind, offender) pair has a cooldown so one long sprint down the vehicle lane is one violation, not forty.
    public class SafetyViolationManager : NetworkBehaviour
    {
        public float cooldown = 15f;
        public float laneMinZ = 2.5f, laneMaxZ = 7.5f; // shortcut: the service road is a world Z band on this map
        public float speedingLimit = 6f;

        static readonly Dictionary<ViolationKind, string> Descriptions = new()
        {
            [ViolationKind.ThrowingLuggage] = "Throwing luggage",
            [ViolationKind.RunningInVehicleLane] = "Running in the vehicle lane",
            [ViolationKind.StandingOnBelt] = "Standing on the belt loader",
            [ViolationKind.Speeding] = "Driving too fast near the aircraft",
            [ViolationKind.HitCoworker] = "Hitting a coworker",
            [ViolationKind.DrivingUnderWing] = "Driving under the wing",
            [ViolationKind.VehicleLeftAtAircraft] = "Equipment left beside the aircraft at push",
            [ViolationKind.RidingCart] = "Riding a baggage cart",
            [ViolationKind.ConesLeftOut] = "Cones left out at push",
            [ViolationKind.HitAircraft] = "Hitting the aircraft",
        };

        public static SafetyViolationManager Instance { get; private set; }

        readonly NetworkVariable<int> count = new();
        readonly NetworkVariable<int> equipmentDamage = new();
        readonly Dictionary<ViolationKind, int> byKind = new(); // host only, for the report
        readonly Dictionary<(ViolationKind, ulong), float> nextAllowed = new();
        readonly Dictionary<ulong, Vector3> lastPositions = new();
        readonly HashSet<PlayerController> onBelt = new();
        readonly Dictionary<ulong, int> fastPolls = new(); // vehicle -> consecutive polls over the limit
        AircraftController aircraft;

        public int Count => count.Value;
        public int EquipmentDamage => equipmentDamage.Value;

        AircraftController Aircraft => aircraft != null ? aircraft : aircraft = FindFirstObjectByType<AircraftController>();

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (IsServer) InvokeRepeating(nameof(Poll), 1f, 0.5f);
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
            CancelInvoke();
        }

        public void ServerReset()
        {
            count.Value = 0;
            equipmentDamage.Value = 0;
            byKind.Clear();
            nextAllowed.Clear();
            fastPolls.Clear();
        }

        public static void Report(ViolationKind kind, ulong offender, string detail = null)
        {
            if (Instance != null && Instance.IsServer) Instance.ServerReport(kind, offender, detail);
        }

        public static void ReportDamage(int dollars, string what)
        {
            if (Instance == null || !Instance.IsServer || dollars <= 0) return;
            Instance.equipmentDamage.Value += dollars;
            if (dollars >= 150) RadioManager.Say("OPS", $"Something just hit {what}. That's ${dollars} we're not telling the airline about.");
        }

        void ServerReport(ViolationKind kind, ulong offender, string detail = null)
        {
            var key = (kind, offender);
            if (nextAllowed.TryGetValue(key, out float until) && Time.time < until) return;
            nextAllowed[key] = Time.time + cooldown;
            count.Value++;
            byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
            RadioManager.Say("SAFETY", $"{Descriptions[kind]}{(detail != null ? $" ({detail})" : "")}. That's {count.Value}.");
        }

        // "3x Throwing luggage, 1x Riding a baggage cart" for the score sheet.
        public string Summary(int max = 3) =>
            string.Join(", ", byKind.OrderByDescending(kv => kv.Value).Take(max).Select(kv => $"{kv.Value}x {Descriptions[kv.Key]}"));

        // Pushback with stuff still parked against the aircraft, or cones still out.
        public void ServerPushbackChecks(AircraftController ac)
        {
            Vector3 nose = ac.transform.position, tail = nose - ac.transform.forward * 24f;
            foreach (var v in FindObjectsByType<VehicleController>(FindObjectsSortMode.None))
                if (v.GetComponent<PushbackTug>() == null && DistanceToSegment(v.transform.position, nose, tail) < 7f)
                    ServerReport(ViolationKind.VehicleLeftAtAircraft, v.NetworkObjectId, v.displayName);
            foreach (var c in BaggageCart.All)
                if (DistanceToSegment(c.transform.position, nose, tail) < 7f)
                    ServerReport(ViolationKind.VehicleLeftAtAircraft, c.NetworkObjectId, c.displayName);
            if (ac.ConesPresent > 0) ServerReport(ViolationKind.ConesLeftOut, ac.NetworkObjectId);
        }

        void Poll()
        {
            var ac = Aircraft;
            foreach (var client in NetworkManager.ConnectedClientsList)
            {
                if (client.PlayerObject == null) continue;
                var player = client.PlayerObject.GetComponent<PlayerController>();
                Vector3 pos = player.transform.position;
                float speed = lastPositions.TryGetValue(client.ClientId, out var last) ? Vector3.Distance(last, pos) / 0.5f : 0f;
                lastPositions[client.ClientId] = pos;
                if (player.Vehicle != null) continue;

                if (pos.z > laneMinZ && pos.z < laneMaxZ && speed > 5.5f)
                    ServerReport(ViolationKind.RunningInVehicleLane, client.ClientId);

                foreach (var cart in BaggageCart.All)
                {
                    var local = cart.transform.InverseTransformPoint(pos);
                    if (Mathf.Abs(local.x) < 0.7f && Mathf.Abs(local.z) < 1.2f && local.y > 0.45f && local.y < 1.5f)
                        ServerReport(ViolationKind.RidingCart, client.ClientId);
                }
            }

            foreach (var belt in FindObjectsByType<BeltLoader>(FindObjectsSortMode.None))
            {
                Core.BoxScan.Collect(belt.carryZone, onBelt);
                foreach (var p in onBelt) ServerReport(ViolationKind.StandingOnBelt, p.OwnerClientId);
            }

            if (ac == null || !ac.IsStationary) return;
            foreach (var v in FindObjectsByType<VehicleController>(FindObjectsSortMode.None))
            {
                if (!v.HasDriver) continue;
                Vector3 local = ac.transform.InverseTransformPoint(v.transform.position);
                // Sustained, not a one-frame spike from a snap: over the limit for a full second.
                bool fast = v.ServerSpeed > speedingLimit && local.magnitude < 30f;
                fastPolls[v.NetworkObjectId] = fast ? fastPolls.GetValueOrDefault(v.NetworkObjectId) + 1 : 0;
                if (fastPolls[v.NetworkObjectId] >= 2) ServerReport(ViolationKind.Speeding, v.NetworkObjectId, v.displayName);
                if (Mathf.Abs(local.x) > 2f && Mathf.Abs(local.x) < 10.5f && local.z < -8f && local.z > -13.5f)
                    ServerReport(ViolationKind.DrivingUnderWing, v.NetworkObjectId, v.displayName);
            }
        }

        static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            p.y = a.y = b.y = 0f;
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector3.Distance(p, a + ab * t);
        }
    }
}
