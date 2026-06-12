using Ctxman.Core.Domain;

namespace Ctxman.Tests.Domain;

/// <summary>
/// Sicherstellt, dass die vom Sweeper konsumierten TimeSpan-Helfer aus dem
/// Config-Format korrekt geparst werden (Spec §7.1: blob_grace_hours, sweep_interval).
/// </summary>
public sealed class RetentionConfigTests
{
    private static RetentionConfig Config(int graceHours, string sweepInterval) => new(
        BlobGraceHours: graceHours,
        EvictedBlobRetentionDays: 0,
        ArchivedSessionBlobs: "delete",
        SweepInterval: sweepInterval);

    [Fact] // Spec §7.1: blob_grace_hours wird als Stunden interpretiert.
    public void BlobGrace_IsHoursFromConfig()
    {
        Assert.Equal(TimeSpan.FromHours(72), Config(72, "24h").BlobGrace);
    }

    [Fact] // Spec §7.1: sweep_interval im "24h"-Format.
    public void SweepInterval_ParsesHourSuffix()
    {
        Assert.Equal(TimeSpan.FromHours(24), Config(72, "24h").SweepIntervalSpan);
    }

    [Fact] // Defaults aus PolicyConfig.Default(): 72h Grace, 24h Sweep.
    public void Default_RetentionHelpers_MatchSpecDefaults()
    {
        var retention = PolicyConfig.Default().Retention;

        Assert.Equal(TimeSpan.FromHours(72), retention.BlobGrace);
        Assert.Equal(TimeSpan.FromHours(24), retention.SweepIntervalSpan);
    }

    [Theory] // Reine Zahl => Stunden; sonst Suffix h/m/s/d.
    [InlineData("24h", 24 * 60 * 60)]
    [InlineData("30m", 30 * 60)]
    [InlineData("45s", 45)]
    [InlineData("2d", 2 * 24 * 60 * 60)]
    [InlineData("12", 12 * 60 * 60)]
    public void ParseDuration_SupportsSuffixesAndBareHours(string value, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RetentionConfig.ParseDuration(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("10x")]
    public void ParseDuration_RejectsInvalidInput(string value)
    {
        Assert.ThrowsAny<Exception>(() => RetentionConfig.ParseDuration(value));
    }
}
