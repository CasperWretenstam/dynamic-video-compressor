using System.Globalization;
using Dvc.Api.Models.Definitions;
using Dvc.Api.Models.DTOs.Requests;
using Dvc.Api.Models.DTOs.Responses;
using Dvc.Api.Models.Options;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Dvc.Api.Endpoints;

public static class OptionsEndpoints
{
    public static IEndpointRouteBuilder MapOptionsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/options", GetJobOptions)
            .WithName("GetJobOptions")
            .WithTags("Options");

        return app;
    }

    // Built from the same definitions and config the job pipeline uses, so
    // it can't drift from what POST /v1/jobs accepts.
    /// <summary>Describe the job parameters</summary>
    /// <remarks>
    /// Lists every field <c>POST /v1/jobs</c> accepts, with its type, allowed
    /// values and default as configured on this deployment, so a client can
    /// build its submission form from the running service instead of
    /// hardcoding values.
    /// </remarks>
    /// <response code="200">Every job parameter.</response>
    internal static Ok<JobOptionsResponse> GetJobOptions(IOptions<EncodingOptions> encodingOptions) =>
        TypedResults.Ok(BuildJobOptions(encodingOptions.Value));

    public static JobOptionsResponse BuildJobOptions(EncodingOptions encodingOptions)
    {
        var resolutionDefault = encodingOptions.DefaultMaxShortSide is { } maxShortSide
            ? $"The source resolution, with the short side capped at {maxShortSide}px. Never upscaled."
            : "The source resolution.";
        var fpsDefault = encodingOptions.DefaultMaxFps is { } maxFps
            ? $"The source framerate, capped at {maxFps.ToString(CultureInfo.InvariantCulture)}fps. Never raised."
            : "The source framerate.";

        return new JobOptionsResponse(
        [
            new ParameterDescription(
                Name: "file",
                Level: ParameterLevel.Standard,
                Type: ParameterType.File,
                Required: true,
                Description: $"The video to compress. Accepted extensions: {string.Join(", ", UploadDefinitions.AllowedExtensions)}.",
                Default: null,
                DefaultDescription: null,
                AllowedValues: null),
            new ParameterDescription(
                Name: nameof(SubmitJobRequest.QualityPreference),
                Level: ParameterLevel.Standard,
                Type: ParameterType.String,
                Required: false,
                Description: "The visual quality to aim for. Output size follows the content rather than a target, " +
                    "so simple footage shrinks far more than complex footage. Ignored when TargetSizeBytes is set.",
                Default: QualityPreferenceDefinitions.Default,
                DefaultDescription: null,
                AllowedValues: QualityPreferenceDefinitions.Tiers
                    .Select(tier => new AllowedValue(tier.Key, tier.Value.Description))
                    .ToList()),
            new ParameterDescription(
                Name: nameof(SubmitJobRequest.Resolution),
                Level: ParameterLevel.Standard,
                Type: ParameterType.String,
                Required: false,
                Description: "The output's short side. The long side follows the source's aspect ratio, so 1080p is " +
                    "1920x1080 for landscape 16:9 and 1080x1920 for portrait. A value that's set is used as given, " +
                    "including upscaling.",
                Default: null,
                DefaultDescription: resolutionDefault,
                AllowedValues: ResolutionDefinitions.ShortSides
                    .Select(resolution => new AllowedValue(resolution.Key, $"Short side {resolution.Value}px."))
                    .ToList()),
            new ParameterDescription(
                Name: nameof(SubmitJobRequest.Fps),
                Level: ParameterLevel.Standard,
                Type: ParameterType.Number,
                Required: false,
                Description: "The output framerate; fractional rates like 29.97 are accepted. A value that's set is " +
                    "used as given, including raising it above the source's (which duplicates frames).",
                Default: null,
                DefaultDescription: fpsDefault,
                AllowedValues: null),
            new ParameterDescription(
                Name: nameof(SubmitJobRequest.TargetSizeBytes),
                Level: ParameterLevel.Advanced,
                Type: ParameterType.Integer,
                Required: false,
                Description: "Compress to at most this many bytes, e.g. to fit an upload limit: 10 MB is 10000000, " +
                    "10 MiB is 10485760. Takes priority over QualityPreference: quality follows from the size " +
                    "instead of the other way round.",
                Default: null,
                DefaultDescription: "No size target; QualityPreference applies.",
                AllowedValues: null),
            new ParameterDescription(
                Name: nameof(SubmitJobRequest.EncoderPreset),
                Level: ParameterLevel.Advanced,
                Type: ParameterType.String,
                Required: false,
                Description: "How much effort the encoder spends finding redundancy, independent of quality. A slower " +
                    "preset gives a smaller file at a given QualityPreference, or better quality at a given " +
                    "TargetSizeBytes, at the cost of encode time.",
                Default: encodingOptions.Preset,
                DefaultDescription: null,
                AllowedValues: EncoderPresetDefinitions.Presets
                    .Select(preset => new AllowedValue(preset.Key, preset.Value))
                    .ToList()),
        ]);
    }
}
