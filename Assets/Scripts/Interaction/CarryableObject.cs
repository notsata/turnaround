using Turnaround.Baggage;
using Turnaround.Events;
using Turnaround.Player;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Interaction
{
    public enum CarryWeight { Light, Medium, Heavy }
    public enum CarryKind { Other, Bag, Cone, Chock, GpuCable, Tool }

    // Host owns the physics; clients only see the replicated transform and who is holding it.
    [RequireComponent(typeof(Rigidbody))]
    public class CarryableObject : NetworkBehaviour, IInteractable, IPushable
    {
        public const ulong NoHolder = ulong.MaxValue;

        public string displayName = "Suitcase";
        public CarryWeight weight = CarryWeight.Medium;
        public CarryKind kind = CarryKind.Other;
        public bool heldInHand; // tools: held in the right hand, pointing where you look (bags sit in front of you)
        public Vector3 heldEuler;

        public static readonly System.Collections.Generic.List<CarryableObject> All = new();

        readonly NetworkVariable<ulong> holderId = new(NoHolder);
        readonly NetworkVariable<bool> pinned = new(); // plugged into something; host keeps it at pinTarget
        Transform pinTarget;

        Rigidbody rb;
        Collider[] colliders;
        PlayerInteraction holder;
        ulong lastThrower = ulong.MaxValue; // host only: who to blame, and who not to knock over on release

        public bool IsHeld => holderId.Value != NoHolder;
        public Rigidbody Body => rb;
        public PlayerInteraction Holder => holder;
        public Quaternion HeldTwist { get; set; } = Quaternion.identity; // extra rotation while held (melee swing)
        public bool IsPinned => pinned.Value;
        public BaggageItem Bag { get; private set; }
        public string Description => Bag != null ? $"{displayName}\n{Bag.TagText}" : displayName;

        public float SpeedMultiplier => weight switch
        {
            CarryWeight.Light => 1f,
            CarryWeight.Medium => 0.8f,
            _ => 0.55f,
        };

        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            colliders = GetComponentsInChildren<Collider>();
            Bag = GetComponent<BaggageItem>();
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            holderId.OnValueChanged += OnHolderChanged;
            pinned.OnValueChanged += OnPinnedChanged;
            ApplyHolder();
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            holderId.OnValueChanged -= OnHolderChanged;
            pinned.OnValueChanged -= OnPinnedChanged;
            if (holder != null && holder.Held == this) holder.Held = null;
        }

        void OnHolderChanged(ulong previous, ulong current) => ApplyHolder();
        void OnPinnedChanged(bool previous, bool current) => ApplyHolder();

        void ApplyHolder()
        {
            if (holder != null && holder.Held == this) holder.Held = null;
            holder = null;
            TryResolveHolder();
            foreach (var c in colliders) c.enabled = !IsHeld && !IsPinned;
            rb.isKinematic = IsHeld || IsPinned || !IsServer;
        }

        // Holder's player object may spawn after us during client sync, so resolve lazily too.
        bool TryResolveHolder()
        {
            if (holder != null) return true;
            if (!IsHeld || !NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(holderId.Value, out var obj)) return false;
            holder = obj.GetComponent<PlayerInteraction>();
            if (holder != null) holder.Held = this;
            return holder != null;
        }

        void LateUpdate()
        {
            if (IsServer && IsPinned && pinTarget != null) transform.SetPositionAndRotation(pinTarget.position, pinTarget.rotation);
            if (!IsSpawned || !IsHeld) return;
            if (!TryResolveHolder())
            {
                if (IsServer) Release(Vector3.zero); // holder disconnected
                return;
            }
            // Every peer snaps the held item to its own view of the holder, so the holder sees zero lag.
            var hp = holder.holdPoint;
            if (heldInHand) transform.SetPositionAndRotation(hp.position + hp.right * 0.28f, hp.rotation * Quaternion.Euler(heldEuler) * HeldTwist);
            else transform.SetPositionAndRotation(hp.position, holder.transform.rotation);
        }

        public string GetPrompt(PlayerInteraction player)
        {
            if (IsHeld || IsPinned) return null;
            return $"[E] Pick Up {displayName}" + (weight == CarryWeight.Heavy ? " (HEAVY)" : "") + (Bag != null ? $"\n{Bag.TagText}" : "");
        }

        public void Interact(PlayerInteraction player) => PickUpRpc();

        [Rpc(SendTo.Server)]
        void PickUpRpc(RpcParams rpcParams = default)
        {
            if (IsHeld || IsPinned) return;
            var player = PlayerInteraction.ServerGetPlayer(NetworkManager, rpcParams.Receive.SenderClientId, transform.position);
            if (player == null || player.Held != null) return;
            holderId.Value = player.NetworkObjectId;
            ApplyHolder();
        }

        // charge 0 = gentle drop, 1 = full yeet.
        [Rpc(SendTo.Server)]
        public void ThrowRpc(Vector3 direction, float charge, RpcParams rpcParams = default)
        {
            if (holder == null || holder.OwnerClientId != rpcParams.Receive.SenderClientId) return;
            charge = Mathf.Clamp01(charge);
            Vector3 dir = direction.sqrMagnitude > 0.01f ? direction.normalized : holder.transform.forward;
            float speed = Mathf.Lerp(1.5f, 17f, charge) * SpeedMultiplier;
            lastThrower = holder.OwnerClientId;
            if (Bag != null && charge > 0.6f) SafetyViolationManager.Report(ViolationKind.ThrowingLuggage, lastThrower);
            Release(dir * speed + Vector3.up * (2.5f * charge));
        }

        // Thrown things that hit people knock them over. Heavy bags do it at a lower speed.
        // Never the thrower, though: a bag bouncing back off a wall used to fling its thrower off the map.
        void OnCollisionEnter(Collision collision)
        {
            if (!IsServer || !IsSpawned || IsHeld) return;
            var player = PlayerController.FromCollider(collision.collider);
            if (player == null || player.Vehicle != null) return;
            if (player.OwnerClientId == lastThrower) return;
            float impact = collision.relativeVelocity.magnitude;
            if (impact < (weight == CarryWeight.Heavy ? 5f : weight == CarryWeight.Medium ? 8f : 12f)) return;
            ImpactRules.Knock(player, transform.position, impact * (weight == CarryWeight.Heavy ? 0.8f : 0.5f));
            if (lastThrower != ulong.MaxValue && lastThrower != player.OwnerClientId)
                SafetyViolationManager.Report(ViolationKind.HitCoworker, lastThrower, $"with a {displayName.ToLower()}");
        }

        public void Push(Vector3 velocity) => KickRpc(velocity);

        [Rpc(SendTo.Server)]
        void KickRpc(Vector3 velocity)
        {
            if (IsHeld || IsPinned) return;
            rb.AddForce(Vector3.ClampMagnitude(velocity, 6f) + Vector3.up * 1.5f, ForceMode.VelocityChange);
        }

        // Host-only helpers for things that grab or yank items (sockets, tethers).
        public void ServerPin(Transform target)
        {
            holderId.Value = NoHolder;
            pinTarget = target;
            pinned.Value = true;
            ApplyHolder();
        }

        public void ServerUnpin(Vector3 velocity)
        {
            pinned.Value = false;
            pinTarget = null;
            Release(velocity);
        }

        public void ServerRelease(Vector3 velocity) => Release(velocity);

        System.Collections.IEnumerator IgnoreBriefly(Collider body)
        {
            foreach (var c in colliders) Physics.IgnoreCollision(c, body, true);
            yield return new WaitForSeconds(0.6f);
            if (body == null) yield break;
            foreach (var c in colliders) Physics.IgnoreCollision(c, body, false);
        }

        void Release(Vector3 velocity)
        {
            var from = holder;
            holderId.Value = NoHolder;
            ApplyHolder();
            if (from != null)
            {
                // Let go in front of the body, never inside it (looking down put the bag inside your own capsule,
                // and the push-out flung it), and ignore the releaser's body for a moment while it clears.
                var eye = from.cameraPivot;
                Vector3 flat = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
                flat = flat.sqrMagnitude > 0.01f ? flat.normalized : from.transform.forward;
                Vector3 at = eye.position + flat * 0.85f + Vector3.up * Mathf.Min(0f, eye.forward.y) * 0.6f;
                at.y = Mathf.Max(at.y, from.transform.position.y + 0.35f);
                rb.position = at;
                transform.position = at;
                var body = from.GetComponent<PlayerController>().ServerBody;
                if (body != null) StartCoroutine(IgnoreBriefly(body));
            }
            rb.linearVelocity = velocity;
            // Exaggerated tumble so throws look dumb.
            rb.angularVelocity = Random.insideUnitSphere * (velocity.magnitude * 0.8f);
        }
    }
}
