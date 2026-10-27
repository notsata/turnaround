namespace Turnaround.Vehicles
{
    // Extra controls for whoever is driving: F (primary) and right mouse (alternate).
    // Prompts include their own key label, or are plain info text when nothing can be done yet.
    public interface IVehicleAction
    {
        string GetActionPrompt(bool alternate);
        void DoAction(bool alternate);
    }
}
