using System.Collections.Generic;
using Turnaround.Interaction;
using Turnaround.Player;
using Turnaround.Vehicles;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Baggage
{
    // Unpowered trailer. Hitches its tongue to the nearest free rear hitch (tractor or another cart) with a
    // yaw-limited joint on the host; that's the whole "articulation physics", jackknifing included.
    [RequireComponent(typeof(Rigidbody))]
    public class BaggageCart : NetworkBehaviour, IInteractable, IPushable
    {
        public const ulong NoHitch = ulong.MaxValue;
        public static readonly List<BaggageCart> All = new();

        public string displayName = "Baggage Cart";
        public Company.EquipmentKind kind = Company.EquipmentKind.Cart;
        public Transform tongueTip;
        public TrailerHitch rearHitch;
        public float hitchRange = 4f;
        public float grip = 10f;
        public float rollingDecel = 1.2f;

        readonly NetworkVariable<ulong> hitchedTo = new(NoHitch); // NetworkObjectId of whatever we're towed by

        Rigidbody rb;
        ConfigurableJoint joint;
        float runawayUntil; // host only

        public ulong HitchedToId => hitchedTo.Value;
        public bool IsHitched => hitchedTo.Value != NoHitch;

        void Awake() => rb = GetComponent<Rigidbody>();

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            rb.isKinematic = !IsServer;
        }

        public override void OnNetworkDespawn() => All.Remove(this);

        void FixedUpdate()
        {
            if (!IsServer || !IsSpawned) return;
            // Wheels: resist sliding sideways and slowly roll to a stop.
            float dt = Time.fixedDeltaTime;
            Vector3 v = rb.linearVelocity;
            Vector3 forward = transform.forward;
            float along = Vector3.Dot(v, forward);
            Vector3 lateral = v - forward * along;
            lateral.y = 0f;
            lateral = Vector3.MoveTowards(lateral, Vector3.zero, grip * dt);
            // Runaway: "the ramp has a slight slope", says nobody who checked. Rolls up to a jog instead of stopping.
            along = Time.time < runawayUntil && !IsHitched ? Mathf.MoveTowards(along, 3.5f, 0.8f * dt) : Mathf.MoveTowards(along, 0f, rollingDecel * dt);
            rb.linearVelocity = forward * along + lateral + Vector3.up * v.y;
        }

        void OnCollisionEnter(Collision collision)
        {
            if (IsServer && IsSpawned) Events.ImpactRules.VehicleHit(collision, transform, displayName, NetworkObjectId, kind);
        }

        TrailerHitch FindFreeHitch()
        {
            TrailerHitch best = null;
            float bestDistance = hitchRange;
            foreach (var hitch in TrailerHitch.All)
            {
                if (hitch == rearHitch || hitch.IsOccupied) continue;
                float d = Vector3.Distance(hitch.transform.position, tongueTip.position);
                if (d < bestDistance)
                {
                    best = hitch;
                    bestDistance = d;
                }
            }
            return best;
        }

        public string GetPrompt(PlayerInteraction player) =>
            IsHitched ? $"[E] Unhitch {displayName}" : FindFreeHitch() != null ? $"[E] Hitch {displayName}" : null;

        public void Interact(PlayerInteraction player) => ToggleHitchRpc();

        [Rpc(SendTo.Server)]
        void ToggleHitchRpc(RpcParams rpcParams = default)
        {
            if (PlayerInteraction.ServerGetPlayer(NetworkManager, rpcParams.Receive.SenderClientId, transform.position, 2.5f) == null) return;
            if (IsHitched)
            {
                Destroy(joint);
                joint = null;
                hitchedTo.Value = NoHitch;
                return;
            }
            var hitch = FindFreeHitch();
            if (hitch != null) Hitch(hitch);
        }

        void Hitch(TrailerHitch hitch)
        {
            // Snap in line behind the hitch so the joint starts straight. Bags already aboard get left behind; that's on you.
            transform.rotation = Quaternion.Euler(0f, hitch.transform.eulerAngles.y, 0f);
            transform.position += hitch.transform.position - tongueTip.position;
            Physics.SyncTransforms();
            rb.linearVelocity = hitch.Body.linearVelocity;

            joint = gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = hitch.Body;
            joint.anchor = transform.InverseTransformPoint(tongueTip.position);
            joint.autoConfigureConnectedAnchor = true;
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = joint.angularZMotion = ConfigurableJointMotion.Locked;
            joint.angularYMotion = ConfigurableJointMotion.Limited;
            joint.angularYLimit = new SoftJointLimit { limit = 80f };
            hitchedTo.Value = hitch.Owner.NetworkObjectId;
        }

        public void ServerRunaway(Vector3 direction, float seconds = 15f)
        {
            if (!IsServer || IsHitched) return;
            direction.y = 0f;
            transform.rotation = Quaternion.LookRotation(direction); // carts only roll along their wheels
            Physics.SyncTransforms();
            rb.linearVelocity = transform.forward * 1f;
            runawayUntil = Time.time + seconds;
        }

        public void Push(Vector3 velocity) => PushRpc(velocity);

        [Rpc(SendTo.Server)]
        void PushRpc(Vector3 velocity)
        {
            if (IsHitched) return;
            rb.AddForce(Vector3.ClampMagnitude(velocity, 3f) * 0.6f, ForceMode.VelocityChange);
        }
    }
}
