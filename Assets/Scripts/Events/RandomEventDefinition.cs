using UnityEngine;

namespace Turnaround.Events
{
    public enum RandomEventId { LateBags, BrokenGpu, MissingBag, OversizedBag, Rain, Lightning, FuelDelay, RunawayCart, CaptainImpatient }

    [CreateAssetMenu(menuName = "Turnaround/Random Event")]
    public class RandomEventDefinition : ScriptableObject
    {
        public RandomEventId id;
        public string displayName;
        public float weight = 1f;
        public string speaker = "OPS";
        [TextArea] public string message;
        [Range(0f, 1f)] public float earliest = 0.15f; // as a fraction of the scheduled turn
        [Range(0f, 1f)] public float latest = 0.7f;
    }
}
