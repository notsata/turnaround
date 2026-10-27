using System.Collections.Generic;
using Turnaround.Aircraft;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Events
{
    // Host decides what gets said; every peer keeps the last few lines for the HUD and plays a burst of static.
    public class RadioManager : NetworkBehaviour
    {
        public struct Message
        {
            public string speaker;
            public string text;
            public float time;
        }

        public float chatterInterval = 75f;

        static readonly (string, string)[] Chatter =
        {
            ("OPS", "Operations wants to know why you're still at the gate."),
            ("COCKPIT", "Ramp, cockpit. Any update on bags?"),
            ("BAG ROOM", "Bag room says you're missing one. They won't say which."),
            ("OPS", "Reminder: the break room microwave is not a heater."),
            ("COCKPIT", "Captain says we're ready for push. We are not ready for push."),
            ("OPS", "Whoever parked on the stop bar yesterday: we know."),
            ("FUEL", "Fuel truck is waiting behind you. Patiently. For now."),
            ("OPS", "Safety audit next week. Act natural."),
        };

        public static RadioManager Instance { get; private set; }
        public readonly List<Message> Recent = new();

        static AudioClip staticClip;
        AudioSource source;
        AircraftController aircraft;
        float nextChatter;

        void Awake()
        {
            source = gameObject.AddComponent<AudioSource>();
            source.spatialBlend = 0f;
            source.volume = 0.25f;
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
            nextChatter = Time.time + chatterInterval;
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        // Host-side entry point for every system that wants to talk. Silently ignored on clients.
        public static void Say(string speaker, string text)
        {
            if (Instance != null && Instance.IsServer) Instance.SayRpc(speaker, text);
        }

        [Rpc(SendTo.Everyone)]
        void SayRpc(string speaker, string text)
        {
            Recent.Add(new Message { speaker = speaker, text = text, time = Time.time });
            if (Recent.Count > 4) Recent.RemoveAt(0);
            source.PlayOneShot(StaticClip());
            Debug.Log($"[RADIO] {speaker}: {text}");
        }

        void Update()
        {
            if (!IsServer || !IsSpawned || Time.time < nextChatter) return;
            nextChatter = Time.time + chatterInterval * Random.Range(0.7f, 1.3f);
            if (aircraft == null) aircraft = FindFirstObjectByType<AircraftController>();
            if (aircraft == null || !aircraft.IsStationary) return;
            var (speaker, text) = Chatter[Random.Range(0, Chatter.Length)];
            Say(speaker, text);
        }

        // Generated hiss with a squelch pop at the start, so there's no audio asset to ship.
        static AudioClip StaticClip()
        {
            if (staticClip != null) return staticClip;
            const int rate = 22050, samples = rate / 3;
            var data = new float[samples];
            var rng = new System.Random(11);
            for (int i = 0; i < samples; i++)
            {
                float envelope = i < 300 ? 1f : Mathf.Lerp(0.5f, 0f, i / (float)samples);
                data[i] = (float)(rng.NextDouble() * 2 - 1) * envelope;
            }
            staticClip = AudioClip.Create("RadioStatic", samples, 1, rate, false);
            staticClip.SetData(data, 0);
            return staticClip;
        }
    }
}
