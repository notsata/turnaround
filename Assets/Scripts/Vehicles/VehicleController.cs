using System.Linq;
using Turnaround.Interaction;
using Turnaround.Player;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Vehicles
{
    // Arcade vehicle with one seat. The host simulates it (so hitched carts and bags stay consistent);
    // the driver only sends throttle/steer/brake when keys change.
    // shortcut: clients feel one round-trip of input lag; fine on LAN, revisit with prediction if online play matters.
    [RequireComponent(typeof(Rigidbody))]
    public class VehicleController : NetworkBehaviour, IInteractable, IPushable, IShovable
    {
        public const ulong NoDriver = ulong.MaxValue;

        public string displayName = "Baggage Tractor";
        public Company.EquipmentKind kind = Company.EquipmentKind.Tractor; // which company-owned item this is
        public Transform seatAnchor;
        public Transform exitPoint;
        public float maxSpeed = 7f;
        public float maxReverse = 3f;
        public float acceleration = 3.5f;
        public float brakeDecel = 6f; // old equipment: brakes are a suggestion
        public float coastDecel = 1.5f;
        public float turnRate = 70f; // deg/s at speed
        public float grip = 12f; // how fast sideways slide is killed; lower = more drift
        [Range(0f, 1f)] public float reliability = 1f; // below 1: hard starting and the odd stall
        public float steerResponse = 6f; // how fast the wheel follows the input; low = sluggish old tug
        public float parkedDecel = 0.6f; // unattended: brakes "on", but a few hard shoves still nudge it along
        public float pushImpulse = 450f; // N*s from a walking player; mass decides how much that actually does
        public float maxRiseSpeed = 2f; // nothing on the ramp should go up faster than this

        public static readonly System.Collections.Generic.List<VehicleController> All = new();

        static AudioClip engineClip;

        readonly NetworkVariable<ulong> driverId = new(NoDriver); // driver's player NetworkObjectId
        readonly NetworkVariable<bool> stalled = new();
        float stallUntil; // host only
        float steerApplied;

        Rigidbody rb;
        readonly Core.PhysicsSanity sanity = new();
        AudioSource engine;
        PlayerController driver;
        float throttle, steer;
        bool brake;
        float sentThrottle, sentSteer;
        bool sentBrake;
        Vector3 lastPosition;

        public bool HasDriver => driverId.Value != NoDriver;
        public PlayerController Driver => driver;
        public IVehicleAction Action { get; private set; } // optional extra controls (e.g. the tug's towbar)
        public bool InputLocked { get; set; } // host only: brakes held regardless of the driver
        public float Speed { get; private set; } // measured from movement, valid on every peer
        public bool Stalled => stalled.Value;
        public float ServerSpeed => rb.linearVelocity.magnitude; // host only; unlike Speed, ignores snaps and teleports

        // Extra line for the driver's prompt when the machine is being difficult.
        public string StatusText =>
            Stalled ? "Engine won't catch... (cranking)" : Events.RandomEventManager.RampClosed ? "Ramp closed: lightning. Sit tight." : null;

        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            // Off-centre or tilted colliders (the belt loader's belt) tilt the inertia frame; with pitch/roll frozen and the
            // yaw rate forced, a tilted frame turns steering into lift. Keep the principal axes upright. Setting the rotation
            // switches off automatic inertia and resets the tensor to 1, so put the computed one back.
            var inertia = rb.inertiaTensor;
            rb.inertiaTensorRotation = Quaternion.identity;
            rb.inertiaTensor = inertia;
            Action = GetComponent<IVehicleAction>();
            engine = gameObject.AddComponent<AudioSource>();
            engine.clip = EngineClip();
            engine.loop = true;
            engine.spatialBlend = 1f;
            engine.minDistance = 3f;
            engine.maxDistance = 40f;
            engine.dopplerLevel = 0f;
            engine.volume = 0f;
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            rb.isKinematic = !IsServer;
            driverId.OnValueChanged += OnDriverChanged;
            ApplyDriver();
            lastPosition = transform.position;
            engine.Play();
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            driverId.OnValueChanged -= OnDriverChanged;
            if (driver != null && driver.Vehicle == this) driver.Vehicle = null;
        }

        void OnDriverChanged(ulong previous, ulong current) => ApplyDriver();

        void ApplyDriver()
        {
            if (driver != null && driver.Vehicle == this) driver.Vehicle = null;
            driver = null;
            TryResolveDriver();
            if (IsServer && HasDriver && Random.value > reliability) Stall(Random.Range(1.5f, 3.5f)); // difficult starting
        }

        void Stall(float seconds)
        {
            stalled.Value = true;
            stallUntil = Time.time + seconds;
        }

        bool TryResolveDriver()
        {
            if (driver != null) return true;
            if (!HasDriver || !NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(driverId.Value, out var obj)) return false;
            driver = obj.GetComponent<PlayerController>();
            if (driver != null) driver.Vehicle = this;
            return driver != null;
        }

        void Update()
        {
            if (!IsSpawned) return;
            if (HasDriver && !TryResolveDriver() && IsServer) ClearDriver(); // driver disconnected

            float measured = (transform.position - lastPosition).magnitude / Mathf.Max(Time.deltaTime, 0.0001f);
            lastPosition = transform.position;
            Speed = Mathf.Lerp(Speed, measured, Time.deltaTime * 5f);
            if (IsServer && Stalled && Time.time >= stallUntil) stalled.Value = false;
            // Cranking sounds like a dying animal; running sounds like a slightly less dying animal.
            engine.volume = !HasDriver ? 0f : Stalled ? 0.2f + Mathf.Abs(Mathf.Sin(Time.time * 12f)) * 0.2f : 0.35f;
            engine.pitch = Stalled ? 0.35f + Mathf.Sin(Time.time * 25f) * 0.1f : 0.7f + Speed * 0.12f;
        }

        void FixedUpdate()
        {
            if (!IsServer || !IsSpawned) return;
            if (sanity.Check(rb)) // rescued after a glitch: let go of anything we were towing
                foreach (var cart in Baggage.BaggageCart.All.ToArray())
                    if (cart.HitchedToId == NetworkObjectId) cart.ServerUnhitch();
            float dt = Time.fixedDeltaTime;
            Vector3 v = rb.linearVelocity;
            if (v.y > maxRiseSpeed) v.y = maxRiseSpeed; // last line of defence against any overlap launching us
            Vector3 forward = transform.forward;
            float along = Vector3.Dot(v, forward);
            Vector3 lateral = v - forward * along;
            lateral.y = 0f;

            if (HasDriver && !Stalled && Random.value < (1f - reliability) * 0.0004f) Stall(Random.Range(2f, 4f)); // ~every 2 min for a 0.55
            bool raining = Events.RandomEventManager.Raining;
            bool parked = !HasDriver || InputLocked || Stalled || Events.RandomEventManager.RampClosed;
            float target = parked || brake ? 0f : throttle * (throttle > 0f ? maxSpeed : maxReverse);
            float rate = !HasDriver ? parkedDecel : parked || brake ? brakeDecel * (raining ? 0.5f : 1f) : throttle != 0f ? acceleration : coastDecel;
            along = Mathf.MoveTowards(along, target, rate * dt);
            lateral = Vector3.MoveTowards(lateral, Vector3.zero, grip * (raining ? 0.3f : 1f) * dt);
            rb.linearVelocity = forward * along + lateral + Vector3.up * v.y;

            steerApplied = Mathf.MoveTowards(steerApplied, parked ? 0f : steer, steerResponse * dt);
            float yawRate = steerApplied * turnRate * Mathf.Clamp(along / 2.5f, -1f, 1f);
            rb.angularVelocity = new Vector3(0f, yawRate * Mathf.Deg2Rad, 0f);
        }

        // A walking player bumping an unattended vehicle. Mass-scaled, so it takes a lot of bumping.
        public void Push(Vector3 velocity) => PushRpc(velocity);

        [Rpc(SendTo.Server)]
        void PushRpc(Vector3 velocity)
        {
            if (HasDriver) return;
            velocity.y = 0f;
            ServerShove(velocity.normalized * pushImpulse * Mathf.Clamp01(velocity.magnitude / 4f));
        }

        public void ServerShove(Vector3 impulse)
        {
            if (!IsServer || HasDriver) return;
            impulse.y = 0f;
            rb.AddForce(impulse, ForceMode.Impulse);
        }

        // Bankruptcy/run reset: kick the driver out.
        public void ServerEjectDriver()
        {
            if (IsServer && HasDriver) ClearDriver();
        }

        public void ServerClearFaults() => stalled.Value = false;

        void OnCollisionEnter(Collision collision)
        {
            if (IsServer && IsSpawned) Events.ImpactRules.VehicleHit(collision, transform, displayName, NetworkObjectId, kind);
        }

        // Called by the driving player every frame; only actually sends when something changed.
        public void SendInput(float newThrottle, float newSteer, bool newBrake)
        {
            if (newThrottle == sentThrottle && newSteer == sentSteer && newBrake == sentBrake) return;
            sentThrottle = newThrottle;
            sentSteer = newSteer;
            sentBrake = newBrake;
            DriveInputRpc(newThrottle, newSteer, newBrake);
        }

        [Rpc(SendTo.Server)]
        void DriveInputRpc(float newThrottle, float newSteer, bool newBrake, RpcParams rpcParams = default)
        {
            if (driver == null || driver.OwnerClientId != rpcParams.Receive.SenderClientId) return;
            throttle = Mathf.Clamp(newThrottle, -1f, 1f);
            steer = Mathf.Clamp(newSteer, -1f, 1f);
            brake = newBrake;
        }

        public string GetPrompt(PlayerInteraction player) =>
            HasDriver || player.Held != null ? null : $"[E] Drive {displayName}";

        public void Interact(PlayerInteraction player) => EnterRpc();

        [Rpc(SendTo.Server)]
        void EnterRpc(RpcParams rpcParams = default)
        {
            if (HasDriver) return;
            var player = PlayerInteraction.ServerGetPlayer(NetworkManager, rpcParams.Receive.SenderClientId, seatAnchor.position, 2.5f);
            if (player == null || player.Held != null || player.GetComponent<PlayerController>().Vehicle != null) return;
            driverId.Value = player.NetworkObjectId;
            ApplyDriver();
        }

        public void RequestExit()
        {
            sentThrottle = sentSteer = 0f;
            sentBrake = false;
            ExitRpc();
        }

        [Rpc(SendTo.Server)]
        void ExitRpc(RpcParams rpcParams = default)
        {
            if (driver != null && driver.OwnerClientId == rpcParams.Receive.SenderClientId) ClearDriver();
        }

        void ClearDriver()
        {
            driverId.Value = NoDriver;
            throttle = steer = 0f;
            brake = false;
            ApplyDriver();
        }

        // Generated so there's no audio asset to ship: a buzzy 60 Hz saw with grit. Exactly 30 cycles, so it loops clean.
        static AudioClip EngineClip()
        {
            if (engineClip != null) return engineClip;
            const int rate = 22050, samples = rate / 2;
            var data = new float[samples];
            var rng = new System.Random(7);
            for (int i = 0; i < samples; i++)
            {
                float saw = (i * 60f / rate) % 1f * 2f - 1f;
                data[i] = saw * 0.5f + (float)(rng.NextDouble() * 2 - 1) * 0.15f;
            }
            engineClip = AudioClip.Create("Engine", samples, 1, rate, false);
            engineClip.SetData(data, 0);
            return engineClip;
        }
    }
}
