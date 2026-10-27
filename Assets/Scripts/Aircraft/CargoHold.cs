using System.Collections.Generic;
using Turnaround.Core;
using Turnaround.Interaction;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Aircraft
{
    // Trigger box marking the hold volume. Host polls what's inside twice a second (simpler than trigger
    // events, which don't fire when a held item's collider is switched off).
    [RequireComponent(typeof(BoxCollider))]
    public class CargoHold : NetworkBehaviour
    {
        public string holdName = "Forward Hold";

        readonly NetworkVariable<int> inboundCount = new();
        readonly NetworkVariable<int> outboundCount = new();
        readonly NetworkVariable<int> wrongCount = new();
        readonly HashSet<CarryableObject> found = new();
        BoxCollider volume;
        AircraftController aircraft;
        bool departed;

        public int InboundCount => inboundCount.Value;
        public int OutboundCount => outboundCount.Value; // bags for this flight
        public int WrongCount => wrongCount.Value; // other flights, untagged bags

        void Awake()
        {
            volume = GetComponent<BoxCollider>();
            volume.isTrigger = true;
            aircraft = GetComponentInParent<AircraftController>();
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer) InvokeRepeating(nameof(Recount), 0.5f, 0.5f);
        }

        public override void OnNetworkDespawn() => CancelInvoke();

        // Grid of spawn slots inside the hold, bags long side along the fuselage. Layer 3 is a tight squeeze on purpose.
        public Vector3 SpawnPoint(int i) =>
            transform.TransformPoint(new Vector3(-0.66f + (i % 4) * 0.44f, -0.3f + (i / 24) * 0.33f, -1.4f + (i / 4 % 6) * 0.56f));

        void Recount()
        {
            if (departed) return; // keep the final tally once it has left
            BoxScan.Collect(volume, found);
            int inbound = 0, outbound = 0, wrong = 0;
            foreach (var item in found)
            {
                if (item.Bag == null || item.IsHeld) continue; // cones in the hold are just sad, not counted
                if (item.Bag.Inbound) inbound++;
                else if (item.Bag.Flight == aircraft.FlightNumber) outbound++;
                else wrong++;
            }
            if (inboundCount.Value != inbound) inboundCount.Value = inbound;
            if (outboundCount.Value != outbound) outboundCount.Value = outbound;
            if (wrongCount.Value != wrong) wrongCount.Value = wrong;
        }

        public void ServerReset()
        {
            departed = false;
            inboundCount.Value = outboundCount.Value = wrongCount.Value = 0;
        }

        // Called by the host at pushback: whatever is in the hold leaves with the flight.
        public void ServerDepartContents()
        {
            Recount();
            departed = true;
            foreach (var item in found) item.NetworkObject.Despawn();
            found.Clear();
        }
    }
}
