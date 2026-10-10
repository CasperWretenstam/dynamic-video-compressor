using Dvc.Api.Models.Entities;

namespace Dvc.Api.Services;

public interface IVideoCompressionService
{
    /// <summary>
    /// Compresses <paramref name="job"/>'s input file according to its
    /// settings, writing the result to <paramref name="outputPath"/>. On
    /// failure the file may be missing, partial or oversized; the caller
    /// cleans it up.
    /// </summary>
    /// <exception cref="JobFailedException">
    /// A failure the caller can be told about, such as an unreadable upload or
    /// a size target that can't be hit. Its message is written for end users.
    /// </exception>
    Task CompressAsync(Job job, string outputPath, CancellationToken cancellationToken);
}
