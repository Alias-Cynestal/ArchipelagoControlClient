using Ap.Control.Utils.Save;

namespace Ap.Control.Models
{
    public enum DataType : uint
    {
        GlobalVariableManager = 542507875,
        MissionManager = 897885379,
        LootDrop = 948665365,
        Vendor = 1019016931,
        PlayerProperties = 1721202367,
        ExpeditionManager = 2134335914,
        Trials = 2227510435,
        TutorialManager = 2500542964,
        Outfit = 3357797087,
        Inventory = 3388499232,
        EncounterDirector = 4156221793,
    }

    public sealed class ControlSave
    {
        public required Header Header { get; init; }
        public required List<Chunk> Chunks { get; init; }

        public static ControlSave Read(SaveReader r)
        {
            var header = Header.Read(r);
            var chunks = new List<Chunk>((int)header.NumChunks);
            for (uint i = 0; i < header.NumChunks; i++)
                chunks.Add(Chunk.Read(r));
            return new ControlSave { Header = header, Chunks = chunks };
        }

        public T? GetData<T>(DataType type) where T : class
            => Chunks.FirstOrDefault(c => c.UidHigh == type)?.Data as T;
    }

    public sealed class Header
    {
        public uint ChecksumCrc32 { get; set; }
        public uint FilenameLen { get; set; }
        public string FilenameStr { get; set; } = string.Empty;
        public byte Scope { get; set; }
        public uint Unk3 { get; set; }
        public uint NumChunks { get; set; }

        public static Header Read(SaveReader r)
        {
            r.ExpectBytes(6, 0, 0, 0, 6, 0, 0, 0);
            var h = new Header
            {
                ChecksumCrc32 = r.ReadUInt32(),
                FilenameLen = r.ReadUInt32(),
            };
            h.FilenameStr = System.Text.Encoding.UTF8.GetString(r.ReadBytes(checked((int)h.FilenameLen)));
            h.Scope = r.ReadByte();
            h.Unk3 = r.ReadUInt32();
            h.NumChunks = r.ReadUInt32();
            return h;
        }
    }

    public sealed class Chunk
    {
        public uint UidLow { get; set; }
        public DataType UidHigh { get; set; }
        public uint Size { get; set; }

        public byte[] RawData { get; set; } = Array.Empty<byte>();

        public object? Data { get; set; }

        public ulong Uid => ((ulong)(uint)UidHigh << 32) | UidLow;

        public static Chunk Read(SaveReader r)
        {
            var c = new Chunk
            {
                UidLow = r.ReadUInt32(),
                UidHigh = (DataType)r.ReadUInt32(),
                Size = r.ReadUInt32(),
            };
            c.RawData = r.ReadBytes(checked((int)c.Size));

            using var ms = new MemoryStream(c.RawData, writable: false);
            using var sub = new SaveReader(ms);
            c.Data = c.UidHigh switch
            {
                DataType.Inventory => GameInventory.Read(sub),
                DataType.MissionManager => MissionManager.Read(sub),
                DataType.Outfit => OutfitData.Read(sub),
                DataType.PlayerProperties => PlayerProperties.Read(sub),
                DataType.Trials => PlayerTrials.Read(sub),
                DataType.GlobalVariableManager => GlobalVariableManager.Read(sub),
                _ => UnknownData.Read(sub),
            };
            return c;
        }
    }

    public sealed class VersionData
    {
        public uint ObjectVersion { get; set; }
        public uint Unk2 { get; set; }
        public uint Unk3 { get; set; }
        public uint Unk4 { get; set; }
        public uint Unk5 { get; set; }

        public static VersionData Read(SaveReader r) => new()
        {
            ObjectVersion = r.ReadUInt32(),
            Unk2 = r.ReadUInt32(),
            Unk3 = r.ReadUInt32(),
            Unk4 = r.ReadUInt32(),
            Unk5 = r.ReadUInt32(),
        };
    }

    public sealed class StrType
    {
        public uint StrLen { get; set; }
        public string Str { get; set; } = string.Empty;

        public static StrType Read(SaveReader r)
        {
            uint len = r.ReadUInt32();
            return new StrType
            {
                StrLen = len,
                Str = System.Text.Encoding.UTF8.GetString(r.ReadBytes(checked((int)len))),
            };
        }

        public override string ToString() => Str;
    }

    public sealed class OspInt
    {
        public uint ObjectVersion { get; set; }
        public uint Value { get; set; }

        public static OspInt Read(SaveReader r) => new()
        {
            ObjectVersion = r.ReadUInt32(),
            Value = r.ReadUInt32(),
        };
    }

