using Turnaround.Interaction;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Equipment
{
    // The plug end of the GPU cable (a normal carryable). Every peer draws the sagging cable locally;
    // the host enforces the length: stretch it too far and it gets yanked out of your hands or out of the aircraft.
    [RequireComponent(typeof(CarryableObject), typeof(LineRenderer))]
    public class GpuCable : NetworkBehaviour
    {
        const int Points = 10;

        CarryableObject carry;
        Rigidbody rb;
        LineRenderer line;
        readonly Vector3[] points = new Vector3[Points];

        void Awake()
        {
            carry = GetComponent<CarryableObject>();
            rb = GetComponent<Rigidbody>();
            line = GetComponent<LineRenderer>();
            line.positionCount = Points;
        }

        void LateUpdate()
        {
            var gpu = GroundPowerUnit.Instance;
            line.enabled = gpu != null;
            if (gpu == null) return;

            Vector3 from = gpu.cableOrigin.position, to = transform.position;
            float stretch = Vector3.Distance(from, to) / gpu.maxCableLength;
            float sag = Mathf.Lerp(0.8f, 0.05f, Mathf.Clamp01(stretch));
            for (int i = 0; i < Points; i++)
            {
                float t = i / (Points - 1f);
                points[i] = Vector3.Lerp(from, to, t) + Vector3.down * (Mathf.Sin(t * Mathf.PI) * sag);
                points[i].y = Mathf.Max(points[i].y, 0.03f); // lies on the ground rather than through it
            }
            line.SetPositions(points);

            if (!IsServer || !IsSpawned) return;
            Vector3 pull = from - to;
            float excess = pull.magnitude - gpu.maxCableLength;
            if (excess <= 0f) return;
            Vector3 yank = pull.normalized * 5f + Vector3.up * 2f;
            if (carry.IsPinned) carry.ServerUnpin(yank);
            else if (carry.IsHeld) carry.ServerRelease(yank);
            else rb.linearVelocity = pull.normalized * Mathf.Min(excess * 3f, 6f); // dragged along behind the GPU
        }
    }
}
