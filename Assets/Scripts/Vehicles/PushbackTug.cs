using Turnaround.Aircraft;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Vehicles
{
    // Driver-only controls: F connects/disconnects the towbar at the nose gear, right mouse asks the captain for push.
    // While connected on stand the tug is held still (brakes on) so the towbar can't shear before pushback.
    [RequireComponent(typeof(VehicleController))]
    public class PushbackTug : NetworkBehaviour, IVehicleAction
    {
        public Transform towPoint;
        public float connectRange = 2.5f;

        public static PushbackTug Instance { get; private set; }

        readonly NetworkVariable<bool> connected = new();
        VehicleController vehicle;
        AircraftController aircraft;

        public bool IsConnected => connected.Value;

        // Clients get fresh network copies of scene objects, so look the aircraft up instead of referencing it.
        AircraftController Aircraft => aircraft != null ? aircraft : aircraft = FindFirstObjectByType<AircraftController>();

        void Awake() => vehicle = GetComponent<VehicleController>();

        public override void OnNetworkSpawn() => Instance = this;

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (!IsServer || !IsSpawned) return;
            vehicle.InputLocked = IsConnected && Aircraft != null && Aircraft.State != AircraftState.Pushback;
        }

        public void ServerReset()
        {
            if (IsServer) connected.Value = false;
        }

        bool InRange(AircraftController ac) => Vector3.Distance(towPoint.position, ac.transform.position) < connectRange;

        public string GetActionPrompt(bool alternate)
        {
            var ac = Aircraft;
            if (ac == null) return null;
            if (!alternate)
            {
                if (!IsConnected) return ac.IsStationary ? InRange(ac) ? "[F] Connect Towbar" : "Line up the tow point with the nose gear" : null;
                if (ac.State == AircraftState.Pushback && !ac.PastPushLimit) return "Push back along the guide line onto the taxiway";
                return "[F] Disconnect Towbar";
            }
            if (!IsConnected || !ac.IsStationary) return null;
            if (ac.State != AircraftState.ReadyForPush) return "Captain: not ready (close doors, unplug, finish services)";
            if (ac.ChocksPresent > 0) return "Captain: \"The chocks are still in.\"";
            return "[RMB] Request Pushback";
        }

        public void DoAction(bool alternate)
        {
            if (alternate) RequestPushbackRpc();
            else ToggleTowbarRpc();
        }

        bool SentByDriver(RpcParams rpcParams) =>
            vehicle.Driver != null && vehicle.Driver.OwnerClientId == rpcParams.Receive.SenderClientId;

        [Rpc(SendTo.Server)]
        void ToggleTowbarRpc(RpcParams rpcParams = default)
        {
            var ac = Aircraft;
            if (!SentByDriver(rpcParams) || ac == null) return;
            if (IsConnected)
            {
                if (ac.State == AircraftState.Pushback && !ac.PastPushLimit) return;
                connected.Value = false;
                ac.TowedBy = null;
                return;
            }
            if (!ac.IsStationary || !InRange(ac)) return;
            // Forgiving alignment: slide the tug so the tow point sits exactly on the nose gear.
            var rb = GetComponent<Rigidbody>();
            Vector3 offset = ac.transform.position - towPoint.position;
            offset.y = 0f;
            transform.position += offset;
            Physics.SyncTransforms();
            rb.linearVelocity = Vector3.zero;
            connected.Value = true;
        }

        [Rpc(SendTo.Server)]
        void RequestPushbackRpc(RpcParams rpcParams = default)
        {
            var ac = Aircraft;
            if (!SentByDriver(rpcParams) || ac == null || !IsConnected) return;
            ac.ServerBeginPushback(towPoint);
        }
    }
}
