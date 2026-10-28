using Turnaround.Interaction;
using UnityEngine;
using UnityEngine.Rendering;

namespace Turnaround.Player
{
    // Procedural, intentionally awkward animation. Runs on every peer from the replicated transform.
    public class PlayerAnimator : MonoBehaviour
    {
        public Transform body, armL, armR, legL, legR, head;
        public Renderer[] vestRenderers;

        PlayerInteraction interaction;
        PlayerController controller;
        PlayerRagdoll ragdoll;
        Vector3 lastPosition;
        float speed, verticalSpeed, phase;

        void Awake()
        {
            interaction = GetComponent<PlayerInteraction>();
            controller = GetComponent<PlayerController>();
            ragdoll = GetComponent<PlayerRagdoll>();
            lastPosition = transform.position;
        }

        public void SetVestColor(Color color)
        {
            foreach (var r in vestRenderers) r.material.color = color;
        }

        // Owner keeps the head's shadow but doesn't render it into their own camera.
        public void HideHeadForFirstPerson()
        {
            foreach (var r in head.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
        }

        void LateUpdate()
        {
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            Vector3 delta = (transform.position - lastPosition) / dt;
            lastPosition = transform.position;
            speed = Mathf.Lerp(speed, new Vector3(delta.x, 0f, delta.z).magnitude, dt * 10f);
            verticalSpeed = Mathf.Lerp(verticalSpeed, delta.y, dt * 10f);

            if (ragdoll.IsDown || ragdoll.HasBody) return; // physics owns the limbs right now

            if (controller.Vehicle != null || controller.Sitting)
            {
                // Seated: knees up, both hands on the wheel, slight anxious bob.
                legL.localRotation = legR.localRotation = Quaternion.Euler(-85f, 0f, 0f);
                armL.localRotation = Quaternion.Euler(-55f, 0f, 10f);
                armR.localRotation = Quaternion.Euler(-55f, 0f, -10f);
                body.localRotation = Quaternion.Euler(Mathf.Sin(Time.time * 9f) * Mathf.Min(speed, 2f), 0f, 0f);
                return;
            }

            phase += speed * dt * 2.2f;
            float s = Mathf.Sin(phase);
            bool airborne = Mathf.Abs(verticalSpeed) > 1f;
            var held = interaction.Held;
            var melee = held != null ? held.GetComponent<MeleeWeapon>() : null;

            // Arms swing way too far, more when sprinting.
            float armSwing = s * Mathf.Min(speed * 14f, 95f);
            float legSwing = s * Mathf.Min(speed * 9f, 45f);
            if (melee != null)
            {
                // Two-handed chop: wind back, then bring it down hard; the off hand helps a bit.
                float chop = melee.SwingPose; // -1 wound back .. +1 follow-through
                armR.localRotation = Quaternion.Euler(-100f + chop * 70f, 0f, 10f);
                armL.localRotation = Quaternion.Euler(-80f + chop * 50f, 0f, -20f);
            }
            else if (held != null)
            {
                float reach = held.weight == CarryWeight.Heavy ? -60f : -80f;
                armL.localRotation = Quaternion.Euler(reach + s * 6f, 0f, -8f);
                armR.localRotation = Quaternion.Euler(reach - s * 6f, 0f, 8f);
            }
            else if (airborne)
            {
                armL.localRotation = Quaternion.Euler(0f, 0f, -150f); // flail
                armR.localRotation = Quaternion.Euler(0f, 0f, 150f);
            }
            else
            {
                armL.localRotation = Quaternion.Euler(armSwing, 0f, -6f);
                armR.localRotation = Quaternion.Euler(-armSwing, 0f, 6f);
            }

            legL.localRotation = Quaternion.Euler(airborne ? -25f : -legSwing, 0f, airborne ? -15f : 0f);
            legR.localRotation = Quaternion.Euler(airborne ? 10f : legSwing, 0f, airborne ? 15f : 0f);

            // Carrying = waddle; heavy = lean back like it's a fridge.
            float lean = held == null ? Mathf.Min(speed * 1.5f, 12f) : held.weight == CarryWeight.Heavy ? -16f : -6f;
            float waddle = held != null ? s * 10f : s * Mathf.Min(speed, 3f);
            body.localRotation = Quaternion.Euler(lean, 0f, waddle);
        }
    }
}
