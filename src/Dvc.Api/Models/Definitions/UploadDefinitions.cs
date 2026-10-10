namespace Dvc.Api.Models.Definitions;

/// <summary>
/// Limits on the video file a <c>POST /v1/jobs</c> upload may carry.
/// </summary>
public static class UploadDefinitions
{
    /// <summary>
    /// Largest accepted request body. Replaces Kestrel's default limit
    /// (~30MB), which is far too small for video.
    /// </summary>
    public const long MaxUploadBytes = 2L * 1024 * 1024 * 1024; // 2GB

    /// <summary>
    /// File extensions ffmpeg can reliably read, so an obviously wrong upload
    /// (e.g. a .zip) fails at the API boundary rather than inside ffprobe.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".webm", ".flv", ".wmv", ".m4v"
    };
}
