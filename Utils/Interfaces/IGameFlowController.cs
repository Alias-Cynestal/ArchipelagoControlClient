namespace Ap.Control.Utils.Interfaces
{
    /// <summary>
    /// Sets Control's GameFlow global variables in the live game — the receive-side lever for
    /// world/access state (sectors, keys, clearance, mission flags). Implemented by
    /// <see cref="Ap.Control.Memory.NativeGameFlowController"/>.
    /// </summary>
    public interface IGameFlowController
    {
        /// <summary>Open the game process if not already open. Returns true once ready (false if the game isn't running).</summary>
        bool EnsureStarted();

        /// <summary>Set a GameFlow bool by name (all live replicas). Returns the number of live nodes written (0 = not present/not running).</summary>
        int SetFlag(string name, bool value);

        /// <summary>Grant Security Clearance up to <paramref name="level"/> (cumulative: sets KEY1..KEYlevel true). Returns nodes written.</summary>
        int SetClearance(int level);

        /// <summary>
        /// Force a whole set of bool variables at once (name -> value) in a SINGLE memory sweep, and
        /// return the total live nodes written. Preferred over looping SetFlag/RelockFlags for
        /// reconciliation: those cost one full sweep per name, which is slow enough to make passes
        /// overlap, and spreads the writes over a longer window in which the game may rebuild the
        /// GameFlow map underneath them.
        /// </summary>
        int ApplyFlags(IReadOnlyDictionary<string, bool> desired);
    }
}
