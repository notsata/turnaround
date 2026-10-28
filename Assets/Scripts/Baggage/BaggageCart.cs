using System.Collections.Generic;
using Turnaround.Interaction;
using Turnaround.Player;
using Turnaround.Vehicles;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Baggage
{
    // Unpowered trailer. Hitches its tongue to the nearest free rear hitch (tractor or another cart). Towing is a
    // horizontal spring-damper tow bar between tongue tip and hitch pin, not a physics joint: a rigid joint between
    // bodies whose velocities we steer ourselves blew up in PhysX (tractor + cart flung at 3000 m/s). Pulling at the
    // tongue swings the cart in behind like a real trailer, jackknifes on reverse, and the force is capped.
    [RequireComponent(typeof(Rigidbody))]
    public class BaggageCart : NetworkBehaviour, IInteractable, IPushable, IShovable
    {
        public const ulong NoHitch = ulong.MaxValue;
        public static readonly List<BaggageCart> All = new();

        public string displayName = "Baggage Cart";
        public Company.EquipmentKind kind = Company.EquipmentKind.Cart;
        public Transform tongueTip;
        public TrailerHitch rearHitch;
        public float hitchRange = 4f;
        public float grip = 10f;
        public float rollingDecel = 1.5f;
        public float pushImpulse = 300f; // N*s per player bump; mass decides the result
        public float maxRiseSpeed = 2f;
        public float towStiffness = 15000f; // N per metre of tow-bar stretch
        public float towDamping = 5000f; // critically damped for a cart behind a tractor
        public float maxTowForce = 60000f;
        public float shearDistance = 2.5f; // stretched this far (snagged on something), the tow pin gives up

        readonly NetworkVariable<ulong> hitchedTo = new(NoHitch); // NetworkObjectId of whatever we're towed by

        Rigidbody rb;
        TrailerHitch towHitch; // host only: what we're being towed by
        Collider[] ownColliders;
        Collider[] towColliders;
        readonly Core.PhysicsSanity sanity = new();
        float runawayUntil; // host only

        public ulong HitchedToId => hitchedTo.Value;
        public bool IsHitched => hitchedTo.Value != NoHitch;

        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            ownColliders = GetComponentsInChildren<Collider>();
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            rb.isKinematic = !IsServer;
        }

        public override void OnNetworkDespawn() => All.Remove(this);

        void FixedUpdate()
        {
            if (!IsServer || !IsSpawned) return;
            if (sanity.Check(rb)) ServerUnhitch(); // put back after a physics glitch; don't drag the tractor along
            if (towHitch != null) Tow();
            // Wheels: resist sliding sideways and slowly roll to a stop.
            float dt = Time.fixedDeltaTime;
            Vector3 v = rb.linearVelocity;
            if (v.y > maxRiseSpeed) v.y = maxRiseSpeed; // last line of defence against any overlap launching us
            Vector3 forward = transform.forward;
            float along = Vector3.Dot(v, forward);
            Vector3 lateral = v - forward * along;
            lateral.y = 0f;
            lateral = Vector3.MoveTowards(lateral, Vector3.zero, grip * dt);
            // Runaway: "the ramp has a slight slope", says nobody who checked. Rolls up to a jog instead of stopping.
            along = Time.time < runawayUntil && !IsHitched ? Mathf.MoveTowards(along, 3.5f, 0.8f * dt) : Mathf.MoveTowards(along, 0f, rollingDecel * dt);
            rb.linearVelocity = forward * along + lateral + Vector3.up * v.y;
        }

        void Tow()
        {
            var other = towHitch.Body;
            if (other == null)
            {
                ServerUnhitch();
                return;
            }
            Vector3 tip = tongueTip.position, pin = towHitch.transform.position;
            Vector3 stretch = pin - tip;
            stretch.y = 0f;
            if (stretch.magnitude > shearDistance)
            {
                ServerUnhitch();
                Events.RadioManager.Say("RAMP", $"Tow pin on that {displayName.ToLower()} just sheared. Impressive.");
                return;
            }
            Vector3 relative = other.GetPointVelocity(pin) - rb.GetPointVelocity(tip);
            relative.y = 0f;
            Vector3 force = Vector3.ClampMagnitude(stretch * towStiffness + relative * towDamping, maxTowForce);
            rb.AddForceAtPosition(force, tip, ForceMode.Force);
            other.AddForceAtPosition(-force, pin, ForceMode.Force);
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
                ServerUnhitch();
                return;
            }
            var hitch = FindFreeHitch();
            if (hitch != null && !Hitch(hitch)) Events.RadioManager.Say("RAMP", $"That {displayName.ToLower()} won't line up. Something's in the way.");
        }

        public void ServerUnhitch()
        {
            IgnoreTower(false);
            towHitch = null;
            towColliders = null;
            hitchedTo.Value = NoHitch;
        }

        // A cart and whatever tows it don't collide while hitched (jackknife bumps against a bar we control are noise).
        void IgnoreTower(bool ignore)
        {
            if (towColliders == null) return;
            foreach (var a in ownColliders)
            foreach (var b in towColliders)
                if (a != null && b != null) Physics.IgnoreCollision(a, b, ignore);
        }

        bool Hitch(TrailerHitch hitch)
        {
            // Snap in line behind the hitch so the tow bar starts unstretched. Bags already aboard get left behind; that's on you.
            Quaternion rotation = Quaternion.Euler(0f, hitch.transform.eulerAngles.y, 0f);
            Vector3 tongueLocal = transform.InverseTransformPoint(tongueTip.position);
            Vector3 position = hitch.transform.position - rotation * tongueLocal;
            position.y = transform.position.y;

            // Snapping into something solid (or someone) is what used to fling (or bury) the tractor.
            if (Blocked(position, rotation, hitch.Body)) return false;

            // Move the body, not just the transform: an interpolated rigidbody otherwise keeps its old pose for a frame.
            rb.position = position;
            rb.rotation = rotation;
            transform.SetPositionAndRotation(position, rotation);
            Physics.SyncTransforms();
            rb.linearVelocity = hitch.Body.linearVelocity;
            rb.angularVelocity = Vector3.zero;

            towHitch = hitch;
            towColliders = hitch.Body.GetComponentsInChildren<Collider>();
            IgnoreTower(true);
            hitchedTo.Value = hitch.Owner.NetworkObjectId;
            return true;
        }

        // Anything solid in the way at the snap target? Ignores us, what we're hitching to, the ground and luggage.
        bool Blocked(Vector3 position, Quaternion rotation, Rigidbody towedBy)
        {
            // People count too: a cart snapped into a player's capsule gets flung (the capsule is immovable to physics).
            var hits = Physics.OverlapBox(position + rotation * new Vector3(0f, 0.7f, 0f), new Vector3(0.65f, 0.55f, 1.15f), rotation,
                Core.Layers.WorldMask | (1 << Core.Layers.Player), QueryTriggerInteraction.Ignore);
            foreach (var h in hits)
            {
                if (h.GetComponentInParent<BaggageCart>() == this || h.attachedRigidbody == towedBy) continue;
                if (h.GetComponentInParent<CarryableObject>() != null) continue;
                var other = h.GetComponentInParent<NetworkObject>();
                Debug.Log($"[TURNAROUND] {displayName} {NetworkObjectId} at {transform.position} hitch target {position} (hitch {towedBy.name} at {towedBy.position}) blocked by {h.name} of {(other != null ? other.name + " " + other.NetworkObjectId + " at " + other.transform.position : "scenery")}");
                return true;
            }
            return false;
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
            velocity.y = 0f;
            ServerShove(velocity.normalized * pushImpulse * Mathf.Clamp01(velocity.magnitude / 4f));
        }

        public void ServerShove(Vector3 impulse)
        {
            if (!IsServer || IsHitched) return;
            impulse.y = 0f;
            rb.AddForce(impulse, ForceMode.Impulse);
        }
    }
}
