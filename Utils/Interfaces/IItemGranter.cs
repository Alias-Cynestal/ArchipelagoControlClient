using Ap.Control.Models;

namespace Ap.Control.Utils.Interfaces
{
    /// <summary>
    /// Grants items into the running Control game at runtime. Abstracts the hooking mechanism
    /// (currently a Frida agent driven out-of-process) so it can be swapped later.
    /// </summary>
    public interface IItemGranter : IAsyncDisposable
    {
        /// <summary>True once attached to the game and the player inventory has been captured.</summary>
        bool IsReady { get; }

        /// <summary>Attach to the running game and load the hook. Completes when the hook is armed.</summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Grant one item to the player by its definition GID (the 0x54-tagged LootDropItem GIDs
        /// from control_gid_lookup.json). <paramref name="parameter"/> is the item's engine
        /// "Parameter" float (matches <c>GameInventoryItemData.Parameter</c> in the save model) —
        /// for mods it is the modifier/roll magnitude, NOT a quantity. It grants exactly one item.
        /// </summary>
        Task<GrantResult> GiveItemAsync(ulong gid, float parameter = 1.0f, CancellationToken cancellationToken = default);
    }
}
