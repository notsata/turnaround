using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Turnaround.Baggage;
using Turnaround.Equipment;
using Turnaround.Events;
using Turnaround.Vehicles;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Turnaround.Company
{
    public struct TurnResult
    {
        public int payment, bonus, equipmentDamage;
        public float score;
        public bool onTime;
    }

    // The ground-handling company: money, reputation, days, contracts, equipment, upgrades.
    // Host owns the state and saves it; clients get the same data as one JSON snapshot for the office screen.
    public class CompanyManager : NetworkBehaviour
    {
        public ContractDefinition[] contracts;
        public EquipmentDefinition[] equipment;
        public UpgradeDefinition[] upgrades;
        public GameObject cartPrefab;
        public Vector3[] extraCartSpots;
        public int dailyOverhead = 800; // the small office isn't free either
        public int maxExtraCarts = 3;

        public static CompanyManager Instance { get; private set; }

        readonly NetworkVariable<FixedString4096Bytes> stateJson = new();
        readonly List<NetworkObject> boughtCarts = new();
        CompanyState state;
        string parsedFrom;
        string currentContractId;
        bool dirty;
        float nextPublish;

        // Host: the live state. Clients: parsed from the last snapshot.
        public CompanyState State
        {
            get
            {
                if (IsServer) return state;
                string json = stateJson.Value.ToString();
                if (json != parsedFrom)
                {
                    parsedFrom = json;
                    state = json.Length > 0 ? JsonUtility.FromJson<CompanyState>(json) : null;
                    if (state != null) Debug.Log($"[COMPANY] (client) ${state.money}, rep {state.reputation}, day {state.day}, extra carts {state.extraCarts}");
                }
                return state;
            }
        }

        public ContractDefinition CurrentContract => ContractDef(currentContractId);
        public int MaxContracts => HasEffect(UpgradeEffect.ExtraContractSlot) ? 2 : 1;
        float WearMultiplier => HasEffect(UpgradeEffect.MaintenanceGarage) ? 0.5f : 1f;

        public ContractDefinition ContractDef(string id) => Array.Find(contracts, c => c.id == id);
        public EquipmentDefinition EquipmentDef(string id) => Array.Find(equipment, e => e.id == id);
        public UpgradeDefinition UpgradeDef(string id) => Array.Find(upgrades, u => u.id == id);
        public bool HasEffect(UpgradeEffect effect) => State != null && State.upgrades.Any(id => UpgradeDef(id)?.effect == effect);

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (!IsServer)
            {
                stateJson.OnValueChanged += (_, _) => _ = State; // parse on arrival, not first time the office screen opens
                return;
            }
            state = SaveManager.Load() ?? NewCompany();
            Publish();
            ServerApplyEquipment();
            Debug.Log($"[COMPANY] {state.companyName}: day {state.day}, ${state.money}, rep {state.reputation}, {state.contracts.Count} contract(s) (save: {SaveManager.Slot})");
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        // One airline, terrible equipment, almost no money.
        CompanyState NewCompany()
        {
            var s = new CompanyState();
            s.contracts.Add(new ContractRecord { id = contracts[0].id });
            foreach (EquipmentKind kind in Enum.GetValues(typeof(EquipmentKind)))
            {
                var starter = equipment.FirstOrDefault(e => e.kind == kind && e.price == 0);
                if (starter != null) s.equipment.Add(new OwnedEquipment { kind = kind, id = starter.id, condition = 70f });
            }
            return s;
        }

        void Publish()
        {
            stateJson.Value = new FixedString4096Bytes(JsonUtility.ToJson(state));
            SaveManager.Save(state);
            dirty = false;
            nextPublish = Time.time + 2f;
        }

        void Update()
        {
            if (IsServer && IsSpawned && dirty && Time.time >= nextPublish)
            {
                Publish();
                ServerApplyEquipment();
            }
        }

        // ---------- flights and days ----------

        // The next contract with turns left today, rolling the day over when there are none. Null: no contracts at all.
        public (ContractDefinition contract, int flightNumber, int turnOfDay)? ServerNextFlight()
        {
            if (state.contracts.Count == 0) return null;
            var record = state.contracts.Where(c => ContractDef(c.id) != null && c.turnsToday < ContractDef(c.id).turnsPerDay)
                .OrderBy(c => c.turnsToday).FirstOrDefault();
            if (record == null)
            {
                EndDay();
                if (state.contracts.Count == 0) return null;
                record = state.contracts[0];
            }
            var def = ContractDef(record.id);
            currentContractId = record.id;
            return (def, def.flightNumberBase + record.turnsToday * 2, state.contracts.Sum(c => c.turnsToday));
        }

        void EndDay()
        {
            var summary = new StringBuilder($"Day {state.day}: overhead -${dailyOverhead}");
            state.money -= dailyOverhead;
            foreach (var record in state.contracts.ToList())
            {
                var def = ContractDef(record.id);
                record.turnsToday = 0;
                bool lost = record.strikes >= 3 || (record.turns >= 3 && record.Otp < def.minimumOtp);
                if (!lost) continue;
                state.contracts.Remove(record);
                summary.Append($". {def.airline} terminated (on-time {record.Otp:P0}, {record.strikes} damage strikes)");
                RadioManager.Say("OPS", $"{def.airline} has terminated our contract. On-time {record.Otp:P0}. Wonderful.");
            }
            foreach (var e in state.equipment) e.condition = Mathf.Max(0f, e.condition - 2f * WearMultiplier);
            state.lastDaySummary = summary.ToString();
            RadioManager.Say("OPS", $"That's day {state.day} done. Overhead was ${dailyOverhead}. Balance ${state.money:N0}.");
            state.day++;
            Publish();
        }

        public int ServerRecordTurn(TurnResult r)
        {
            state.money += r.payment + r.bonus;
            state.totalTurns++;
            int rep = r.score >= 97f ? 3 : r.score >= 90f ? 2 : r.score >= 80f ? 1 : r.score >= 70f ? 0 : r.score >= 60f ? -1 : -3;
            rep += r.onTime ? 1 : -2;
            if (HasEffect(UpgradeEffect.BetterBreakRoom)) rep++;
            state.reputation = Mathf.Max(0, state.reputation + rep);

            var record = state.Contract(currentContractId); // null if it was dropped mid-turn: still gets paid
            if (record != null)
            {
                record.turns++;
                record.turnsToday++;
                if (r.onTime) record.onTime++;
                var def = ContractDef(record.id);
                if (r.equipmentDamage > def.damageTolerance)
                {
                    record.strikes++;
                    RadioManager.Say("OPS", $"{def.airline} is filing a damage report. That's strike {record.strikes} of 3.");
                }
            }
            foreach (var e in state.equipment) e.condition = Mathf.Max(0f, e.condition - 3f * WearMultiplier);
            Publish();
            ServerApplyEquipment();
            Debug.Log($"[COMPANY] Turn recorded: +${r.payment + r.bonus}, rep {rep:+0;-0;0} -> {state.reputation}, balance ${state.money}");
            return state.money;
        }

        // ---------- equipment ----------

        public void ServerWear(EquipmentKind kind, float amount)
        {
            var owned = state?.Equipment(kind);
            if (owned == null || amount <= 0f) return;
            owned.condition = Mathf.Max(0f, owned.condition - amount * WearMultiplier);
            dirty = true; // crashes come in bursts; publish at most every couple of seconds
        }

        public int RepairCost(EquipmentKind kind)
        {
            var owned = State?.Equipment(kind);
            return owned == null ? 0 : Mathf.RoundToInt((100f - owned.condition) * 15f * (HasEffect(UpgradeEffect.MaintenanceGarage) ? 0.5f : 1f));
        }

        // Push owned models and their condition onto the actual machines on the ramp (host simulates them).
        public void ServerApplyEquipment()
        {
            foreach (var v in FindObjectsByType<VehicleController>(FindObjectsSortMode.None))
            {
                var (def, wear) = Owned(v.kind);
                if (def == null) continue;
                v.reliability = Mathf.Clamp01(def.reliability * wear);
                if (def.maxSpeed > 0f) v.maxSpeed = def.maxSpeed;
                if (def.acceleration > 0f) v.acceleration = def.acceleration;
                if (def.brakeDecel > 0f) v.brakeDecel = def.brakeDecel;
                if (def.steerResponse > 0f) v.steerResponse = def.steerResponse;
                if (def.beltSpeed > 0f)
                    foreach (var belt in v.GetComponentsInChildren<BeltLoader>()) belt.beltSpeed = def.beltSpeed;
            }
            var gpu = GroundPowerUnit.Instance;
            var (gpuDef, gpuWear) = Owned(EquipmentKind.Gpu);
            if (gpu != null && gpuDef != null) gpu.reliability = Mathf.Clamp01(gpuDef.reliability * gpuWear);

            while (boughtCarts.Count < Mathf.Min(state.extraCarts, extraCartSpots.Length))
            {
                var cart = Instantiate(cartPrefab, extraCartSpots[boughtCarts.Count], Quaternion.identity).GetComponent<NetworkObject>();
                cart.Spawn(true);
                boughtCarts.Add(cart);
            }

            var events = RandomEventManager.Instance;
            if (events != null) events.maxEvents = HasEffect(UpgradeEffect.BetterRadios) ? 1 : 2;
        }

        // Worn-out equipment is up to 25% less reliable.
        (EquipmentDefinition def, float wear) Owned(EquipmentKind kind)
        {
            var owned = state.Equipment(kind);
            return owned == null ? (null, 1f) : (EquipmentDef(owned.id), Mathf.Lerp(0.75f, 1f, owned.condition / 100f));
        }

        // ---------- office computer actions (any crew member; the host checks the money) ----------

        [Rpc(SendTo.Server)]
        public void BuyEquipmentRpc(string id)
        {
            var def = EquipmentDef(id);
            if (def == null || def.price <= 0 || state.money < def.price) return;
            if (def.kind == EquipmentKind.Cart)
            {
                if (state.extraCarts >= maxExtraCarts) return;
                state.extraCarts++;
            }
            else
            {
                var owned = state.Equipment(def.kind);
                if (owned.id == id) return;
                owned.id = id;
                owned.condition = 100f;
            }
            state.money -= def.price;
            RadioManager.Say("OPS", $"{def.displayName} delivered. Try not to crash it today.");
            Publish();
            ServerApplyEquipment();
        }

        [Rpc(SendTo.Server)]
        public void RepairRpc(EquipmentKind kind)
        {
            int cost = RepairCost(kind);
            var owned = state.Equipment(kind);
            if (owned == null || cost <= 0 || state.money < cost) return;
            state.money -= cost;
            owned.condition = 100f;
            Publish();
            ServerApplyEquipment();
        }

        [Rpc(SendTo.Server)]
        public void BuyUpgradeRpc(string id)
        {
            var def = UpgradeDef(id);
            if (def == null || state.Has(id) || state.money < def.price) return;
            state.money -= def.price;
            state.upgrades.Add(id);
            RadioManager.Say("OPS", $"{def.displayName}: done. Accounting is crying.");
            Publish();
            ServerApplyEquipment();
        }

        [Rpc(SendTo.Server)]
        public void AcceptContractRpc(string id)
        {
            var def = ContractDef(id);
            if (def == null || state.Contract(id) != null || state.reputation < def.reputationRequired || state.contracts.Count >= MaxContracts) return;
            state.contracts.Add(new ContractRecord { id = id });
            RadioManager.Say("OPS", $"We signed {def.airline}. {def.turnsPerDay} turns a day. Don't make me regret this.");
            Publish();
        }

        [Rpc(SendTo.Server)]
        public void DropContractRpc(string id)
        {
            if (state.contracts.RemoveAll(c => c.id == id) > 0) Publish();
        }

        // Host only: wipe the save and start over with nothing.
        [Rpc(SendTo.Server)]
        public void NewCompanyRpc(RpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId != NetworkManager.ServerClientId) return;
            SaveManager.Delete();
            state = NewCompany();
            RadioManager.Say("OPS", "Bankruptcy filed. New company, same people. Same equipment, somehow.");
            Publish();
            ServerApplyEquipment();
        }
    }
}
