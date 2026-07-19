namespace Ap.Control.Utils
{
    /// <summary>
    /// The AP world's synthetic item ids for Clearance Level 1..6 and Progressive Clearance Level, and their decode back to a level.
    /// </summary>
    public static class ApClearanceIds
    {
        /// <summary>Low-byte type tag the world mints clearance ids into (shared with sector GIDs).</summary>
        private const long TypeTag = 0x49;

        public const int MaxLevel = 6;

        /// <summary>Id of Clearance Level 1 (0x149).</summary>
        public const long First = (1L << 8) | TypeTag;

        /// <summary>Id of Clearance Level 6 (0x649).</summary>
        public const long Last = ((long)MaxLevel << 8) | TypeTag;

        /// <summary>
        /// Id of Progressive Clearance Level (0x749)
        /// </summary>
        public const long Progressive = (((long)MaxLevel + 1) << 8) | TypeTag;

        /// <summary>True if <paramref name="itemId"/> is the Progressive Clearance Level id.</summary>
        public static bool IsProgressive(long itemId) => itemId == Progressive;

        /// <summary>The synthetic id the world mints for <paramref name="level"/>.</summary>
        public static long ForLevel(int level) => ((long)level << 8) | TypeTag;

        /// <summary>
        /// True if <paramref name="itemId"/> is one of the six fixed synthetic clearance ids.
        /// </summary>
        public static bool TryGetLevel(long itemId, out int level)
        {
            level = 0;
            if (itemId < First || itemId > Last || (itemId & 0xFF) != TypeTag)
                return false;
            level = (int)(itemId >> 8);
            return true;
        }
    }
}
