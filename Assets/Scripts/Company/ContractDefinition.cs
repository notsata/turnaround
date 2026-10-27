using UnityEngine;

namespace Turnaround.Company
{
    [CreateAssetMenu(menuName = "Turnaround/Contract")]
    public class ContractDefinition : ScriptableObject
    {
        public string id;
        public string airline;
        public string aircraft = "RJ-200";
        public int turnsPerDay = 3;
        [Range(0f, 1f)] public float minimumOtp = 0.85f;
        public int paymentPerTurn = 4200;
        public int damageTolerance = 1500; // per turn, before it counts as a strike
        public int reputationRequired;
        public int flightNumberBase = 3821;
        public string destination = "IND";
    }
}
