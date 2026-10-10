using Dvc.Api.Models.DTOs.Responses;
using Dvc.Api.Models.Entities;

namespace Dvc.Api.Tests;

public class JobStatusResponseTests
{
    private static Job NewJob() => new()
    {
        Id = Guid.NewGuid(),
        Status = JobStatus.Queued,
        InputPath = "input.mp4",
        CreatedAt = DateTimeOffset.UtcNow,
        QualityPreference = "Balanced",
        EncoderPreset = "slow",
    };

    [Fact]
    public void FromJob_OmitsOutput_UntilResolved()
    {
        var response = JobStatusResponse.FromJob(NewJob());

        Assert.Null(response.Output);
        Assert.Equal(new JobSettings("Balanced", null, null, null, "slow"), response.Settings);
    }

    [Fact]
    public void FromJob_ReportsResolvedOutput()
    {
        var job = NewJob();
        job.OutputWidth = 1080;
        job.OutputHeight = 1920;
        job.OutputFps = 29.97;

        Assert.Equal(new JobOutput(1080, 1920, 29.97), JobStatusResponse.FromJob(job).Output);
    }

    [Fact]
    public void FromJob_ReportsOutput_WhenFpsUnknown()
    {
        var job = NewJob();
        job.OutputWidth = 1920;
        job.OutputHeight = 1080;

        Assert.Equal(new JobOutput(1920, 1080, null), JobStatusResponse.FromJob(job).Output);
    }
}
