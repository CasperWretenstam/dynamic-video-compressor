namespace Dvc.Core.Compression;

/// <summary>What probing a source video with ffprobe found.</summary>
public sealed record SourceInfo
{
    /// <summary>The duration in seconds, or 0 when ffprobe couldn't determine it.</summary>
    public required double DurationSeconds { get; init; }

    /// <summary>
    /// The width in pixels as displayed, after any rotation tag is applied.
    /// </summary>
    public required int Width { get; init; }

    /// <summary>
    /// The height in pixels as displayed, after any rotation tag is applied.
    /// </summary>
    public required int Height { get; init; }

    /// <summary>The average framerate, or null if unknown.</summary>
    public double? Fps { get; init; }

    /// <summary>Whether the source has an audio stream.</summary>
    public required bool HasAudio { get; init; }

    /// <summary>
    /// The audio stream's bitrate in bps, or null if unknown or the source
    /// has no audio.
    /// </summary>
    public long? AudioBitrateBps { get; init; }

    /// <summary>
    /// The video stream's bitrate in bps: its own when ffprobe reports it,
    /// otherwise estimated as the container's total minus the audio. Null if
    /// neither is usable.
    /// </summary>
    public long? VideoBitrateBps { get; init; }
}
