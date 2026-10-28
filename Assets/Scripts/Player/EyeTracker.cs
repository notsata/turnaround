using System.Collections.Generic;
using Turnaround.Aircraft;
using Turnaround.Interaction;
using Turnaround.Vehicles;
using UnityEngine;

namespace Turnaround.Player
{
    // Googly eyes that look at whatever is interesting nearby: other people's faces, what they're carrying, moving
    // vehicles and aircraft. The pupils do most of the work; the head follows a little. Purely local, every peer.
    [DefaultExecutionOrder(100)] // after PlayerAnimator
    public class EyeTracker : MonoBehaviour
    {
        public Transform head;
        public Transform pupilL, pupilR;
        public float pupilTravel = 0.028f; // how far a pupil can slide from centre (head-local)
        public float headFollow = 0.22f; // fraction of the look direction the head turns, max 18 degrees
        public float range = 18f;

        static readonly List<Transform> aircraft = new();
        static float aircraftRefreshAt;

        PlayerController self;
        Vector3 restL, restR, lookDir = Vector3.forward, wander;
        Transform target;
        float retargetAt, wanderAt;
        Quaternion headRest;

        void Awake()
        {
            self = GetComponentInParent<PlayerController>();
            restL = pupilL.localPosition;
            restR = pupilR.localPosition;
            headRest = head.localRotation;
        }

        void LateUpdate()
        {
            if (Time.time >= retargetAt) { retargetAt = Time.time + Random.Range(0.25f, 0.6f); target = PickTarget(); }
            if (Time.time >= wanderAt) { wanderAt = Time.time + Random.Range(0.4f, 1.6f); wander = Random.insideUnitSphere * 0.35f; }

            Vector3 want = target != null ? head.parent.InverseTransformDirection((target.position - head.position).normalized) : (Vector3.forward + wander).normalized;
            if (want.z < 0.1f) want = (Vector3.forward + wander).normalized; // behind us: give up
            lookDir = Vector3.Slerp(lookDir, want, 1f - Mathf.Exp(-14f * Time.deltaTime)); // eyes are fast

            var headTurn = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(Vector3.forward, lookDir), headFollow);
            head.localRotation = headRest * Quaternion.RotateTowards(Quaternion.identity, headTurn, 18f);

            Vector3 inHead = Quaternion.Inverse(headTurn) * lookDir; // what's left for the pupils
            Vector3 slide = new Vector3(inHead.x, inHead.y, 0f) * (pupilTravel / 0.6f);
            slide = Vector3.ClampMagnitude(slide, pupilTravel);
            pupilL.localPosition = restL + slide + Vector3.right * Mathf.Max(0f, -slide.x) * 0.3f; // slightly cross-eyed, on purpose
            pupilR.localPosition = restR + slide - Vector3.right * Mathf.Max(0f, slide.x) * 0.3f;
        }

        Transform PickTarget()
        {
            Transform best = null;
            float bestScore = 0f;
            Vector3 eye = head.position, fwd = head.parent.forward;

            void Consider(Transform t, float interest)
            {
                if (t == null) return;
                Vector3 d = t.position - eye;
                float dist = d.magnitude;
                if (dist < 0.5f || dist > range * (interest > 1.5f ? 6f : 1f)) return;
                float facing = Vector3.Dot(fwd, d / dist);
                if (facing < 0.2f) return;
                float score = interest * facing / (1f + dist * 0.15f);
                if (score > bestScore) { bestScore = score; best = t; }
            }

            foreach (var p in PlayerController.All)
            {
                if (p == self) continue;
                var anim = p.GetComponent<PlayerAnimator>();
                Consider(anim != null ? anim.head : p.transform, 1f);
                var held = p.GetComponent<PlayerInteraction>().Held;
                if (held != null) Consider(held.transform, 1.3f);
            }
            foreach (var v in VehicleController.All) if (v.Speed > 1.5f) Consider(v.transform, 1.2f);
            if (Time.time >= aircraftRefreshAt)
            {
                aircraftRefreshAt = Time.time + 2f;
                aircraft.Clear();
                foreach (var a in FindObjectsByType<AircraftController>(FindObjectsSortMode.None)) aircraft.Add(a.transform);
                foreach (var a in FindObjectsByType<TrafficAircraft>(FindObjectsSortMode.None)) aircraft.Add(a.transform);
            }
            foreach (var a in aircraft) Consider(a, 2f); // a jet is the most interesting thing in the world
            foreach (var c in CarryableObject.All)
                if (c != null && !c.IsHeld && c.Body.linearVelocity.sqrMagnitude > 9f) Consider(c.transform, 1.6f); // flying luggage (shortcut: host only, client bodies are kinematic)
            return best;
        }
    }
}
