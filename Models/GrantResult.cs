namespace Ap.Control.Models
{
    /// <summary>Outcome of an attempt to grant an item into the running game.</summary>
    public sealed class GrantResult
    {
        /// <summary>The grant call completed without error (does not imply the item was added).</summary>
        public bool Ok { get; init; }

        /// <summary>The game accepted and added the item (spawn worker returned a live instance).</summary>
        public bool Accepted { get; init; }

        /// <summary>Error detail when <see cref="Ok"/> is false, otherwise null.</summary>
        public string? Error { get; init; }

        public static GrantResult Fail(string error) => new() { Ok = false, Error = error };

        public override string ToString() =>
            Ok ? (Accepted ? "accepted" : "rejected") : $"failed: {Error}";
    }
}
