namespace Dvc.Core.Definitions;

/// <summary>
/// The video files the engine accepts as input.
/// </summary>
public static class VideoFileDefinitions
{
    /// <summary>
    /// File extensions ffmpeg can reliably read, so an obviously wrong file
    /// (e.g. a .zip) can be turned away before it reaches ffprobe.
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".webm", ".flv", ".wmv", ".m4v"
    };
}
