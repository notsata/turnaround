using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Turnaround.Company
{
    [Serializable]
    public class ContractRecord
    {
        public string id;
        public int turns, onTime, strikes, turnsToday;
        public float Otp => turns == 0 ? 1f : onTime / (float)turns;
    }

    [Serializable]
    public class OwnedEquipment
    {
        public EquipmentKind kind;
        public string id;
        public float condition = 100f;
    }

    // Everything about the company. Also what clients receive (as JSON) to draw the office screen.
    [Serializable]
    public class CompanyState
    {
        public string companyName = "Barely Ground Services";
        public int money = 2000;
        public int reputation = 5;
        public int day = 1;
        public int totalTurns;
        public int extraCarts;
        public List<ContractRecord> contracts = new();
        public List<OwnedEquipment> equipment = new();
        public List<string> upgrades = new();
        public string lastDaySummary = "";

        public ContractRecord Contract(string id) => contracts.Find(c => c.id == id);
        public OwnedEquipment Equipment(EquipmentKind kind) => equipment.Find(e => e.kind == kind);
        public bool Has(string upgradeId) => upgrades.Contains(upgradeId);
    }

    // One JSON file per save slot in the player's data folder. The host is the only one that ever saves.
    public static class SaveManager
    {
        // "-save <name>" picks another slot (the automated test uses its own so it never touches yours).
        public static string Slot
        {
            get
            {
                var args = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, "-save");
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : "company";
            }
        }

        public static string PathFor(string slot) => System.IO.Path.Combine(Application.persistentDataPath, slot + ".json");

        public static CompanyState Load()
        {
            string path = PathFor(Slot);
            if (!File.Exists(path)) return null;
            try
            {
                return JsonUtility.FromJson<CompanyState>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                // A broken save shouldn't brick the game; keep the bad file next to the new one for inspection.
                Debug.LogWarning($"[COMPANY] Save {path} unreadable ({e.Message}); starting fresh");
                File.Copy(path, path + ".broken", true);
                return null;
            }
        }

        public static void Save(CompanyState state)
        {
            string path = PathFor(Slot);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(state, true));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path); // write-then-swap so a crash mid-save can't leave half a file
        }

        public static void Delete()
        {
            string path = PathFor(Slot);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
