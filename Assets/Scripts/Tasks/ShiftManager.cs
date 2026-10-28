using System.Collections;
using Turnaround.Events;
using Turnaround.Player;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Tasks
{
    // Before the shift everyone hangs around the terminal lobby; the host's big red button starts the day:
    // a buzzer, a flash, and every crew member is moved out onto the ramp. Flights only get assigned once started.
    public class ShiftManager : NetworkBehaviour
    {
        public static ShiftManager Instance { get; private set; }
        public static bool InLobby => Instance == null || !Instance.started.Value;
        public static float FlashUntil { get; private set; } // local: the HUD flashes red while this is in the future

        readonly NetworkVariable<bool> started = new();
        static AudioClip buzzer;

        public bool Started => started.Value;

        public override void OnNetworkSpawn() => Instance = this;

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        public void ServerStartShift()
        {
            if (!IsServer || started.Value) return;
            RunReset.ServerSnapshot();
            started.Value = true;
            StartShiftRpc();
            RadioManager.Say("OPS", "Shift's started. Everyone on the ramp. Try to look like professionals.");
        }

        // Bankruptcy: back to the lobby, nothing has happened yet.
        public void ServerReturnToLobby()
        {
            if (!IsServer) return;
            started.Value = false;
            MoveEveryoneRpc(true);
        }

        [Rpc(SendTo.Everyone)]
        void StartShiftRpc()
        {
            FlashUntil = Time.time + 0.6f;
            if (StartShiftButton.Instance != null) StartShiftButton.Instance.Press();
            AudioSource.PlayClipAtPoint(Buzzer(), Camera.main != null ? Camera.main.transform.position : Vector3.zero, 1f);
            if (PlayerController.Local != null) StartCoroutine(MoveAfterBuzz(PlayerController.Local));
        }

        IEnumerator MoveAfterBuzz(PlayerController local)
        {
            yield return new WaitForSeconds(0.8f); // let the buzzer land before the scene changes under you
            if (local != null) local.GoToSpawn(lobby: false);
        }

        [Rpc(SendTo.Everyone)]
        void MoveEveryoneRpc(bool lobby)
        {
            var local = PlayerController.Local;
            if (local == null) return;
            var ragdoll = local.GetComponent<PlayerRagdoll>();
            if (ragdoll.HasBody) ragdoll.ForceEnd();
            local.GoToSpawn(lobby);
        }

        // Game-show buzzer: two detuned square waves with a hard stop, so there's no audio asset to ship.
        static AudioClip Buzzer()
        {
            if (buzzer != null) return buzzer;
            const int rate = 22050, samples = rate * 7 / 10;
            var data = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)rate;
                float sq = Mathf.Sign(Mathf.Sin(2 * Mathf.PI * 140f * t)) + Mathf.Sign(Mathf.Sin(2 * Mathf.PI * 147f * t));
                data[i] = sq * 0.25f * Mathf.Clamp01((samples - i) / 600f);
            }
            buzzer = AudioClip.Create("ShiftBuzzer", samples, 1, rate, false);
            buzzer.SetData(data, 0);
            return buzzer;
        }
    }
}
