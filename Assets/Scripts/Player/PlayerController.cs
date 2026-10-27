using Turnaround.Core;
using Turnaround.Equipment;
using Turnaround.Events;
using Turnaround.Interaction;
using Turnaround.UI;
using Turnaround.Vehicles;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Turnaround.Player
{
    // Owner-authoritative movement (replicated by an owner-mode NetworkTransform) so it feels instant.
    // While seated, the owner pins itself to the vehicle's seat and sends drive input instead of walking.
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : NetworkBehaviour
    {
        public Transform cameraPivot;
        public Camera playerCamera;

        public float walkSpeed = 4.5f;
        public float sprintSpeed = 7.5f;
        public float acceleration = 35f;
        public float jumpVelocity = 3.2f; // deliberately pathetic
        public float gravity = -22f;
        public float lookSensitivity = 0.1f;

        static readonly Color[] VestColors =
        {
            new(1f, 0.55f, 0f), new(0.75f, 1f, 0.1f), new(1f, 0.2f, 0.45f), new(0.2f, 0.85f, 1f),
        };

        readonly NetworkVariable<float> pitch = new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public static PlayerController Local { get; private set; }
        public bool IsSprinting { get; private set; }

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

        VehicleController vehicle;

        CharacterController cc;
        PlayerInteraction interaction;
        PlayerRagdoll ragdoll;
        VehicleController lastVehicle;
        Vector3 eyeLocal;
        float downBlend;
        Vector3 velocity;
        Vector3 beltVelocity;
        float localPitch;
        float seatYaw;
        float sprintBlend;
        float nextPush;

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            interaction = GetComponent<PlayerInteraction>();
            ragdoll = GetComponent<PlayerRagdoll>();
            eyeLocal = cameraPivot.localPosition;
        }

        public override void OnNetworkSpawn()
        {
            playerCamera.enabled = IsOwner;
            playerCamera.GetComponent<AudioListener>().enabled = IsOwner;
            playerCamera.GetComponent<LowResRenderer>().enabled = IsOwner;
            GetComponent<PlayerAnimator>().SetVestColor(VestColors[OwnerClientId % (ulong)VestColors.Length]);
            Debug.Log($"[TURNAROUND] Crew member {OwnerClientId} clocked in (local={IsOwner})");

            if (!IsOwner) return;
            Local = this;
            GetComponent<PlayerAnimator>().HideHeadForFirstPerson();
            var spawn = GameObject.Find("PlayerSpawn");
            Vector3 basePos = spawn ? spawn.transform.position : Vector3.up;
            Teleport(basePos + Vector3.right * (1.5f * OwnerClientId), spawn ? spawn.transform.rotation : Quaternion.identity);
        }

        public override void OnNetworkDespawn()
        {
            if (Local == this) Local = null;
        }

        public void Teleport(Vector3 position, Quaternion rotation)
        {
            cc.enabled = false;
            transform.SetPositionAndRotation(position, rotation);
            cc.enabled = true;
        }

        void Update()
        {
            if (!IsOwner)
            {
                cameraPivot.localRotation = Quaternion.Euler(pitch.Value, 0f, 0f);
                cc.enabled = Vehicle == null; // a seated remote capsule would shove the vehicle it's sitting in
                return;
            }

            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

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

            if (ragdoll.PendingKnock != Vector3.zero)
            {
                velocity = ragdoll.PendingKnock;
                ragdoll.PendingKnock = Vector3.zero;
            }
            downBlend = Mathf.MoveTowards(downBlend, ragdoll.IsDown ? 1f : 0f, Time.deltaTime * 4f);
            cameraPivot.localPosition = Vector3.Lerp(eyeLocal, new Vector3(0f, 0.35f, -0.9f), downBlend); // eyes on the concrete
            if (ragdoll.IsDown)
            {
                // Flung: no control, just flight and then a long undignified slide.
                Vector3 flat = new(velocity.x, 0f, velocity.z);
                bool landed = cc.isGrounded && velocity.y <= 0f;
                if (landed) flat = Vector3.MoveTowards(flat, Vector3.zero, 9f * Time.deltaTime);
                float vy = landed ? -2f : velocity.y + gravity * Time.deltaTime;
                cc.Move((flat + Vector3.up * vy) * Time.deltaTime);
                velocity = new Vector3(flat.x, vy, flat.z);
                IsSprinting = false;
                ApplyCameraPitch(70f * downBlend);
                return;
            }

            bool sprint = inputEnabled && kb.leftShiftKey.isPressed;
            bool jump = inputEnabled && kb.spaceKey.wasPressedThisFrame;

            float weightFactor = interaction.Held != null ? interaction.Held.SpeedMultiplier : 1f;
            IsSprinting = sprint && y > 0 && weightFactor > 0.6f; // no sprinting with heavy bags
            float speed = (IsSprinting ? sprintSpeed : walkSpeed) * weightFactor;

            Vector3 target = transform.TransformDirection(new Vector3(x, 0f, y).normalized) * speed;
            float grip = RandomEventManager.Raining ? 0.3f : 1f; // wet concrete: starting and stopping both take a while
            Vector3 horizontal = Vector3.MoveTowards(new Vector3(velocity.x, 0f, velocity.z), target, acceleration * grip * Time.deltaTime);
            float vertical = velocity.y;
            if (cc.isGrounded)
            {
                vertical = -2f;
                if (jump) vertical = jumpVelocity * Mathf.Lerp(0.6f, 1f, weightFactor);
            }
            vertical += gravity * Time.deltaTime;

            // beltVelocity was set by last frame's collisions: standing on a running belt carries you along.
            Vector3 carried = beltVelocity;
            beltVelocity = Vector3.zero;
            cc.Move((horizontal + Vector3.up * vertical + carried) * Time.deltaTime);
            velocity = new Vector3(cc.velocity.x - carried.x, vertical, cc.velocity.z - carried.z);

            // Sprinting is slightly uncontrolled: the camera rolls around.
            sprintBlend = Mathf.MoveTowards(sprintBlend, IsSprinting ? 1f : 0f, Time.deltaTime * 3f);
            ApplyCameraPitch(Mathf.Sin(Time.time * 11f) * 2.5f * sprintBlend);
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
            var belt = hit.collider.GetComponent<BeltLoader>();
            if (belt != null && belt.Running && hit.normal.y > 0.5f) beltVelocity = belt.SurfaceVelocity;

            // CharacterController doesn't push rigidbodies, and clients can't simulate them anyway: ask the host.
            if (Time.time < nextPush || hit.moveDirection.y < -0.5f) return;
            var pushable = hit.collider.GetComponentInParent<IPushable>();
            if (pushable == null) return;
            nextPush = Time.time + 0.25f;
            pushable.Push(hit.moveDirection * new Vector3(velocity.x, 0f, velocity.z).magnitude);
        }
    }
}
