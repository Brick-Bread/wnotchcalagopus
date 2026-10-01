using System.Diagnostics;
using Notch.Core.Activities;
using Notch.Core.Plugins;
using NotchCalagopus.Api;
using NotchCalagopus.Monitoring;
using NotchCalagopus.Ui;

namespace NotchCalagopus;

/// <summary>
/// The plugin's entry point. Polls the panel for the servers and their usage, keeps one live
/// connection for the server the user picked, and hands what it learns to the presenter.
/// </summary>
public sealed partial class CalagopusPlugin : INotchPlugin
{
    /// <summary>Polls that fail in a row before the panel counts as unreachable.</summary>
    private const int FailuresBeforeUnreachable = 3;

    private const string PageId = "server";

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();

    private IPluginHost? _host;
    private PluginOptions _options = new();
    private CalagopusClient? _client;
    private AlertEngine _alerts = new(new PluginOptions());
    private Presenter? _presenter;
    private Task? _loop;
    private bool _stopped;

    private List<ServerView> _servers = [];
    private bool _loaded;
    private PanelStatus _panel = PanelStatus.Connecting;
    private string? _panelMessage;
    private int _failures;

    private string? _selectedUuid;
    private LiveSocket? _live;
    private ResourceUsage? _liveUsage;
    private bool _liveConnected;

    public void Start(IPluginHost host)
    {
        _host = host;
        _options = PluginOptions.Load(host.Settings, host.Log);
        _alerts = new AlertEngine(_options);
        _presenter = new Presenter(host, _options, _alerts, ServerClicked, SummaryClicked, CommandSubmitted);

        if (_options.Problem is not null)
        {
            Render([]);
            return;
        }

        _client = new CalagopusClient(_options.PanelUrl!, _options.ApiKey!);
        _loop = Task.Run(() => PollLoopAsync(_stop.Token));
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _live?.Dispose();
            _live = null;
        }

