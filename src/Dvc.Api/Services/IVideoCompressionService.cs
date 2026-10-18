using Dvc.Api.Models.Entities;

namespace Dvc.Api.Services;

public interface IVideoCompressionService
{
    /// <summary>
    /// Compresses a job's input file according to its settings, writing the
    /// result to <c>outputPath</c>. On failure the file may be missing,
    /// partial or oversized; the caller cleans it up.
    /// </summary>
    /// <remarks>
    /// Throws <c>JobFailedException</c> for a failure the caller can be told
    /// about, such as an unreadable upload or a size target that can't be
    /// hit. Its message is written for end users.
    /// </remarks>
    Task CompressAsync(Job job, string outputPath, CancellationToken cancellationToken);
}
