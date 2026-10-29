using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Baggage
{
    // Tag data for one bag. Set by the host before spawn, read-only afterwards except damage.
    public class BaggageItem : NetworkBehaviour
    {
        public const float DamagedThreshold = 30f;
        const float HarmlessImpact = 7f; // m/s; a firm toss is fine, a full yeet is not

        public static readonly List<BaggageItem> All = new();
        public static event Action<BaggageItem> BecameDamaged;

        static readonly Color[] TagPalette = { Color.cyan, Color.white, Color.yellow, new(1f, 0.5f, 0.8f) };

        public Renderer tagRenderer;

        readonly NetworkVariable<int> bagId = new();
        readonly NetworkVariable<int> flight = new();
        readonly NetworkVariable<FixedString32Bytes> destination = new();
        readonly NetworkVariable<bool> inbound = new();
        readonly NetworkVariable<bool> fragile = new();
        readonly NetworkVariable<float> damage = new();

        public int BagId => bagId.Value;
        public int Flight => flight.Value;
        public bool Inbound => inbound.Value;
        public bool Fragile => fragile.Value;
        public bool IsDamaged => damage.Value >= DamagedThreshold;

        public string TagText =>
            Flight == 0 ? "NO TAG (whose is this?)"
            : (Inbound ? $"ARRIVED ON {Flight}" : $"FLIGHT {Flight} -> {destination.Value}")
              + (Fragile ? "  FRAGILE" : "") + (IsDamaged ? "  (DAMAGED)" : "");

        public void ServerSetTag(int id, int flightNumber, string dest, bool isInbound, bool isFragile)
        {
            if (!IsServer || !IsSpawned)
            {
                Debug.LogError($"[TURNAROUND] Cannot tag {name} before its NetworkObject is spawned");
                return;
            }
            bagId.Value = id;
            flight.Value = flightNumber;
            destination.Value = new FixedString32Bytes(dest);
            inbound.Value = isInbound;
            fragile.Value = isFragile;
            RefreshTagColor();
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            flight.OnValueChanged += OnTagChanged;
            inbound.OnValueChanged += OnTagChanged;
            RefreshTagColor();
        }

        public override void OnNetworkDespawn()
        {
            flight.OnValueChanged -= OnTagChanged;
            inbound.OnValueChanged -= OnTagChanged;
            All.Remove(this);
        }

        void OnTagChanged<T>(T previous, T current) => RefreshTagColor();

        void RefreshTagColor()
        {
            if (tagRenderer == null) return;
            tagRenderer.material.color = Flight == 0 ? Color.gray
                : Inbound ? new Color(0.4f, 0.9f, 0.4f)
                : TagPalette[Flight % TagPalette.Length];
        }

        // Only the host simulates bag physics, so only it sees collisions.
        void OnCollisionEnter(Collision collision)
        {
            if (!IsServer || !IsSpawned) return;
            float impact = collision.relativeVelocity.magnitude;
            if (impact < HarmlessImpact) return;
            bool wasDamaged = IsDamaged;
            damage.Value = Mathf.Min(100f, damage.Value + (impact - HarmlessImpact) * 8f * (Fragile ? 2f : 1f));
            if (!wasDamaged && IsDamaged) BecameDamaged?.Invoke(this);
        }
    }
}
