using UnityEngine;

namespace Turnaround.Tasks
{
    // Each id maps to one completion check in TaskManager.Measure.
    public enum TaskId
    {
        InstallChocks, PlaceCones, ConnectGpu, OpenCargoDoor, UnloadBags, ServiceFuel, ServiceWater, ServiceLav,
        LoadBags, CloseCargoDoor, DisconnectGpu, RemoveCones, ConnectTug, RemoveChocks, Pushback, Depart,
    }

    public enum TaskState : byte { Locked, Available, Active, Complete, Failed }

    [CreateAssetMenu(menuName = "Turnaround/Task Definition")]
    public class TaskDefinition : ScriptableObject
    {
        public TaskId id;
        public string displayName;
        [TextArea] public string description;
        public int scoreValue = 100;
        public TaskDefinition[] dependencies;
        public bool failIfIncompleteAtPushback = true; // skipped work counts as failed once the aircraft moves
    }
}
