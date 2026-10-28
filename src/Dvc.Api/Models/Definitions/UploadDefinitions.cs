namespace Dvc.Api.Models.Definitions;

/// <summary>
/// Limits on the video file a <c>POST /v1/jobs</c> upload may carry. The
/// accepted file extensions are the engine's own
/// (<c>VideoFileDefinitions.SupportedExtensions</c>).
/// </summary>
public static class UploadDefinitions
{
    /// <summary>
    /// Largest accepted request body. Replaces Kestrel's default limit
    /// (~30MB), which is far too small for video.
    /// </summary>
    public const long MaxUploadBytes = 2L * 1024 * 1024 * 1024; // 2GB
}
