using GameProfileManager.Core;

namespace GameProfileManager.Infrastructure;

public sealed class GameConfigTransactionRecord
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public Guid? SnapshotId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    public bool Completed { get; set; }
    public bool RecoveryCompleted { get; set; }
    public string Status { get; set; } = "Preparing";
    public Dictionary<Guid, string> AppliedHashes { get; set; } = [];
    public Dictionary<Guid, bool> AppliedExists { get; set; } = [];
    public List<GameConfigTransactionItem> Items { get; set; } = [];
}

public sealed class GameConfigTransactionItem
{
    public Guid SourceId { get; set; }
    public GameConfigSourceKind Kind { get; set; }
    public string Target { get; set; } = "";
    public string RegistryValueName { get; set; } = "";
    public bool PreviousExisted { get; set; }
    public FileAttributes? PreviousAttributes { get; set; }
    public string PreviousHash { get; set; } = "";
    public string? PreviousBlob { get; set; }
    public string DesiredHash { get; set; } = "";
    public bool DesiredExists { get; set; }
    public string? TemporaryPath { get; set; }
    public string? RollbackTemporaryPath { get; set; }
    public bool Started { get; set; }
    public bool Applied { get; set; }
    public string ApprovedRootPath { get; set; } = "";
    public bool? OriginalBackupExisted { get; set; }
    public string? OriginalBackupPath { get; set; }
    public string? OriginalBackupHash { get; set; }
    public string OriginalTargetPath { get; set; } = "";
    public string OriginalRegistryValueName { get; set; } = "";
    public string OriginalRegistryKind { get; set; } = "";
    public FileAttributes? OriginalAttributes { get; set; }
    public Guid? SafetyCopyId { get; set; }
    public string? SafetyCopyPath { get; set; }
    public string? SafetyCopyHash { get; set; }
    public bool? SafetyCopyExisted { get; set; }
    public string SafetyCopyReason { get; set; } = "Непосредственно перед изменением";
}
