namespace Dvc.Core.Compression;

/// <summary>
/// Everything an encode needs, worked out from a request and its probed
/// source (<c>CompressionPlanner.PlanEncode</c>).
/// </summary>
/// <param name="InputPath">The video file to compress.</param>
/// <param name="Source">What probing the source found.</param>
/// <param name="Output">The output dimensions, framerate, audio bitrate and preset.</param>
/// <param name="Mode">Whether to encode at a constant quality or aim for a file size.</param>
/// <param name="Warnings">
/// Where the plan departs from the request, such as a target size that
/// allows more bitrate than the source has. Written to be shown to end
/// users.
/// </param>
public sealed record CompressionPlan(
    string InputPath, SourceInfo Source, OutputSettings Output, EncodeMode Mode, IReadOnlyList<string> Warnings);
