using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Notch.Core.Plugins;
using NotchCalagopus.Api;

namespace NotchCalagopus.Monitoring;

/// <summary>
/// A live connection to one server on its node, which pushes usage about once a second and
/// state changes as they happen. It reconnects by itself until disposed.
/// </summary>
/// <remarks>
/// The callbacks run on the connection's own task. Frames are JSON:
/// <c>{"event": "stats", "args": ["..."]}</c>.
/// </remarks>
internal sealed class LiveSocket : IDisposable
{
    private const int MaxMessageBytes = 1024 * 1024;

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile ClientWebSocket? _current;
    private readonly Func<CancellationToken, Task<WebsocketCredentials>> _credentials;
    private readonly Action<ResourceUsage> _stats;
    private readonly Action<string> _status;
    private readonly Action<bool> _connected;
    private readonly Action<string>? _console;
    private readonly IPluginLog _log;
    private readonly TimeSpan _minBackoff;

    /// <param name="credentials">Asks the panel for a fresh token and the node's address.</param>
    /// <param name="stats">Called with each usage update.</param>
    /// <param name="status">Called with each power state: offline, starting, stopping or running.</param>
    /// <param name="connected">Called with true once the node accepts the token, and false when the connection ends.</param>
    /// <param name="console">Called with each line of the server's console, starting with its recent history.</param>
    public LiveSocket(
        Func<CancellationToken, Task<WebsocketCredentials>> credentials,
        Action<ResourceUsage> stats,
        Action<string> status,
        Action<bool> connected,
        IPluginLog log,
        TimeSpan? minBackoff = null,
        Action<string>? console = null)
    {
        _console = console;
        _credentials = credentials;
        _stats = stats;
        _status = status;
        _connected = connected;
        _log = log;
        _minBackoff = minBackoff ?? TimeSpan.FromSeconds(2);
    }

    public void Start() => _ = Task.Run(RunAsync);

    public void Dispose() => _stop.Cancel();

    /// <summary>Sends a line to the server's console. False when there is no live connection to send it on.</summary>
    public async Task<bool> SendCommandAsync(string command)
    {
        ClientWebSocket? socket = _current;
        if (socket is not { State: WebSocketState.Open })
        {
            return false;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await SendAsync(socket, "send command", command, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunAsync()
    {
        // Nothing may escape: this task is not awaited by anyone.
        try
        {
            CancellationToken cancellation = _stop.Token;
            TimeSpan backoff = _minBackoff;
            bool warned = false;

            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await SessionAsync(
                        () =>
                        {
                            backoff = _minBackoff;
                            warned = false;
                        },
                        cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    // Once per outage, not once per attempt.
                    if (!warned)
                    {
                        warned = true;
                        _log.Warn($"The live connection is down, retrying: {e.Message}");
                    }
                }

                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                _connected(false);
                await Task.Delay(backoff, cancellation).ConfigureAwait(false);
                backoff = backoff * 2 < MaxBackoff ? backoff * 2 : MaxBackoff;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.Error("The live connection stopped.", e);
        }
    }

    /// <summary>One connection, from fetching a token until the socket closes or fails.</summary>
    private async Task SessionAsync(Action authenticated, CancellationToken cancellation)
    {
        WebsocketCredentials credentials = await _credentials(cancellation).ConfigureAwait(false);

        using var socket = new ClientWebSocket();
        _current = socket;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(SocketAddress(credentials.Url), cancellation).ConfigureAwait(false);
        await SendAsync(socket, "auth", credentials.Token, cancellation).ConfigureAwait(false);

        byte[] buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellation).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException($"The node closed the connection ({socket.CloseStatusDescription ?? socket.CloseStatus?.ToString() ?? "no reason"}).");
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                {
                    throw new InvalidDataException("The node sent an oversized message.");
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text
                || !TryParse(message.GetBuffer().AsSpan(0, (int)message.Length), out string? name, out JsonElement argument))
            {
                continue;
            }

            switch (name)
            {
                case "auth success":
                    authenticated();
                    _connected(true);

                    // Usage is otherwise only pushed when it changes, which an idle server's does not.
                    await SendAsync(socket, "send stats", null, cancellation).ConfigureAwait(false);
                    if (_console is not null)
                    {
                        await SendAsync(socket, "send logs", null, cancellation).ConfigureAwait(false);
                    }
                    break;

                case "stats":
                    if (ReadUsage(argument) is { } usage)
                    {
                        _stats(usage);
                    }

                    break;

                case "console output":
                case "install output":
                case "daemon message":
                    if (_console is not null && argument.ValueKind == JsonValueKind.String)
                    {
                        _console(argument.GetString()!);
                    }

                    break;

                case "status":
                    if (argument.ValueKind == JsonValueKind.String)
                    {
                        _status(argument.GetString()!);
                    }

                    break;

                case "token expiring":
                    credentials = await _credentials(cancellation).ConfigureAwait(false);
                    await SendAsync(socket, "auth", credentials.Token, cancellation).ConfigureAwait(false);
                    break;

                case "token expired":
                case "jwt error":
                    throw new WebSocketException($"The node refused the session ({name}).");
            }
        }
    }

    private async Task SendAsync(ClientWebSocket socket, string name, string? argument, CancellationToken cancellation)
    {
        string[] args = argument is null ? [] : [argument];
        byte[] frame = JsonSerializer.SerializeToUtf8Bytes(new { @event = name, args });
        // One send at a time: commands from the user race with the session's own frames.
        await _sendLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, cancellation).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Reads a frame's event name and first argument. False for anything that is not a frame.</summary>
    private static bool TryParse(ReadOnlySpan<byte> utf8, out string? name, out JsonElement argument)
    {
        name = null;
        argument = default;
        try
        {
            using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(utf8));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("event", out JsonElement eventName)
                || eventName.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            name = eventName.GetString();
            if (root.TryGetProperty("args", out JsonElement args) && args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0)
            {
                argument = args[0].Clone();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Usage arrives as JSON inside a string argument; an object is accepted too.</summary>
    private static ResourceUsage? ReadUsage(JsonElement argument)
    {
        try
        {
            return argument.ValueKind switch
            {
                JsonValueKind.String => JsonSerializer.Deserialize<ResourceUsage>(argument.GetString()!, CalagopusJson.Options),
                JsonValueKind.Object => argument.Deserialize<ResourceUsage>(CalagopusJson.Options),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The panel gives the node's address as ws(s) or http(s); the socket needs ws(s).</summary>
    private static Uri SocketAddress(string url)
    {
        var builder = new UriBuilder(url);
        builder.Scheme = builder.Scheme switch
        {
            "https" or "wss" => "wss",
            "http" or "ws" => "ws",
            _ => throw new InvalidDataException($"The panel gave an address that is not a websocket: {builder.Scheme}."),
        };
        return builder.Uri;
    }
}
