using System.Collections;
using Turnaround.Core;
using Turnaround.Interaction;
using Turnaround.Player;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Tasks
{
    // The big red button in the terminal lobby. Only the host's press counts.
    public class StartShiftButton : MonoBehaviour, IInteractable
    {
        public Transform cap; // the bit that goes down
        public static StartShiftButton Instance { get; private set; }

        Vector3 capRest;

        void Awake()
        {
            Instance = this;
            capRest = cap.localPosition;
        }

        public string GetPrompt(PlayerInteraction player)
        {
            if (!ShiftManager.InLobby || ShiftManager.Instance == null) return null;
            return NetworkManager.Singleton.IsServer ? "[E] START SHIFT" : "Only the host can start the shift";
        }

        public void Interact(PlayerInteraction player)
        {
            if (NetworkManager.Singleton.IsServer) ShiftManager.Instance?.ServerStartShift();
            else Press(); // a satisfying click, at least
        }

        // Every peer, from ShiftManager's start Rpc.
        public void Press()
        {
            StopAllCoroutines();
            StartCoroutine(PressRoutine());
        }

        IEnumerator PressRoutine()
        {
            ProceduralAudio.PlayAt(ProceduralAudio.Clang(), cap.position, 0.6f);
            cap.localPosition = capRest + Vector3.down * 0.1f;
            yield return new WaitForSeconds(0.35f);
            cap.localPosition = capRest;
        }
    }
}
