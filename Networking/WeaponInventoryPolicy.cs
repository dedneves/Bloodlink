namespace ULTRAKILL.Bloodlink.Networking
{
    public static class WeaponInventoryPolicy
    {
        public const int Slots = 6;
        public const int GunSlots = 5;
        public const int VariationsPerSlot = 8;
        public const ulong ValidMask = (1UL << (Slots * VariationsPerSlot)) - 1UL;

        // GunControl.currentSlotIndex and Bloodlink's wire format both use
        // one-based slots. Slot zero remains reserved for no weapon.
        public static byte ToNetworkSlot(int gameSlotIndex)
        {
            return gameSlotIndex > 0 && gameSlotIndex <= Slots
                ? (byte)gameSlotIndex
                : (byte)0;
        }

        public static bool IsValidMask(ulong inventoryMask)
        {
            return (inventoryMask & ~ValidMask) == 0;
        }

        public static bool IsUnlocked(ulong inventoryMask, byte slot, byte variation)
        {
            if (slot == 0 || slot > Slots || variation >= VariationsPerSlot) return false;
            var bit = (slot - 1) * VariationsPerSlot + variation;
            return (inventoryMask & (1UL << bit)) != 0;
        }

        public static ulong Add(ulong inventoryMask, byte slot, byte variation)
        {
            if (slot == 0 || slot > Slots || variation >= VariationsPerSlot) return inventoryMask;
            var bit = (slot - 1) * VariationsPerSlot + variation;
            return inventoryMask | (1UL << bit);
        }
    }
}
