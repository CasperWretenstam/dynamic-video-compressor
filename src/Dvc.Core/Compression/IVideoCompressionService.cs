namespace Dvc.Core.Compression;

/// <summary>
/// Probes and encodes videos with ffmpeg. What to encode is worked out in
/// between, from the probe result (<c>CompressionPlanner</c>).
/// </summary>
/// <remarks>
/// Both methods throw <c>CompressionFailedException</c> for a failure that
/// can be explained to end users, such as an unreadable file or a size
/// target that can't be hit.
/// </remarks>
public interface IVideoCompressionService
{
    /// <summary>
    /// What ffprobe finds about the video at <c>inputPath</c>: its duration,
    /// display dimensions, framerate, audio and bitrates.
    /// </summary>
    Task<SourceInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken);

    /// <summary>
    /// Encodes a planned compression, writing the result to
    /// <c>outputPath</c>. On failure the file may be missing, partial or
    /// oversized; the caller cleans it up.
    /// </summary>
    Task<CompressionResult> EncodeAsync(CompressionPlan plan, string outputPath, CancellationToken cancellationToken);
}
