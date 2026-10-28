using Turnaround.Interaction;
using Turnaround.UI;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Turnaround.Player
{
    public class PlayerInteraction : NetworkBehaviour
    {
        public Transform cameraPivot;
        public Transform holdPoint;
        public float reach = 3f;
        public float throwChargeTime = 1f;

        public static PlayerInteraction Local { get; private set; }

        // Set on every peer by CarryableObject when the replicated holder changes.
        public CarryableObject Held { get; internal set; }
        public string Prompt { get; private set; }
        public float ThrowCharge { get; private set; }

        bool charging;
        PlayerController controller;
        PlayerRagdoll ragdoll;

        void Awake()
        {
            controller = GetComponent<PlayerController>();
            ragdoll = GetComponent<PlayerRagdoll>();
        }

        public override void OnNetworkSpawn()
        {
            if (IsOwner) Local = this;
        }

        // Host-side check for Rpcs: the sender has a player and is close enough to plausibly be touching the target.
        public static PlayerInteraction ServerGetPlayer(NetworkManager net, ulong clientId, Vector3 target, float slack = 1.5f)
        {
            if (!net.ConnectedClients.TryGetValue(clientId, out var client) || client.PlayerObject == null) return null;
            var player = client.PlayerObject.GetComponent<PlayerInteraction>();
            return Vector3.Distance(player.cameraPivot.position, target) <= player.reach + slack ? player : null;
        }

        public override void OnNetworkDespawn()
        {
            if (Local == this) Local = null;
        }

        void Update()
        {
            if (!IsOwner) return;
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            Prompt = null;
            if (kb == null || mouse == null || GameUI.MenuOpen)
            {
                charging = false;
                ThrowCharge = 0f;
                return;
            }

            if (ragdoll.IsDown)
            {
                charging = false;
                ThrowCharge = 0f;
                Prompt = "You are on the ground. Take a moment.";
                return;
            }

            var vehicle = controller.Vehicle;
            if (vehicle != null)
            {
                Prompt = $"{vehicle.displayName}\n[E] Get Out    WASD Drive    Space Brake";
                if (vehicle.StatusText != null) Prompt += "\n" + vehicle.StatusText;
                if (vehicle.Action != null)
                {
                    foreach (bool alternate in new[] { false, true })
                    {
                        string line = vehicle.Action.GetActionPrompt(alternate);
                        if (line != null) Prompt += "\n" + line;
                    }
                    if (kb.fKey.wasPressedThisFrame) vehicle.Action.DoAction(false);
                    if (mouse.rightButton.wasPressedThisFrame) vehicle.Action.DoAction(true);
                }
                if (kb.eKey.wasPressedThisFrame) vehicle.RequestExit();
                return;
            }

            var melee = Held != null ? Held.GetComponent<MeleeWeapon>() : null;
            if (melee != null)
            {
                Prompt = $"{Held.displayName}\n[LMB] Swing    [G] Drop";
                if (mouse.leftButton.wasPressedThisFrame) melee.TrySwing(cameraPivot);
                else if (kb.gKey.wasPressedThisFrame) Throw(0f);
                return;
            }

            if (Held != null)
            {
                Prompt = $"{Held.Description}\n[LMB] Hold to throw    [G] Drop";
                if (mouse.leftButton.wasPressedThisFrame) charging = true;
                if (charging) ThrowCharge = Mathf.Clamp01(ThrowCharge + Time.deltaTime / throwChargeTime);

                if (charging && mouse.leftButton.wasReleasedThisFrame) Throw(ThrowCharge);
                else if (kb.gKey.wasPressedThisFrame) Throw(0f);
                return;
            }

            charging = false;
            ThrowCharge = 0f;
            if (!Physics.Raycast(cameraPivot.position, cameraPivot.forward, out var hit, reach, ~0, QueryTriggerInteraction.Ignore)) return;
            var target = hit.collider.GetComponentInParent<IInteractable>();
            Prompt = target?.GetPrompt(this);
            if (Prompt != null && kb.eKey.wasPressedThisFrame) target.Interact(this);
        }

        void Throw(float charge)
        {
            Held.ThrowRpc(cameraPivot.forward, charge);
            charging = false;
            ThrowCharge = 0f;
        }
    }
}
