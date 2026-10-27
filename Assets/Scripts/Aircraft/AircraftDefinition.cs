using UnityEngine;

namespace Turnaround.Aircraft
{
    [CreateAssetMenu(menuName = "Turnaround/Aircraft Definition")]
    public class AircraftDefinition : ScriptableObject
    {
        public string displayName = "AeroJet RJ-200";
        public string shortName = "RJ-200";
        public float taxiSpeed = 6f;
        public float turnRate = 25f; // deg/s at full taxi speed
        public float arrivalDelay = 8f; // seconds after the host starts before it shows up
        public float wheelbase = 10.5f; // nose gear to main gear, drives how the tail swings on pushback
    }
}
