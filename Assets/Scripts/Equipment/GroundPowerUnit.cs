using Turnaround.Aircraft;
using Turnaround.Interaction;
using Turnaround.Player;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Equipment
{
    // Power switch on the GPU cart (the cart itself moves like a baggage cart). Only the on/off flag is networked;
    // "aircraft has power" is derived: switched on AND the cable is plugged into the aircraft's GPU port.
    public class GroundPowerUnit : NetworkBehaviour, IInteractable
    {
        public Transform cableOrigin;
        public Renderer statusLight;
        public float maxCableLength = 14f;
        [Range(0f, 1f)] public float reliability = 0.7f; // old unit: dies now and then, needs a slap

        // shortcut: one GPU on the ramp; make this a lookup when there are several.
        public static GroundPowerUnit Instance { get; private set; }

        readonly NetworkVariable<bool> running = new();
        readonly NetworkVariable<bool> faulted = new();

        public bool Running => running.Value;
        public bool Faulted => faulted.Value;

        public override void OnNetworkSpawn()
        {
            Instance = this;
            running.OnValueChanged += OnRunningChanged;
            faulted.OnValueChanged += OnRunningChanged;
            UpdateLight();
        }

        public override void OnNetworkDespawn()
        {
            running.OnValueChanged -= OnRunningChanged;
            faulted.OnValueChanged -= OnRunningChanged;
            if (Instance == this) Instance = null;
        }

        void OnRunningChanged(bool previous, bool current) => UpdateLight();

        void UpdateLight() => statusLight.material.color = Faulted ? new Color(1f, 0.5f, 0f) : Running ? Color.green : Color.red;

        void Update()
        {
            // ~once every 15 minutes of running for a 0.7; the Broken GPU event forces it.
            if (IsServer && IsSpawned && Running && Random.value < (1f - reliability) / 300f * Time.deltaTime)
                ServerFault("The GPU just died. Again.");
        }

        public void ServerReset()
        {
            running.Value = false;
            faulted.Value = false;
        }

        public void ServerFault(string reason)
        {
            running.Value = false;
            faulted.Value = true;
            Events.RadioManager.Say("RAMP", reason);
        }

        public bool IsPowering(AircraftController aircraft)
        {
            if (!Running || aircraft == null) return false;
            foreach (var sp in aircraft.Services)
                if (sp.type == ServiceType.GroundPower && sp.IsConnected) return true;
            return false;
        }

        public string GetPrompt(PlayerInteraction player) =>
            Faulted ? "[E] Reset GPU (give it a firm slap)" : $"[E] Turn GPU {(Running ? "Off" : "On")}";

        public void Interact(PlayerInteraction player) => ToggleRpc();

        [Rpc(SendTo.Server)]
        void ToggleRpc(RpcParams rpcParams = default)
        {
            if (PlayerInteraction.ServerGetPlayer(NetworkManager, rpcParams.Receive.SenderClientId, transform.position) == null) return;
            if (Faulted) faulted.Value = false; // reset only; it still has to be switched back on
            else running.Value = !running.Value;
        }
    }
}
