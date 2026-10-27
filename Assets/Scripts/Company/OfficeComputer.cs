using Turnaround.Interaction;
using Turnaround.Player;
using Turnaround.UI;
using UnityEngine;

namespace Turnaround.Company
{
    // The beige box in the operations office. Purely local: it opens the company screen, which talks to the host.
    public class OfficeComputer : MonoBehaviour, IInteractable
    {
        public string GetPrompt(PlayerInteraction player) => "[E] Use Office Computer (contracts, equipment, upgrades)";

        public void Interact(PlayerInteraction player) => GameUI.OpenCompanyScreen();
    }
}
