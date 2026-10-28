using System.Collections.Generic;
using Turnaround.Core;
using Turnaround.Interaction;
using Turnaround.Player;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Equipment
{
    // Sits on the belt itself (the belt's forward = uphill). Host drags rigidbodies in carryZone up the slope;
    // players riding it get pushed by their own PlayerController via SurfaceVelocity.
    [RequireComponent(typeof(BoxCollider))]
    public class BeltLoader : NetworkBehaviour, IInteractable, ISurfaceMotion
    {
        public float beltSpeed = 1.6f;
        public BoxCollider carryZone;
        public Renderer beltRenderer;

        readonly NetworkVariable<bool> running = new();
        readonly HashSet<Rigidbody> riders = new();
        Rigidbody ownBody;
        Material beltMaterial;
        float scroll;

        public bool Running => running.Value;
        public Vector3 SurfaceVelocity => Running ? transform.forward * beltSpeed : Vector3.zero;

        void Awake()
        {
            ownBody = GetComponentInParent<Rigidbody>();
            beltMaterial = beltRenderer.material;
        }

        void Update()
        {
            if (!Running) return;
            scroll += beltSpeed * Time.deltaTime * 0.5f;
            beltMaterial.mainTextureOffset = new Vector2(0f, -scroll);
        }

        void FixedUpdate()
        {
            if (!IsServer || !IsSpawned || !Running) return;
            BoxScan.Collect(carryZone, riders);
            Vector3 dir = transform.forward;
            foreach (var body in riders)
            {
                if (body == ownBody || body.isKinematic) continue;
                float along = Vector3.Dot(body.linearVelocity, dir);
                if (along < beltSpeed) body.linearVelocity += dir * (beltSpeed - along);
            }
        }

        public string GetPrompt(PlayerInteraction player) => $"[E] {(Running ? "Stop" : "Start")} Belt";

        public void Interact(PlayerInteraction player) => ToggleRpc();

        [Rpc(SendTo.Server)]
        void ToggleRpc(RpcParams rpcParams = default)
        {
            if (PlayerInteraction.ServerGetPlayer(NetworkManager, rpcParams.Receive.SenderClientId, transform.position, 3f) == null) return;
            running.Value = !running.Value;
        }
    }
}
