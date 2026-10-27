using System.Collections.Generic;
using System.Linq;
using Turnaround.Aircraft;
using Turnaround.Baggage;
using Turnaround.Equipment;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Events
{
    // Picks one or two complications when the aircraft parks and fires them partway through the turn.
    // Only the ongoing conditions (rain, ramp closed) are networked; everything else is a one-off host action.
    public class RandomEventManager : NetworkBehaviour
    {
        public RandomEventDefinition[] events;
        public int minEvents = 1;
        public int maxEvents = 2;
        public float lightningSeconds = 30f;
        public float fuelDelaySeconds = 60f;

        public static RandomEventManager Instance { get; private set; }
        public static bool Raining => Instance != null && Instance.raining.Value;
        public static bool RampClosed => Instance != null && Instance.rampClosed.Value;

        readonly NetworkVariable<bool> raining = new();
        readonly NetworkVariable<bool> rampClosed = new();
        readonly List<(RandomEventDefinition def, float at)> scheduled = new();
        AircraftController aircraft;
        bool planned;
        float rampOpensAt, nextNag = float.MaxValue;
        Color dryFog;
        float dryFogEnd;
        Color dryAmbient;

        AircraftController Aircraft => aircraft != null ? aircraft : aircraft = FindFirstObjectByType<AircraftController>();

        public override void OnNetworkSpawn()
        {
            Instance = this;
            dryFog = RenderSettings.fogColor;
            dryFogEnd = RenderSettings.fogEndDistance;
            dryAmbient = RenderSettings.ambientLight;
            raining.OnValueChanged += OnWeatherChanged;
            OnWeatherChanged(false, raining.Value);
        }

        public override void OnNetworkDespawn()
        {
            raining.OnValueChanged -= OnWeatherChanged;
            if (Instance == this) Instance = null;
        }

        // Every peer darkens its own sky; the "slippery" part lives in the vehicle and player movement code.
        void OnWeatherChanged(bool previous, bool wet)
        {
            RenderSettings.fogColor = wet ? dryFog * 0.6f : dryFog;
            RenderSettings.fogEndDistance = wet ? dryFogEnd * 0.5f : dryFogEnd;
            RenderSettings.ambientLight = wet ? dryAmbient * 0.7f : dryAmbient;
        }

        void Update()
        {
            if (!IsServer || !IsSpawned || Aircraft == null) return;
            if (!planned && Aircraft.State == AircraftState.Parked) Plan();

            for (int i = scheduled.Count - 1; i >= 0; i--)
            {
                if (Time.time < scheduled[i].at) continue;
                var def = scheduled[i].def;
                scheduled.RemoveAt(i);
                if (Aircraft.IsStationary) ServerTrigger(def); // too late to matter once it's moving
            }

            if (RampClosed && Time.time >= rampOpensAt)
            {
                rampClosed.Value = false;
                RadioManager.Say("OPS", "Lightning's cleared. Ramp is open. Pretend you were working.");
            }
            if (Time.time >= nextNag)
            {
                if (!Aircraft.IsStationary) nextNag = float.MaxValue;
                else
                {
                    nextNag = Time.time + 30f;
                    RadioManager.Say("COCKPIT", NagLines[Random.Range(0, NagLines.Length)]);
                }
            }
        }

        static readonly string[] NagLines =
        {
            "Ramp, cockpit. Status?", "Cockpit again. Just checking in. Again.", "We're showing doors closed. Are the doors closed?",
            "Captain would like an ETA on the ETA.",
        };

        void Plan()
        {
            planned = true;
            float turnSeconds = Aircraft.flight.scheduledTurnMinutes * Aircraft.flight.realSecondsPerGameMinute;
            var pool = events.ToList();
            int count = Random.Range(minEvents, maxEvents + 1);
            for (int n = 0; n < count && pool.Count > 0; n++)
            {
                var pick = WeightedPick(pool);
                pool.Remove(pick);
                if (pick.id == RandomEventId.Rain) pool.RemoveAll(e => e.id == RandomEventId.Lightning); // one weather event is plenty
                if (pick.id == RandomEventId.Lightning) pool.RemoveAll(e => e.id == RandomEventId.Rain);
                scheduled.Add((pick, Time.time + Mathf.Lerp(pick.earliest, pick.latest, Random.value) * turnSeconds));
            }
            Debug.Log($"[TURNAROUND] Complications planned: {string.Join(", ", scheduled.Select(s => s.def.displayName))}");
        }

        static RandomEventDefinition WeightedPick(List<RandomEventDefinition> pool)
        {
            float roll = Random.value * pool.Sum(e => e.weight);
            foreach (var e in pool)
            {
                roll -= e.weight;
                if (roll <= 0f) return e;
            }
            return pool[^1];
        }

        public void ServerTrigger(RandomEventDefinition def)
        {
            if (!IsServer) return;
            RadioManager.Say(def.speaker, def.message);
            var bags = BaggageManager.Instance;
            switch (def.id)
            {
                case RandomEventId.LateBags:
                    bags.ServerSpawnExtra(5, lost: false);
                    break;
                case RandomEventId.MissingBag:
                    bags.ServerSpawnExtra(1, lost: true);
                    break;
                case RandomEventId.OversizedBag:
                    bags.ServerSpawnExtra(1, lost: false, oversized: true);
                    break;
                case RandomEventId.BrokenGpu:
                    var gpu = GroundPowerUnit.Instance;
                    if (gpu != null) gpu.ServerFault("GPU's down. It made a noise first. A bad one.");
                    break;
                case RandomEventId.Rain:
                    raining.Value = true;
                    break;
                case RandomEventId.Lightning:
                    rampClosed.Value = true;
                    rampOpensAt = Time.time + lightningSeconds;
                    break;
                case RandomEventId.FuelDelay:
                    foreach (var sp in Aircraft.Services) if (sp.type == ServiceType.Fuel) sp.ServerDelay(fuelDelaySeconds);
                    break;
                case RandomEventId.RunawayCart:
                    var cart = BaggageCart.All.Where(c => !c.IsHitched && c.displayName == "Baggage Cart").OrderBy(_ => Random.value).FirstOrDefault();
                    if (cart != null) cart.ServerRunaway(Quaternion.Euler(0f, Random.Range(-30f, 30f), 0f) * (Aircraft.transform.position - cart.transform.position).normalized);
                    break;
                case RandomEventId.CaptainImpatient:
                    nextNag = Time.time + 20f;
                    break;
            }
            Debug.Log($"[TURNAROUND] Complication: {def.displayName}");
        }

        public void ServerReset()
        {
            planned = false;
            scheduled.Clear();
            raining.Value = false;
            rampClosed.Value = false;
            nextNag = float.MaxValue;
        }

        // Dev/QA: fire every complication right now (used by -autoturn).
        public void ServerTriggerAll()
        {
            foreach (var def in events) ServerTrigger(def);
        }
    }
}
