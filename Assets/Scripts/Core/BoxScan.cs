using System.Collections.Generic;
using UnityEngine;

namespace Turnaround.Core
{
    // "What's inside this box?" for zones that poll instead of relying on trigger events.
    public static class BoxScan
    {
        // Is a world point inside the box (rotation and scale included)?
        public static bool Contains(BoxCollider box, Vector3 point)
        {
            Vector3 local = box.transform.InverseTransformPoint(point) - box.center;
            Vector3 half = box.size * 0.5f;
            return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
        }

        public static void Collect<T>(BoxCollider box, HashSet<T> results) where T : Component
        {
            results.Clear();
            if (!box.enabled) return;
            var t = box.transform;
            var hits = Physics.OverlapBox(t.TransformPoint(box.center), Vector3.Scale(box.size, t.lossyScale) * 0.5f, t.rotation, ~0, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits)
            {
                var c = hit.GetComponentInParent<T>();
                if (c != null) results.Add(c);
            }
        }
    }
}
