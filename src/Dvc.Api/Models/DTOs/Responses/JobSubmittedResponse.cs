using Dvc.Api.Models.Entities;

namespace Dvc.Api.Models.DTOs.Responses;

/// <summary>A newly queued job.</summary>
/// <param name="Id">The job's ID, used to check on, download and delete it.</param>
/// <param name="Status">
/// Always <c>Queued</c>: the job is processed in the background, in the order
/// jobs were submitted.
/// </param>
public sealed record JobSubmittedResponse(Guid Id, JobStatus Status)
{
    public static JobSubmittedResponse FromJob(Job job) => new(job.Id, job.Status);
}
