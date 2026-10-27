using UnityEngine;

namespace Turnaround.Company
{
    public enum UpgradeEffect { MaintenanceGarage, BetterBreakRoom, ExtraContractSlot, BetterRadios }

    [CreateAssetMenu(menuName = "Turnaround/Upgrade")]
    public class UpgradeDefinition : ScriptableObject
    {
        public string id;
        public string displayName;
        public UpgradeEffect effect;
        public int price;
        [TextArea] public string blurb;
    }
}
