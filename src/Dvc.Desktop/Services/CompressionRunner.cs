using Dvc.Core.Compression;
using Dvc.Core.Definitions;
using Microsoft.Extensions.Logging;

namespace Dvc.Desktop.Services;

/// <summary>
/// Compresses one video into the output folder with the app's fixed
/// settings (<c>FixedCompressionSettings</c>), leaving the source untouched
/// and never overwriting a file.
/// </summary>
/// <param name="outputFolder">
/// Where compressed videos go, created when first needed. Null uses the
/// default folder (<c>DefaultOutputFolder</c>).
/// </param>
public sealed class CompressionRunner(
    IVideoCompressionService compressionService, ILogger<CompressionRunner> logger, string? outputFolder = null)
{
    // Up to five seconds in all for ffmpeg to release a file it was writing.
    private const int DeleteAttempts = 50;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The folder compressed videos go to unless another is given:
    /// <c>Dynamic Video Compressor</c> in the user's Videos folder.
    /// </summary>
    public static string DefaultOutputFolder { get; } = Path.Combine(VideosFolder(), "Dynamic Video Compressor");

    /// <summary>The folder this runner writes compressed videos to.</summary>
    public string OutputFolder { get; } = outputFolder ?? DefaultOutputFolder;

    /// <summary>
    /// Compresses the video at <c>inputPath</c>. The encode is written to a
    /// temporary file in the output folder and only given its final name
    /// once it's finished, so a half-written file never looks done. On
    /// failure or cancellation the temporary file is deleted.
    /// </summary>
    /// <remarks>
    /// Throws <c>CompressionFailedException</c> for a failure whose message
    /// can be shown to the user, such as an unsupported or unreadable file.
    /// </remarks>
    public async Task<CompressedVideo> CompressAsync(string inputPath, CancellationToken cancellationToken)
    {
        // The file picker filters by extension, but a name can still be
        // typed into it, so the extension is checked here as well.
        if (!VideoFileDefinitions.SupportedExtensions.Contains(Path.GetExtension(inputPath)))
        {
            throw new CompressionFailedException(
                $"The file isn't a supported video type. Supported: {string.Join(", ", VideoFileDefinitions.SupportedExtensions)}.");
        }

        var request = FixedCompressionSettings.CreateRequest(inputPath);
        var source = await compressionService.ProbeAsync(inputPath, cancellationToken);
        var output = CompressionPlanner.PlanOutput(request, source);
        var plan = CompressionPlanner.PlanEncode(request, source, output);

        Directory.CreateDirectory(OutputFolder);
        var temporaryPath = Path.Combine(OutputFolder, OutputNaming.TemporaryFileName(inputPath));

        try
        {
            var result = await compressionService.EncodeAsync(plan, temporaryPath, progress: null, cancellationToken);
            var outputPath = OutputNaming.MoveToFreeName(temporaryPath, OutputFolder, inputPath);

            logger.LogInformation("Compressed {InputPath} to {OutputPath}", inputPath, outputPath);
            return new CompressedVideo(outputPath, [.. plan.Warnings, .. result.Warnings]);
        }
        catch
        {
            // Whatever ffmpeg wrote before stopping is incomplete.
            await TryDeleteAsync(temporaryPath);
            throw;
        }
    }

    private async Task TryDeleteAsync(string path)
    {
        // A cancelled encode returns as soon as ffmpeg is told to die, which
        // can be a moment before it lets go of the file, so a locked file is
        // retried for a while.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == DeleteAttempts)
                {
                    logger.LogWarning(ex, "Could not delete the unfinished output {Path}", path);
                    return;
                }
            }

            await Task.Delay(DeleteRetryDelay);
        }
    }

    // MyVideos is empty on systems without a known Videos folder, such as a
    // Linux desktop without XDG user directories.
    private static string VideosFolder()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return videos.Length > 0
            ? videos
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
    }
}

/// <summary>A video the app finished compressing (<c>CompressionRunner.CompressAsync</c>).</summary>
/// <param name="OutputPath">Where the compressed video was saved.</param>
/// <param name="Warnings">
/// Where the compression departed from its settings, written to be shown to
/// the user. Usually empty.
/// </param>
public sealed record CompressedVideo(string OutputPath, IReadOnlyList<string> Warnings);
