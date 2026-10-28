using UnityEngine;

namespace Turnaround.Core
{
    // Last line of defence for heavy host-simulated bodies. If a body ever ends up with NaN/absurd coordinates
    // (a solver blow-up) it is put back where it last made sense and stopped. That matters beyond the one body:
    // a NaN body's bounds cover the whole world, so it "hits" everything and spreads the garbage to its neighbours.
    public class PhysicsSanity
    {
        Vector3 safePosition;
        Quaternion safeRotation = Quaternion.identity;
        bool hasSafe;
        float nextSample;

        // Returns true when it had to rescue the body.
        public bool Check(Rigidbody rb)
        {
            Vector3 p = rb.position, v = rb.linearVelocity;
            bool broken = !Finite(p) || !Finite(v) || p.sqrMagnitude > 3000f * 3000f || p.y < -10f || p.y > 60f || v.sqrMagnitude > 80f * 80f;
            if (!broken)
            {
                if (Time.time >= nextSample && Mathf.Abs(v.y) < 1f && p.y < 3f)
                {
                    (safePosition, safeRotation, hasSafe) = (p, rb.rotation, true);
                    nextSample = Time.time + 0.5f;
                }
                return false;
            }
            Debug.LogWarning($"[TURNAROUND] {rb.name} physics blew up (pos {p}, vel {v}); putting it back at {safePosition}");
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            if (hasSafe)
            {
                rb.position = safePosition;
                rb.rotation = safeRotation;
                rb.transform.SetPositionAndRotation(safePosition, safeRotation);
            }
            return true;
        }

        static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    }
}
