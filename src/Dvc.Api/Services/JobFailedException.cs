namespace Dvc.Api.Services;

/// <summary>
/// A compression failure whose message is safe to show as the job's error
/// message (<see cref="Models.Entities.Job.ErrorMessage"/>). Other exceptions
/// reaching the background worker
/// (<see cref="Infrastructure.JobProcessingWorker"/>) are logged and replaced
/// with a generic message.
/// </summary>
public class JobFailedException(string message, Exception? innerException = null)
    : Exception(message, innerException);
