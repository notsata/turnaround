using System.Collections.Generic;
using Turnaround.Tasks;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Aircraft
{
    // Background traffic so the airport isn't just you: at most a couple of other jets at once, on the stands you
    // don't work (B5, B6). Host only, and only while a shift is running.
    public class TrafficManager : MonoBehaviour
    {
        public GameObject trafficPrefab;
        public float[] gates = { 36f, 60f };
        public int maxAircraft = 2;
        public Vector2 spawnGap = new(45f, 100f);
        public Vector2 parkTime = new(60f, 120f);

        public static TrafficManager Instance { get; private set; }

        readonly List<TrafficAircraft> active = new();
        float nextSpawn = 20f;

        void Awake() => Instance = this;

        void Update()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || trafficPrefab == null) return;
            for (int i = active.Count - 1; i >= 0; i--)
            {
                var a = active[i];
                if (a == null) active.RemoveAt(i);
                else if (a.Done) { a.NetworkObject.Despawn(); active.RemoveAt(i); }
            }
            if (ShiftManager.InLobby || Time.time < nextSpawn || active.Count >= maxAircraft) return;
            nextSpawn = Time.time + Random.Range(spawnGap.x, spawnGap.y);
            float? gate = FreeGate();
            if (gate == null) return;
            var go = Instantiate(trafficPrefab);
            var t = go.GetComponent<TrafficAircraft>();
            t.ServerBegin(gate.Value, Random.Range(parkTime.x, parkTime.y));
            go.GetComponent<NetworkObject>().Spawn(true);
            active.Add(t);
        }

        float? FreeGate()
        {
            foreach (float g in gates)
            {
                bool taken = false;
                foreach (var a in active) if (a != null && Mathf.Approximately(a.gateX, g)) taken = true;
                if (!taken) return g;
            }
            return null;
        }

        // Bankruptcy / back to the lobby: clear the sky.
        public void ServerClear()
        {
            foreach (var a in active) if (a != null && a.IsSpawned) a.NetworkObject.Despawn();
            active.Clear();
            nextSpawn = Time.time + 20f;
        }
    }
}
