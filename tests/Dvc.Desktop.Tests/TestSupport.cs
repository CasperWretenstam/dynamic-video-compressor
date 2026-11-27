using Dvc.Core.Compression;
using Dvc.Desktop.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dvc.Desktop.Tests;

// A fresh folder under the system temp folder, deleted with everything in it
// on dispose.
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateDirectory(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dvc-desktop-tests-{Guid.NewGuid():N}")).FullName;

    public string File(string name, string content = "")
    {
        var path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public string[] FileNames() =>
        [.. Directory.GetFiles(Path).Select(System.IO.Path.GetFileName).Order().Cast<string>()];

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

// Stands in for ffmpeg: probing returns a fixed 1080p source, and encoding
// writes a few bytes to the output, reports any progress the test set up,
// then does whatever else the test sets up. Everything up to a held probe or
// encode runs synchronously, so a test that holds nothing sees each
// compression finish before its call returns.
internal sealed class FakeCompressionService : IVideoCompressionService
{
    public const string EncodedContent = "encoded";

    public const string UnreadableMessage =
        "The file could not be read as a video. It may be corrupt or in an unsupported format.";

    public static readonly SourceInfo Source = new()
    {
        DurationSeconds = 10,
        Width = 1920,
        Height = 1080,
        Fps = 30,
        HasAudio = true,
        AudioBitrateBps = 128_000,
        VideoBitrateBps = 8_000_000,
    };

    private readonly SemaphoreSlim _releases = new(0);
    private readonly SemaphoreSlim _probeReleases = new(0);
    private readonly List<CompressionPlan> _encodedPlans = [];

    // Thrown by encoding after it has written its partial output.
    public Exception? EncodeFailure { get; set; }

    // Inputs whose encode fails with an explained failure, after writing
    // partial output.
    public HashSet<string> FailingInputs { get; } = [];

    // Makes each encode wait, after writing its partial output, until
    // ReleaseEncode lets it finish or it's cancelled.
    public bool HoldEncodes { get; set; }

    // Inputs whose probe fails as if ffprobe couldn't read them.
    public HashSet<string> FailingProbes { get; } = [];

    // Makes each probe wait until ReleaseProbe lets it finish or it's
    // cancelled.
    public bool HoldProbes { get; set; }

    // Reported, in order, by each encode once it has written its output.
    public List<EncodeProgress> ProgressReports { get; } = [];

    private int _probeCount;

    public int ProbeCount => Volatile.Read(ref _probeCount);

    // The plans encoding has started for, in order.
    public IReadOnlyList<CompressionPlan> EncodedPlans
    {
        get { lock (_encodedPlans) { return [.. _encodedPlans]; } }
    }

    // The inputs encoding has started for, in order.
    public IReadOnlyList<string> EncodedInputs => [.. EncodedPlans.Select(plan => plan.InputPath)];

    public void ReleaseEncode() => _releases.Release();

    public void ReleaseProbe() => _probeReleases.Release();

    public async Task<SourceInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _probeCount);

        if (HoldProbes)
        {
            await _probeReleases.WaitAsync(cancellationToken);
        }

        return FailingProbes.Contains(inputPath) ? throw new CompressionFailedException(UnreadableMessage) : Source;
    }

    public async Task<CompressionResult> EncodeAsync(
        CompressionPlan plan, string outputPath, IProgress<EncodeProgress>? progress, CancellationToken cancellationToken)
    {
        lock (_encodedPlans)
        {
            _encodedPlans.Add(plan);
        }

        File.WriteAllText(outputPath, EncodedContent);

        foreach (var report in ProgressReports)
        {
            progress?.Report(report);
        }

        if (HoldEncodes)
        {
            await _releases.WaitAsync(cancellationToken);
        }

        if (FailingInputs.Contains(plan.InputPath))
        {
            throw new CompressionFailedException("The file has no video stream.");
        }

        if (EncodeFailure is not null)
        {
            throw EncodeFailure;
        }

        return new CompressionResult([], EncodedContent.Length);
    }
}

// Returns fixed paths, or none as if the user cancelled the picker.
internal sealed class FakeFilePicker(params string[] paths) : IVideoFilePicker
{
    public Task<IReadOnlyList<string>> PickVideosAsync() => Task.FromResult<IReadOnlyList<string>>(paths);
}

// A settings store over a file in a temporary folder, starting from the
// defaults unless the file already holds settings.
internal static class TestSettings
{
    public static SettingsStore Store(TempFolder folder) =>
        new(NullLogger<SettingsStore>.Instance, System.IO.Path.Combine(folder.Path, "settings.json"));
}

internal static class Wait
{
    // Polls until the condition holds, for state that changes on another
    // thread once a held encode is released or cancelled.
    public static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition didn't hold within 5 seconds.");
            }

            await Task.Delay(10);
        }
    }
}
