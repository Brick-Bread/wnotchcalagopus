using NotchCalagopus.Api;

namespace NotchCalagopus.Monitoring;

internal enum DisplayState
{
    Running,
    Starting,
    Stopping,
    Offline,
    Installing,
    InstallFailed,
    Restoring,
    RestoreFailed,
    Suspended,

    /// <summary>The panel has no usage data for the server, e.g. its node is not answering.</summary>
    Unknown,
}

/// <summary>A server and its latest usage, with the figures the cards and the alerts are built from.</summary>
internal sealed record ServerView(ServerInfo Info, ResourceUsage? Usage)
{
    private const long Mebibyte = 1024 * 1024;

    public string Uuid => Info.Uuid;

    public string Name => Info.Name.Length > 0 ? Info.Name : Info.UuidShort;

    public DisplayState State =>
        Info.IsSuspended ? DisplayState.Suspended : Info.Status switch
        {
            "installing" => DisplayState.Installing,
            "install_failed" => DisplayState.InstallFailed,
            "restoring_backup" => DisplayState.Restoring,
            "backup_restore_failed" => DisplayState.RestoreFailed,
            _ => Usage?.Power switch
            {
                PowerState.Running => DisplayState.Running,
                PowerState.Starting => DisplayState.Starting,
                PowerState.Stopping => DisplayState.Stopping,
                PowerState.Offline => DisplayState.Offline,
                _ => DisplayState.Unknown,
            },
        };

    /// <summary>The power state, or null while the server is suspended, installing, restoring or has no data.</summary>
    public PowerState? Power => State switch
    {
        DisplayState.Running => PowerState.Running,
        DisplayState.Starting => PowerState.Starting,
        DisplayState.Stopping => PowerState.Stopping,
        DisplayState.Offline => PowerState.Offline,
        _ => null,
    };

    /// <summary>The configured memory limit, or what the container is allowed when there is none. 0 when unknown.</summary>
    public long MemoryLimitBytes => Info.Limits is { Memory: > 0 } limits ? limits.Memory * Mebibyte : Usage?.MemoryLimitBytes ?? 0;

    /// <summary>Memory in use as 0..1 of <see cref="MemoryLimitBytes"/>, for the card's bar.</summary>
    public double? MemoryFraction =>
        Usage is not null && MemoryLimitBytes > 0 ? Math.Clamp((double)Usage.MemoryBytes / MemoryLimitBytes, 0, 1) : null;

    // The percentages below compare against configured limits only: a server without a limit
    // has nothing to be "nearly out of".

    public double? MemoryPercent =>
        Usage is not null && Info.Limits is { Memory: > 0 } limits ? 100.0 * Usage.MemoryBytes / (limits.Memory * Mebibyte) : null;

    public double? DiskPercent =>
        Usage is not null && Info.Limits is { Disk: > 0 } limits ? 100.0 * Usage.DiskBytes / (limits.Disk * Mebibyte) : null;

    public double? CpuPercent =>
        Usage is not null && Info.Limits is { Cpu: > 0 } limits ? 100.0 * Usage.CpuAbsolute / limits.Cpu : null;
}