        _stop.Cancel();
        _client?.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken cancellation)
    {
        // Nothing may escape: this task is not awaited by anyone.
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollSeconds));
            do
            {
                await PollAsync(cancellation).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _host!.Log.Error("The polling stopped.", e);
        }
    }

    private async Task PollAsync(CancellationToken cancellation)
    {
        List<ServerView>? servers = null;
        PanelStatus failure = PanelStatus.Ok;
        string? message = null;

        try
        {
            servers = await FetchAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (CalagopusApiException e) when (e.IsRejected)
        {
            failure = PanelStatus.Rejected;
            message = e.Message;
        }
        catch (Exception e) when (e is CalagopusApiException or HttpRequestException or TaskCanceledException)
        {
            failure = PanelStatus.Unreachable;
            message = e.Message;
        }

        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            if (servers is not null)
            {
                _failures = 0;
                _panel = PanelStatus.Ok;
                _panelMessage = null;
                _loaded = true;
                _servers = servers;
                _alerts.Retain(servers.Select(s => s.Uuid));

                List<ServerEvent> events = [];
                foreach (ServerView server in servers)
                {
                    events.AddRange(_alerts.Observe(Effective(server), countThresholds: true));
                }

                ResolveSelection();
                Render(events);
                return;
            }

            _failures++;
            if (failure == PanelStatus.Rejected)
            {
                _panel = PanelStatus.Rejected;
            }
            else if (_failures >= FailuresBeforeUnreachable)
            {
                _panel = PanelStatus.Unreachable;
            }

            _panelMessage = message;
            if (_failures == 1)
            {
                _host!.Log.Warn($"Could not poll the panel: {message}");
            }

            Render([]);
        }
    }

    /// <summary>The listed servers with their usage. A node that does not answer leaves its servers without data.</summary>
    private async Task<List<ServerView>> FetchAsync(CancellationToken cancellation)
    {
        CalagopusClient client = _client!;
        List<ServerInfo> infos = await client.ListServersAsync(cancellation).ConfigureAwait(false);
        infos = [.. infos.Where(Included)];

        string[] nodes = [.. infos.Select(i => i.NodeUuid).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        Dictionary<string, Dictionary<string, ResourceUsage>?> usage = [];
        await Task.WhenAll(nodes.Select(async node =>
        {
            Dictionary<string, ResourceUsage>? resources = null;
            try
            {
                resources = await client.GetNodeResourcesAsync(node, cancellation).ConfigureAwait(false);
            }
            catch (CalagopusApiException e) when (!e.IsRejected)
            {
                // The node is not answering; its servers show as "No data".
            }

            lock (usage)
            {
                usage[node] = resources;
            }
        })).ConfigureAwait(false);

        return
        [
            .. infos.Select(info =>
            {
                ResourceUsage? resource = null;
                if (usage.TryGetValue(info.NodeUuid, out var node))
                {
                    node?.TryGetValue(info.Uuid, out resource);
                }

                return new ServerView(info, resource);
            }),
        ];
    }

    private bool Included(ServerInfo server)
    {
        bool Matches(IReadOnlyList<string> names) => names.Any(n =>
            string.Equals(n, server.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, server.Uuid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, server.UuidShort, StringComparison.OrdinalIgnoreCase));

        return (_options.IncludeServers.Count == 0 || Matches(_options.IncludeServers)) && !Matches(_options.ExcludeServers);
    }

    /// <summary>The server as it is now: for the live one, the latest pushed usage rather than the last poll's.</summary>
    private ServerView Effective(ServerView server) =>
        server.Uuid == _selectedUuid && _liveUsage is not null ? server with { Usage = _liveUsage } : server;

    /// <summary>Starts the live connection for the configured server once it shows up in the list.</summary>
    private void ResolveSelection()
    {
        if (_selectedUuid is not null && _servers.All(s => s.Uuid != _selectedUuid))
        {
            Deselect();
        }

        if (_selectedUuid is null && _options.SelectedServer.Length > 0)
        {
            ServerView? match = _servers.FirstOrDefault(s =>
                string.Equals(s.Info.UuidShort, _options.SelectedServer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.Uuid, _options.SelectedServer, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                Select(match.Uuid);
            }
        }
    }

    private void Select(string uuid)
    {
        Deselect();
        _selectedUuid = uuid;
        CalagopusClient client = _client!;
        _live = new LiveSocket(
            cancellation => client.GetWebsocketAsync(uuid, cancellation),
            LiveStats,
            LiveStatus,
            LiveConnected,
            _host!.Log,
            console: ConsoleLine);
        _live.Start();
    }

    private void Deselect()
    {
        _live?.Dispose();
        _live = null;
        _selectedUuid = null;
        _liveUsage = null;
        _liveConnected = false;
    }

    /// <summary>Adds a console line to the server's page. The node sends the recent history after every (re)connect, which clears the console first.</summary>
    private void ConsoleLine(string text)
    {
        try
        {
            lock (_gate)
            {
                if (_stopped || _selectedUuid is null)
                {
                    return;
                }

                string line = AnsiCodes().Replace(text, "").TrimEnd();
                if (line.Length > 0)
                {
                    _host!.Pages.Append(PageId, line);
                }
            }
        }
        catch (Exception e)
        {
            _host!.Log.Error("Could not show a console line.", e);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B[@-_]")]
    private static partial System.Text.RegularExpressions.Regex AnsiCodes();

    private void LiveStats(ResourceUsage usage) => OnLive(() => _liveUsage = usage);

    private void LiveStatus(string state) =>
        OnLive(() => _liveUsage = (_liveUsage ?? new ResourceUsage()) with { State = state });

    private void LiveConnected(bool connected) => OnLive(() =>
    {
        _liveConnected = connected;
        if (connected)
        {
            _host!.Pages.ClearConsole(PageId);
        }
    });

    /// <summary>The user typed a line in the console. Runs on a background thread, so it may wait for the send.</summary>
    private void CommandSubmitted(string command)
    {
        LiveSocket? live;
        lock (_gate)
        {
            live = _stopped ? null : _live;
        }

        bool sent = live is not null && live.SendCommandAsync(command).GetAwaiter().GetResult();
        _host!.Pages.Append(PageId, sent ? "> " + command : "! Not sent: the console is not connected.");
    }

    /// <summary>Applies a push from the live connection and redraws. Runs on the connection's task.</summary>
    private void OnLive(Action apply)
    {
        try
        {
            lock (_gate)
            {
                if (_stopped || _selectedUuid is null)
                {
                    return;
                }

                apply();
                ServerView? server = _servers.FirstOrDefault(s => s.Uuid == _selectedUuid);
                IReadOnlyList<ServerEvent> events = server is null ? [] : _alerts.Observe(Effective(server), countThresholds: false);
                Render(events);
            }
        }
        catch (Exception e)
        {
            _host!.Log.Error("Could not apply a live update.", e);
        }
    }

    private void ServerClicked(string uuid)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            if (_alerts.HasAlert(uuid))
            {
                _alerts.Acknowledge(uuid);
            }
            else if (_selectedUuid != uuid && _servers.FirstOrDefault(s => s.Uuid == uuid) is { } server)
            {
                Select(uuid);
                _host!.Settings.Set(PluginOptions.SelectedServerKey, server.Info.UuidShort);
                _options = _options with { SelectedServer = server.Info.UuidShort };
            }

            Render([]);

            // The page exists once rendered. A click on the live server's card brings its tab back.
            if (_selectedUuid == uuid && !_alerts.HasAlert(uuid))
            {
                _presenter!.OpenPage();
            }
        }
    }

    private void SummaryClicked()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            if (_options.Problem is not null || _panel == PanelStatus.Rejected)
            {
                OpenSettings();
                return;
            }

            foreach (ServerView server in _servers)
            {
                _alerts.Acknowledge(server.Uuid);
            }

            Render([]);
        }
    }

    private void OpenSettings()
    {
        try
        {
            string path = Path.Combine(_host!.DataDirectory, "settings.json");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e)
        {
            _host!.Log.Warn($"Could not open settings.json: {e.Message}");
        }
    }

    /// <summary>Must be called with the gate held, except from <see cref="Start"/>.</summary>
    private void Render(IReadOnlyList<ServerEvent> events)
    {
        if (_options.Problem is not null)
        {
            _host!.Cards.Set(new PluginCard
            {
                Id = "summary",
                Label = "Calagopus",
                Value = "Not set up",
                Detail = _options.Problem,
                Color = GlowColor.Amber,
                Clicked = SummaryClicked,
            });
            return;
        }

        _presenter!.Render(
            new ViewState(
                [.. _servers.Select(Effective)],
                _loaded,
                _panel,
                _panelMessage,
                _selectedUuid,
                _liveConnected),
            events);
    }
}
