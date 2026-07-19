namespace Ap.Control.Models
{
    public sealed class SaveWatchErrorEventArgs : EventArgs
    {
        public required Exception Exception { get; init; }
        public required string Path { get; init; }
    }
}
