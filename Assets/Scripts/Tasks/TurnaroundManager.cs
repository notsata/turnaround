using System.Text;
using Turnaround.Aircraft;
using Turnaround.Baggage;
using Turnaround.Company;
using Turnaround.Equipment;
using Turnaround.Vehicles;
using Turnaround.Events;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Tasks
{
    // The turnaround clock (parked -> off-block, in compressed game minutes), the end-of-round report, and the
    // flight-to-flight loop: the company picks the next flight, everything per-flight is reset in place.
    // Only timestamps, the arrival time and the finished report text cross the network.
    public class TurnaroundManager : NetworkBehaviour
    {
        public FlightDefinition flight;

        public static TurnaroundManager Instance { get; private set; }

        readonly NetworkVariable<double> parkedAt = new(-1);
        readonly NetworkVariable<double> offBlockAt = new(-1);
        readonly NetworkVariable<FixedString4096Bytes> report = new();
        readonly NetworkVariable<int> arrivalMinute = new(-1);
        readonly NetworkVariable<FixedString64Bytes> airline = new();
        AircraftController aircraft;
        bool flightAssigned;
        int payment; // host only: this flight's contract rate
        float nextAssignTry;

        AircraftController Aircraft => aircraft != null ? aircraft : aircraft = FindFirstObjectByType<AircraftController>();

        public bool ClockRunning => parkedAt.Value >= 0;
        public string Report => report.Value.ToString();
        public string Airline => airline.Value.ToString();
        public bool HasFlight => arrivalMinute.Value >= 0;
        int Arrival => HasFlight ? arrivalMinute.Value : flight.arrivalMinuteOfDay;

        public float TurnMinutes
        {
            get
            {
                if (!ClockRunning) return 0f;
                double end = offBlockAt.Value >= 0 ? offBlockAt.Value : NetworkManager.ServerTime.Time;
                return (float)((end - parkedAt.Value) / flight.realSecondsPerGameMinute);
            }
        }

        public float MinutesToDeparture => flight.scheduledTurnMinutes - TurnMinutes;
        public string ClockText => Clock(Arrival + TurnMinutes);
        public string DepartureText => Clock(Arrival + flight.scheduledTurnMinutes);

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (!IsServer) report.OnValueChanged += (_, r) => Debug.Log("[TURNAROUND] (client) " + r);
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (!IsServer || !IsSpawned || Aircraft == null) return;
            if (!flightAssigned && Time.time >= nextAssignTry) AssignFlight();
            var state = Aircraft.State;
            double now = NetworkManager.ServerTime.Time;
            if (parkedAt.Value < 0 && state >= AircraftState.Parked) parkedAt.Value = now;
            if (offBlockAt.Value < 0 && state >= AircraftState.Pushback) offBlockAt.Value = now;
            if (report.Value.Length == 0 && state == AircraftState.Gone)
            {
                report.Value = new FixedString4096Bytes(BuildReport());
                Debug.Log("[TURNAROUND] " + report.Value);
            }
        }

        // Ask the company what we're flying next. No contract: the aircraft waits on the taxiway until someone signs one.
        void AssignFlight()
        {
            nextAssignTry = Time.time + 1f;
            var company = CompanyManager.Instance;
            if (company == null || company.State == null) return;
            var next = company.ServerNextFlight();
            Aircraft.HoldArrival = next == null;
            if (next == null)
            {
                if (airline.Value.Length == 0) RadioManager.Say("OPS", "No airline contracts. Use the office computer before we all get sent home.");
                airline.Value = new FixedString64Bytes("NO CONTRACT");
                return;
            }
            var (contract, number, turnOfDay) = next.Value;
            Aircraft.ServerAssignFlight(number, contract.destination);
            arrivalMinute.Value = 8 * 60 + turnOfDay * 100; // first arrival 08:00, then every 1h40
            airline.Value = new FixedString64Bytes(contract.airline);
            payment = contract.paymentPerTurn;
            flightAssigned = true;
            Debug.Log($"[TURNAROUND] Next flight: {contract.airline} {number} to {contract.destination}, day {company.State.day}");
        }

        // Host's NEXT FLIGHT button: same aircraft and ramp, fresh flight. Equipment stays wherever the crew left it.
        public void ServerStartNextFlight()
        {
            if (!IsServer || Aircraft == null || Aircraft.State != AircraftState.Gone) return;
            BaggageManager.Instance?.ServerReset();
            TaskManager.Instance?.ServerReset();
            SafetyViolationManager.Instance?.ServerReset();
            RandomEventManager.Instance?.ServerReset();
            GroundPowerUnit.Instance?.ServerReset();
            PushbackTug.Instance?.ServerReset();
            Aircraft.ServerResetForNextFlight();
            parkedAt.Value = -1;
            offBlockAt.Value = -1;
            report.Value = default;
            flightAssigned = false;
            AssignFlight();
        }

        string BuildReport()
        {
            var bags = BaggageManager.Instance;
            var tasks = TaskManager.Instance;
            tasks.ServerEvaluateNow();
            float scheduled = flight.scheduledTurnMinutes, actual = TurnMinutes, early = scheduled - actual;
            int loaded = bags.OutboundLoaded, outTotal = bags.OutboundTotal, wrong = bags.WrongLoaded, damaged = bags.Damaged;
            int unloaded = Mathf.Min(bags.InboundDelivered, bags.InboundTotal), inTotal = bags.InboundTotal;
            int failed = tasks.FailedCount;
            var safety = SafetyViolationManager.Instance;
            int violations = safety != null ? safety.Count : 0, equipmentDamage = safety != null ? safety.EquipmentDamage : 0;

            float score = 100f + (early >= 0f ? Mathf.Min(early, 5f) : early * 2f)
                          - wrong * 3f - damaged * 2f - (outTotal - loaded) * 3f - (inTotal - unloaded) - failed * 6f
                          - violations * 1f - equipmentDamage / 250f;
            int basePayment = payment > 0 ? payment : flight.basePayment;
            int paid = Mathf.Max(0, Round50(basePayment * Mathf.Clamp(score, 0f, 110f) / 100f) - Round50(equipmentDamage * 0.5f)); // half the damage comes out of your pay
            // Bonuses are for decent work: early time counts up to 5 minutes, and nothing below a C-.
            int bonus = score < 70f ? 0 : Round50(Mathf.Clamp(early, 0f, 5f) * 100f) + (wrong == 0 && damaged == 0 ? 250 : 0);

            var sb = new StringBuilder();
            sb.AppendLine(score >= 60f ? "TURNAROUND COMPLETE" : "TURNAROUND \"COMPLETE\"");
            sb.AppendLine($"{Airline} {Aircraft.FlightNumber} to {Aircraft.Destination}  -  GATE {flight.gate}");
            sb.AppendLine();
            sb.AppendLine($"Scheduled Turn:      {Duration(scheduled)}");
            sb.AppendLine($"Actual:              {Duration(actual)}");
            sb.AppendLine($"Bags Loaded:         {loaded} / {outTotal}");
            sb.AppendLine($"Bags Unloaded:       {unloaded} / {inTotal}");
            sb.AppendLine($"Wrong Bags:          {wrong}");
            sb.AppendLine($"Damaged Bags:        {damaged}");
            sb.AppendLine($"Tasks:               {tasks.CompletedCount} / {tasks.Count}  ({failed} failed)");
            if (failed > 0) sb.AppendLine($"   Skipped: {tasks.FailedNames()}");
            sb.AppendLine($"Equipment Damage:    ${equipmentDamage:N0}{(equipmentDamage == 0 ? " (allegedly)" : "")}");
            sb.AppendLine($"Safety Violations:   {violations}{(violations == 0 ? " (nobody was watching)" : "")}");
            if (violations > 0) sb.AppendLine($"   Mostly: {safety.Summary(2)}");
            sb.AppendLine($"Departure:           {(Mathf.Abs(early) < 0.5f ? "ON TIME" : Duration(Mathf.Abs(early)) + (early > 0f ? " EARLY" : " LATE"))}");
            sb.AppendLine();
            sb.AppendLine($"AIRLINE RATING:      {Grade(score)}");
            sb.AppendLine($"PAYMENT:             ${paid:N0}");
            sb.Append($"BONUS:               ${bonus:N0}");

            var company = CompanyManager.Instance;
            if (company != null)
            {
                int balance = company.ServerRecordTurn(new TurnResult
                {
                    payment = paid, bonus = bonus, equipmentDamage = equipmentDamage, score = score, onTime = early >= -1f,
                });
                sb.AppendLine();
                sb.Append($"COMPANY BALANCE:     ${balance:N0}   (REP {company.State.reputation})");
            }
            return sb.ToString();
        }

        public static string Grade(float score) => score switch
        {
            >= 97f => "A+", >= 93f => "A", >= 90f => "A-", >= 87f => "B+", >= 83f => "B", >= 80f => "B-",
            >= 77f => "C+", >= 73f => "C", >= 70f => "C-", >= 60f => "D", _ => "F",
        };

        static int Round50(float v) => Mathf.Max(0, Mathf.RoundToInt(v / 50f) * 50);

        static string Clock(float minuteOfDay)
        {
            int m = Mathf.FloorToInt(minuteOfDay);
            return $"{m / 60 % 24:00}:{m % 60:00}";
        }

        // Game minutes -> "27:42" (minutes:seconds).
        public static string Duration(float minutes)
        {
            int seconds = Mathf.RoundToInt(Mathf.Abs(minutes) * 60f);
            return $"{seconds / 60}:{seconds % 60:00}";
        }
    }
}
