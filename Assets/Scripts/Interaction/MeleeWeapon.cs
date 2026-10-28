using Turnaround.Core;
using Turnaround.Events;
using Turnaround.Player;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Interaction
{
    // Shovel / hammer. Sits next to a CarryableObject: pick it up like anything else, LMB swings instead of throwing.
    // The swinger asks the host; the host checks the hit (a cone in front of the attacker) after the wind-up and
    // ragdolls whoever it connects with. Everyone sees the swing and hears the CLANG.
    [RequireComponent(typeof(CarryableObject))]
    public class MeleeWeapon : NetworkBehaviour
    {
        public float swingCooldown = 0.6f;
        public float swingDuration = 0.35f;
        public float range = 2.2f;
        public float arcDegrees = 55f; // half-angle of the hit cone: wider = easier to hit
        public float knockback = 9f;
        public float upwardForce = 4f;
        public float ragdollDuration = 2.2f;
        public float equipmentShove = 1500f; // impulse when you whack a vehicle or cart instead of a person

        CarryableObject carry;
        float swingStarted = -10f;
        float nextSwing; // swinger-side cooldown
        float serverNextSwing;

        // -1 wound back .. +1 follow-through, 0 at rest. Drives the arm and the tool's angle on every peer.
        public float SwingPose
        {
            get
            {
                float t = (Time.time - swingStarted) / swingDuration;
                if (t < 0f || t > 1.6f) return 0f;
                if (t < 0.35f) return -t / 0.35f; // wind up
                if (t < 0.6f) return Mathf.Lerp(-1f, 1f, (t - 0.35f) / 0.25f); // the chop
                return Mathf.Lerp(1f, 0f, (t - 0.6f) / 1f); // lazy recovery
            }
        }

        void Awake() => carry = GetComponent<CarryableObject>();

        void Update()
        {
            if (carry.IsHeld) carry.HeldTwist = Quaternion.Euler(SwingPose * 75f, 0f, 0f);
        }

        // Called by the holding player when LMB goes down.
        public void TrySwing(Transform eye)
        {
            if (Time.time < nextSwing) return;
            nextSwing = Time.time + swingCooldown;
            SwingRpc(eye.position, eye.forward);
        }

        [Rpc(SendTo.Server)]
        void SwingRpc(Vector3 origin, Vector3 direction, RpcParams rpcParams = default)
        {
            var holder = carry.Holder;
            if (holder == null || holder.OwnerClientId != rpcParams.Receive.SenderClientId || Time.time < serverNextSwing) return;
            if (Vector3.Distance(origin, holder.cameraPivot.position) > 1.5f) origin = holder.cameraPivot.position; // don't trust a far-off origin
            serverNextSwing = Time.time + swingCooldown * 0.9f;
            SwingFxRpc();
            StartCoroutine(HitAfterWindUp(holder, origin, direction.normalized));
        }

        [Rpc(SendTo.Everyone)]
        void SwingFxRpc() => swingStarted = Time.time;

        System.Collections.IEnumerator HitAfterWindUp(PlayerInteraction holder, Vector3 origin, Vector3 dir)
        {
            yield return new WaitForSeconds(swingDuration * 0.45f);
            if (holder == null) yield break;
            var attacker = holder.GetComponent<PlayerController>();

            // People first: the closest one inside the cone.
            PlayerController victim = null;
            float best = float.MaxValue;
            foreach (var p in PlayerController.All)
            {
                if (p == attacker || p.Vehicle != null) continue;
                Vector3 to = p.transform.position + Vector3.up * 1.1f - origin;
                float d = to.magnitude;
                if (d > range + 0.4f || Vector3.Angle(dir, to) > arcDegrees || d >= best) continue;
                best = d;
                victim = p;
            }
            if (victim != null)
            {
                Vector3 away = victim.transform.position - attacker.transform.position;
                away.y = 0f;
                away = away.sqrMagnitude > 0.01f ? away.normalized : attacker.transform.forward;
                victim.GetComponent<PlayerRagdoll>().ServerKnockDown(away * knockback + Vector3.up * upwardForce, ragdollDuration);
                HitFxRpc(victim.transform.position + Vector3.up * 1.6f, true);
                SafetyViolationManager.Report(ViolationKind.HitCoworker, attacker.OwnerClientId, $"with a {carry.displayName.ToLower()}");
                yield break;
            }

            // Otherwise whack whatever is there: equipment gets shoved, luggage gets launched.
            // The head comes down in front of you, so test low (eye-height tests sailed over a tractor's bonnet).
            foreach (var hit in Physics.OverlapSphere(origin + dir * (range * 0.65f) + Vector3.down * 0.7f, 0.9f, Layers.WorldMask, QueryTriggerInteraction.Ignore))
            {
                var shovable = hit.GetComponentInParent<IShovable>();
                if (shovable != null)
                {
                    shovable.ServerShove(dir * equipmentShove);
                    HitFxRpc(hit.ClosestPoint(origin), false);
                    yield break;
                }
                var item = hit.GetComponentInParent<CarryableObject>();
                if (item != null && item != carry)
                {
                    item.ServerRelease(dir * (knockback * 0.8f) + Vector3.up * upwardForce * 0.5f);
                    HitFxRpc(item.transform.position, false);
                    yield break;
                }
            }
        }

        [Rpc(SendTo.Everyone)]
        void HitFxRpc(Vector3 at, bool person) =>
            ProceduralAudio.PlayAt(person ? ProceduralAudio.Clang() : ProceduralAudio.Thud(), at, person ? 1f : 0.7f);
    }
}
