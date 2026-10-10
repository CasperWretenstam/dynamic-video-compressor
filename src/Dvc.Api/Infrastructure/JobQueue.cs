using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;

namespace Dvc.Api.Infrastructure;

public class JobQueue : IJobQueue
{
    // Unbounded: each item is just a Guid.
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true, // one background worker consumes
        SingleWriter = false // multiple requests can enqueue concurrently
    });

    public void Enqueue(Guid jobId)
    {
        if (!_channel.Writer.TryWrite(jobId))
        {
            throw new InvalidOperationException("Failed to enqueue job: channel is closed.");
        }
    }

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
