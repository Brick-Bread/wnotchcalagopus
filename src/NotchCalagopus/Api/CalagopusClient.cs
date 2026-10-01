using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NotchCalagopus.Api;

/// <summary>The panel answered, but not with what was asked for. The message is fit to show the user.</summary>
internal sealed class CalagopusApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>The key is missing, wrong, disabled, expired or lacks a permission.</summary>
    public bool IsRejected => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

/// <summary>Read-only access to a Calagopus panel's client API with a user API key.</summary>
internal sealed class CalagopusClient : IDisposable
{
    private const int PageSize = 100;
    private const int MaxPages = 50;

    private readonly HttpClient _http;

    public CalagopusClient(Uri panelUrl, string apiKey, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);

        // Relative request paths only resolve under a base address that ends in a slash.
        _http.BaseAddress = new Uri(panelUrl.AbsoluteUri.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NotchCalagopus", "1.0"));
    }

    /// <summary>Every server the key's user can see, across all pages.</summary>
    public async Task<List<ServerInfo>> ListServersAsync(CancellationToken cancellation)
    {
        var servers = new List<ServerInfo>();
        for (int page = 1; page <= MaxPages; page++)
        {
            ServerListResponse response = await GetAsync<ServerListResponse>(
                $"api/client/servers?page={page}&per_page={PageSize}&other=false", cancellation).ConfigureAwait(false);

            List<ServerInfo> data = response.Servers?.Data ?? [];
            servers.AddRange(data);
            if (data.Count < PageSize || servers.Count >= (response.Servers?.Total ?? 0))
            {
                break;
            }
        }

        return servers;
    }

    /// <summary>Usage of every server on a node that the user can see, keyed by server UUID.</summary>
    public async Task<Dictionary<string, ResourceUsage>> GetNodeResourcesAsync(string nodeUuid, CancellationToken cancellation)
    {
        NodeResourcesResponse response = await GetAsync<NodeResourcesResponse>(
            $"api/client/servers/nodes/{Uri.EscapeDataString(nodeUuid)}/resources", cancellation).ConfigureAwait(false);
        return new Dictionary<string, ResourceUsage>(response.Resources ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One server's usage, or null when the panel has none to give: the node has no data for it,
    /// the server is installing or suspended, or the node is not answering.
    /// </summary>
    public async Task<ResourceUsage?> GetServerResourcesAsync(string serverUuid, CancellationToken cancellation)
    {
        try
        {
            ResourcesResponse response = await GetAsync<ResourcesResponse>(
                $"api/client/servers/{Uri.EscapeDataString(serverUuid)}/resources", cancellation).ConfigureAwait(false);
            return response.Resources;
        }
        catch (CalagopusApiException e) when (
            e.Status is HttpStatusCode.NotFound or HttpStatusCode.Conflict || (int)e.Status >= 500)
        {
            return null;
        }
    }

    /// <summary>A short-lived token and the address of the node's websocket for one server.</summary>
    public Task<WebsocketCredentials> GetWebsocketAsync(string serverUuid, CancellationToken cancellation) =>
        GetAsync<WebsocketCredentials>($"api/client/servers/{Uri.EscapeDataString(serverUuid)}/websocket", cancellation);

    public void Dispose() => _http.Dispose();

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await _http.GetAsync(path, cancellation).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new CalagopusApiException(response.StatusCode, ErrorMessage(response.StatusCode, body));
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, CalagopusJson.Options)
                ?? throw new CalagopusApiException(response.StatusCode, "The panel sent an empty reply.");
        }
        catch (JsonException)
        {
            // Typically a web page: the address is not a Calagopus panel, or a proxy answered instead.
            throw new CalagopusApiException(response.StatusCode, "The reply was not from a Calagopus panel. Check panelUrl.");
        }
    }

    private static string ErrorMessage(HttpStatusCode status, string body)
    {
        try
        {
            ErrorResponse? error = JsonSerializer.Deserialize<ErrorResponse>(body, CalagopusJson.Options);
            if (error?.Errors is { Count: > 0 } errors)
            {
                return string.Join("; ", errors);
            }
        }
        catch (JsonException)
        {
            // Not the panel's error format; the status code has to do.
        }

        return $"The panel answered {(int)status} {status}.";
    }
}