    public sealed class GidArray
    {
        public uint NumItems { get; set; }
        public List<ulong> Gid { get; set; } = new();

        public static GidArray Read(SaveReader r)
        {
            uint n = r.ReadUInt32();
            var list = new List<ulong>((int)n);
            for (uint i = 0; i < n; i++)
                list.Add(r.ReadUInt64());
            return new GidArray { NumItems = n, Gid = list };
        }
    }

    public sealed class GameTimer
    {
        public uint ObjectVersion { get; set; }
        public float TimeLeft { get; set; }
        public byte? IsPaused { get; set; }

        public static GameTimer Read(SaveReader r)
        {
            var t = new GameTimer
            {
                ObjectVersion = r.ReadUInt32(),
                TimeLeft = r.ReadSingle(),
            };
            if (t.ObjectVersion >= 2)
                t.IsPaused = r.ReadByte();
            return t;
        }
    }

    public sealed class GameInventoryItemData
    {
        public uint ObjectVersion { get; set; }
        public ulong Gid { get; set; }
        public float Parameter { get; set; }
        public uint OverchargeNormalizedValue { get; set; }
        public ulong PersistentId { get; set; }
        public uint Quantity { get; set; }

        public static GameInventoryItemData Read(SaveReader r) => new()
        {
            ObjectVersion = r.ReadUInt32(),
            Gid = r.ReadUInt64(),
            Parameter = r.ReadSingle(),
            OverchargeNormalizedValue = r.ReadUInt32(),
            PersistentId = r.ReadUInt64(),
            Quantity = r.ReadUInt32(),
        };
    }

    public sealed class GameInventoryActivePersistingItem
    {
        public ulong GidItem { get; set; }
        public ulong GidUnk { get; set; }

        public static GameInventoryActivePersistingItem Read(SaveReader r) => new()
        {
            GidItem = r.ReadUInt64(),
            GidUnk = r.ReadUInt64(),
        };
    }

    public sealed class GameInventoryActiveItemIndicies
    {
        public uint ObjectVersion { get; set; }
        public uint ItemIndex { get; set; }
        public int ParentIndex { get; set; }

        public static GameInventoryActiveItemIndicies Read(SaveReader r) => new()
        {
            ObjectVersion = r.ReadUInt32(),
            ItemIndex = r.ReadUInt32(),
            ParentIndex = r.ReadInt32(),
        };
    }

    public sealed class GameInventory
    {
        public uint ObjectVersion { get; set; }
        public uint NumItems { get; set; }
        public List<GameInventoryItemData> ItemData { get; set; } = new();
        public uint EquippedWeaponIndex { get; set; }
        public uint NumActivePersistingItems { get; set; }
        public List<GameInventoryActivePersistingItem> ActivePersistingItems { get; set; } = new();
        public uint NumActiveItems { get; set; }
        public List<GameInventoryActiveItemIndicies> ActiveItemData { get; set; } = new();
        public byte? Pseven34843Patched { get; set; }

        public static GameInventory Read(SaveReader r)
        {
            var inv = new GameInventory
            {
                ObjectVersion = r.ReadUInt32(),
                NumItems = r.ReadUInt32(),
            };
            for (uint i = 0; i < inv.NumItems; i++)
                inv.ItemData.Add(GameInventoryItemData.Read(r));

            inv.EquippedWeaponIndex = r.ReadUInt32();
            inv.NumActivePersistingItems = r.ReadUInt32();
            for (uint i = 0; i < inv.NumActivePersistingItems; i++)
                inv.ActivePersistingItems.Add(GameInventoryActivePersistingItem.Read(r));

            inv.NumActiveItems = r.ReadUInt32();
            for (uint i = 0; i < inv.NumActiveItems; i++)
                inv.ActiveItemData.Add(GameInventoryActiveItemIndicies.Read(r));

            if (inv.ObjectVersion >= 4)
                inv.Pseven34843Patched = r.ReadByte();
            return inv;
        }
    }

    public sealed class MissionManagerMissionStep
    {
        public ulong GidMissionStepId { get; set; }
        public uint CurrentProgress { get; set; }
        public uint GoalProgress { get; set; }
        public uint StepState { get; set; }
        public uint Index { get; set; }
        public uint Unk { get; set; }
        public ulong GidLocation { get; set; }
        public ulong GidMissionStepId2 { get; set; }

        public static MissionManagerMissionStep Read(SaveReader r) => new()
        {
            GidMissionStepId = r.ReadUInt64(),
            CurrentProgress = r.ReadUInt32(),
            GoalProgress = r.ReadUInt32(),
            StepState = r.ReadUInt32(),
            Index = r.ReadUInt32(),
            Unk = r.ReadUInt32(),
            GidLocation = r.ReadUInt64(),
            GidMissionStepId2 = r.ReadUInt64(),
        };
    }

