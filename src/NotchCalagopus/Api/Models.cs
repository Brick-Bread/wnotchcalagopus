using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotchCalagopus.Api;

// The parts of the Calagopus client API this plugin reads. The panel adds fields between
// releases and extensions can add their own, so every type tolerates unknown and missing ones.

internal enum PowerState
{
    Offline,
    Starting,
    Stopping,
    Running,
}

internal static class CalagopusJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static PowerState? ParseState(string? state) => state switch
    {
        "offline" => PowerState.Offline,
        "starting" => PowerState.Starting,
        "stopping" => PowerState.Stopping,
        "running" => PowerState.Running,
        _ => null,
    };
}

internal sealed record ServerInfo
{
    public string Uuid { get; init; } = "";

    public string UuidShort { get; init; } = "";

    public string Name { get; init; } = "";

    public string NodeUuid { get; init; } = "";

    public string NodeName { get; init; } = "";

    /// <summary>Install or restore progress, not the power state: installing, install_failed, restoring_backup, backup_restore_failed or null.</summary>
    public string? Status { get; init; }

    public bool IsSuspended { get; init; }

    public ServerLimits? Limits { get; init; }
}

/// <summary>Configured limits. Zero means unlimited.</summary>
internal sealed record ServerLimits
{
    /// <summary>Percent, where 100 is one core.</summary>
    public int Cpu { get; init; }

    /// <summary>MiB.</summary>
    public long Memory { get; init; }

    /// <summary>MiB.</summary>
    public long Disk { get; init; }
}

internal sealed record ResourceUsage
{
    public long MemoryBytes { get; init; }

    /// <summary>What the container is allowed, which is the host's memory when the server has no limit.</summary>
    public long MemoryLimitBytes { get; init; }

    public long DiskBytes { get; init; }

    public string? State { get; init; }

    /// <summary>Percent, where 100 is one core.</summary>
    public double CpuAbsolute { get; init; }

    /// <summary>Milliseconds.</summary>
    public long Uptime { get; init; }

    public NetworkUsage? Network { get; init; }

    [JsonIgnore]
    public PowerState? Power => CalagopusJson.ParseState(State);
}

/// <summary>Bytes the server has moved since it started.</summary>
internal sealed record NetworkUsage
{
    public long RxBytes { get; init; }

    public long TxBytes { get; init; }
}

internal sealed record WebsocketCredentials
{
    public string Token { get; init; } = "";

    public string Url { get; init; } = "";
}

internal sealed record ServerListResponse
{
    public ServerPage? Servers { get; init; }
}

internal sealed record ServerPage
{
    public long Total { get; init; }

    public List<ServerInfo>? Data { get; init; }
}

internal sealed record ResourcesResponse
{
    public ResourceUsage? Resources { get; init; }
}

internal sealed record NodeResourcesResponse
{
    public Dictionary<string, ResourceUsage>? Resources { get; init; }
}

internal sealed record ErrorResponse
{
    public List<string>? Errors { get; init; }
}
