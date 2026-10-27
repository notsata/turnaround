using UnityEngine;

namespace Turnaround.Company
{
    public enum EquipmentKind { Tractor, BeltLoader, Tug, Gpu, Cart }

    // One model of a piece of equipment. Zero means "keep whatever the prefab has".
    [CreateAssetMenu(menuName = "Turnaround/Equipment")]
    public class EquipmentDefinition : ScriptableObject
    {
        public string id;
        public string displayName;
        public EquipmentKind kind;
        public int price;
        [Range(0f, 1f)] public float reliability = 1f;
        public float maxSpeed, acceleration, brakeDecel, steerResponse, beltSpeed;
        [TextArea] public string blurb;
    }
}
