namespace Dvc.Api.Models.Entities;

/// <summary>
/// Where a job is in processing. <c>Queued</c>: waiting its turn, since jobs
/// are encoded one at a time in the order they were submitted.
/// <c>Processing</c>: being encoded; if the service restarts meanwhile, the
/// job goes back to <c>Queued</c> and starts over. <c>Completed</c>: the
/// compressed video is ready to download. <c>Failed</c>: the video couldn't
/// be encoded as requested, and the job's <c>errorMessage</c> says why.
/// </summary>
public enum JobStatus { Queued, Processing, Completed, Failed }
