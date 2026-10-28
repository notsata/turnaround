using UnityEngine;

namespace Turnaround.Core
{
    // Unnamed layer slots used by index (no TagManager edit needed).
    public static class Layers
    {
        public const int Player = 9; // player capsules and their host-side proxies
        public const int Ragdoll = 10; // flopping limbs: they hit the world, not people's capsules or each other

        public static int WorldMask => ~((1 << Player) | (1 << Ragdoll));

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Configure()
        {
            Physics.IgnoreLayerCollision(Ragdoll, Player, true);
            Physics.IgnoreLayerCollision(Ragdoll, Ragdoll, true);
        }
    }
}
