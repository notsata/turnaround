using System.Collections.Generic;
using Turnaround.Core;
using Turnaround.Events;
using Turnaround.Interaction;
using Turnaround.Tasks;
using Turnaround.UI;
using Turnaround.Vehicles;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Turnaround.Player
{
    // Owner-authoritative movement (replicated by an owner-mode NetworkTransform) so it feels instant.
    // Ground/air movement is Quake/CS style: friction + acceleration toward the wish direction on the ground, a tiny
    // air speed cap with high air acceleration in the air. That is what makes air strafing and bunnyhopping work:
    // turning the mouse while strafing adds speed, holding W and spamming jump does not.
    // While seated, the owner pins itself to the vehicle's seat and sends drive input instead of walking.
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : NetworkBehaviour
    {
        public Transform cameraPivot;
        public Camera playerCamera;

        [Header("Ground")]
        public float walkSpeed = 4.5f;
        public float sprintSpeed = 7.5f;
        public float groundAcceleration = 10f; // Quake-style: fraction of wish speed gained per second
        public float friction = 6f;
        public float stopSpeed = 1.5f; // below this, friction acts as if you were going this fast (crisp stops)

        [Header("Air / bunnyhop")]
        public float airAcceleration = 12f;
        public float airSpeedCap = 0.8f; // wish speed allowed in the air: small, so only strafe-turning gains speed
        public float bhopSpeedCap = 11f; // hard ceiling on horizontal speed, however good you are
        public float jumpVelocity = 5.2f; // ~0.6 m hop: still silly, no longer pathetic
        public float jumpBuffer = 0.1f; // a press this long before landing still counts; holding the key does not
        public float gravity = -22f;

        public float lookSensitivity = 0.1f;

        static readonly Color[] VestColors =
        {
            new(1f, 0.55f, 0f), new(0.75f, 1f, 0.1f), new(1f, 0.2f, 0.45f), new(0.2f, 0.85f, 1f),
        };

        public static readonly List<PlayerController> All = new();

        readonly NetworkVariable<float> pitch = new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<bool> sitting = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public static PlayerController Local { get; private set; }
        public bool IsSprinting { get; private set; }
        public bool Sitting => sitting.Value;
        public float HorizontalSpeed => new Vector3(velocity.x, 0f, velocity.z).magnitude; // owner only

        // Set on every peer by VehicleController when the replicated driver changes. The capsule goes off right here:
        // being moved into the seat with it on shoves the vehicle like an immovable object (it launched the belt loader).
        public VehicleController Vehicle
        {
            get => vehicle;
            internal set
            {
                vehicle = value;
                if (value != null) cc.enabled = false;
            }
        }

        // The collider the host's physics sees for this player: our own capsule, or the proxy for a remote player.
        public Collider ServerBody => proxy != null ? proxy.Body : cc;

        VehicleController vehicle;
        CharacterController cc;
        PlayerInteraction interaction;
        PlayerRagdoll ragdoll;
        PlayerProxy proxy;
        VehicleController lastVehicle;
        Transform seatTarget;
        Vector3 eyeLocal;
        Vector3 lastSafePosition;
        float downBlend;
        Vector3 velocity;
        Vector3 beltVelocity;
        float localPitch;
        float seatYaw;
        float sprintBlend;
        float nextPush;
        float jumpQueuedUntil;
        bool wasDown;

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            interaction = GetComponent<PlayerInteraction>();
            ragdoll = GetComponent<PlayerRagdoll>();
            eyeLocal = cameraPivot.localPosition;
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            playerCamera.enabled = IsOwner;
            playerCamera.GetComponent<AudioListener>().enabled = IsOwner;
            playerCamera.GetComponent<LowResRenderer>().enabled = IsOwner;
            GetComponent<PlayerAnimator>().SetVestColor(VestColors[OwnerClientId % (ulong)VestColors.Length]);
            Debug.Log($"[TURNAROUND] Crew member {OwnerClientId} clocked in (local={IsOwner})");

            if (!IsOwner)
            {
                if (IsServer)
                {
                    cc.enabled = false; // the host never uses a remote player's CharacterController; the proxy stands in
                    proxy = PlayerProxy.Create(this, cc);
                }
                return;
            }
            Local = this;
            GetComponent<PlayerAnimator>().HideHeadForFirstPerson();
            GoToSpawn(ShiftManager.InLobby);
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            if (Local == this) Local = null;
            if (proxy != null) Destroy(proxy.gameObject);
        }

        // Owner only. Lobby spawn inside the terminal before the shift, ramp spawn after.
        public void GoToSpawn(bool lobby)
        {
            var spawn = GameObject.Find(lobby ? "LobbySpawn" : "PlayerSpawn") ?? GameObject.Find("PlayerSpawn");
            Vector3 basePos = spawn ? spawn.transform.position : Vector3.up;
            Vector3 side = spawn ? spawn.transform.right : Vector3.right;
            sitting.Value = false;
            seatTarget = null;
            velocity = Vector3.zero;
            Teleport(basePos + side * (1.5f * (OwnerClientId % 4)), spawn ? spawn.transform.rotation : Quaternion.identity);
        }

        public void Teleport(Vector3 position, Quaternion rotation)
        {
            cc.enabled = false;
            transform.SetPositionAndRotation(position, rotation);
            cc.enabled = Vehicle == null && !ragdoll.IsDown;
            lastSafePosition = position;
        }

        // Owner only: plonk down on a chair until you move.
        public void SitOn(Transform seat)
        {
            if (Vehicle != null || ragdoll.IsDown) return;
            seatTarget = seat;
            sitting.Value = true;
            velocity = Vector3.zero;
        }

        void FixedUpdate()
        {
            if (proxy != null) proxy.Follow(transform.position, transform.rotation, Vehicle == null && !ragdoll.IsDown);
        }

        void Update()
        {
            if (!IsOwner)
            {
                cameraPivot.localRotation = Quaternion.Euler(pitch.Value, 0f, 0f);
                if (!IsServer) cc.enabled = Vehicle == null && !ragdoll.IsDown; // clients: just something to bump into
                return;
            }

            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null)
            {
                FollowRagdoll(); // headless runs still need to come back from a knockdown
                return;
            }

            if (Vehicle != lastVehicle)
            {
                if (lastVehicle != null && Vehicle == null) Teleport(lastVehicle.exitPoint.position, Quaternion.Euler(0f, lastVehicle.transform.eulerAngles.y, 0f));
                lastVehicle = Vehicle;
                seatYaw = 0f;
                velocity = Vector3.zero;
            }

            bool inputEnabled = !GameUI.MenuOpen;
            Cursor.lockState = inputEnabled ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !inputEnabled;

            Vector2 look = inputEnabled ? mouse.delta.ReadValue() * lookSensitivity : Vector2.zero;
            localPitch = Mathf.Clamp(localPitch - look.y, -85f, 85f);
            float x = inputEnabled ? (kb.dKey.isPressed ? 1 : 0) - (kb.aKey.isPressed ? 1 : 0) : 0f;
            float y = inputEnabled ? (kb.wKey.isPressed ? 1 : 0) - (kb.sKey.isPressed ? 1 : 0) : 0f;
            bool jumpPressed = inputEnabled && kb.spaceKey.wasPressedThisFrame;

            if (Vehicle != null)
            {
                cc.enabled = false;
                seatYaw = Mathf.Clamp(seatYaw + look.x, -160f, 160f);
                Vehicle.SendInput(y, x, inputEnabled && kb.spaceKey.isPressed);
                IsSprinting = false;
                ApplyCameraPitch(0f);
                return;
            }

            transform.Rotate(0f, look.x, 0f);
            if (FollowRagdoll()) return;

            if (Sitting)
            {
                if (x != 0f || y != 0f || jumpPressed || seatTarget == null) sitting.Value = false; // get up by moving
                else
                {
                    cc.enabled = false;
                    transform.position = seatTarget.position;
                    ApplyCameraPitch(0f);
                    return;
                }
                cc.enabled = true;
            }

            bool sprint = inputEnabled && kb.leftShiftKey.isPressed;
            if (jumpPressed) jumpQueuedUntil = Time.time + jumpBuffer;

            float weightFactor = interaction.Held != null ? interaction.Held.SpeedMultiplier : 1f;
            IsSprinting = sprint && y > 0 && weightFactor > 0.6f; // no sprinting with heavy bags
            Vector3 wish = transform.TransformDirection(new Vector3(x, 0f, y));
            Vector3 wishDir = wish.sqrMagnitude > 0.01f ? wish.normalized : Vector3.zero;
            float wishSpeed = wishDir == Vector3.zero ? 0f : (IsSprinting ? sprintSpeed : walkSpeed) * weightFactor;
            float grip = RandomEventManager.Raining ? 0.3f : 1f; // wet concrete: less friction, less bite

            Vector3 horizontal = new(velocity.x, 0f, velocity.z);
            float vertical = velocity.y;
            if (cc.isGrounded)
            {
                lastSafePosition = transform.position;
                if (Time.time <= jumpQueuedUntil)
                {
                    // Jumping on the frame you land skips friction: that's the whole trick of bunnyhopping.
                    jumpQueuedUntil = 0f;
                    vertical = jumpVelocity * Mathf.Lerp(0.6f, 1f, weightFactor);
                }
                else
                {
                    ApplyFriction(ref horizontal, friction * grip);
                    vertical = -2f;
                }
                Accelerate(ref horizontal, wishDir, wishSpeed, wishSpeed, groundAcceleration * grip);
            }
            else
            {
                // Air: wish speed capped tiny for the "is there room to add?" test, full wish speed for how much is added.
                Accelerate(ref horizontal, wishDir, Mathf.Min(wishSpeed, airSpeedCap), wishSpeed, airAcceleration);
            }
            if (horizontal.magnitude > bhopSpeedCap) horizontal = horizontal.normalized * bhopSpeedCap;
            vertical += gravity * Time.deltaTime;

            // beltVelocity was set by last frame's collisions: standing on a running belt carries you along.
            Vector3 carried = beltVelocity;
            beltVelocity = Vector3.zero;
            cc.Move((horizontal + Vector3.up * vertical + carried) * Time.deltaTime);
            // Keep our own horizontal velocity (cc.velocity would forget bunnyhop speed when grazing walls),
            // but stop dead when the move was actually blocked.
            Vector3 moved = new(cc.velocity.x - carried.x, 0f, cc.velocity.z - carried.z);
            if (moved.sqrMagnitude < horizontal.sqrMagnitude * 0.25f) horizontal = moved;
            if ((cc.collisionFlags & CollisionFlags.Above) != 0 && vertical > 0f) vertical = 0f;
            velocity = new Vector3(horizontal.x, vertical, horizontal.z);
            if (transform.position.y < -20f) Teleport(lastSafePosition + Vector3.up, transform.rotation); // fell out of the world

            // Sprinting is slightly uncontrolled: the camera rolls around.
            sprintBlend = Mathf.MoveTowards(sprintBlend, IsSprinting ? 1f : 0f, Time.deltaTime * 3f);
            ApplyCameraPitch(Mathf.Sin(Time.time * 11f) * 2.5f * sprintBlend);
        }

        static void ApplyFriction(ref Vector3 v, float amount)
        {
            float speed = v.magnitude;
            if (speed < 0.01f)
            {
                v = Vector3.zero;
                return;
            }
            float drop = Mathf.Max(speed, 1.5f) * amount * Time.deltaTime;
            v *= Mathf.Max(speed - drop, 0f) / speed;
        }

        static void Accelerate(ref Vector3 v, Vector3 wishDir, float capSpeed, float wishSpeed, float accel)
        {
            if (wishDir == Vector3.zero) return;
            float add = capSpeed - Vector3.Dot(v, wishDir);
            if (add <= 0f) return;
            v += wishDir * Mathf.Min(accel * wishSpeed * Time.deltaTime, add);
        }

        // Owner: while knocked down the capsule is off and we ride along with the flopping torso; when it ends, stand
        // up on real ground (never underground). Returns true while down.
        bool FollowRagdoll()
        {
            bool down = ragdoll.IsDown;
            downBlend = Mathf.MoveTowards(downBlend, down ? 1f : 0f, Time.deltaTime * 4f);
            cameraPivot.localPosition = Vector3.Lerp(eyeLocal, new Vector3(0f, 0.35f, 0f), downBlend); // eyes near the concrete
            if (down)
            {
                cc.enabled = false;
                sitting.Value = false;
                if (ragdoll.HasBody)
                {
                    Vector3 c = ragdoll.Center;
                    if (c.y < -5f) ragdoll.ForceEnd(); // tunnelled through something: give up on this flop
                    else transform.position = c - Vector3.up * 0.3f;
                }
                velocity = Vector3.zero;
                ApplyCameraPitch(0f);
                wasDown = true;
                return true;
            }
            if (wasDown)
            {
                wasDown = false;
                StandUp();
            }
            return false;
        }

        void StandUp()
        {
            Vector3 from = transform.position + Vector3.up * 1.5f;
            if (Physics.Raycast(from, Vector3.down, out var hit, 6f, Layers.WorldMask, QueryTriggerInteraction.Ignore))
                Teleport(hit.point + Vector3.up * 0.05f, Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
            else
                Teleport(lastSafePosition + Vector3.up * 0.2f, Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
            Debug.Log($"[TURNAROUND] Got back up at y={transform.position.y:F2}");
        }

        void ApplyCameraPitch(float roll)
        {
            cameraPivot.localRotation = Quaternion.Euler(localPitch, 0f, roll);
            if (Mathf.Abs(pitch.Value - localPitch) > 0.5f) pitch.Value = localPitch;
        }

        // After the vehicle has moved this frame, sit in it.
        void LateUpdate()
        {
            if (!IsOwner || Vehicle == null) return;
            cc.enabled = false;
            var seat = Vehicle.seatAnchor;
            transform.SetPositionAndRotation(seat.position, seat.rotation * Quaternion.Euler(0f, seatYaw, 0f));
        }

        void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (!IsOwner) return;
            var surface = hit.collider.GetComponentInParent<ISurfaceMotion>();
            if (surface != null && hit.normal.y > 0.5f) beltVelocity = surface.SurfaceVelocity;

            // CharacterController doesn't push rigidbodies, and clients can't simulate them anyway: ask the host.
            if (Time.time < nextPush || hit.moveDirection.y < -0.5f) return;
            var pushable = hit.collider.GetComponentInParent<IPushable>();
            if (pushable == null) return;
            nextPush = Time.time + 0.25f;
            pushable.Push(hit.moveDirection * HorizontalSpeed);
        }

        // Who does this collider belong to? Works for capsules and the host-side proxies.
        public static PlayerController FromCollider(Collider c)
        {
            if (c == null) return null;
            var p = c.GetComponentInParent<PlayerController>();
            if (p != null) return p;
            var proxy = c.GetComponent<PlayerProxy>();
            return proxy != null ? proxy.Player : null;
        }
    }
}
