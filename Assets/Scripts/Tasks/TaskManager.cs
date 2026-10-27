using System.Collections.Generic;
using Turnaround.Aircraft;
using Turnaround.Baggage;
using Turnaround.Equipment;
using Turnaround.Vehicles;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Tasks
{
    // Host evaluates every task twice a second against live game state; clients only get state + progress per task.
    // Tasks latch: once complete they stay complete. When the aircraft starts moving, skipped work is marked failed.
    public class TaskManager : NetworkBehaviour
    {
        public TaskDefinition[] tasks;

        public static TaskManager Instance { get; private set; }

        NetworkList<byte> states;
        NetworkList<int> progress;
        NetworkList<int> targets;
        readonly Dictionary<TaskDefinition, int> index = new();
        AircraftController aircraft;
        bool pushbackHandled;

        AircraftController Aircraft => aircraft != null ? aircraft : aircraft = FindFirstObjectByType<AircraftController>();

        public int Count => Mathf.Min(tasks.Length, states.Count);
        public TaskDefinition Definition(int i) => tasks[i];
        public TaskState StateOf(int i) => (TaskState)states[i];
        public int ProgressOf(int i) => progress[i];
        public int TargetOf(int i) => targets[i];

        public int CompletedCount => CountState(TaskState.Complete);
        public int FailedCount => CountState(TaskState.Failed);

        void Awake()
        {
            states = new NetworkList<byte>();
            progress = new NetworkList<int>();
            targets = new NetworkList<int>();
            for (int i = 0; i < tasks.Length; i++) index[tasks[i]] = i;
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (!IsServer) return;
            foreach (var t in tasks)
            {
                states.Add((byte)(t.dependencies.Length > 0 ? TaskState.Locked : TaskState.Available));
                progress.Add(0);
                targets.Add(1);
            }
            InvokeRepeating(nameof(Evaluate), 1f, 0.5f);
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
            CancelInvoke();
        }

        int CountState(TaskState s)
        {
            int n = 0;
            for (int i = 0; i < Count; i++) if (StateOf(i) == s) n++;
            return n;
        }

        public void ServerReset()
        {
            pushbackHandled = false;
            for (int i = 0; i < tasks.Length; i++)
            {
                SetState(i, tasks[i].dependencies.Length > 0 ? TaskState.Locked : TaskState.Available);
                progress[i] = 0;
                targets[i] = 1;
            }
        }

        // The report is written the moment the aircraft leaves; catch up first so the last task counts.
        public void ServerEvaluateNow() => Evaluate();

        public string FailedNames()
        {
            var names = new List<string>();
            for (int i = 0; i < Count; i++) if (StateOf(i) == TaskState.Failed) names.Add(tasks[i].displayName);
            return string.Join(", ", names);
        }

        void Evaluate()
        {
            var ac = Aircraft;
            if (ac == null || ac.State == AircraftState.Approaching) return;

            for (int i = 0; i < tasks.Length; i++) // list order matters: dependents see this tick's completions
            {
                var current = StateOf(i);
                if (current is TaskState.Complete or TaskState.Failed) continue;
                if (!DependenciesComplete(tasks[i]))
                {
                    SetState(i, TaskState.Locked);
                    continue;
                }
                var (value, target) = Measure(tasks[i].id, ac);
                if (progress[i] != value) progress[i] = value;
                if (targets[i] != target) targets[i] = target;
                SetState(i, value >= target ? TaskState.Complete : value > 0 ? TaskState.Active : TaskState.Available);
            }

            if (pushbackHandled || ac.State < AircraftState.Pushback) return;
            pushbackHandled = true;
            for (int i = 0; i < tasks.Length; i++)
                if (tasks[i].failIfIncompleteAtPushback && StateOf(i) != TaskState.Complete) SetState(i, TaskState.Failed);
        }

        void SetState(int i, TaskState s)
        {
            if (states[i] != (byte)s) states[i] = (byte)s;
        }

        bool DependenciesComplete(TaskDefinition t)
        {
            foreach (var dep in t.dependencies)
                if (StateOf(index[dep]) != TaskState.Complete) return false;
            return true;
        }

        static (int, int) Flag(bool done) => (done ? 1 : 0, 1);

        static (int, int) Counted(int value, int total) => total > 0 ? (value, total) : (0, 1);

        static (int, int) Service(AircraftController ac, ServiceType type)
        {
            foreach (var sp in ac.Services)
                if (sp.type == type) return sp.IsComplete ? (100, 100) : ((int)(sp.Progress * 100f), 100);
            return (0, 1);
        }

        static bool CargoDoorOpen(AircraftController ac)
        {
            foreach (var d in ac.Doors) if (d.isCargoDoor && d.IsOpen) return true;
            return false;
        }

        static bool GpuPlugged(AircraftController ac)
        {
            foreach (var sp in ac.Services) if (sp.type == ServiceType.GroundPower && sp.IsConnected) return true;
            return false;
        }

        static (int, int) Measure(TaskId id, AircraftController ac)
        {
            var bags = BaggageManager.Instance;
            var gpu = GroundPowerUnit.Instance;
            var tug = PushbackTug.Instance;
            return id switch
            {
                TaskId.InstallChocks => Counted(ac.ChocksPlaced, ac.ChocksRequired),
                TaskId.PlaceCones => Counted(ac.ConesPlaced, ac.ConesRequired),
                TaskId.ConnectGpu => Flag(gpu != null && gpu.IsPowering(ac)),
                TaskId.OpenCargoDoor => Flag(CargoDoorOpen(ac)),
                TaskId.UnloadBags => bags == null ? (0, 1) : Counted(bags.InboundDelivered, bags.InboundTotal),
                TaskId.ServiceFuel => Service(ac, ServiceType.Fuel),
                TaskId.ServiceWater => Service(ac, ServiceType.PotableWater),
                TaskId.ServiceLav => Service(ac, ServiceType.Lavatory),
                TaskId.LoadBags => bags == null ? (0, 1) : Counted(bags.OutboundLoaded, bags.OutboundTotal),
                TaskId.CloseCargoDoor => Flag(!CargoDoorOpen(ac)),
                TaskId.DisconnectGpu => Flag(!GpuPlugged(ac)),
                TaskId.RemoveCones => Flag(ac.ConesPresent == 0),
                TaskId.ConnectTug => Flag(tug != null && tug.IsConnected),
                TaskId.RemoveChocks => Flag(ac.ChocksPresent == 0),
                TaskId.Pushback => Flag(ac.State >= AircraftState.Departing || (ac.State == AircraftState.Pushback && ac.TowedBy == null)),
                TaskId.Depart => Flag(ac.State == AircraftState.Gone),
                _ => (0, 1),
            };
        }
    }
}
