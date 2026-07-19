namespace Ap.Control.Models
{
    public sealed class SaveChangedEventArgs : EventArgs
    {
        public required ControlSave Save { get; init; }
        public ControlSave? Previous { get; init; }
        public required SaveDiff Diff { get; init; }
        public required string Path { get; init; }
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    }
}
