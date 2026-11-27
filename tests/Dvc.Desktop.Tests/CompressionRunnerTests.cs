using Dvc.Core.Compression;
using Dvc.Desktop.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dvc.Desktop.Tests;

public class CompressionRunnerTests : IDisposable
{
    private readonly TempFolder _sources = new();
    private readonly TempFolder _outputs = new();
    private readonly FakeCompressionService _compressionService = new();

    public void Dispose()
    {
        _sources.Dispose();
        _outputs.Dispose();
    }

    private CompressionRunner CreateRunner() =>
        new(_compressionService, NullLogger<CompressionRunner>.Instance, Path.Combine(_outputs.Path, "Output"));

    [Fact]
    public async Task CompressAsync_SavesUnderTheSourcesNameAndLeavesTheSourceAlone()
    {
        var source = _sources.File("clip.mov", "original");
        var runner = CreateRunner();

        var video = await runner.CompressAsync(source, CancellationToken.None);

        Assert.Equal(Path.Combine(runner.OutputFolder, "clip.mp4"), video.OutputPath);
        Assert.Equal(FakeCompressionService.EncodedContent, File.ReadAllText(video.OutputPath));
        Assert.Equal(["clip.mp4"], Directory.GetFiles(runner.OutputFolder).Select(Path.GetFileName));
        Assert.Equal("original", File.ReadAllText(source));
    }

    [Fact]
    public async Task CompressAsync_TheSameSourceAgain_NumbersTheSecondOutput()
    {
        var source = _sources.File("clip.mov");
        var runner = CreateRunner();

        var first = await runner.CompressAsync(source, CancellationToken.None);
        var second = await runner.CompressAsync(source, CancellationToken.None);

        Assert.Equal(Path.Combine(runner.OutputFolder, "clip.mp4"), first.OutputPath);
        Assert.Equal(Path.Combine(runner.OutputFolder, "clip (1).mp4"), second.OutputPath);
    }

    [Fact]
    public async Task CompressAsync_WhenTheEncodeFails_RethrowsAndLeavesNoOutput()
    {
        var source = _sources.File("clip.mov");
        var runner = CreateRunner();
        _compressionService.EncodeFailure = new CompressionFailedException("The encode failed.");

        var ex = await Assert.ThrowsAsync<CompressionFailedException>(
            () => runner.CompressAsync(source, CancellationToken.None));

        Assert.Equal("The encode failed.", ex.Message);
        Assert.Empty(Directory.GetFiles(runner.OutputFolder));
    }

    [Fact]
    public async Task CompressAsync_WhenCancelled_LeavesNoOutput()
    {
        var source = _sources.File("clip.mov");
        var runner = CreateRunner();
        _compressionService.HoldEncodes = true;
        using var cancellation = new CancellationTokenSource();

        var compression = runner.CompressAsync(source, cancellation.Token);
        await WaitForFileAsync(runner.OutputFolder);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compression);
        Assert.Empty(Directory.GetFiles(runner.OutputFolder));
    }

    [Fact]
    public async Task CompressAsync_WithAnUnsupportedExtension_FailsBeforeProbing()
    {
        var source = _sources.File("notes.txt");
        var runner = CreateRunner();

        await Assert.ThrowsAsync<CompressionFailedException>(() => runner.CompressAsync(source, CancellationToken.None));

        Assert.Equal(0, _compressionService.ProbeCount);
        Assert.False(Directory.Exists(runner.OutputFolder));
    }

    [Fact]
    public void FixedSettings_MatchTheServicesShippedDefaults()
    {
        var request = FixedCompressionSettings.CreateRequest("Videos/clip.mov");

        Assert.Equal("Balanced", request.QualityPreference);
        Assert.Null(request.TargetSizeBytes);
        Assert.Null(request.ShortSide);
        Assert.Null(request.Fps);
        Assert.Equal(1080, request.DefaultMaxShortSide);
        Assert.Equal(30, request.DefaultMaxFps);
        Assert.Equal("slow", request.EncoderPreset);
    }

    // The fake encode writes its partial output before waiting to be
    // cancelled, so a file appearing means the encode is under way.
    private static async Task WaitForFileAsync(string folder)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0))
        {
            Assert.True(DateTime.UtcNow < deadline, "The encode never started.");
            await Task.Delay(10);
        }
    }
}
