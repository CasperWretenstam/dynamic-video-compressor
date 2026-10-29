namespace Dvc.Core.Compression;

/// <summary>The outcome of a successful encode.</summary>
/// <param name="Warnings">
/// Where the encode departed from the plan, such as an output that needed
/// re-encoding to fit its target size. Written to be shown to end users.
/// </param>
/// <param name="OutputSizeBytes">The output file's size, in bytes.</param>
public sealed record CompressionResult(IReadOnlyList<string> Warnings, long OutputSizeBytes);
