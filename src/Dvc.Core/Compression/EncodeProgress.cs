namespace Dvc.Core.Compression;

/// <summary>Which ffmpeg run of an encode is in progress.</summary>
public enum EncodePass
{
    /// <summary>
    /// The only run: a constant-quality encode, or a one-pass encode aiming
    /// for a file size.
    /// </summary>
    Single,

    /// <summary>
    /// The analysis run of a two-pass encode aiming for a file size. It
    /// writes no output file.
    /// </summary>
    First,

    /// <summary>The run of a two-pass encode that writes the output file.</summary>
    Second,
}

/// <summary>
/// How far along an encode is, as reported while
/// <c>IVideoCompressionService.EncodeAsync</c> runs.
/// </summary>
/// <param name="Fraction">
/// The share of the current attempt that's done, from 0 to 1. Both runs of a
/// two-pass encode count towards one range, the first up to
/// <c>FirstPassShare</c> and the second the rest. Null when the source's
/// duration is unknown, so there's nothing to measure against.
/// </param>
/// <param name="Pass">Which ffmpeg run of the attempt is in progress.</param>
/// <param name="Attempt">
/// 1 for the first encode. Higher when an encode aiming for a file size came
/// out too large and is being re-run at a lower bitrate; the fraction then
/// starts again from 0.
/// </param>
public sealed record EncodeProgress(double? Fraction, EncodePass Pass, int Attempt)
{
    /// <summary>
    /// The share of a two-pass encode's progress range given to its first
    /// run, the rest going to the second.
    /// </summary>
    /// <remarks>
    /// Two-pass encodes measured roughly 1.7-1.8 times as long as one-pass
    /// encodes, so the analysis run takes a little under half the time.
    /// </remarks>
    public const double FirstPassShare = 0.4;

    /// <summary>
    /// The progress once ffmpeg has encoded up to <c>position</c> of a source
    /// lasting <c>durationSeconds</c>, in the given run of the given attempt.
    /// A position outside the source's duration is clamped to it.
    /// </summary>
    /// <param name="durationSeconds">The source's duration. Zero or less means unknown.</param>
    public static EncodeProgress At(TimeSpan position, double durationSeconds, EncodePass pass, int attempt)
    {
        if (!(durationSeconds > 0))
        {
            return new EncodeProgress(null, pass, attempt);
        }

        var passFraction = Math.Clamp(position.TotalSeconds / durationSeconds, 0, 1);
        var fraction = pass switch
        {
            EncodePass.First => passFraction * FirstPassShare,
            EncodePass.Second => FirstPassShare + passFraction * (1 - FirstPassShare),
            _ => passFraction,
        };

        return new EncodeProgress(fraction, pass, attempt);
    }
}
