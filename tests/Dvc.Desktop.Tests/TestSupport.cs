using Dvc.Core.Compression;
using Dvc.Desktop.Services;

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
// writes a few bytes to the output, then does whatever the test sets up.
// Everything up to a held encode runs synchronously, so a test that holds
// nothing sees each compression finish before its call returns.
internal sealed class FakeCompressionService : IVideoCompressionService
{
    public const string EncodedContent = "encoded";

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
    private readonly List<string> _encodedInputs = [];

    // Thrown by encoding after it has written its partial output.
    public Exception? EncodeFailure { get; set; }

    // Inputs whose encode fails with an explained failure, after writing
    // partial output.
    public HashSet<string> FailingInputs { get; } = [];

    // Makes each encode wait, after writing its partial output, until
    // ReleaseEncode lets it finish or it's cancelled.
    public bool HoldEncodes { get; set; }

    public int ProbeCount { get; private set; }

    // The inputs encoding has started for, in order.
    public IReadOnlyList<string> EncodedInputs
    {
        get { lock (_encodedInputs) { return [.. _encodedInputs]; } }
    }

    public void ReleaseEncode() => _releases.Release();

    public Task<SourceInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        ProbeCount++;
        return Task.FromResult(Source);
    }

    public async Task<CompressionResult> EncodeAsync(
        CompressionPlan plan, string outputPath, IProgress<EncodeProgress>? progress, CancellationToken cancellationToken)
    {
        lock (_encodedInputs)
        {
            _encodedInputs.Add(plan.InputPath);
        }

        File.WriteAllText(outputPath, EncodedContent);

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
