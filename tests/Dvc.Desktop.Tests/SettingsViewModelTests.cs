using Dvc.Desktop.Services;
using Dvc.Desktop.ViewModels;

namespace Dvc.Desktop.Tests;

public class SettingsViewModelTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void OffersEachChoiceWithReadableLabels()
    {
        var viewModel = new SettingsViewModel(TestSettings.Store(_folder));

        Assert.Equal(["Smaller size", "Balanced", "Higher quality"], viewModel.QualityOptions.Select(option => option.Label));
        Assert.Equal(
            ["Original", "Up to 4K", "Up to 1440p", "Up to 1080p", "Up to 720p", "Up to 480p", "Up to 360p"],
            viewModel.ResolutionOptions.Select(option => option.Label));
        Assert.Equal(
            ["Original", "Up to 60 fps", "Up to 30 fps", "Up to 24 fps"],
            viewModel.FramerateOptions.Select(option => option.Label));
        Assert.Equal("ultrafast", viewModel.PresetOptions[0].Label);
        Assert.All(viewModel.QualityOptions, option => Assert.NotEmpty(option.Description));
        Assert.All(viewModel.PresetOptions, option => Assert.NotEmpty(option.Description));
    }

    [Fact]
    public void ShowsTheSavedSettings()
    {
        var store = TestSettings.Store(_folder);
        store.Update(new CompressionSettings
        {
            Quality = "SmallerSize",
            MaxShortSide = null,
            MaxFps = 24,
            EncoderPreset = "medium",
            AimForFileSize = true,
            TargetSizeMb = 50,
        });

        var viewModel = new SettingsViewModel(store);

        Assert.Equal("SmallerSize", viewModel.SelectedQuality?.Value);
        Assert.Equal("Original", viewModel.SelectedResolution?.Label);
        Assert.Equal(24, viewModel.SelectedFramerate?.Value);
        Assert.Equal("medium", viewModel.SelectedPreset?.Value);
        Assert.True(viewModel.AimForFileSize);
        Assert.False(viewModel.IsQualityEnabled);
        Assert.Equal(50, viewModel.TargetSizeMb);
    }

    [Fact]
    public void ChangingAChoice_SavesItStraightAway()
    {
        var store = TestSettings.Store(_folder);
        var viewModel = new SettingsViewModel(store);

        viewModel.SelectedQuality = viewModel.QualityOptions.Single(option => option.Value == "HigherQuality");
        viewModel.SelectedResolution = viewModel.ResolutionOptions.Single(option => option.Value == 720);
        viewModel.SelectedFramerate = viewModel.FramerateOptions.Single(option => option.Value is null);
        viewModel.SelectedPreset = viewModel.PresetOptions.Single(option => option.Value == "fast");

        var expected = new CompressionSettings
        {
            Quality = "HigherQuality",
            MaxShortSide = 720,
            MaxFps = null,
            EncoderPreset = "fast",
        };
        Assert.Equal(expected, store.Current);
        Assert.Equal(expected, TestSettings.Store(_folder).Current);
    }

    [Fact]
    public void AimingForAFileSize_SavesTheSizeAndDisablesTheQualityChoice()
    {
        var store = TestSettings.Store(_folder);
        var viewModel = new SettingsViewModel(store);

        viewModel.AimForFileSize = true;
        viewModel.TargetSizeMb = 9.5m;

        Assert.False(viewModel.IsQualityEnabled);
        Assert.True(store.Current.AimForFileSize);
        Assert.Equal(9.5m, store.Current.TargetSizeMb);
        Assert.Null(store.Current.CreateRequest("clip.mov").QualityPreference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void AnEmptyOrInvalidFileSize_KeepsTheLastValidOne(int? size)
    {
        var store = TestSettings.Store(_folder);
        var viewModel = new SettingsViewModel(store) { TargetSizeMb = 40 };

        viewModel.TargetSizeMb = size;

        Assert.Equal(40, store.Current.TargetSizeMb);
    }

    [Fact]
    public void ResetToDefaults_RestoresAndSavesTheDefaults()
    {
        var store = TestSettings.Store(_folder);
        var viewModel = new SettingsViewModel(store);
        Assert.False(viewModel.ResetToDefaultsCommand.CanExecute(null));

        viewModel.SelectedPreset = viewModel.PresetOptions.Single(option => option.Value == "ultrafast");
        viewModel.AimForFileSize = true;
        Assert.True(viewModel.ResetToDefaultsCommand.CanExecute(null));

        viewModel.ResetToDefaultsCommand.Execute(null);

        Assert.Equal(CompressionSettings.Default, store.Current);
        Assert.Equal(CompressionSettings.Default, TestSettings.Store(_folder).Current);
        Assert.Equal("slow", viewModel.SelectedPreset?.Value);
        Assert.False(viewModel.AimForFileSize);
        Assert.False(viewModel.ResetToDefaultsCommand.CanExecute(null));
    }
}
