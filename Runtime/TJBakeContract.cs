namespace MemoriStudios.TJBake
{
    /// <summary>The positional slot table a unit visual follows. Hosts may use other tables; this one is the default the baker offers.</summary>
    public static class TJBakeContract
    {
        public static readonly string[] UnitSlotNames =
        {
            "idle", "walk", "run", "meleeStance", "meleeAttack", "idleVariant1", "idleVariant2", "idleVariant3",
            "death1", "death2", "death3", "thrown", "meleeAttackAlt", "rangedStance", "rangedAttack",
        };

        public static readonly string[] RiderSlotNames = { "idle", "attack", "death" };

        /// <summary>Loop, one-shot that returns, or one-shot that holds, by slot position.</summary>
        public static void SlotKind(int slot, bool isRider, out bool loop, out bool returnToIdle)
        {
            if (isRider)
            {
                loop = slot == 0;
                returnToIdle = slot == 1;
                return;
            }
            switch (slot)
            {
                case 0: case 1: case 2: case 3: case 13: loop = true; returnToIdle = false; return;
                case 8: case 9: case 10: loop = false; returnToIdle = false; return;
                default: loop = false; returnToIdle = true; return;
            }
        }

        public static string SlotName(int slot, bool isRider)
        {
            string[] names = isRider ? RiderSlotNames : UnitSlotNames;
            return slot >= 0 && slot < names.Length ? names[slot] : $"slot{slot}";
        }
    }
}
