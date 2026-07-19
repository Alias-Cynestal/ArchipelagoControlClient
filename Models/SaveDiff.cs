namespace Ap.Control.Models
{
    public sealed class ItemQuantityChange
    {
        public required ulong PersistentId { get; init; }
        public required ulong Gid { get; init; }
        public required uint OldQuantity { get; init; }
        public required uint NewQuantity { get; init; }
    }

    public sealed class MissionStateChange
    {
        public required ulong GidMissionId { get; init; }
        public required uint? OldState { get; init; }
        public required uint NewState { get; init; }
        public bool IsNew => OldState is null;
    }

    public sealed class ScalarChange<T> where T : struct
    {
        public required T OldValue { get; init; }
        public required T NewValue { get; init; }
    }

    public sealed class SaveDiff
    {
        public IReadOnlyList<GameInventoryItemData> AddedItems { get; init; } = Array.Empty<GameInventoryItemData>();
        public IReadOnlyList<GameInventoryItemData> RemovedItems { get; init; } = Array.Empty<GameInventoryItemData>();
        public IReadOnlyList<ItemQuantityChange> QuantityChanges { get; init; } = Array.Empty<ItemQuantityChange>();
        public IReadOnlyList<ulong> NewSectorsVisited { get; init; } = Array.Empty<ulong>();
        public IReadOnlyList<ulong> NewFoundLocations { get; init; } = Array.Empty<ulong>();
        public IReadOnlyList<ulong> NewFoundNarrativeObjects { get; init; } = Array.Empty<ulong>();
        public IReadOnlyList<ulong> NewUnlockedControlPoints { get; init; } = Array.Empty<ulong>();
        public ScalarChange<uint>? Level { get; init; }
        public ScalarChange<uint>? AbilityPoints { get; init; }
        public ScalarChange<uint>? AbilityPointsSpent { get; init; }
        public IReadOnlyList<MissionStateChange> MissionChanges { get; init; } = Array.Empty<MissionStateChange>();
        public IReadOnlyList<ulong> NewCompletedTrials { get; init; } = Array.Empty<ulong>();
        public IReadOnlyList<uint> NewOutfits { get; init; } = Array.Empty<uint>();
        public bool HasChanges =>
            AddedItems.Count > 0 || RemovedItems.Count > 0 || QuantityChanges.Count > 0 ||
            NewSectorsVisited.Count > 0 || NewFoundLocations.Count > 0 ||
            NewFoundNarrativeObjects.Count > 0 || NewUnlockedControlPoints.Count > 0 ||
            Level is not null || AbilityPoints is not null || AbilityPointsSpent is not null ||
            MissionChanges.Count > 0 || NewCompletedTrials.Count > 0 || NewOutfits.Count > 0;
    }
}
