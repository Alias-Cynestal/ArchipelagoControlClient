using Ap.Control.Utils.Interfaces;

namespace Ap.Control.Models
{
    /// <summary>Which in-game lever a received Archipelago item drives.</summary>
    public enum ApActionKind
    {
        /// <summary>Grant an inventory item/mod by its content GID (via the item granter).</summary>
        Inventory,
        /// <summary>
        /// Set one or more GameFlow bool flags true (sector unlock, key, mission flag). More than one
        /// because a single AP item can own several in-game flags — Maintenance Sector grants both
        /// the Lobby and the Pump Room travel flags, which reach the same content.
        /// </summary>
        Flag,
        /// <summary>Grant Security Clearance up to <see cref="ApItemAction.Level"/> (cumulative KEY1..N).</summary>
        Clearance,
        /// <summary>
        /// Grant one step of Security Clearance: the Nth such item received grants clearance N.
        /// Carries no level of its own — the client counts them.
        /// </summary>
        ProgressiveClearance
    }

    /// <summary>
    /// What receiving a given Archipelago item should do. Produced by the item map and consumed by
    /// the client's dispatch. Exactly one payload field is meaningful per <see cref="Kind"/>:
    /// <see cref="Gid"/> for Inventory, <see cref="Flags"/> for Flag, <see cref="Level"/> for Clearance,
    /// <see cref="Ability"/> for Ability. ProgressiveClearance carries no payload — its level comes from
    /// how many of them the client has received.
    /// </summary>
    public sealed record ApItemAction(
        ApActionKind Kind, ulong Gid = 0UL, IReadOnlyList<string>? Flags = null, int Level = 0,
        string? Ability = null, IReadOnlyList<ElevatorBit>? Bits = null)
    {
        public static ApItemAction ForInventory(ulong gid) => new(ApActionKind.Inventory, Gid: gid);
        public static ApItemAction ForFlag(string flag) => new(ApActionKind.Flag, Flags: new[] { flag });

        /// <summary>
        /// One AP item driving several GameFlow flags at once, and optionally the elevator UI bits
        /// the patched panel reads. Sector items need both: the bits decide what the panel offers,
        /// the flags decide whether the game permits the trip once a floor is chosen.
        /// </summary>
        public static ApItemAction ForFlags(IReadOnlyList<string> flags,
            IReadOnlyList<ElevatorBit>? bits = null) =>
            new(ApActionKind.Flag, Flags: flags, Bits: bits);
        public static ApItemAction ForClearance(int level) => new(ApActionKind.Clearance, Level: level);
        public static ApItemAction ProgressiveClearance { get; } = new(ApActionKind.ProgressiveClearance);
    }
}
