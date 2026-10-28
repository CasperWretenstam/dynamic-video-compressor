using Dvc.Core.Definitions;

namespace Dvc.Core.Tests;

public class QualityPreferenceDefinitionsTests
{
    [Theory]
    [InlineData(QualityPreferenceDefinitions.SmallerSize, 1920, 1080, 26)]
    [InlineData(QualityPreferenceDefinitions.Balanced, 1920, 1080, 23)]
    [InlineData(QualityPreferenceDefinitions.HigherQuality, 1920, 1080, 20)]
    [InlineData(QualityPreferenceDefinitions.Balanced, 1080, 1920, 23)] // portrait matches landscape
    [InlineData(QualityPreferenceDefinitions.Balanced, 640, 360, 22)]   // offset bottoms out at -1
    [InlineData(QualityPreferenceDefinitions.Balanced, 854, 480, 22)]
    [InlineData(QualityPreferenceDefinitions.Balanced, 1280, 720, 22)]
    [InlineData(QualityPreferenceDefinitions.Balanced, 2560, 1440, 24)]
    [InlineData(QualityPreferenceDefinitions.Balanced, 3840, 2160, 25)]
    [InlineData(QualityPreferenceDefinitions.Balanced, 7680, 4320, 25)] // offset tops out at +2
    public void ResolveCrf_ShiftsByPixelCountRelativeTo1080p(string preference, int width, int height, int expectedCrf)
    {
        Assert.Equal(expectedCrf, QualityPreferenceDefinitions.ResolveCrf(preference, width, height));
    }

    [Fact]
    public void ResolveCrf_IsCaseInsensitive()
    {
        Assert.Equal(
            QualityPreferenceDefinitions.ResolveCrf("balanced", 1920, 1080),
            QualityPreferenceDefinitions.ResolveCrf("Balanced", 1920, 1080));
    }

    [Fact]
    public void ResolveCrf_ThrowsForUnknownPreference()
    {
        Assert.Throws<ArgumentException>(() =>
            QualityPreferenceDefinitions.ResolveCrf("Cinematic", 1920, 1080));
    }

    [Theory]
    [InlineData(640, 360)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(7680, 4320)]
    public void ResolveCrf_KeepsTiersOrderedAndInRange(int width, int height)
    {
        // Lower CRF is higher quality, so tiers must stay strictly ordered
        // and within libx264's valid 0-51 range after offsets are applied.
        var smaller = QualityPreferenceDefinitions.ResolveCrf(QualityPreferenceDefinitions.SmallerSize, width, height);
        var balanced = QualityPreferenceDefinitions.ResolveCrf(QualityPreferenceDefinitions.Balanced, width, height);
        var higher = QualityPreferenceDefinitions.ResolveCrf(QualityPreferenceDefinitions.HigherQuality, width, height);

        Assert.True(smaller > balanced && balanced > higher, $"Tiers out of order at {width}x{height}");
        Assert.InRange(higher, 0, 51);
        Assert.InRange(smaller, 0, 51);
    }

    [Fact]
    public void Default_IsAKnownTier()
    {
        Assert.True(QualityPreferenceDefinitions.Tiers.ContainsKey(QualityPreferenceDefinitions.Default));
    }
}

public class EncoderPresetDefinitionsTests
{
    [Fact]
    public void Default_IsAKnownPreset()
    {
        Assert.True(EncoderPresetDefinitions.Presets.ContainsKey(EncoderPresetDefinitions.Default));
    }

    [Fact]
    public void Presets_AreCaseInsensitive()
    {
        Assert.True(EncoderPresetDefinitions.Presets.ContainsKey("SLOW"));
    }
}

public class DefinitionTableExtensionsTests
{
    [Theory]
    [InlineData("SLOW", "slow")]
    [InlineData("VeryFast", "veryfast")]
    [InlineData("medium", "medium")]
    public void CanonicalKey_ReturnsTheTablesOwnSpelling(string input, string expected)
    {
        Assert.Equal(expected, EncoderPresetDefinitions.Presets.CanonicalKey(input));
    }

    [Fact]
    public void CanonicalKey_PreservesMixedCaseKeys()
    {
        Assert.Equal("4K", ResolutionDefinitions.ShortSides.CanonicalKey("4k"));
        Assert.Equal(QualityPreferenceDefinitions.HigherQuality, QualityPreferenceDefinitions.Tiers.CanonicalKey("higherquality"));
    }

    [Fact]
    public void CanonicalKey_ReturnsNull_WhenAbsent()
    {
        Assert.Null(EncoderPresetDefinitions.Presets.CanonicalKey("bogus"));
    }
}
