using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Turnaround.Aircraft
{
    [Serializable]
    public struct Waypoint
    {
        public Vector3 position;
        public float speed; // target speed while heading here, m/s
        public bool needsRunway; // don't head here until the runway is ours
        public bool freesRunway; // reaching here hands the runway back

        public Waypoint(float x, float y, float z, float speed, bool needsRunway = false, bool freesRunway = false)
        {
            position = new Vector3(x, y, z);
            this.speed = speed;
            this.needsRunway = needsRunway;
            this.freesRunway = freesRunway;
        }
    }

    // One aircraft on the runway at a time (landing roll or takeoff). Host only.
    public static class Runway
    {
        static object holder;

        public static bool TryAcquire(object who)
        {
            if (holder == null || holder == who || (holder is Object o && o == null)) // previous holder destroyed
            {
                holder = who;
                return true;
            }
            return false;
        }

        public static void Release(object who)
        {
            if (holder == who) holder = null;
        }
    }

    // Moves an aircraft along waypoints without real flight physics: steers like a vehicle on the ground, glides or
    // climbs to each waypoint's height in the air, pitches with its climb rate and banks into turns. Holds behind
    // any other aircraft close ahead, and at a runway entry until the runway is free. Host only.
    public class PathFollower
    {
        public static readonly List<Transform> Movers = new(); // every aircraft that others should keep clear of
        static readonly Dictionary<Transform, Vector3> Travel = new(); // which way each follower last moved

        public float groundTurnRate = 25f;
        public float airTurnRate = 7f;
        public float spacing = 60f;

        float yaw, speed;
        int index;

        public float Speed => speed;
        public int Index => index;

        public void Begin(Transform t, float initialSpeed)
        {
            index = 0;
            yaw = t.eulerAngles.y;
            speed = initialSpeed;
        }

        // Returns true once the last waypoint is reached. reverse = being pushed back (travel opposite the nose).
        public bool Step(Transform t, Waypoint[] route, object owner, float dt, bool reverse = false)
        {
            if (index >= route.Length) return true;
            var wp = route[index];
            bool last = index == route.Length - 1;
            Vector3 pos = t.position;
            Vector3 flat = wp.position - pos;
            flat.y = 0f;
            float dist = flat.magnitude;
            Vector3 heading = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 travel = reverse ? -heading : heading;
            bool airborne = pos.y > 0.5f || wp.position.y > 0.5f;

            float reach = last ? 0.25f : Mathf.Max(3f, speed);
            if (dist < reach || (last && Vector3.Dot(flat, travel) < 0f))
            {
                if (wp.needsRunway && !Runway.TryAcquire(owner)) return false; // wait here (e.g. out in the haze) for the runway
                if (wp.freesRunway) Runway.Release(owner);
                index++;
                if (last) speed = 0f;
                return index >= route.Length;
            }

            bool holding = wp.needsRunway && !Runway.TryAcquire(owner);
            float target = holding ? 0f : wp.speed;
            if (!airborne)
            {
                float headingError = Mathf.Abs(Mathf.DeltaAngle(yaw, Yaw(reverse ? -flat : flat)));
                if (wp.speed < 20f) target = Mathf.Min(target, Mathf.Max(2f, wp.speed * (1f - headingError / 60f))); // slow for corners
                if (last) target = Mathf.Min(target, 0.6f + dist * 0.25f);
                Travel[t] = travel;
                if (BlockedAhead(t, travel)) target = 0f;
            }
            float rate = target > speed ? (target > 20f ? 6f : 2f) : (speed > 20f ? 3.5f : 2.5f);
            speed = Mathf.MoveTowards(speed, target, rate * dt);

            float turn = (airborne ? airTurnRate : groundTurnRate * Mathf.Clamp01(speed / 1.5f)) * dt;
            float before = yaw;
            yaw = Mathf.MoveTowardsAngle(yaw, Yaw(reverse ? -flat : flat), turn);
            heading = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;

            // Reach the waypoint's height as we reach it horizontally: a straight glide or climb.
            float vy = 0f;
            if (airborne) vy = Mathf.Clamp((wp.position.y - pos.y) / Mathf.Max(dist / Mathf.Max(speed, 1f), 0.25f), -8f, 12f);
            Vector3 next = pos + (reverse ? -heading : heading) * (speed * dt) + Vector3.up * (vy * dt);
            if (!airborne) next.y = Mathf.MoveTowards(pos.y, 0f, 3f * dt); // settle the last bit of the flare onto the wheels
            if (next.y < 0f) next.y = 0f;

            if (airborne) Travel.Remove(t);
            float pitch = airborne && speed > 1f ? -Mathf.Atan2(vy, speed) * Mathf.Rad2Deg - 3f : 0f; // nose up when climbing, a little anyway
            float bank = airborne ? Mathf.Clamp(-Mathf.DeltaAngle(before, yaw) / Mathf.Max(dt, 0.0001f) * 2f, -25f, 25f) : 0f;
            t.SetPositionAndRotation(next, Quaternion.Euler(pitch, yaw, bank));
            return false;
        }

        // Someone parked or rolling close in front of us (on the ground)? Wait.
        bool BlockedAhead(Transform self, Vector3 travel)
        {
            foreach (var other in Movers)
            {
                if (other == null || other == self) continue;
                Vector3 d = other.position - self.position;
                if (Mathf.Abs(d.y) > 15f) continue; // one of us is in the air
                d.y = 0f;
                float dist = d.magnitude;
                if (dist < 1f || dist > spacing || Vector3.Dot(travel, d / dist) < 0.85f) continue;
                // Both waiting on each other (pushback meets a taxiing jet): the lower instance id goes first.
                bool mutual = Travel.TryGetValue(other, out var theirs) && Vector3.Dot(theirs, -d / dist) > 0.85f;
                if (!mutual || self.GetInstanceID() > other.GetInstanceID()) return true;
            }
            return false;
        }

        static float Yaw(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
    }

    // The airport's standard routes. Land eastbound on 09, exit at A5, taxi west on A; depart west to A1, take off
    // eastbound. Everyone flows the same way on every piece of tarmac, so nobody meets head-on.
    public static class AirportRoutes
    {
        public const float RunwayZ = 160f, TaxiwayZ = 85f, A1X = -460f, A5X = 260f, StandStopZ = 15f;

        public static Waypoint[] Arrival(float gateX) => new[]
        {
            new Waypoint(-2400f, 160f, RunwayZ, 75f, needsRunway: true), // waits out here (in the haze) for the runway
            new Waypoint(-1500f, 95f, RunwayZ, 72f),
            new Waypoint(-900f, 52f, RunwayZ, 68f),
            new Waypoint(-520f, 7f, RunwayZ, 62f),
            new Waypoint(-440f, 0f, RunwayZ, 58f), // touchdown
            new Waypoint(-150f, 0f, RunwayZ, 32f),
            new Waypoint(150f, 0f, RunwayZ, 14f),
            new Waypoint(A5X - 15f, 0f, RunwayZ, 8f),
            new Waypoint(A5X, 0f, RunwayZ - 14f, 8f),
            new Waypoint(A5X, 0f, TaxiwayZ + 14f, 8f, freesRunway: true),
            new Waypoint(A5X - 14f, 0f, TaxiwayZ, 8f),
            new Waypoint(gateX + 22f, 0f, TaxiwayZ, 7f),
            new Waypoint(gateX, 0f, TaxiwayZ - 13f, 5f),
            new Waypoint(gateX, 0f, 50f, 5f),
            new Waypoint(gateX, 0f, StandStopZ, 4f),
        };

        // Pushed back (reverse), tail swinging east, so the nose ends up pointing west down taxiway A.
        public static Waypoint[] Pushback(float gateX) => new[]
        {
            new Waypoint(gateX, 0f, 58f, 1.8f),
            new Waypoint(gateX + 10f, 0f, 79f, 1.8f),
            new Waypoint(gateX + 22f, 0f, TaxiwayZ, 1.5f),
        };

        public static Waypoint[] Departure(float fromX) => new[]
        {
            new Waypoint(fromX - 30f, 0f, TaxiwayZ, 8f),
            new Waypoint(A1X + 25f, 0f, TaxiwayZ, 8f),
            new Waypoint(A1X, 0f, TaxiwayZ + 14f, 6f),
            new Waypoint(A1X, 0f, RunwayZ - 40f, 5f), // hold short
            new Waypoint(A1X, 0f, RunwayZ - 10f, 4f, needsRunway: true),
            new Waypoint(A1X + 30f, 0f, RunwayZ, 5f), // lined up
            new Waypoint(150f, 0f, RunwayZ, 72f), // takeoff roll
            new Waypoint(450f, 40f, RunwayZ, 78f), // rotate and climb
            new Waypoint(900f, 160f, RunwayZ, 82f, freesRunway: true),
            new Waypoint(2400f, 380f, RunwayZ, 88f),
        };
    }
}
