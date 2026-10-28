using System.Collections.Generic;
using Turnaround.Core;
using Turnaround.Interaction;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Baggage
{
    // Bag room conveyor: drop inbound bags on the outside end and it carries them in to the inbound area. Always on.
    // Bags are eased to belt speed and their spin is damped, so they ride instead of tumbling off. Host moves them.
    public class Conveyor : MonoBehaviour, ISurfaceMotion
    {
        public float speed = 1.3f;
        public BoxCollider carryZone;
        public Renderer beltRenderer;

        readonly HashSet<Rigidbody> riders = new();
        Material beltMaterial;
        float scroll;

        public Vector3 SurfaceVelocity => transform.forward * speed;

        void Awake() => beltMaterial = beltRenderer.material;

        void Update()
        {
            scroll += speed * Time.deltaTime * 0.5f;
            beltMaterial.mainTextureOffset = new Vector2(0f, -scroll);
        }

        void FixedUpdate()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            BoxScan.Collect(carryZone, riders);
            Vector3 dir = transform.forward;
            float dt = Time.fixedDeltaTime;
            foreach (var body in riders)
            {
                if (body.isKinematic) continue;
                Vector3 v = body.linearVelocity;
                Vector3 flat = Vector3.MoveTowards(new Vector3(v.x, 0f, v.z), dir * speed, 12f * dt);
                body.linearVelocity = flat + Vector3.up * Mathf.Min(v.y, 0.5f);
                body.angularVelocity = Vector3.MoveTowards(body.angularVelocity, Vector3.zero, 20f * dt);
            }
        }
    }
}
