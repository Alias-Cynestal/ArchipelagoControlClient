using Ap.Control.Models;

namespace Ap.Control.Utils.Interfaces
{
    /// <summary>
    /// Grants ability-tree upgrades (and menu-purchasable base ability unlocks — same mechanism)
    /// into the running Control game at runtime, via the engine's own native buy/apply worker.
    /// </summary>
    public interface IAbilityGranter : IAsyncDisposable
    {
        /// <summary>True once attached to the game and the player's ability-tree manager has been found.</summary>
        bool IsReady { get; }

        /// <summary>Attach to the running game. Completes once the manager is resolved.</summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Grant one ability upgrade (or base ability unlock) by its definition GID — the type-77
        /// <c>ability_upgrades\{unlock_*,upgrade_*}</c> content GID. Resolves the currently-live
        /// runtime instance for that definition and applies it via the engine's low-level flow-pin
        /// apply (not the menu's buy-button worker), which does not gate on or deduct AbilityPoints —
        /// Archipelago decides what the player owns, not the in-game point economy. Self-persists.
        /// </summary>
        Task<GrantResult> GrantAbilityAsync(ulong definitionGid, CancellationToken cancellationToken = default);

        /// <summary>
        /// Grant the ability-point milestone rewards up to <paramref name="level"/> (1 = extra weapon
        /// slot, 2 = +first personal-mod slot, 3 = +second personal-mod slot). These normally unlock by
        /// spending ability points, which the AP client severs — this raises the spent-points high-water
        /// mark to the level's threshold and fires the milestone reward pin. Cumulative: intended to be
        /// driven progressively (the Nth progressive-milestone item received grants level N).
        /// </summary>
        Task<GrantResult> GrantMilestoneAsync(int level, CancellationToken cancellationToken = default);
    }
}
