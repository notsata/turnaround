using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Player
{
    // Knockdowns: the host decides (it sees every collision), the owner gets flung (it owns its movement),
    // and everyone sees the flop pose from the replicated flag. A tumble, not a jointed ragdoll; recovers on its own.
    public class PlayerRagdoll : NetworkBehaviour
    {
        readonly NetworkVariable<bool> down = new();
        double upAt;

        public bool IsDown => down.Value;

        // Owner only: launch velocity waiting to be applied by PlayerController.
        public Vector3 PendingKnock { get; set; }

        public void ServerKnockDown(Vector3 velocity, float seconds = 2.5f)
        {
            if (!IsServer || IsDown) return;
            down.Value = true;
            upAt = NetworkManager.ServerTime.Time + seconds;
            var held = GetComponent<PlayerInteraction>().Held;
            if (held != null) held.ServerRelease(velocity * 0.5f + Vector3.up * 3f); // whatever you were carrying goes flying too
            KnockRpc(Vector3.ClampMagnitude(velocity, 14f));
            Debug.Log($"[TURNAROUND] Crew member {OwnerClientId} knocked down");
        }

        [Rpc(SendTo.Owner)]
        void KnockRpc(Vector3 velocity)
        {
            PendingKnock = velocity;
            Debug.Log($"[TURNAROUND] I got knocked down ({velocity.magnitude:F1} m/s)");
        }

        void Update()
        {
            if (IsServer && IsDown && NetworkManager.ServerTime.Time >= upAt) down.Value = false;
        }
    }
}
