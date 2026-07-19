namespace Ap.Control.Utils.Interfaces
{
    /// <summary>
    /// Grants Control's base abilities (Launch, Evade, Seize, Shield, Levitate, Slam) in the live game
    /// — the receive-side lever for ability unlocks. Implemented by
    /// <see cref="Ap.Control.Memory.NativeAbilityController"/>, which drives each ability's flow gate
    /// pin true.
    ///
    /// NOTE: the underlying live grant is not yet functional for most abilities — reversing each
    /// ability's gate value location (a <c>FlowPinBase</c> reached through the flow virtuals) is the
    /// remaining RE. Until that lands, <see cref="Unlock"/> is a safe no-op (returns 0) rather than a
    /// crash, so the whole AP pipeline can be wired and exercised now and the grant drops in later.
    /// </summary>
    public interface IAbilityController
    {
        /// <summary>Open the game process if not already open. Returns true once ready (false if the game isn't running).</summary>
        bool EnsureStarted();

        /// <summary>
        /// Unlock a base ability by its key (e.g. <c>"levitate"</c>; see
        /// <see cref="Ap.Control.Memory.NativeAbilityController.Abilities"/>). Returns the number of live
        /// ability instances driven — 0 means the game isn't running, the key is unknown, or the
        /// ability's gate offset hasn't been reversed yet (inert).
        /// </summary>
        int Unlock(string abilityKey);
    }
}
