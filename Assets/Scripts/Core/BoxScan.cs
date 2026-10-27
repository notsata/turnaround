using System.Collections.Generic;
using UnityEngine;

namespace Turnaround.Core
{
    // "What's inside this box?" for zones that poll instead of relying on trigger events.
    public static class BoxScan
    {
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
