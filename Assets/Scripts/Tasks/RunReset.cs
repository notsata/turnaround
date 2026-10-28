using System.Collections.Generic;
using Turnaround.Aircraft;
using Turnaround.Baggage;
using Turnaround.Interaction;
using Turnaround.Player;
using Turnaround.Vehicles;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

namespace Turnaround.Tasks
{
    // Bankruptcy: put the whole airport back the way the first shift found it. Host only.
    public static class RunReset
    {
        // Every in-scene physics object (vehicles, carts, GPU, chocks, cones, tools) as it stood when the first shift started.
        static readonly Dictionary<NetworkObject, Pose> start = new();

        public static void ServerSnapshot()
        {
            if (start.Count > 0) return;
            foreach (var no in Object.FindObjectsByType<NetworkObject>(FindObjectsSortMode.None))
                if (no.IsSpawned && no.IsSceneObject == true && no.GetComponent<Rigidbody>() is { isKinematic: false } && no.GetComponent<PlayerController>() == null)
                    start[no] = new Pose(no.transform.position, no.transform.rotation);
        }

        public static void ServerResetRun()
        {
            foreach (var v in VehicleController.All) { v.ServerEjectDriver(); v.ServerClearFaults(); }
            foreach (var c in BaggageCart.All) { c.ServerUnhitch(); c.ServerClearFaults(); }
            foreach (var item in CarryableObject.All.ToArray())
            {
                if (item.IsPinned) item.ServerUnpin(Vector3.zero);
                if (item.IsHeld) item.ServerRelease(Vector3.zero);
            }
            foreach (var (no, pose) in start)
            {
                if (no == null || !no.IsSpawned) continue;
                var rb = no.GetComponent<Rigidbody>();
                rb.linearVelocity = rb.angularVelocity = Vector3.zero;
                rb.position = pose.position;
                rb.rotation = pose.rotation;
                var nt = no.GetComponent<NetworkTransform>();
                if (nt != null) nt.Teleport(pose.position, pose.rotation, no.transform.localScale);
                else no.transform.SetPositionAndRotation(pose.position, pose.rotation);
            }
            TrafficManager.Instance?.ServerClear();
            BaggageManager.Instance?.ServerReset(everything: true);
            ShiftManager.Instance?.ServerReturnToLobby();
            TurnaroundManager.Instance?.ServerResetRun(); // bags, tasks, violations, events, GPU, tug, aircraft
            Debug.Log($"[TURNAROUND] Run reset: {start.Count} objects back in place");
        }
    }
}
