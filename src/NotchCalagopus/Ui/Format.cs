using System.Globalization;

namespace NotchCalagopus.Ui;

/// <summary>Short texts for cards, where space is tight.</summary>
internal static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>"512 MB", "2.1 GB". Binary units, as the panel shows them.</summary>
    public static string Bytes(long bytes)
    {
        int unit = UnitFor(bytes);
        return $"{Scaled(bytes, unit)} {Units[unit]}";
    }

    /// <summary>"2.1/4 GB": both numbers in the limit's unit. Without a limit, just what is used.</summary>
    public static string Usage(long used, long limit)
    {
        if (limit <= 0)
        {
            return Bytes(used);
        }

        int unit = UnitFor(limit);
        return $"{Scaled(used, unit)}/{Scaled(limit, unit)} {Units[unit]}";
    }

    public static string Percent(double percent) =>
        Math.Round(Math.Max(0, percent)).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>"3d 4h", "4h 12m", "12m", "45s".</summary>
    public static string Uptime(long milliseconds)
    {
        TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time switch
        {
            { TotalDays: >= 1 } => $"{(int)time.TotalDays}d {time.Hours}h",
            { TotalHours: >= 1 } => $"{(int)time.TotalHours}h {time.Minutes}m",
            { TotalMinutes: >= 1 } => $"{(int)time.TotalMinutes}m",
            _ => $"{(int)time.TotalSeconds}s",
        };
    }

    private static int UnitFor(long bytes)
    {
        int unit = 0;
        for (double value = Math.Max(0, bytes); value >= 1024 && unit < Units.Length - 1; value /= 1024)
        {
            unit++;
        }

        return unit;
    }

    private static string Scaled(long bytes, int unit)
    {
        double value = Math.Max(0, bytes) / Math.Pow(1024, unit);
        return value.ToString(value < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);
    }
}
