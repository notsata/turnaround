using UnityEngine;

namespace Turnaround.Player
{
    // Host-only stand-in for a remote player's body. Remote players arrive as network updates, i.e. teleports, and a
    // teleported CharacterController shoves rigidbodies like an immovable object: that's what launched vehicles into
    // the sky. This kinematic body is moved with MovePosition instead, so it pushes with its real walking speed.
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
    public class PlayerProxy : MonoBehaviour
    {
        public PlayerController Player { get; private set; }
        public Collider Body { get; private set; }
        Rigidbody rb;

        public static PlayerProxy Create(PlayerController player, CharacterController shape)
        {
            var go = new GameObject($"PlayerProxy_{player.OwnerClientId}") { layer = Core.Layers.Player };
            go.transform.SetPositionAndRotation(player.transform.position, player.transform.rotation);
            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.center = shape.center;
            capsule.height = shape.height;
            capsule.radius = shape.radius;
            var proxy = go.AddComponent<PlayerProxy>();
            proxy.rb = go.GetComponent<Rigidbody>();
            proxy.rb.isKinematic = true;
            proxy.rb.interpolation = RigidbodyInterpolation.None;
            proxy.Player = player;
            proxy.Body = capsule;
            return proxy;
        }

        public void Follow(Vector3 position, Quaternion rotation, bool solid)
        {
            Body.enabled = solid;
            rb.MovePosition(position);
            rb.MoveRotation(rotation);
        }
    }
}
