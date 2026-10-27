using Turnaround.Player;

namespace Turnaround.Interaction
{
    // Anything the player can look at and press E on. Implementations send an Rpc to the host for networked effects.
    public interface IInteractable
    {
        // Return null to hide the prompt (e.g. not interactable right now).
        string GetPrompt(PlayerInteraction player);
        void Interact(PlayerInteraction player);
    }
}
