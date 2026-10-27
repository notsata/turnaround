using System.Collections.Generic;
using Turnaround.Baggage;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Vehicles
{
    // Rear tow point on a tractor or cart. Occupancy is derived from the carts' replicated hitch state.
    public class TrailerHitch : MonoBehaviour
    {
        public static readonly List<TrailerHitch> All = new();

        public Rigidbody Body { get; private set; }
        public NetworkObject Owner { get; private set; }

        void Awake()
        {
            Body = GetComponentInParent<Rigidbody>();
            Owner = GetComponentInParent<NetworkObject>();
        }

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        public bool IsOccupied
        {
            get
            {
                foreach (var cart in BaggageCart.All)
                    if (cart.HitchedToId == Owner.NetworkObjectId) return true;
                return false;
            }
        }
    }
}
