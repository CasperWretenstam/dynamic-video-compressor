using Dvc.Api.Models.Entities;

namespace Dvc.Api.Models.DTOs.Responses;

/// <summary>
/// The body of <c>POST /v1/jobs</c>: the new job's ID, to poll
/// <c>GET /v1/jobs/{id}</c> with.
/// </summary>
/// <param name="Status">Always queued at this point; processing happens in the background.</param>
public sealed record JobSubmittedResponse(Guid Id, JobStatus Status)
{
    public static JobSubmittedResponse FromJob(Job job) => new(job.Id, job.Status);
}
