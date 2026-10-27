using Turnaround.Baggage;
using Turnaround.Events;
using Turnaround.Player;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Interaction
{
    public enum CarryWeight { Light, Medium, Heavy }
    public enum CarryKind { Other, Bag, Cone, Chock, GpuCable }

    // Host owns the physics; clients only see the replicated transform and who is holding it.
    [RequireComponent(typeof(Rigidbody))]
    public class CarryableObject : NetworkBehaviour, IInteractable, IPushable
    {
        public const ulong NoHolder = ulong.MaxValue;

        public string displayName = "Suitcase";
        public CarryWeight weight = CarryWeight.Medium;
        public CarryKind kind = CarryKind.Other;

        readonly NetworkVariable<ulong> holderId = new(NoHolder);
        readonly NetworkVariable<bool> pinned = new(); // plugged into something; host keeps it at pinTarget
        Transform pinTarget;

        Rigidbody rb;
        Collider[] colliders;
        PlayerInteraction holder;
        ulong lastThrower = ulong.MaxValue; // host only: who to blame, and who not to knock over on release
        float thrownAt;

        public bool IsHeld => holderId.Value != NoHolder;
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
            holderId.OnValueChanged += OnHolderChanged;
            pinned.OnValueChanged += OnPinnedChanged;
            ApplyHolder();
        }

        public override void OnNetworkDespawn()
        {
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
            transform.SetPositionAndRotation(holder.holdPoint.position, holder.transform.rotation);
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
            thrownAt = Time.time;
            if (Bag != null && charge > 0.6f) SafetyViolationManager.Report(ViolationKind.ThrowingLuggage, lastThrower);
            Release(dir * speed + Vector3.up * (2.5f * charge));
        }

        // Thrown things that hit people knock them over. Heavy bags do it at a lower speed.
        void OnCollisionEnter(Collision collision)
        {
            if (!IsServer || !IsSpawned || IsHeld) return;
            var player = collision.collider.GetComponentInParent<PlayerController>();
            if (player == null || player.Vehicle != null) return;
            if (player.OwnerClientId == lastThrower && Time.time - thrownAt < 0.5f) return; // don't floor yourself on release
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

        void Release(Vector3 velocity)
        {
            holderId.Value = NoHolder;
            ApplyHolder();
            rb.linearVelocity = velocity;
            // Exaggerated tumble so throws look dumb.
            rb.angularVelocity = Random.insideUnitSphere * (velocity.magnitude * 0.8f);
        }
    }
}
