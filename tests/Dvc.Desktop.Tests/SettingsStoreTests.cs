using Dvc.Desktop.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dvc.Desktop.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    // In a subfolder, as in AppData, so saving has to create it.
    private string FilePath => Path.Combine(_folder.Path, "Dynamic Video Compressor", "settings.json");

    private SettingsStore CreateStore() => new(NullLogger<SettingsStore>.Instance, FilePath);

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, json);
    }

    [Fact]
    public void WithNoFile_StartsFromTheDefaults()
    {
        var store = CreateStore();

        Assert.Equal(CompressionSettings.Default, store.Current);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Update_SavesSettingsTheNextStoreLoads()
    {
        var settings = new CompressionSettings
        {
            Quality = "HigherQuality",
            MaxShortSide = null,
            MaxFps = 60,
            EncoderPreset = "veryfast",
            AimForFileSize = true,
            TargetSizeMb = 9.5m,
        };

        CreateStore().Update(settings);

        Assert.Equal(settings, CreateStore().Current);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Update_WritesReadableJson()
    {
        CreateStore().Update(CompressionSettings.Default with { MaxShortSide = null });

        var json = File.ReadAllText(FilePath);
        Assert.Contains("\"quality\": \"Balanced\"", json);
        Assert.Contains("\"maxShortSide\": null", json);
        Assert.DoesNotContain("targetSizeBytes", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Update_WhenTheFileCantBeWritten_KeepsTheSettingsForNow()
    {
        // A folder where the file should be makes the write fail.
        Directory.CreateDirectory(FilePath);
        var store = CreateStore();
        var settings = CompressionSettings.Default with { EncoderPreset = "fast" };

        store.Update(settings);

        Assert.Equal(settings, store.Current);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    public void ACorruptFile_GivesTheDefaults(string json)
    {
        WriteFile(json);

        Assert.Equal(CompressionSettings.Default, CreateStore().Current);
    }

    [Fact]
    public void AnInvalidValue_FallsBackForThatSettingAndKeepsTheRest()
    {
        WriteFile("""
            {
              "quality": "Ultra",
              "maxShortSide": 1234,
              "maxFps": 60,
              "encoderPreset": "veryfast",
              "aimForFileSize": "yes",
              "targetSizeMb": -5
            }
            """);

        var settings = CreateStore().Current;

        Assert.Equal(CompressionSettings.Default.Quality, settings.Quality);
        Assert.Equal(CompressionSettings.Default.MaxShortSide, settings.MaxShortSide);
        Assert.Equal(60, settings.MaxFps);
        Assert.Equal("veryfast", settings.EncoderPreset);
        Assert.Equal(CompressionSettings.Default.AimForFileSize, settings.AimForFileSize);
        Assert.Equal(CompressionSettings.Default.TargetSizeMb, settings.TargetSizeMb);
    }

    [Fact]
    public void AMissingSetting_GetsItsDefault()
    {
        WriteFile("""{ "encoderPreset": "medium" }""");

        var settings = CreateStore().Current;

        Assert.Equal(CompressionSettings.Default with { EncoderPreset = "medium" }, settings);
    }

    [Fact]
    public void AnExplicitNullCap_KeepsTheSourcesValue()
    {
        WriteFile("""{ "maxShortSide": null, "maxFps": null }""");

        var settings = CreateStore().Current;

        Assert.Null(settings.MaxShortSide);
        Assert.Null(settings.MaxFps);
    }

    [Fact]
    public void Names_AreMatchedIgnoringCaseAndSpelledAsTheDefinitionsDo()
    {
        WriteFile("""{ "quality": "smallersize", "encoderPreset": "VERYSLOW" }""");

        var settings = CreateStore().Current;

        Assert.Equal("SmallerSize", settings.Quality);
        Assert.Equal("veryslow", settings.EncoderPreset);
    }
}
