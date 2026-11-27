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

        var video = await CompressAsync(runner, source);

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

        var first = await CompressAsync(runner, source);
        var second = await CompressAsync(runner, source);

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
            () => CompressAsync(runner, source));

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

        var probed = await runner.ProbeAsync(source, CancellationToken.None);
        var compression = runner.CompressAsync(probed, progress: null, cancellation.Token);
        await WaitForFileAsync(runner.OutputFolder);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compression);
        Assert.Empty(Directory.GetFiles(runner.OutputFolder));
    }

    [Fact]
    public async Task ProbeAsync_WithAnUnsupportedExtension_FailsBeforeProbing()
    {
        var source = _sources.File("notes.txt");
        var runner = CreateRunner();

        await Assert.ThrowsAsync<CompressionFailedException>(() => runner.ProbeAsync(source, CancellationToken.None));

        Assert.Equal(0, _compressionService.ProbeCount);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsWhatFfprobeFoundAndTheFilesSize()
    {
        var source = _sources.File("clip.mov", new string('x', 2048));
        var runner = CreateRunner();

        var probed = await runner.ProbeAsync(source, CancellationToken.None);

        Assert.Equal(source, probed.Path);
        Assert.Equal(FakeCompressionService.Source, probed.Info);
        Assert.Equal(2048, probed.SizeBytes);
    }

    [Fact]
    public async Task CompressAsync_ReturnsTheOutputsSizeAndPassesProgressOn()
    {
        var source = _sources.File("clip.mov");
        var runner = CreateRunner();
        var report = EncodeProgress.At(TimeSpan.FromSeconds(5), 10, EncodePass.Single, attempt: 1);
        _compressionService.ProgressReports.Add(report);
        var received = new List<EncodeProgress>();

        var probed = await runner.ProbeAsync(source, CancellationToken.None);
        var video = await runner.CompressAsync(probed, new ListProgress(received), CancellationToken.None);

        Assert.Equal(FakeCompressionService.EncodedContent.Length, video.SizeBytes);
        Assert.Equal([report], received);
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

    private static async Task<CompressedVideo> CompressAsync(CompressionRunner runner, string source)
    {
        var probed = await runner.ProbeAsync(source, CancellationToken.None);
        return await runner.CompressAsync(probed, progress: null, CancellationToken.None);
    }

    // Collects reports as they're made, on the reporting thread.
    private sealed class ListProgress(List<EncodeProgress> reports) : IProgress<EncodeProgress>
    {
        public void Report(EncodeProgress value) => reports.Add(value);
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
