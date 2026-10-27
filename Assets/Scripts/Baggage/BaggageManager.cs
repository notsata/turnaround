using System.Collections.Generic;
using System.Linq;
using Turnaround.Aircraft;
using Turnaround.Core;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Baggage
{
    // Host spawns inbound bags into the hold and outbound bags into the bag room when the aircraft parks,
    // then keeps the counters the HUD (and later the score screen) read.
    public class BaggageManager : NetworkBehaviour
    {
        public AircraftController aircraft; // host only: clients get a fresh prefab copy, so scene references are null there
        public GameObject[] bagPrefabs; // duplicates act as weighting
        public GameObject oversizedPrefab;
        public Vector3[] lostBagSpots; // where a "missing" bag can end up
        public BoxCollider inboundZone;
        public Vector3 outboundOrigin = new(-37f, 0.5f, -3.8f);
        public int otherFlight = 3822;
        public string otherDestination = "PIT";
        public int wrongFlightBags = 3;
        [Range(0f, 1f)] public float fragileChance = 0.15f;

        public static BaggageManager Instance { get; private set; }

        readonly NetworkVariable<int> inboundTotal = new();
        readonly NetworkVariable<int> outboundTotal = new();
        readonly NetworkVariable<int> inboundDelivered = new();
        readonly NetworkVariable<int> damaged = new();
        readonly HashSet<BaggageItem> scan = new();
        bool spawned;
        int nextBagId = 1000;
        int extraSpawned;
        System.Random rng;

        public bool BagsArrived => inboundTotal.Value > 0;
        public int InboundTotal => inboundTotal.Value;
        public int OutboundTotal => outboundTotal.Value;
        public int InboundDelivered => inboundDelivered.Value;
        public int Damaged => damaged.Value;
        public int InboundInHold => SumHolds(h => h.InboundCount);
        public int OutboundLoaded => SumHolds(h => h.OutboundCount);
        public int WrongLoaded => SumHolds(h => h.WrongCount);

        // Balancing table: 20 / 30 / 40 / 50 bags each way for 1-4 crew.
        public static int BagsForCrew(int crew) => 10 + 10 * Mathf.Clamp(crew, 1, 4);

        int SumHolds(System.Func<CargoHold, int> f)
        {
            int n = 0;
            if (aircraft == null) aircraft = FindFirstObjectByType<AircraftController>();
            if (aircraft == null) return 0;
            foreach (var h in aircraft.Holds) n += f(h);
            return n;
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (!IsServer)
            {
                inboundTotal.OnValueChanged += (_, n) =>
                {
                    Debug.Log($"[TURNAROUND] Bags arrived (client): {n} each way, {BaggageItem.All.Count} bag objects");
                    Invoke(nameof(LogSettled), 3f);
                };
                return;
            }
            BaggageItem.BecameDamaged += OnBagDamaged;
            InvokeRepeating(nameof(Recount), 1f, 0.5f);
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
            BaggageItem.BecameDamaged -= OnBagDamaged;
            CancelInvoke();
        }

        void OnBagDamaged(BaggageItem bag) => damaged.Value++;

        void Update()
        {
            if (!IsServer || !IsSpawned || spawned || aircraft.State != AircraftState.Parked) return;
            spawned = true;
            SpawnBags(BagsForCrew(NetworkManager.ConnectedClientsIds.Count));
        }

        void SpawnBags(int count)
        {
            rng = new System.Random(aircraft.FlightNumber);
            var hold = aircraft.Holds[0];
            var holdRotation = hold.transform.rotation * Quaternion.Euler(0f, 90f, 0f);
            for (int i = 0; i < count; i++) Spawn(rng, hold.SpawnPoint(i), holdRotation, aircraft.FlightNumber, "HERE", true);

            // Outbound: our bags plus a few strays for another flight, shuffled so someone has to read tags.
            var flights = new List<int>();
            for (int i = 0; i < count; i++) flights.Add(aircraft.FlightNumber);
            for (int i = 0; i < wrongFlightBags; i++) flights.Add(otherFlight);
            for (int i = flights.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (flights[i], flights[j]) = (flights[j], flights[i]);
            }
            for (int i = 0; i < flights.Count; i++)
            {
                var pos = outboundOrigin + new Vector3(i % 8 * 0.85f, i / 48 * 0.6f, i / 8 % 6 * 0.7f);
                bool ours = flights[i] == aircraft.FlightNumber;
                Spawn(rng, pos, Quaternion.Euler(0f, rng.Next(4) * 90f, 0f), flights[i], ours ? aircraft.Destination : otherDestination, false);
            }

            inboundTotal.Value = count;
            outboundTotal.Value = count;
            Debug.Log($"[TURNAROUND] Bags spawned: {count} inbound, {count} outbound, {wrongFlightBags} strays");
            Invoke(nameof(LogSettled), 3f);
        }

        // Next flight: clear this flight's luggage (untagged strays stay; they're nobody's problem) and start counting again.
        public void ServerReset()
        {
            foreach (var bag in BaggageItem.All.Where(b => b.Flight != 0).ToList()) bag.NetworkObject.Despawn();
            spawned = false;
            extraSpawned = 0;
            inboundTotal.Value = outboundTotal.Value = inboundDelivered.Value = damaged.Value = 0;
        }

        // Random events: late connecting bags in the bag room, one lost somewhere stupid, or one enormous one.
        public void ServerSpawnExtra(int count, bool lost, bool oversized = false)
        {
            if (!IsServer || rng == null) return;
            for (int i = 0; i < count; i++, extraSpawned++)
            {
                Vector3 pos = lost ? lostBagSpots[rng.Next(lostBagSpots.Length)] : outboundOrigin + new Vector3(extraSpawned % 8 * 0.85f, 0.2f, 4.4f);
                Spawn(rng, pos, Quaternion.identity, aircraft.FlightNumber, aircraft.Destination, false, oversized ? oversizedPrefab : null);
            }
            outboundTotal.Value += count;
        }

        void Spawn(System.Random rng, Vector3 position, Quaternion rotation, int flight, string destination, bool inbound, GameObject prefab = null)
        {
            var go = Instantiate(prefab != null ? prefab : bagPrefabs[rng.Next(bagPrefabs.Length)], position, rotation);
            go.GetComponent<BaggageItem>().ServerSetTag(nextBagId++, flight, destination, inbound, rng.NextDouble() < fragileChance);
            go.GetComponent<NetworkObject>().Spawn(true);
        }

        void LogSettled() => Debug.Log($"[TURNAROUND] After settling{(IsServer ? "" : " (client)")}: {InboundInHold}/{InboundTotal} inbound still in hold, {WrongLoaded} other items in hold");

        void Recount()
        {
            BoxScan.Collect(inboundZone, scan);
            int delivered = 0;
            foreach (var bag in scan) if (bag.Inbound) delivered++;
            if (inboundDelivered.Value != delivered) inboundDelivered.Value = delivered;
        }
    }
}
