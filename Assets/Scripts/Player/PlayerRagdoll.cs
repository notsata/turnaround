using System.Collections.Generic;
using Turnaround.Core;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Player
{
    // Knockdowns. The host decides (it sees every hit) and owns the "down" flag and its timer; every peer then turns
    // its copy of the body into physics pieces (torso+head, two arms, two legs on joints) with the same launch
    // velocity, so everyone sees roughly the same flop. The owner's player root rides along with the torso, and when
    // the flag clears every peer reassembles the body where the owner landed. Purely cosmetic physics: the limbs live
    // on their own layer and never touch player capsules, so a flop can't launch anyone.
    public class PlayerRagdoll : NetworkBehaviour
    {
        public float maxLaunchSpeed = 12f;
        public float maxDownSeconds = 6f; // stacking hits extends the flop, but never into a permanent one

        readonly NetworkVariable<bool> down = new();
        double upAt, maxUpAt; // host only

        struct Piece
        {
            public Transform t;
            public Transform parent;
            public Vector3 localPos;
            public Quaternion localRot;
            public int layer;
        }

        readonly List<Piece> pieces = new();
        readonly List<Object> added = new();
        PlayerAnimator anim;
        Rigidbody torso;

        public bool IsDown => down.Value;
        public bool HasBody => torso != null;
        public Vector3 Center => torso != null ? torso.worldCenterOfMass : transform.position;

        void Awake() => anim = GetComponent<PlayerAnimator>();

        public override void OnNetworkSpawn()
        {
            down.OnValueChanged += OnDownChanged;
            if (down.Value) Fall(Vector3.zero); // joined mid-flop
        }

        public override void OnNetworkDespawn()
        {
            down.OnValueChanged -= OnDownChanged;
            Reassemble();
        }

        void OnDownChanged(bool previous, bool current)
        {
            if (!current) Reassemble();
        }

        public void ServerKnockDown(Vector3 velocity, float seconds = 2.5f)
        {
            if (!IsServer) return;
            double now = NetworkManager.ServerTime.Time;
            if (!IsDown)
            {
                maxUpAt = now + maxDownSeconds;
                var held = GetComponent<PlayerInteraction>().Held;
                if (held != null) held.ServerRelease(velocity * 0.5f + Vector3.up * 3f); // whatever you held goes flying too
            }
            upAt = System.Math.Min(System.Math.Max(upAt, now + seconds), maxUpAt);
            down.Value = true;
            KnockRpc(Vector3.ClampMagnitude(velocity, maxLaunchSpeed));
            Debug.Log($"[TURNAROUND] Crew member {OwnerClientId} knocked down");
        }

        [Rpc(SendTo.Everyone)]
        void KnockRpc(Vector3 velocity)
        {
            if (HasBody) torso.linearVelocity += velocity; // hit again mid-flop
            else Fall(velocity);
            if (IsOwner) Debug.Log($"[TURNAROUND] I got knocked down ({velocity.magnitude:F1} m/s)");
        }

        void Update()
        {
            if (IsServer && IsDown && NetworkManager.ServerTime.Time >= upAt) down.Value = false;
        }

        // Owner safety valve (fell through something): cut the flop short; the host clears the flag shortly after.
        public void ForceEnd()
        {
            Reassemble();
            if (IsOwner) EndEarlyRpc();
        }

        [Rpc(SendTo.Server)]
        void EndEarlyRpc() => upAt = 0;

        void Fall(Vector3 velocity)
        {
            if (HasBody) return;
            var body = anim.body;
            Remember(body);
            body.SetParent(null, true);
            torso = AddBody(body.gameObject, 40f, velocity);
            var box = body.gameObject.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 1.3f, 0f);
            box.size = new Vector3(0.6f, 1.05f, 0.42f);
            added.Add(box);
            torso.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic; // fast flops shouldn't tunnel the ground
            torso.angularVelocity = Random.insideUnitSphere * 6f;

            foreach (var limb in new[] { anim.armL, anim.armR, anim.legL, anim.legR })
            {
                Remember(limb);
                limb.SetParent(null, true);
                var rb = AddBody(limb.gameObject, 6f, velocity);
                var capsule = limb.gameObject.AddComponent<CapsuleCollider>();
                capsule.center = new Vector3(0f, -0.42f, 0f);
                capsule.height = 0.9f;
                capsule.radius = 0.1f;
                var joint = limb.gameObject.AddComponent<CharacterJoint>();
                joint.connectedBody = torso;
                joint.anchor = Vector3.zero; // the pivot is the shoulder/hip
                joint.lowTwistLimit = new SoftJointLimit { limit = -60f };
                joint.highTwistLimit = new SoftJointLimit { limit = 60f };
                joint.swing1Limit = new SoftJointLimit { limit = 80f };
                joint.swing2Limit = new SoftJointLimit { limit = 80f };
                added.Add(capsule);
                added.Add(joint);
            }
            foreach (var p in pieces) SetLayer(p.t, Layers.Ragdoll);
        }

        Rigidbody AddBody(GameObject go, float mass, Vector3 velocity)
        {
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.linearDamping = 0.1f;
            rb.angularDamping = 0.5f;
            rb.maxDepenetrationVelocity = 3f;
            rb.linearVelocity = velocity;
            added.Add(rb);
            return rb;
        }

        void Remember(Transform t) => pieces.Add(new Piece { t = t, parent = t.parent, localPos = t.localPosition, localRot = t.localRotation, layer = t.gameObject.layer });

        void Reassemble()
        {
            if (!HasBody) return;
            // Joints first, then colliders, then bodies, immediately: Unity refuses to remove a Rigidbody a joint still needs.
            foreach (var o in added) if (o is Joint) DestroyImmediate(o);
            foreach (var o in added) if (o is Collider) DestroyImmediate(o);
            foreach (var o in added) if (o is Rigidbody) DestroyImmediate(o);
            added.Clear();
            for (int i = 0; i < pieces.Count; i++) // body first (parented to the root), then the limbs onto the body
            {
                var p = pieces[i];
                p.t.SetParent(p.parent, false);
                p.t.SetLocalPositionAndRotation(p.localPos, p.localRot);
                SetLayer(p.t, p.layer);
            }
            pieces.Clear();
            torso = null;
        }

        static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform child in t) SetLayer(child, layer);
        }
    }
}
