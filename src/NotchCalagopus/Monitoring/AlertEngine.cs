using NotchCalagopus.Api;

namespace NotchCalagopus.Monitoring;

internal enum ServerEventKind
{
    Started,
    Stopped,
    BackOnline,
}

/// <summary>A state change worth a brief notice.</summary>
internal sealed record ServerEvent(ServerEventKind Kind, string ServerName);

internal enum Metric
{
    Memory,
    Disk,
    Cpu,
}

/// <summary>A server that has stayed over one of its resource thresholds.</summary>
internal sealed record Breach(string Uuid, string ServerName, Metric Metric, double Percent);

/// <summary>
/// Turns successive observations of each server into alerts. It does no I/O and keeps no clock,
/// so the same observations always give the same alerts. Not thread-safe; the caller serializes.
/// </summary>
internal sealed class AlertEngine(PluginOptions options)
{
    // A value has to fall this far under its threshold before the alert clears, so one that
    // hovers at the limit does not come and go.
    private const double ClearMargin = 5;

    private static readonly Metric[] Metrics = [Metric.Memory, Metric.Disk, Metric.Cpu];

    private readonly Dictionary<string, Track> _tracks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names of the servers that went offline without being stopped, and have not been dismissed.</summary>
    public IReadOnlyList<string> OfflineAlerts =>
        [.. _tracks.Values.Where(t => t.OfflineAlert).Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Threshold alerts that have not been dismissed.</summary>
    public IReadOnlyList<Breach> Breaches =>
    [
        .. from pair in _tracks
           from metric in Metrics
           let gauge = pair.Value.Gauges[(int)metric]
           where gauge.Active && !gauge.Dismissed
           orderby pair.Value.Name, metric
           select new Breach(pair.Key, pair.Value.Name, metric, gauge.Percent),
    ];

    public bool IsOfflineAlert(string uuid) => _tracks.TryGetValue(uuid, out Track? track) && track.OfflineAlert;

    /// <summary>The first undismissed threshold alert of a server, or null.</summary>
    public Breach? BreachOf(string uuid) => Breaches.FirstOrDefault(b => string.Equals(b.Uuid, uuid, StringComparison.OrdinalIgnoreCase));

    /// <summary>True while a value is over its threshold, dismissed or not.</summary>
    public bool IsOverThreshold(string uuid) =>
        _tracks.TryGetValue(uuid, out Track? track) && track.Gauges.Any(g => g.Active);

    /// <summary>True when the server has an alert the user can dismiss.</summary>
    public bool HasAlert(string uuid) => IsOfflineAlert(uuid) || BreachOf(uuid) is not null;

    /// <summary>Dismisses a server's alerts. A threshold alert stays quiet until the value has recovered and risen again.</summary>
    public void Acknowledge(string uuid)
    {
        if (!_tracks.TryGetValue(uuid, out Track? track))
        {
            return;
        }

        track.OfflineAlert = false;
        foreach (Gauge gauge in track.Gauges)
        {
            gauge.Dismissed = gauge.Active;
        }
    }

    /// <summary>Forgets servers that are no longer listed.</summary>
    public void Retain(IEnumerable<string> uuids)
    {
        var keep = new HashSet<string>(uuids, StringComparer.OrdinalIgnoreCase);
        foreach (string uuid in _tracks.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _tracks.Remove(uuid);
        }
    }

    /// <summary>
    /// Records a server's current state and returns the changes worth a brief notice.
    /// </summary>
    /// <param name="countThresholds">
    /// True for a regular poll. Live updates arrive every second and must not count towards
    /// "over the limit for several polls in a row".
    /// </param>
    public IReadOnlyList<ServerEvent> Observe(ServerView server, bool countThresholds)
    {
        if (!_tracks.TryGetValue(server.Uuid, out Track? track))
        {
            _tracks[server.Uuid] = track = new Track();
        }

        track.Name = server.Name;
        List<ServerEvent> events = [];
        PowerState? now = server.Power;

        if (now is null)
        {
            // Suspended, installing or no data: nothing to compare, and whatever comes next is a new baseline.
            track.Last = null;
            track.SawStopping = false;
            track.OfflineAlert = false;
        }
        else if (track.Last is { } last && last != now)
        {
            switch (now)
            {
                case PowerState.Offline:
                    // Someone stopping a server takes it through "stopping"; a crash does not.
                    if (track.SawStopping || !options.AlertOffline)
                    {
                        events.Add(new ServerEvent(ServerEventKind.Stopped, server.Name));
                    }
                    else
                    {
                        track.OfflineAlert = true;
                    }

                    track.SawStopping = false;
                    break;

                case PowerState.Starting:
                    track.SawStopping = false;
                    break;

                case PowerState.Running:
                    events.Add(new ServerEvent(
                        track.OfflineAlert ? ServerEventKind.BackOnline : ServerEventKind.Started, server.Name));
                    track.OfflineAlert = false;
                    track.SawStopping = false;
                    break;
            }
        }

        if (now is not null)
        {
            track.Last = now;
            track.SawStopping |= now == PowerState.Stopping;
        }

        if (countThresholds)
        {
            bool running = now == PowerState.Running;
            Measure(track.Gauges[(int)Metric.Memory], running ? server.MemoryPercent : null, options.MemoryPercent);
            Measure(track.Gauges[(int)Metric.Disk], server.DiskPercent, options.DiskPercent);
            Measure(track.Gauges[(int)Metric.Cpu], running ? server.CpuPercent : null, options.CpuPercent);
        }

        return options.AlertStateChanges ? events : [];
    }

    private void Measure(Gauge gauge, double? percent, int threshold)
    {
        if (!options.AlertThresholds || threshold <= 0 || percent is not { } value || value < threshold - ClearMargin)
        {
            gauge.Count = 0;
            gauge.Active = false;
            gauge.Dismissed = false;
            return;
        }

        gauge.Percent = value;
        if (value >= threshold)
        {
            gauge.Count = Math.Min(gauge.Count + 1, options.ThresholdPolls);
            gauge.Active |= gauge.Count >= options.ThresholdPolls;
        }
        else if (!gauge.Active)
        {
            // Just under the threshold and not yet reported: the run of polls over it is broken.
            gauge.Count = 0;
        }
    }

    private sealed class Track
    {
        public string Name { get; set; } = "";

        public PowerState? Last { get; set; }

        public bool SawStopping { get; set; }

        public bool OfflineAlert { get; set; }

        public Gauge[] Gauges { get; } = [new(), new(), new()];
    }

    private sealed class Gauge
    {
        public int Count { get; set; }

        public bool Active { get; set; }

        public bool Dismissed { get; set; }

        public double Percent { get; set; }
    }
}
