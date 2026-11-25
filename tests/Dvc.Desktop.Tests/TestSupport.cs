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

    // Thrown by encoding after it has written its partial output.
    public Exception? EncodeFailure { get; set; }

    // Makes encoding wait, after writing its partial output, until cancelled.
    public bool EncodeUntilCancelled { get; set; }

    public int ProbeCount { get; private set; }

    public Task<SourceInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        ProbeCount++;
        return Task.FromResult(Source);
    }

    public async Task<CompressionResult> EncodeAsync(
        CompressionPlan plan, string outputPath, IProgress<EncodeProgress>? progress, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(outputPath, EncodedContent, CancellationToken.None);

        if (EncodeUntilCancelled)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (EncodeFailure is not null)
        {
            throw EncodeFailure;
        }

        return new CompressionResult([], EncodedContent.Length);
    }
}

// Returns a fixed path, or null as if the user cancelled the picker.
internal sealed class FakeFilePicker(string? path = null) : IVideoFilePicker
{
    public Task<string?> PickVideoAsync() => Task.FromResult(path);
}
