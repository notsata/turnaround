using Turnaround.Interaction;
using Turnaround.Player;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Aircraft
{
    // Sits on the hinge; rotates between closed/open. Only the open flag is networked.
    public class AircraftDoor : NetworkBehaviour, IInteractable
    {
        public string doorName = "Cargo Door";
        public bool isCargoDoor = true;
        public Vector3 closedEuler;
        public Vector3 openEuler;
        public float swingSpeed = 90f;

        readonly NetworkVariable<bool> open = new();
        AircraftController aircraft;

        public bool IsOpen => open.Value;

        void Awake() => aircraft = GetComponentInParent<AircraftController>();

        void Update()
        {
            var target = Quaternion.Euler(IsOpen ? openEuler : closedEuler);
            if (transform.localRotation != target)
                transform.localRotation = Quaternion.RotateTowards(transform.localRotation, target, swingSpeed * Time.deltaTime);
        }

        public string GetPrompt(PlayerInteraction player) =>
            aircraft.IsStationary ? $"[E] {(IsOpen ? "Close" : "Open")} {doorName}" : null;

        public void Interact(PlayerInteraction player) => ToggleRpc();

        public void ServerClose()
        {
            if (IsServer) open.Value = false;
        }

        [Rpc(SendTo.Server)]
        void ToggleRpc(RpcParams rpcParams = default)
        {
            if (!aircraft.IsStationary) return;
            if (PlayerInteraction.ServerGetPlayer(NetworkManager, rpcParams.Receive.SenderClientId, transform.position, 3f) == null) return;
            open.Value = !open.Value;
        }
    }
}
