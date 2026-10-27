using UnityEngine;

namespace Turnaround.Aircraft
{
    [CreateAssetMenu(menuName = "Turnaround/Flight Definition")]
    public class FlightDefinition : ScriptableObject
    {
        public int flightNumber = 3821;
        public string gate = "B4";
        public string outboundDestination = "IND";
        public int arrivalMinuteOfDay = 14 * 60 + 8; // 14:08, the moment the aircraft parks
        public float scheduledTurnMinutes = 30f;
        public float realSecondsPerGameMinute = 30f; // 30-minute turn = 15 real minutes
        public int basePayment = 4200;
    }
}
