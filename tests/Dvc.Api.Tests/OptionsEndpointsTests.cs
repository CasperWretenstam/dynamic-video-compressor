using Dvc.Api.Endpoints;
using Dvc.Api.Models.DTOs.Requests;
using Dvc.Api.Models.Options;

namespace Dvc.Api.Tests;

public class OptionsEndpointsTests
{
    [Fact]
    public void BuildJobOptions_ReflectsConfiguredDefaults()
    {
        var response = OptionsEndpoints.BuildJobOptions(new EncodingOptions
        {
            Preset = "veryfast",
            DefaultMaxShortSide = 720,
            DefaultMaxFps = 24,
        });

        var parameters = response.Parameters.ToDictionary(p => p.Name);
        Assert.Equal("veryfast", parameters[nameof(SubmitJobRequest.EncoderPreset)].Default);
        Assert.Contains("720px", parameters[nameof(SubmitJobRequest.Resolution)].DefaultDescription);
        Assert.Contains("24fps", parameters[nameof(SubmitJobRequest.Fps)].DefaultDescription);
    }

    [Fact]
    public void BuildJobOptions_DescribesUncappedDefaults_WhenCapsDisabled()
    {
        var parameters = OptionsEndpoints.BuildJobOptions(new EncodingOptions()).Parameters.ToDictionary(p => p.Name);

        Assert.Equal("The source resolution.", parameters[nameof(SubmitJobRequest.Resolution)].DefaultDescription);
        Assert.Equal("The source framerate.", parameters[nameof(SubmitJobRequest.Fps)].DefaultDescription);
    }

    [Fact]
    public void BuildJobOptions_DescribesEverySubmittableField()
    {
        // Every form field POST /v1/jobs reads must be discoverable. Read
        // from SubmitJobRequest itself, so a field added there fails this
        // until GET /v1/options describes it too.
        string[] setByServer =
        [
            nameof(SubmitJobRequest.InputPath),
            nameof(SubmitJobRequest.OriginalFileName),
            nameof(SubmitJobRequest.FileSizeBytes),
        ];
        var submittable = typeof(SubmitJobRequest).GetProperties()
            .Select(p => p.Name)
            .Except(setByServer)
            .ToList();
        var described = OptionsEndpoints.BuildJobOptions(new EncodingOptions()).Parameters.Select(p => p.Name).ToHashSet();

        Assert.NotEmpty(submittable);
        Assert.All(submittable, field => Assert.Contains(field, described));
    }
}
