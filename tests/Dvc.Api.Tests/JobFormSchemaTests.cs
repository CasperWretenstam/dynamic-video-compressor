using Dvc.Api.Endpoints;
using Dvc.Api.Infrastructure.OpenApi;
using Dvc.Api.Models.DTOs.Requests;
using Dvc.Api.Models.Options;
using Dvc.Core.Definitions;
using Microsoft.OpenApi;

namespace Dvc.Api.Tests;

public class JobFormSchemaTests
{
    private static OpenApiSchema BuildFormSchema(EncodingOptions encodingOptions)
    {
        var requestBody = JobFormSchema.Build(OptionsEndpoints.BuildJobOptions(encodingOptions));
        return Assert.IsType<OpenApiSchema>(requestBody.Content![JobFormSchema.ContentType].Schema);
    }

    private static OpenApiSchema Property(OpenApiSchema form, string name) =>
        Assert.IsType<OpenApiSchema>(form.Properties![name]);

    [Fact]
    public void Build_DescribesEveryOptionsParameter()
    {
        // GET /v1/options already fails a test for any submittable field it
        // doesn't describe, so matching it covers every field.
        var parameters = OptionsEndpoints.BuildJobOptions(new EncodingOptions()).Parameters.Select(p => p.Name);
        var form = BuildFormSchema(new EncodingOptions());

        Assert.Equal(parameters.Order(), form.Properties!.Keys.Order());
        Assert.False(form.AdditionalPropertiesAllowed);
    }

    [Fact]
    public void Build_RequiresOnlyTheFile_AndListsItLast()
    {
        var form = BuildFormSchema(new EncodingOptions());

        Assert.Equal(["file"], form.Required!);
        Assert.Equal("file", form.Properties!.Keys.Last());

        var file = Property(form, "file");
        Assert.Equal(JsonSchemaType.String, file.Type);
        Assert.Equal("binary", file.Format);
    }

    [Fact]
    public void Build_ListsEachDefinitionTableAsAnEnum()
    {
        var form = BuildFormSchema(new EncodingOptions());

        Assert.Equal(
            ResolutionDefinitions.ShortSides.Keys,
            Property(form, nameof(SubmitJobRequest.Resolution)).Enum!.Select(v => v.GetValue<string>()));
        Assert.Equal(
            QualityPreferenceDefinitions.Tiers.Keys,
            Property(form, nameof(SubmitJobRequest.QualityPreference)).Enum!.Select(v => v.GetValue<string>()));
        Assert.Equal(
            EncoderPresetDefinitions.Presets.Keys,
            Property(form, nameof(SubmitJobRequest.EncoderPreset)).Enum!.Select(v => v.GetValue<string>()));
    }

    [Fact]
    public void Build_UsesConfiguredPresetAsDefault()
    {
        var form = BuildFormSchema(new EncodingOptions { Preset = "veryfast" });

        Assert.Equal("veryfast", Property(form, nameof(SubmitJobRequest.EncoderPreset)).Default!.GetValue<string>());
    }

    [Fact]
    public void Build_DescribesFpsAsNumberGreaterThanZero()
    {
        var property = Property(BuildFormSchema(new EncodingOptions()), nameof(SubmitJobRequest.Fps));

        Assert.Equal(JsonSchemaType.Number, property.Type);
        Assert.Equal("0", property.ExclusiveMinimum);
    }

    [Fact]
    public void Build_DescribesTargetSizeAsInt64GreaterThanZero()
    {
        var property = Property(BuildFormSchema(new EncodingOptions()), nameof(SubmitJobRequest.TargetSizeBytes));

        Assert.Equal(JsonSchemaType.Integer, property.Type);
        Assert.Equal("int64", property.Format);
        Assert.Equal("0", property.ExclusiveMinimum);
    }
}
