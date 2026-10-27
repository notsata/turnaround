using Turnaround.Interaction;
using Turnaround.Player;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Aircraft
{
    public enum ServiceType { GroundPower, Fuel, PotableWater, Lavatory }

    // Connect -> wait -> disconnect. Clients derive progress from the replicated finish time, so nothing ticks over the network.
    // The GPU port needs the actual GPU cable in hand; the cable gets pinned to the port while connected.
    // shortcut: fuel/water/lav hoses are still a plain interaction; service trucks can come later.
    public class AircraftServicePoint : NetworkBehaviour, IInteractable
    {
        public ServiceType type;
        public string displayName = "Fuel";
        public string hoseName = "Fuel Hose";
        public float duration = 20f;
        public bool requiredForDeparture = true;
        public bool requiresGpuCable;
        public Renderer statusLight;
        public GameObject connectedVisual;

        readonly NetworkVariable<bool> connected = new();
        readonly NetworkVariable<bool> complete = new();
        readonly NetworkVariable<double> doneAt = new();
        readonly NetworkVariable<double> availableAt = new(); // a delayed truck blocks connecting until then
        AircraftController aircraft;
        CarryableObject pinnedCable; // host only

        public bool IsConnected => connected.Value;
        public bool IsComplete => complete.Value;

        public float Progress => IsComplete ? 1f
            : IsConnected && duration > 0f ? Mathf.Clamp01(1f - (float)((doneAt.Value - NetworkManager.ServerTime.Time) / duration))
            : 0f;

        void Awake() => aircraft = GetComponentInParent<AircraftController>();

        public override void OnNetworkSpawn()
        {
            connected.OnValueChanged += OnChanged;
            complete.OnValueChanged += OnChanged;
            UpdateVisuals();
        }

        public override void OnNetworkDespawn()
        {
            connected.OnValueChanged -= OnChanged;
            complete.OnValueChanged -= OnChanged;
        }

        void OnChanged(bool previous, bool current) => UpdateVisuals();

        void UpdateVisuals()
        {
            connectedVisual.SetActive(IsConnected);
            statusLight.material.color = IsComplete ? Color.green : IsConnected ? Color.yellow : Color.red;
        }

        void Update()
        {
            if (!IsServer || !IsSpawned) return;
            if (IsConnected && !IsComplete && NetworkManager.ServerTime.Time >= doneAt.Value) complete.Value = true;
            if (pinnedCable != null && !pinnedCable.IsPinned) // yanked out by someone dragging the GPU away
            {
                pinnedCable = null;
                connected.Value = false;
            }
        }

        public string GetPrompt(PlayerInteraction player)
        {
            if (!aircraft.IsStationary) return null;
            double wait = availableAt.Value - NetworkManager.ServerTime.Time;
            if (!IsConnected && !IsComplete && wait > 0) return $"{displayName}: truck delayed ({(int)wait / 60}:{(int)wait % 60:00})";
            if (!IsConnected && requiresGpuCable && (player.Held == null || player.Held.kind != CarryKind.GpuCable))
                return $"{displayName}: bring the {hoseName} here";
            if (!IsConnected) return $"[E] Connect {hoseName}" + (IsComplete ? $"  ({displayName} already done)" : "");
            if (!IsComplete) return $"{displayName}: {Progress:P0}\n[E] Disconnect {hoseName} (early)";
            return $"[E] Disconnect {hoseName}  ({displayName} complete)";
        }

        public void Interact(PlayerInteraction player) => ToggleRpc();

        public void ServerReset()
        {
            if (!IsServer) return;
            if (pinnedCable != null) pinnedCable.ServerUnpin(Vector3.down);
            pinnedCable = null;
            connected.Value = false;
            complete.Value = false;
            availableAt.Value = 0;
        }

        public void ServerDelay(float seconds)
        {
            if (IsServer && !IsComplete && !IsConnected) availableAt.Value = NetworkManager.ServerTime.Time + seconds;
        }

        [Rpc(SendTo.Server)]
        void ToggleRpc(RpcParams rpcParams = default)
        {
            if (!aircraft.IsStationary) return;
            var player = PlayerInteraction.ServerGetPlayer(NetworkManager, rpcParams.Receive.SenderClientId, transform.position);
            if (player == null) return;
            if (IsConnected)
            {
                connected.Value = false; // leaving early is allowed; it just isn't done
                if (pinnedCable != null) pinnedCable.ServerUnpin(Vector3.down);
                pinnedCable = null;
                return;
            }
            if (!IsComplete && NetworkManager.ServerTime.Time < availableAt.Value) return;
            if (requiresGpuCable)
            {
                if (player.Held == null || player.Held.kind != CarryKind.GpuCable) return;
                pinnedCable = player.Held;
                pinnedCable.ServerPin(transform);
            }
            connected.Value = true;
            if (!IsComplete) doneAt.Value = NetworkManager.ServerTime.Time + duration;
        }
    }
}
