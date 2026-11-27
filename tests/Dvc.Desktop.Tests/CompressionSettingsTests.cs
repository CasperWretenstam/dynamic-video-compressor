using Dvc.Core.Definitions;
using Dvc.Desktop.Services;

namespace Dvc.Desktop.Tests;

public class CompressionSettingsTests
{
    [Fact]
    public void Default_MatchesTheServicesShippedDefaults()
    {
        var request = CompressionSettings.Default.CreateRequest("Videos/clip.mov");

        Assert.Equal("Videos/clip.mov", request.InputPath);
        Assert.Equal("Balanced", request.QualityPreference);
        Assert.Null(request.TargetSizeBytes);
        Assert.Null(request.ShortSide);
        Assert.Null(request.Fps);
        Assert.Equal(1080, request.DefaultMaxShortSide);
        Assert.Equal(30, request.DefaultMaxFps);
        Assert.Equal("slow", request.EncoderPreset);
        Assert.True(request.TwoPass);
    }

    [Theory]
    [InlineData(QualityPreferenceDefinitions.SmallerSize)]
    [InlineData(QualityPreferenceDefinitions.HigherQuality)]
    public void CreateRequest_UsesTheQualityTier(string quality)
    {
        var request = (CompressionSettings.Default with { Quality = quality }).CreateRequest("clip.mov");

        Assert.Equal(quality, request.QualityPreference);
        Assert.Null(request.TargetSizeBytes);
    }

    [Theory]
    [InlineData(2160)]
    [InlineData(720)]
    [InlineData(null)]
    public void CreateRequest_CapsTheResolutionWithoutForcingIt(int? maxShortSide)
    {
        var request = (CompressionSettings.Default with { MaxShortSide = maxShortSide }).CreateRequest("clip.mov");

        Assert.Equal(maxShortSide, request.DefaultMaxShortSide);
        Assert.Null(request.ShortSide);
    }

    [Theory]
    [InlineData(60.0)]
    [InlineData(24.0)]
    [InlineData(null)]
    public void CreateRequest_CapsTheFramerateWithoutForcingIt(double? maxFps)
    {
        var request = (CompressionSettings.Default with { MaxFps = maxFps }).CreateRequest("clip.mov");

        Assert.Equal(maxFps, request.DefaultMaxFps);
        Assert.Null(request.Fps);
    }

    [Fact]
    public void CreateRequest_UsesTheEncoderPreset()
    {
        var request = (CompressionSettings.Default with { EncoderPreset = "veryfast" }).CreateRequest("clip.mov");

        Assert.Equal("veryfast", request.EncoderPreset);
    }

    [Fact]
    public void CreateRequest_AimingForAFileSize_SendsBytesInsteadOfAQualityTier()
    {
        var settings = CompressionSettings.Default with { AimForFileSize = true, TargetSizeMb = 9.5m };

        var request = settings.CreateRequest("clip.mov");

        Assert.Null(request.QualityPreference);
        Assert.Equal(9_961_472, request.TargetSizeBytes);
        Assert.True(request.TwoPass);
    }

    [Fact]
    public void CreateRequest_NotAimingForAFileSize_IgnoresTheSize()
    {
        var settings = CompressionSettings.Default with { AimForFileSize = false, TargetSizeMb = 9.5m };

        Assert.Null(settings.CreateRequest("clip.mov").TargetSizeBytes);
    }

    [Fact]
    public void TheCapsOnOffer_FollowTheResolutionTableLargestFirst()
    {
        Assert.Equal([2160, 1440, 1080, 720, 480, 360], CompressionSettings.ShortSideCaps);
        Assert.Equal([60.0, 30.0, 24.0], CompressionSettings.FpsCaps);
    }
}
