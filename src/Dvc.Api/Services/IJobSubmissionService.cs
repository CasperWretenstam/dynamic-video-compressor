using Dvc.Api.Models.DTOs.Requests;
using Dvc.Api.Models.Entities;

namespace Dvc.Api.Services;

public interface IJobSubmissionService
{
    Task<Job> SubmitAsync(SubmitJobRequest request, CancellationToken cancellationToken);
}