    public sealed class MissionManagerMission
    {
        public uint ObjectVersion { get; set; }
        public ulong GidMissionId { get; set; }
        public uint NumMissionSteps { get; set; }
        public List<MissionManagerMissionStep> MissionSteps { get; set; } = new();
        public uint MissionState { get; set; }
        public byte IsActive { get; set; }
        public byte IsAlert { get; set; }
        public float AlertDuration { get; set; }
        public StrType StrOnCompleteCallback { get; set; } = new();
        public ulong GidOnCompleteCallbackTarget { get; set; }
        public StrType StrOnAlertAppearedCallback { get; set; } = new();
        public StrType StrOnAlertDisappearedCallback { get; set; } = new();
        public ulong GidAlertCallbackTarget { get; set; }

        public static MissionManagerMission Read(SaveReader r)
        {
            var m = new MissionManagerMission
            {
                ObjectVersion = r.ReadUInt32(),
                GidMissionId = r.ReadUInt64(),
                NumMissionSteps = r.ReadUInt32(),
            };
            for (uint i = 0; i < m.NumMissionSteps; i++)
                m.MissionSteps.Add(MissionManagerMissionStep.Read(r));

            m.MissionState = r.ReadUInt32();
            m.IsActive = r.ReadByte();
            m.IsAlert = r.ReadByte();
            m.AlertDuration = r.ReadSingle();
            m.StrOnCompleteCallback = StrType.Read(r);
            m.GidOnCompleteCallbackTarget = r.ReadUInt64();
            m.StrOnAlertAppearedCallback = StrType.Read(r);
            m.StrOnAlertDisappearedCallback = StrType.Read(r);
            m.GidAlertCallbackTarget = r.ReadUInt64();
            return m;
        }
    }

    public sealed class MissionManager
    {
        public VersionData VersionData { get; set; } = new();
        public uint NumMissions { get; set; }
        public List<MissionManagerMission> Missions { get; set; } = new();
        public ulong GidAlertMission { get; set; }
        public GameTimer AlertTimer { get; set; } = new();
        public GidArray BlockedAlertMissions { get; set; } = new();

        public static MissionManager Read(SaveReader r)
        {
            var mm = new MissionManager
            {
                VersionData = VersionData.Read(r),
                NumMissions = r.ReadUInt32(),
            };
            for (uint i = 0; i < mm.NumMissions; i++)
                mm.Missions.Add(MissionManagerMission.Read(r));

            mm.GidAlertMission = r.ReadUInt64();
            mm.AlertTimer = GameTimer.Read(r);
            mm.BlockedAlertMissions = GidArray.Read(r);
            return mm;
        }
    }

    public sealed class ControlPointData
    {
        public ulong Gid { get; set; }
        public uint Unk2 { get; set; }
        public StrType Str1 { get; set; } = new();
        public StrType Str2 { get; set; } = new();
        public StrType Str3 { get; set; } = new();

        public static ControlPointData Read(SaveReader r) => new()
        {
            Gid = r.ReadUInt64(),
            Unk2 = r.ReadUInt32(),
            Str1 = StrType.Read(r),
            Str2 = StrType.Read(r),
            Str3 = StrType.Read(r),
        };
    }

    public sealed class UiTagManagerTag
    {
        public uint ObjectVersion { get; set; }
        public ulong Id { get; set; }
        public uint TagValue { get; set; }

        public static UiTagManagerTag Read(SaveReader r) => new()
        {
            ObjectVersion = r.ReadUInt32(),
            Id = r.ReadUInt64(),
            TagValue = r.ReadUInt32(),
        };
    }

    public sealed class PlayerProperties
    {
        public VersionData VersionData { get; set; } = new();
        public ulong Source { get; set; }
        public uint AbilityPoints { get; set; }
        public GidArray SectorsVisited { get; set; } = new();
        public uint NumEnemyTypesUnlocked { get; set; }
        public List<uint> EnemyType { get; set; } = new();
        public ulong GidCurrentSector { get; set; }
        public GidArray FoundNarrativeObjects { get; set; } = new();
        public uint NumUnlockedControlPoints { get; set; }
        public List<ControlPointData> UnlockedControlPoints { get; set; } = new();
        public ulong GidLastUsedControlPoint { get; set; }
        public uint UiTagManagerObjectVersionU { get; set; }
        public uint UiTagManagerNumTags { get; set; }
        public List<UiTagManagerTag> UiTagManagerTags { get; set; } = new();
        public OspInt ProgressionLevel { get; set; } = new();
        public GidArray FoundLocations { get; set; } = new();
        public ulong GidCurrentLocation { get; set; }
        public ulong? GidPreviousLocation { get; set; }
        public uint AbilityPointsSpent { get; set; }
        public uint? AbilityPointsSpentHighWaterMark { get; set; }
        public OspInt Level { get; set; } = new();
        public OspInt AbilityPointsTotal { get; set; } = new();
        public byte ControlPointsUpgraded { get; set; }
        public byte? Pseven34843Patched { get; set; }

