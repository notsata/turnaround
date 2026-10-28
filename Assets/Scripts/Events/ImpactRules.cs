using Turnaround.Aircraft;
using Turnaround.Interaction;
using Turnaround.Player;
using UnityEngine;

namespace Turnaround.Events
{
    // What happens when a moving vehicle or cart hits something. Called from OnCollisionEnter on the host,
    // which is the only peer simulating them.
    public static class ImpactRules
    {
        const float KnockdownSpeed = 2f;
        const float DamageSpeed = 3f;
        const float ImpossibleSpeed = 20f; // nothing on this ramp goes this fast: it's a snap or teleport, not a crash

        public static void VehicleHit(Collision collision, Transform self, string what, ulong offender, Company.EquipmentKind kind)
        {
            float impact = collision.relativeVelocity.magnitude;
            if (impact > ImpossibleSpeed) return;
            var player = PlayerController.FromCollider(collision.collider);
            if (player != null)
            {
                if (player.Vehicle != null || impact < KnockdownSpeed) return;
                Knock(player, self.position, impact);
                SafetyViolationManager.Report(ViolationKind.HitCoworker, offender, $"{what} vs crew member");
                return;
            }

            // Luggage has its own damage model; the ground and gentle bumps don't count.
            if (impact < DamageSpeed || collision.collider.GetComponentInParent<CarryableObject>() != null) return;
            bool aircraft = collision.collider.GetComponentInParent<AircraftController>() != null;
            int dollars = Mathf.RoundToInt((impact - DamageSpeed) * (aircraft ? 900f : 60f)) + (aircraft ? 500 : 0);
            SafetyViolationManager.ReportDamage(dollars, aircraft ? "the aircraft" : $"the {what}");
            Company.CompanyManager.Instance?.ServerWear(kind, dollars / 40f); // a $400 crash costs 10% condition
            if (aircraft) SafetyViolationManager.Report(ViolationKind.HitAircraft, offender, what);
        }

        // Away from whatever hit you, plus a generous amount of up.
        public static void Knock(PlayerController player, Vector3 from, float impact)
        {
            Vector3 away = player.transform.position - from;
            away.y = 0f;
            away = away.sqrMagnitude > 0.01f ? away.normalized : -player.transform.forward;
            player.GetComponent<PlayerRagdoll>().ServerKnockDown(away * Mathf.Min(impact * 1.1f, 9f) + Vector3.up * Mathf.Min(2.5f + impact * 0.4f, 6f));
        }
    }
}
