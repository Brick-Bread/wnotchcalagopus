using Notch.Core.Activities;
using Notch.Core.Plugins;
using NotchCalagopus.Api;
using NotchCalagopus.Monitoring;

namespace NotchCalagopus.Ui;

internal enum PanelStatus
{
    /// <summary>No answer from the panel yet.</summary>
    Connecting,
    Ok,

    /// <summary>Unreachable, three polls in a row or more.</summary>
    Unreachable,

    /// <summary>The panel refuses the API key.</summary>
    Rejected,
}

/// <summary>Everything the cards and the pill are drawn from.</summary>
/// <param name="Servers">In display order.</param>
/// <param name="Loaded">False until the first successful poll.</param>
/// <param name="SelectedUuid">The server with the live connection, or null.</param>
internal sealed record ViewState(
    IReadOnlyList<ServerView> Servers,
    bool Loaded,
    PanelStatus Panel,
    string? PanelMessage,
    string? SelectedUuid,
    bool LiveConnected);

/// <summary>
/// Shows the monitor's state as cards and pill activities, and only tells Notch about what has
/// changed. Not thread-safe; the caller serializes.
/// </summary>
internal sealed class Presenter(
    IPluginHost host,
    PluginOptions options,
    AlertEngine alerts,
    Action summaryClicked,
    Action<string> commandSubmitted,
    Action<string> serverPicked,
    Action backClicked)
{
    private const string PageId = "server";

    private const string SummaryId = "summary";
    private const string OfflineId = "offline";
    private const string ThresholdId = "threshold";
    private const string PanelId = "panel";
    private const string EventId = "event";
    private const string LiveId = "live";

    // Segoe Fluent Icons.
    private const string ErrorGlyph = "";
    private const string WarningGlyph = "";
    private const string GlobeGlyph = "";
    private const string PlayGlyph = "";
    private const string StopGlyph = "";
    private const string LiveGlyph = "";

    private readonly Dictionary<string, CardFace> _faces = [];
    private readonly Dictionary<string, string> _activities = [];
    private string[] _cardIds = [];
    private string? _pageShown;

    public void Render(ViewState view, IReadOnlyList<ServerEvent> events)
    {
        // Only the overview lives on the Plugins tab; the servers themselves are on the plugin's own tab.
        List<PluginCard> cards = [Summary(view)];
        Apply(cards);

        ShowPage(view);

        ShowOffline();
        ShowThresholds();
        ShowPanel(view);
        ShowLive(view);
        ShowEvents(events);
    }

    /// <summary>Asks Notch to open the selected server's page.</summary>
    public void OpenPage() => host.Pages.Open(PageId);

    /// <summary>
    /// The plugin's tab. With no server picked it lists them all; with one picked it shows every
    /// figure the panel reports on top and the console below, and a button to go back to the list.
    /// The console's lines are appended by the caller as they arrive.
    /// </summary>
    private void ShowPage(ViewState view)
    {
        if (view.Servers.FirstOrDefault(s => s.Uuid == view.SelectedUuid) is not { } server)
        {
            ShowList(view);
            return;
        }

        ResourceUsage? usage = server.Usage;
        bool active = server.State is DisplayState.Running or DisplayState.Starting or DisplayState.Stopping;
        bool hasUsage = usage is not null && active;
        string none = view.LiveConnected ? "–" : "…";

        PluginStat Stat(string label, string? value, string? detail, double? progress, Metric? metric) => new()
        {
            Label = label,
            Value = value,
            Detail = detail,
            Progress = progress,
            Color = metric is { } m && alerts.Breaches.Any(b => b.Uuid == server.Uuid && b.Metric == m) ? GlowColor.Amber : null,
        };

        PluginStat[] stats =
        [
            new PluginStat { Label = "State", Value = StateText(server.State), Detail = $"Node {server.Info.NodeName}", Color = StateColor(server) },
            Stat(
                "CPU",
                hasUsage ? Format.Percent(usage!.CpuAbsolute) : none,
                server.Info.Limits is { Cpu: > 0 } cpu ? $"of {Format.Percent(cpu.Cpu)}" : "No limit",
                hasUsage && server.CpuPercent is { } cpuPercent ? Math.Clamp(cpuPercent / 100, 0, 1) : null,
                Metric.Cpu),
            Stat(
                "Memory",
                hasUsage ? Format.Bytes(usage!.MemoryBytes) : none,
                server.Info.Limits is { Memory: > 0 } ? $"of {Format.Bytes(server.MemoryLimitBytes)}" : "No limit",
                hasUsage ? server.MemoryFraction : null,
                Metric.Memory),
            Stat(
                "Disk",
                usage is not null ? Format.Bytes(usage.DiskBytes) : none,
                server.Info.Limits is { Disk: > 0 } disk ? $"of {Format.Bytes(disk.Disk * 1024 * 1024)}" : "No limit",
                usage is not null && server.DiskPercent is { } diskPercent ? Math.Clamp(diskPercent / 100, 0, 1) : null,
                Metric.Disk),
            Stat("Download", hasUsage && usage!.Network is { } rx ? Format.Bytes(rx.RxBytes) : none, "Received", null, null),
            Stat("Upload", hasUsage && usage!.Network is { } tx ? Format.Bytes(tx.TxBytes) : none, "Sent", null, null),
            Stat("Uptime", hasUsage ? Format.Uptime(usage!.Uptime) : none, null, null, null),
        ];

        var page = new PluginPage
        {
            Id = PageId,
            Title = server.Name.Length > 18 ? server.Name[..17] + "…" : server.Name,
            Stats = stats,
            Input = commandSubmitted,
            InputHint = view.LiveConnected ? $"Send a command to {server.Name}" : "Connecting to the console…",
            Back = backClicked,
            BackLabel = "Servers",
        };

        // Only what is drawn counts, so an unchanged second of live data costs nothing.
        string signature = $"{page.Title}\n{page.InputHint}\n{string.Join("\n", stats.Select(s => $"{s.Label}|{s.Value}|{s.Detail}|{s.Progress}|{s.Color}"))}";
        SetPage(page, signature);
    }

    /// <summary>The server list: one row per server, click one to open it.</summary>
    private void ShowList(ViewState view)
    {
        if (view.Servers.Count == 0)
        {
            if (_pageShown is not null)
            {
                _pageShown = null;
                host.Pages.Remove(PageId);
            }

            return;
        }

        PluginChoice[] choices =
        [
            .. view.Servers.Select(server => new PluginChoice
            {
                Label = server.Name,
                Value = StateText(server.State),
                Detail = RowDetail(server),
                Color = StateColor(server),
                Clicked = () => serverPicked(server.Uuid),
            }),
        ];

        var page = new PluginPage { Id = PageId, Title = "Servers", Choices = choices };
        SetPage(page, string.Join("\n", choices.Select(c => $"{c.Label}|{c.Value}|{c.Detail}|{c.Color}")));
    }

    /// <summary>The line under a server in the list: an alert if there is one, else what it is using.</summary>
    private string RowDetail(ServerView server)
    {
        if (alerts.IsOfflineAlert(server.Uuid))
        {
            return "Went offline unexpectedly";
        }

        if (alerts.BreachOf(server.Uuid) is { } breach)
        {
            return Describe(breach);
        }

        return server.State is DisplayState.Running or DisplayState.Starting or DisplayState.Stopping && server.Usage is { } usage
            ? $"CPU {Format.Percent(usage.CpuAbsolute)} · {Format.Usage(usage.MemoryBytes, server.MemoryLimitBytes)} · Node {server.Info.NodeName}"
            : $"Node {server.Info.NodeName}";
    }

    private void SetPage(PluginPage page, string signature)
    {
        if (signature != _pageShown)
        {
            _pageShown = signature;
            host.Pages.Set(page);
        }
    }

    private PluginCard Summary(ViewState view)
    {
        int total = view.Servers.Count;
        int online = view.Servers.Count(s => s.State == DisplayState.Running);
        IReadOnlyList<string> offline = alerts.OfflineAlerts;
        IReadOnlyList<Breach> breaches = alerts.Breaches;

        string detail = view.Panel switch
        {
            PanelStatus.Rejected => $"Key rejected: {view.PanelMessage}",
            PanelStatus.Unreachable => view.Loaded ? "Panel unreachable, showing the last data" : "Panel unreachable",
            PanelStatus.Connecting => "Connecting to the panel",
            _ when total == 0 => "No servers to show",
            _ when offline.Count > 0 => $"Offline: {string.Join(", ", offline)}",
            _ when breaches.Count == 1 => $"{breaches[0].ServerName}: {Describe(breaches[0])}",
            _ when breaches.Count > 1 => $"{breaches.Count} resource warnings",
            _ when online == total => "All running",
            _ => $"{total - online} not running",
        };

        return new PluginCard
        {
            Id = SummaryId,
            Label = "Calagopus",
            Value = view.Loaded ? $"{online}/{total} online" : "…",
            Detail = detail,
            Color = view.Panel switch
            {
                PanelStatus.Rejected => GlowColor.Red,
                PanelStatus.Unreachable => GlowColor.Amber,
                PanelStatus.Connecting => null,
                _ when offline.Count > 0 => GlowColor.Red,
                _ when breaches.Count > 0 => GlowColor.Amber,
                _ when total > 0 && online == total => GlowColor.Green,
                _ => null,
            },
            Clicked = summaryClicked,
        };
    }

    private static string StateText(DisplayState state) => state switch
    {
        DisplayState.Running => "Running",
        DisplayState.Starting => "Starting",
        DisplayState.Stopping => "Stopping",
        DisplayState.Offline => "Offline",
        DisplayState.Installing => "Installing",
        DisplayState.InstallFailed => "Install failed",
        DisplayState.Restoring => "Restoring",
        DisplayState.RestoreFailed => "Restore failed",
        DisplayState.Suspended => "Suspended",
        _ => "No data",
    };

    private GlowColor? StateColor(ServerView server) => server.State switch
    {
        _ when alerts.IsOfflineAlert(server.Uuid) => GlowColor.Red,
        DisplayState.InstallFailed or DisplayState.RestoreFailed => GlowColor.Red,
        _ when alerts.IsOverThreshold(server.Uuid) => GlowColor.Amber,
        DisplayState.Running => GlowColor.Green,
        DisplayState.Starting or DisplayState.Stopping or DisplayState.Installing or DisplayState.Restoring => GlowColor.Amber,
        _ => null,
    };

    private void Apply(List<PluginCard> cards)
    {
        // Cards stay in the order they were first added, so a different set has to be rebuilt.
        string[] ids = [.. cards.Select(c => c.Id)];
        if (!ids.SequenceEqual(_cardIds))
        {
            host.Cards.Clear();
            _faces.Clear();
            _cardIds = ids;
        }

        foreach (PluginCard card in cards)
        {
            var face = new CardFace(card.Label, card.Value, card.Detail, card.Progress, card.Color);
            if (!_faces.TryGetValue(card.Id, out CardFace? shown) || shown != face)
            {
                _faces[card.Id] = face;
                host.Cards.Set(card);
            }
        }
    }

    private void ShowOffline()
    {
        IReadOnlyList<string> offline = alerts.OfflineAlerts;
        if (offline.Count == 0)
        {
            Retract(OfflineId);
            return;
        }

        Publish(new Activity
        {
            Id = OfflineId,
            Tier = ActivityTier.Attention,
            Title = offline.Count == 1 ? offline[0] : $"{offline.Count} servers",
            Detail = "Offline",
            Glyph = ErrorGlyph,
            Glow = new Glow(GlowColor.Red, GlowPattern.Pulse),
        });
    }

    private void ShowThresholds()
    {
        IReadOnlyList<Breach> breaches = alerts.Breaches;
        if (breaches.Count == 0)
        {
            Retract(ThresholdId);
            return;
        }

        Publish(new Activity
        {
            Id = ThresholdId,
            Tier = ActivityTier.Attention,
            Title = breaches.Count == 1 ? breaches[0].ServerName : $"{breaches.Count} warnings",
            Detail = breaches.Count == 1 ? Describe(breaches[0]) : "Resources",
            Glyph = WarningGlyph,
            Glow = new Glow(GlowColor.Amber, GlowPattern.Pulse),
        });
    }

    private void ShowPanel(ViewState view)
    {
        if (!options.AlertPanelUnreachable || view.Panel is not (PanelStatus.Rejected or PanelStatus.Unreachable))
        {
            Retract(PanelId);
            return;
        }

        bool rejected = view.Panel == PanelStatus.Rejected;
        Publish(new Activity
        {
            Id = PanelId,
            Tier = ActivityTier.Attention,
            Title = "Calagopus",
            Detail = rejected ? "Key rejected" : "Unreachable",
            Glyph = GlobeGlyph,
            Glow = new Glow(rejected ? GlowColor.Red : GlowColor.Amber, GlowPattern.Pulse),
        });
    }

    private void ShowLive(ViewState view)
    {
        ServerView? server = options.ShowSelectedInPill ? view.Servers.FirstOrDefault(s => s.Uuid == view.SelectedUuid) : null;
        if (server is null)
        {
            Retract(LiveId);
            return;
        }

        bool running = server.State == DisplayState.Running;
        Publish(new Activity
        {
            Id = LiveId,
            Tier = ActivityTier.Ongoing,
            Title = server.Name,
            Detail = running && server.Usage is { } usage
                ? $"{Format.Percent(usage.CpuAbsolute)} · {Format.Bytes(usage.MemoryBytes)}"
                : server.State.ToString(),
            Glyph = LiveGlyph,
            Glow = server.State switch
            {
                DisplayState.Running => new Glow(GlowColor.Green, GlowPattern.Breathe, 0.6),
                DisplayState.Starting or DisplayState.Stopping => new Glow(GlowColor.Amber, GlowPattern.Breathe, 0.6),
                _ => null,
            },
        });
    }

    private void ShowEvents(IReadOnlyList<ServerEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        ServerEvent first = events[0];
        bool up = events.All(e => e.Kind != ServerEventKind.Stopped);

        // Removed first so the notice counts as new and gets its full time, even right after another.
        host.Activities.Remove(EventId);
        host.Activities.Publish(new Activity
        {
            Id = EventId,
            Tier = ActivityTier.Transient,
            Title = events.Count == 1 ? first.ServerName : $"{events.Count} servers",
            Detail = events.Count > 1 ? "Changed state" : first.Kind switch
            {
                ServerEventKind.Started => "Started",
                ServerEventKind.BackOnline => "Back online",
                _ => "Stopped",
            },
            Glyph = up ? PlayGlyph : StopGlyph,
            Glow = new Glow(up ? GlowColor.Green : GlowColor.White, GlowPattern.Flash),
            Lifetime = TimeSpan.FromSeconds(4),
        });
    }

    private void Publish(Activity activity)
    {
        string signature = $"{activity.Title}\n{activity.Detail}\n{activity.Glow}";
        if (!_activities.TryGetValue(activity.Id, out string? shown) || shown != signature)
        {
            _activities[activity.Id] = signature;
            host.Activities.Publish(activity);
        }
    }

    private void Retract(string id)
    {
        if (_activities.Remove(id))
        {
            host.Activities.Remove(id);
        }
    }

    private static string Describe(Breach breach) => breach.Metric switch
    {
        Metric.Memory => $"Memory {Format.Percent(breach.Percent)}",
        Metric.Disk => $"Disk {Format.Percent(breach.Percent)}",
        _ => $"CPU {Format.Percent(breach.Percent)}",
    };

    /// <summary>What a card shows, without its click handler, to tell whether it changed.</summary>
    private sealed record CardFace(string Label, string? Value, string? Detail, double? Progress, GlowColor? Color);
}