        public static PlayerProperties Read(SaveReader r)
        {
            var p = new PlayerProperties
            {
                VersionData = VersionData.Read(r),
                Source = r.ReadUInt64(),
                AbilityPoints = r.ReadUInt32(),
                SectorsVisited = GidArray.Read(r),
                NumEnemyTypesUnlocked = r.ReadUInt32(),
            };
            for (uint i = 0; i < p.NumEnemyTypesUnlocked; i++)
                p.EnemyType.Add(r.ReadUInt32());

            p.GidCurrentSector = r.ReadUInt64();
            p.FoundNarrativeObjects = GidArray.Read(r);
            p.NumUnlockedControlPoints = r.ReadUInt32();
            for (uint i = 0; i < p.NumUnlockedControlPoints; i++)
                p.UnlockedControlPoints.Add(ControlPointData.Read(r));

            p.GidLastUsedControlPoint = r.ReadUInt64();
            p.UiTagManagerObjectVersionU = r.ReadUInt32();
            p.UiTagManagerNumTags = r.ReadUInt32();
            for (uint i = 0; i < p.UiTagManagerNumTags; i++)
                p.UiTagManagerTags.Add(UiTagManagerTag.Read(r));

            p.ProgressionLevel = OspInt.Read(r);
            p.FoundLocations = GidArray.Read(r);
            p.GidCurrentLocation = r.ReadUInt64();
            if (p.VersionData.ObjectVersion >= 19)
                p.GidPreviousLocation = r.ReadUInt64();

            p.AbilityPointsSpent = r.ReadUInt32();
            if (p.VersionData.ObjectVersion >= 21)
                p.AbilityPointsSpentHighWaterMark = r.ReadUInt32();

            p.Level = OspInt.Read(r);
            p.AbilityPointsTotal = OspInt.Read(r);
            p.ControlPointsUpgraded = r.ReadByte();
            if (p.VersionData.ObjectVersion >= 20)
                p.Pseven34843Patched = r.ReadByte();
            return p;
        }
    }

    public sealed class TrialReward
    {
        public uint ObjectVersion { get; set; }
        public ulong GidItem { get; set; }
        public float Parameter { get; set; }

        public static TrialReward Read(SaveReader r) => new()
        {
            ObjectVersion = r.ReadUInt32(),
            GidItem = r.ReadUInt64(),
            Parameter = r.ReadSingle(),
        };
    }

    public sealed class TrialData
    {
        public uint ObjectVersion { get; set; }
        public ulong Gid { get; set; }
        public uint CurrentProgress { get; set; }
        public uint NumRewards { get; set; }
        public List<TrialReward> Rewards { get; set; } = new();

        public static TrialData Read(SaveReader r)
        {
            var t = new TrialData
            {
                ObjectVersion = r.ReadUInt32(),
                Gid = r.ReadUInt64(),
                CurrentProgress = r.ReadUInt32(),
                NumRewards = r.ReadUInt32(),
            };
            for (uint i = 0; i < t.NumRewards; i++)
                t.Rewards.Add(TrialReward.Read(r));
            return t;
        }
    }

    public sealed class TrialVec
    {
        public uint NumItems { get; set; }
        public List<TrialData> Trials { get; set; } = new();

        public static TrialVec Read(SaveReader r)
        {
            uint n = r.ReadUInt32();
            var v = new TrialVec { NumItems = n };
            for (uint i = 0; i < n; i++)
                v.Trials.Add(TrialData.Read(r));
            return v;
        }
    }

    public sealed class PlayerTrials
    {
        public VersionData VersionData { get; set; } = new();
        public TrialVec ActiveTrials { get; set; } = new();
        public TrialVec SelectableTrials { get; set; } = new();
        public TrialVec CompletedTrials { get; set; } = new();
        public TrialVec PotentialTrials { get; set; } = new();
        public TrialVec AllTrials { get; set; } = new();
        public GidArray AvailableWeaponArchetypes { get; set; } = new();

