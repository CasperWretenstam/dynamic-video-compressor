namespace Dvc.Core.Compression;

/// <summary>
/// How an encode spends its bits: at a constant quality
/// (<c>ConstantQualityEncode</c>) or at a bitrate aiming for a file size
/// (<c>TargetSizeEncode</c>).
/// </summary>
public abstract record EncodeMode
{
    private protected EncodeMode()
    {
    }
}

/// <summary>
/// A single constant-quality (CRF) pass: the encoder holds a perceived
/// quality level and spends whatever bitrate each scene needs, so the output
/// size follows the content.
/// </summary>
/// <param name="QualityPreference">The quality tier the CRF was resolved from.</param>
/// <param name="Crf">The libx264 constant rate factor.</param>
/// <param name="MaxRateBps">
/// The VBV maxrate, in bps, which only bounds the worst case on unusually
/// complex content.
/// </param>
public sealed record ConstantQualityEncode(string QualityPreference, int Crf, long MaxRateBps) : EncodeMode;

/// <summary>
/// An average-bitrate encode aiming for a file size. An output over the
/// target is re-encoded at a lower bitrate, a bounded number of times.
/// </summary>
/// <param name="TargetSizeBytes">The file size to stay under, in bytes.</param>
/// <param name="VideoBitrateBps">
/// The video bitrate, in bps, for the first encode, already capped at the
/// source's own.
/// </param>
/// <param name="TwoPass">Whether each encode runs in two passes.</param>
public sealed record TargetSizeEncode(long TargetSizeBytes, long VideoBitrateBps, bool TwoPass) : EncodeMode;
