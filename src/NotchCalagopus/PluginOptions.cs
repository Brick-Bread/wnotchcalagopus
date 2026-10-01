using System.Security.Cryptography;
using System.Text;
using Notch.Core.Plugins;

namespace NotchCalagopus;

/// <summary>
/// The plugin's options, read from its settings.json. Notch has no settings UI for plugins, so
/// the user edits that file by hand and switches the plugin off and on.
/// </summary>
internal sealed record PluginOptions
{
    public const string SelectedServerKey = "selectedServer";

    private const string PanelUrlKey = "panelUrl";
    private const string ApiKeyKey = "apiKey";
    private const string ApiKeyProtectedKey = "apiKeyProtected";

    /// <summary>The panel's address, or null when it is missing or not a web address.</summary>
    public Uri? PanelUrl { get; init; }

    /// <summary>The API key in the clear, or null when none is stored or it cannot be read.</summary>
    public string? ApiKey { get; init; }

    /// <summary>What the user has to fix before the plugin can run; null when it can.</summary>
    public string? Problem { get; init; }

    public int PollSeconds { get; init; } = 15;


    /// <summary>Names or ids of the only servers to show; empty for all.</summary>
    public IReadOnlyList<string> IncludeServers { get; init; } = [];

    public IReadOnlyList<string> ExcludeServers { get; init; } = [];

    /// <summary>Short id of the server with the live connection; empty for none.</summary>
    public string SelectedServer { get; init; } = "";

    public bool ShowSelectedInPill { get; init; } = true;

    public bool AlertOffline { get; init; } = true;

    public bool AlertStateChanges { get; init; } = true;

    public bool AlertThresholds { get; init; } = true;

    public bool AlertPanelUnreachable { get; init; } = true;

    /// <summary>Percent of the server's limit; 0 switches the check off.</summary>
    public int MemoryPercent { get; init; } = 90;

    public int DiskPercent { get; init; } = 90;

    public int CpuPercent { get; init; }

    /// <summary>How many polls in a row a value has to be over its limit before it is reported.</summary>
    public int ThresholdPolls { get; init; } = 3;

    public static PluginOptions Load(IPluginSettings settings, IPluginLog log)
    {
        var defaults = new PluginOptions();

        string url = settings.Get(PanelUrlKey, "").Trim();
        Uri? panelUrl = ParsePanelUrl(url);
        string? apiKey = ReadApiKey(settings, log, out bool unreadable);

        var options = new PluginOptions
        {
            PanelUrl = panelUrl,
            ApiKey = apiKey,
            Problem = url.Length == 0 ? "Add panelUrl and apiKey to settings.json. Click to open it."
                : panelUrl is null ? "panelUrl is not a web address. Click to open settings.json."
                : unreadable ? "The stored key cannot be read here. Enter apiKey again."
                : apiKey is null ? "Add apiKey to settings.json. Click to open it."
                : null,
            PollSeconds = Math.Clamp(settings.Get("pollSeconds", defaults.PollSeconds), 15, 300),
            IncludeServers = Names(settings.Get<string[]>("includeServers", [])),
            ExcludeServers = Names(settings.Get<string[]>("excludeServers", [])),
            SelectedServer = settings.Get(SelectedServerKey, "").Trim(),
            ShowSelectedInPill = settings.Get("showSelectedInPill", defaults.ShowSelectedInPill),
            AlertOffline = settings.Get("alertOffline", defaults.AlertOffline),
            AlertStateChanges = settings.Get("alertStateChanges", defaults.AlertStateChanges),
            AlertThresholds = settings.Get("alertThresholds", defaults.AlertThresholds),
            AlertPanelUnreachable = settings.Get("alertPanelUnreachable", defaults.AlertPanelUnreachable),
            MemoryPercent = Math.Clamp(settings.Get("memoryPercent", defaults.MemoryPercent), 0, 100),
            DiskPercent = Math.Clamp(settings.Get("diskPercent", defaults.DiskPercent), 0, 100),
            CpuPercent = Math.Clamp(settings.Get("cpuPercent", defaults.CpuPercent), 0, 100),
            ThresholdPolls = Math.Clamp(settings.Get("thresholdPolls", defaults.ThresholdPolls), 1, 20),
        };

        if (panelUrl is { Scheme: "http", IsLoopback: false })
        {
            log.Warn("panelUrl uses http, so the API key is sent unencrypted. Use https if the panel offers it.");
        }

        // Written back so every option shows up in settings.json for the user to edit.
        settings.Set(PanelUrlKey, url);
        settings.Set("pollSeconds", options.PollSeconds);
        settings.Set("includeServers", options.IncludeServers);
        settings.Set("excludeServers", options.ExcludeServers);
        settings.Set(SelectedServerKey, options.SelectedServer);
        settings.Set("showSelectedInPill", options.ShowSelectedInPill);
        settings.Set("alertOffline", options.AlertOffline);
        settings.Set("alertStateChanges", options.AlertStateChanges);
        settings.Set("alertThresholds", options.AlertThresholds);
        settings.Set("alertPanelUnreachable", options.AlertPanelUnreachable);
        settings.Set("memoryPercent", options.MemoryPercent);
        settings.Set("diskPercent", options.DiskPercent);
        settings.Set("cpuPercent", options.CpuPercent);
        settings.Set("thresholdPolls", options.ThresholdPolls);

        return options;
    }

    /// <summary>
    /// The user types the key into "apiKey". It is moved straight into "apiKeyProtected",
    /// encrypted for this Windows account, so it does not stay readable in the file.
    /// </summary>
    private static string? ReadApiKey(IPluginSettings settings, IPluginLog log, out bool unreadable)
    {
        unreadable = false;

        string typed = settings.Get(ApiKeyKey, "").Trim();
        if (typed.Length > 0)
        {
            settings.Set(ApiKeyProtectedKey, KeyVault.Protect(typed));
            settings.Set(ApiKeyKey, "");
            log.Info("Encrypted the API key from settings.json.");
            return typed;
        }

        string stored = settings.Get(ApiKeyProtectedKey, "");
        settings.Set(ApiKeyKey, "");
        settings.Set(ApiKeyProtectedKey, stored);
        if (stored.Length == 0)
        {
            return null;
        }

        string? key = KeyVault.Unprotect(stored);
        if (key is null)
        {
            // Encrypted by another Windows account or on another PC, or edited by hand.
            unreadable = true;
            log.Warn("The stored API key could not be decrypted. Enter apiKey in settings.json again.");
        }

        return key;
    }

    private static Uri? ParsePanelUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            ? url
            : null;

    private static string[] Names(string[]? values) =>
        (values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToArray();
}

/// <summary>Encrypts the API key with Windows DPAPI, so only this Windows account can read it back.</summary>
internal static class KeyVault
{
    private static readonly byte[] Entropy = "brick-bread.calagopus"u8.ToArray();

    public static string Protect(string key) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string stored)
    {
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