        public static PlayerTrials Read(SaveReader r) => new()
        {
            VersionData = VersionData.Read(r),
            ActiveTrials = TrialVec.Read(r),
            SelectableTrials = TrialVec.Read(r),
            CompletedTrials = TrialVec.Read(r),
            PotentialTrials = TrialVec.Read(r),
            AllTrials = TrialVec.Read(r),
            AvailableWeaponArchetypes = GidArray.Read(r),
        };
    }

    public enum OutfitStatus : uint
    {
        Locked = 0,
        Unlocked = 1,
    }

    public sealed class PlayerOutfitInfo
    {
        public uint ObjectVersion { get; set; }
        public uint OutfitId { get; set; }
        public uint EStatus { get; set; }

        public bool IsObtained => EStatus != (uint)OutfitStatus.Locked;

        public static PlayerOutfitInfo Read(SaveReader r)
        {
            r.ExpectBytes(2, 0, 0, 0);
            return new PlayerOutfitInfo
            {
                ObjectVersion = 2,
                OutfitId = r.ReadUInt32(),
                EStatus = r.ReadUInt32(),
            };
        }
    }

    public sealed class OutfitData
    {
        public VersionData VersionData { get; set; } = new();
        public uint CurrentOutfitId { get; set; }
        public uint ScriptOverrideOutfitId { get; set; }
        public uint NumOutfits { get; set; }
        public List<PlayerOutfitInfo> Outfits { get; set; } = new();

        public static OutfitData Read(SaveReader r)
        {
            var o = new OutfitData
            {
                VersionData = VersionData.Read(r),
                CurrentOutfitId = r.ReadUInt32(),
                ScriptOverrideOutfitId = r.ReadUInt32(),
                NumOutfits = r.ReadUInt32(),
            };
            for (uint i = 0; i < o.NumOutfits; i++)
                o.Outfits.Add(PlayerOutfitInfo.Read(r));
            return o;
        }
    }

    public sealed class UnknownData
    {
        public VersionData VersionData { get; set; } = new();

        public static UnknownData Read(SaveReader r) => new()
        {
            VersionData = VersionData.Read(r),
        };
    }

    public enum GlobalVariableType : uint
    {
        Bool = 0,
        Int = 1,
        Float = 2,
        // Type 3 observed but not yet confirmed (likely a u64/GID); RawValue holds all 8 bytes.
    }

    // One entry of GlobalVariableManagerSingletonComponentState::m_mapGlobalVariables.
    // KeyHash is a 32-bit name hash (entries are stored sorted ascending — it's a std::map).
    // Value is a fixed 8-byte slot interpreted per Type. Ability "unlocked/allowed" state
    // lives here as Bool (Type 0) variables; identify the specific key by diffing two saves.
    public sealed class GlobalVariable
    {
        public uint KeyHash { get; set; }
        public uint ObjectVersion { get; set; }
        public GlobalVariableType Type { get; set; }
        public ulong RawValue { get; set; }

        public bool AsBool => RawValue != 0;
        public int AsInt => unchecked((int)(uint)RawValue);
        public float AsFloat => BitConverter.Int32BitsToSingle(unchecked((int)(uint)RawValue));
        public ulong AsGid => RawValue;

        public object Value => Type switch
        {
            GlobalVariableType.Bool => AsBool,
            GlobalVariableType.Int => AsInt,
            GlobalVariableType.Float => AsFloat,
            _ => RawValue,
        };

        public static GlobalVariable Read(SaveReader r) => new()
        {
            KeyHash = r.ReadUInt32(),
            ObjectVersion = r.ReadUInt32(),
            Type = (GlobalVariableType)r.ReadUInt32(),
            RawValue = r.ReadUInt64(),
        };

        public override string ToString() => $"{KeyHash:X8} {Type}={Value}";
    }

    // GlobalVariableManager chunk (DataType 542507875 / CID_GLOBALVARIABLES): the singleton
    // GameFlow global-variable store. Layout: VersionData (5 u32), u32 count, then count
    // 20-byte GlobalVariable entries. This is the upstream persistent source that drives
    // ability m_in_bAbilityAllowed flow pins — where base-ability unlocks live.
    public sealed class GlobalVariableManager
    {
        public VersionData VersionData { get; set; } = new();
        public uint NumVariables { get; set; }
        public List<GlobalVariable> Variables { get; set; } = new();

        public static GlobalVariableManager Read(SaveReader r)
        {
            var g = new GlobalVariableManager
            {
                VersionData = VersionData.Read(r),
                NumVariables = r.ReadUInt32(),
            };
            for (uint i = 0; i < g.NumVariables; i++)
                g.Variables.Add(GlobalVariable.Read(r));
            return g;
        }
    }
}
