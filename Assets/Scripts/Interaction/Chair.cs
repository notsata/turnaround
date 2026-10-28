using Turnaround.Player;
using UnityEngine;

namespace Turnaround.Interaction
{
    // Somewhere to sit while waiting for the host to press the button. Move to stand up.
    public class Chair : MonoBehaviour, IInteractable
    {
        public Transform seat; // where your hips go, facing the way you'll face

        public string GetPrompt(PlayerInteraction player)
        {
            if (player.GetComponent<PlayerController>().Sitting) return null;
            foreach (var p in PlayerController.All)
                if (p.Sitting && Vector3.Distance(p.transform.position, seat.position) < 0.5f) return "Taken";
            return "[E] Sit";
        }

        public void Interact(PlayerInteraction player)
        {
            if (GetPrompt(player) == "[E] Sit") player.GetComponent<PlayerController>().SitOn(seat);
        }
    }
}
