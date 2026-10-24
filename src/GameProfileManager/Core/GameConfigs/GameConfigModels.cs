using System.Collections.ObjectModel;

namespace GameProfileManager.Core;

public enum GameConfigSourceKind { File, Registry }
public enum GameConfigApplyStatus
{
    NotConfigured, Ready, Pending, WaitingForGameExit, Applying, Applied,
    Conflict, RecoveryRequired, Error
}

public sealed class GameConfigSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public GameConfigSourceKind Kind { get; set; }
    public string TargetPath { get; set; } = "";
    public string ApprovedRootPath { get; set; } = "";
    public string RegistryValueName { get; set; } = "";
    public string DefinitionId { get; set; } = "manual";
    public bool UserConfirmed { get; set; }
    public bool BinaryExplicitlyAllowed { get; set; }
}

public sealed class GameConfigSnapshotItem
{
    public Guid SourceId { get; set; }
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string ContentPath { get; set; } = "";
    public bool SourceExisted { get; set; }
    public FileAttributes? OriginalAttributes { get; set; }
    public string RegistryKind { get; set; } = "";
}

public sealed class GameConfigSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public int SchemaVersion { get; set; } = 1;
    public List<GameConfigSnapshotItem> Items { get; set; } = [];
}

public sealed class GameConfigAssignment
{
    public Guid GameId { get; set; }
    public string DisplayId { get; set; } = "";
    public Guid SnapshotId { get; set; }
}

public sealed class GameConfigOriginalBackup
{
    public Guid GameId { get; set; }
    public Guid SourceId { get; set; }
    public string TargetPath { get; set; } = "";
    public string RegistryValueName { get; set; } = "";
    public bool Existed { get; set; }
    public string ContentPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string RegistryKind { get; set; } = "";
    public FileAttributes? Attributes { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class GameConfigSafetyCopy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public Guid SourceId { get; set; }
    public string ContentPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool Existed { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string Reason { get; set; } = "";
}

public sealed class GameConfigEvent
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public GameConfigApplyStatus Status { get; set; }
    public string Message { get; set; } = "";
}

public sealed class GameConfigSettings
{
    public bool AutoApply { get; set; }
    public ObservableCollection<GameConfigSource> Sources { get; set; } = new();
    public List<string> AdditionalProcessPaths { get; set; } = [];
    public Dictionary<Guid, string> LastAppliedHashes { get; set; } = [];
    public Dictionary<Guid, bool> LastAppliedExists { get; set; } = [];
    public Guid? LastAppliedSnapshotId { get; set; }
    public Guid? DesiredSnapshotId { get; set; }
    public string DesiredDisplayId { get; set; } = "";
    public long DesiredGeneration { get; set; }
    public DateTimeOffset? LastAppliedAt { get; set; }
    public GameConfigApplyStatus Status { get; set; } = GameConfigApplyStatus.NotConfigured;
    public string StatusDetail { get; set; } = "";
    public List<GameConfigEvent> Events { get; set; } = [];
}
