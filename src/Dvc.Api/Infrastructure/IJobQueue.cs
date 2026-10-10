using System.Collections.Generic;
using System.Threading;

namespace Dvc.Api.Infrastructure;

/// <summary>
/// The IDs of jobs waiting to be processed, in submission order. Held in
/// memory only; startup re-enqueues waiting jobs from the database.
/// </summary>
public interface IJobQueue
{
    void Enqueue(Guid jobId);

    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken);
}
