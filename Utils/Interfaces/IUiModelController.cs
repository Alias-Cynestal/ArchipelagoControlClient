namespace Ap.Control.Utils.Interfaces
{
    /// <summary>
    /// Which elevator destination each bit of the repurposed UI model drives. The stock game decides
    /// the elevator's floor list from a single ordinal, which makes the destinations a strict ladder
    /// (Research &lt; Maintenance &lt; Containment &lt; Investigation) and hardcodes the Pump Room and
    /// Executive entries as always available. Driving these five independent bits instead — together
    /// with the matching UI patch — is what lets Archipelago gate each destination on its own.
    /// </summary>
    public enum ElevatorBit
    {
        Research = 0,
        MaintenanceLobby = 1,
        Containment = 2,
        Investigation = 3,
        MaintenancePumpRoom = 4,
    }

    /// <summary>
    /// Writes the Coherent UI model the patched elevator panel reads.
    ///
    /// The model is <c>ControlPointPrimeVisibilityState</c>, five consecutive bools that vanilla uses
    /// for the control-point "new" badges. Poking them by raw memory write is known to reach the UI
    /// (verified in game), and the panel samples the model when it builds its view — exactly when the
    /// elevator list is assembled.
    ///
    /// The game still writes these bytes for their original purpose (an ability becoming available
    /// sets the Abilities badge), so the client must re-assert continuously rather than write once,
    /// or a legitimate badge would read as an unlocked sector.
    /// </summary>
    public interface IUiModelController
    {
        /// <summary>Attach to the running game. Safe to call repeatedly; a no-op once attached.</summary>
        bool EnsureStarted();

        /// <summary>
        /// Write all five bits. Returns true if the model was located and written. False means the
        /// game isn't running, no save is loaded, or the model moved — callers should treat it as
        /// "try again next tick" rather than an error.
        /// </summary>
        bool SetBits(IReadOnlySet<ElevatorBit> granted);

        /// <summary>Current five bytes as the game holds them, or null if the model can't be read.</summary>
        byte[]? ReadRaw();
    }
}
