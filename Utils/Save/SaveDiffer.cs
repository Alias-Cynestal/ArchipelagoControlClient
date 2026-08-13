using Ap.Control.Models;

namespace Ap.Control.Utils.Save
{
    public static class SaveDiffer
    {
        public static SaveDiff Diff(ControlSave? previous, ControlSave current)
        {
            var prevInv = previous?.GetData<GameInventory>(DataType.Inventory);
            var curInv = current.GetData<GameInventory>(DataType.Inventory);
            var (added, removed, qtyChanges) = DiffInventory(prevInv, curInv);

            var prevPp = previous?.GetData<PlayerProperties>(DataType.PlayerProperties);
            var curPp = current.GetData<PlayerProperties>(DataType.PlayerProperties);

            var prevMm = previous?.GetData<MissionManager>(DataType.MissionManager);
            var curMm = current.GetData<MissionManager>(DataType.MissionManager);

            var prevTr = previous?.GetData<PlayerTrials>(DataType.Trials);
            var curTr = current.GetData<PlayerTrials>(DataType.Trials);

            var prevOut = previous?.GetData<OutfitData>(DataType.Outfit);
            var curOut = current.GetData<OutfitData>(DataType.Outfit);

            return new SaveDiff
            {
                AddedItems = added,
                RemovedItems = removed,
                QuantityChanges = qtyChanges,

                NewSectorsVisited = NewGids(prevPp?.SectorsVisited.Gid, curPp?.SectorsVisited.Gid),
                NewFoundLocations = NewGids(prevPp?.FoundLocations.Gid, curPp?.FoundLocations.Gid),
                NewCollectibles = NewGids(prevPp?.FoundNarrativeObjects.Gid, curPp?.FoundNarrativeObjects.Gid),
                NewUnlockedControlPoints = NewGids(
                    prevPp?.UnlockedControlPoints.Select(cp => cp.Gid),
                    curPp?.UnlockedControlPoints.Select(cp => cp.Gid)),
                Level = ScalarDelta(prevPp?.Level.Value, curPp?.Level.Value),
                AbilityPoints = ScalarDelta(prevPp?.AbilityPoints, curPp?.AbilityPoints),
                AbilityPointsSpent = ScalarDelta(prevPp?.AbilityPointsSpent, curPp?.AbilityPointsSpent),

                MissionChanges = DiffMissions(prevMm, curMm),

                NewCompletedTrials = NewGids(
                    prevTr?.CompletedTrials.Trials.Select(t => t.Gid),
                    curTr?.CompletedTrials.Trials.Select(t => t.Gid)),

                NewOutfits = NewValues(
                    prevOut?.Outfits.Where(o => o.IsObtained).Select(o => o.OutfitId),
                    curOut?.Outfits.Where(o => o.IsObtained).Select(o => o.OutfitId)),
            };
        }

        private static (List<GameInventoryItemData> Added, List<GameInventoryItemData> Removed, List<ItemQuantityChange> Changes)
            DiffInventory(GameInventory? prev, GameInventory? cur)
        {
            var added = new List<GameInventoryItemData>();
            var removed = new List<GameInventoryItemData>();
            var changes = new List<ItemQuantityChange>();
            if (cur is null)
                return (added, removed, changes);

            var prevById = (prev?.ItemData ?? new List<GameInventoryItemData>())
                .GroupBy(i => i.PersistentId)
                .ToDictionary(g => g.Key, g => g.First());
            var curIds = new HashSet<ulong>();

            foreach (var item in cur.ItemData)
            {
                curIds.Add(item.PersistentId);
                if (!prevById.TryGetValue(item.PersistentId, out var old))
                {
                    added.Add(item);
                }
                else if (old.Quantity != item.Quantity)
                {
                    changes.Add(new ItemQuantityChange
                    {
                        PersistentId = item.PersistentId,
                        Gid = item.Gid,
                        OldQuantity = old.Quantity,
                        NewQuantity = item.Quantity,
                    });
                }
            }

            if (prev is not null)
                foreach (var old in prev.ItemData)
                    if (!curIds.Contains(old.PersistentId))
                        removed.Add(old);

            return (added, removed, changes);
        }

        private static List<MissionStateChange> DiffMissions(MissionManager? prev, MissionManager? cur)
        {
            var changes = new List<MissionStateChange>();
            if (cur is null)
                return changes;

            var prevByGid = (prev?.Missions ?? new List<MissionManagerMission>())
                .GroupBy(m => m.GidMissionId)
                .ToDictionary(g => g.Key, g => g.First().MissionState);

            foreach (var m in cur.Missions)
            {
                if (!prevByGid.TryGetValue(m.GidMissionId, out var oldState))
                    changes.Add(new MissionStateChange { GidMissionId = m.GidMissionId, OldState = null, NewState = m.MissionState });
                else if (oldState != m.MissionState)
                    changes.Add(new MissionStateChange { GidMissionId = m.GidMissionId, OldState = oldState, NewState = m.MissionState });
            }
            return changes;
        }

        private static IReadOnlyList<ulong> NewGids(IEnumerable<ulong>? prev, IEnumerable<ulong>? cur)
            => NewValues(prev, cur);

        private static IReadOnlyList<T> NewValues<T>(IEnumerable<T>? prev, IEnumerable<T>? cur)
        {
            if (cur is null)
                return Array.Empty<T>();
            var old = prev is null ? new HashSet<T>() : new HashSet<T>(prev);
            var result = new List<T>();
            var seen = new HashSet<T>();
            foreach (var v in cur)
                if (!old.Contains(v) && seen.Add(v))
                    result.Add(v);
            return result;
        }

        private static ScalarChange<uint>? ScalarDelta(uint? old, uint? cur)
        {
            if (cur is null)
                return null;
            uint oldVal = old ?? 0;
            return oldVal == cur.Value ? null : new ScalarChange<uint> { OldValue = oldVal, NewValue = cur.Value };
        }
    }
}
