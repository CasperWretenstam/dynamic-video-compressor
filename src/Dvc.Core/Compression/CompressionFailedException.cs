namespace Dvc.Core.Compression;

/// <summary>
/// A compression failure the engine can explain, such as an unreadable file
/// or a target size that can't be hit. Its message is written to be shown to
/// end users. Any other exception from compressing carries technical detail,
/// often ffmpeg's raw output, and is better logged than shown.
/// </summary>
public class CompressionFailedException(string message, Exception? innerException = null)
    : Exception(message, innerException);
