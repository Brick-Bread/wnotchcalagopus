using NotchCalagopus.Ui;

namespace NotchCalagopus.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512L * 1024 * 1024, "512 MB")]
    [InlineData(2254857830, "2.1 GB")]
    public void Bytes(long bytes, string expected) => Assert.Equal(expected, Format.Bytes(bytes));

    [Fact]
    public void UsageUsesTheLimitsUnit() =>
        Assert.Equal("0.5/4 GB", Format.Usage(512L * 1024 * 1024, 4L * 1024 * 1024 * 1024));

    [Fact]
    public void UsageWithoutLimitShowsOnlyWhatIsUsed() => Assert.Equal("1 MB", Format.Usage(1024 * 1024, 0));

    [Theory]
    [InlineData(45_000, "45s")]
    [InlineData(12 * 60_000, "12m")]
    [InlineData(4 * 3_600_000 + 12 * 60_000, "4h 12m")]
    [InlineData(3 * 86_400_000L + 4 * 3_600_000, "3d 4h")]
    public void Uptime(long ms, string expected) => Assert.Equal(expected, Format.Uptime(ms));

    [Fact]
    public void PercentRoundsAndNeverGoesNegative()
    {
        Assert.Equal("43%", Format.Percent(42.6));
        Assert.Equal("0%", Format.Percent(-3));
    }
}
