namespace Dvc.Core.Compression;

/// <summary>The output every encode of a compression shares.</summary>
/// <param name="Width">The output width, in pixels.</param>
/// <param name="Height">The output height, in pixels.</param>
/// <param name="Fps">
/// The output framerate: the forced rate when the framerate is forced
/// (<c>ForceFps</c>), otherwise the source's own. Null if neither is known.
/// </param>
/// <param name="ForceFps">
/// Whether the encode forces the framerate. Otherwise the source's timing is
/// kept as-is, so variable-framerate footage stays variable.
/// </param>
/// <param name="AudioBitrateBps">
/// The AAC bitrate, in bps. 0 when the source has no audio, which encodes no
/// audio track.
/// </param>
/// <param name="EncoderPreset">The libx264 preset.</param>
public sealed record OutputSettings(
    int Width, int Height, double? Fps, bool ForceFps, long AudioBitrateBps, string EncoderPreset);
