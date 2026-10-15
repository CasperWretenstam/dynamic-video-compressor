using System.Text;
using System.Text.Json.Nodes;
using Dvc.Api.Models.DTOs.Responses;
using Microsoft.OpenApi;

namespace Dvc.Api.Infrastructure.OpenApi;

// Describes the POST /v1/jobs upload form for the OpenAPI document. The
// endpoint parses its multipart body by hand, so the generator can't infer
// the form; it's built from the same parameter descriptions GET /v1/options
// returns instead, so the two can't disagree.
public static class JobFormSchema
{
    public const string ContentType = "multipart/form-data";

    public static OpenApiRequestBody Build(JobOptionsResponse jobOptions)
    {
        var formSchema = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = new Dictionary<string, IOpenApiSchema>(),
            Required = new HashSet<string>(),
            // Unknown fields are rejected with a 400.
            AdditionalPropertiesAllowed = false,
        };

        // File last: clients send fields in the listed order, and the
        // endpoint checks each one as it arrives.
        foreach (var parameter in jobOptions.Parameters.OrderBy(p => p.Type == ParameterType.File))
        {
            formSchema.Properties[parameter.Name] = BuildProperty(parameter);

            if (parameter.Required)
            {
                formSchema.Required.Add(parameter.Name);
            }
        }

        return new OpenApiRequestBody
        {
            Description = "Send the fields before the file: each is checked as it arrives, so a bad field " +
                "is rejected before the video uploads.",
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                [ContentType] = new OpenApiMediaType { Schema = formSchema },
            },
        };
    }

    private static OpenApiSchema BuildProperty(ParameterDescription parameter)
    {
        var schema = new OpenApiSchema { Description = BuildDescription(parameter) };

        switch (parameter.Type)
        {
            case ParameterType.File:
                schema.Type = JsonSchemaType.String;
                schema.Format = "binary";
                break;
            case ParameterType.String:
                schema.Type = JsonSchemaType.String;
                if (parameter.AllowedValues is not null)
                {
                    schema.Enum = [.. parameter.AllowedValues.Select(allowed => (JsonNode)JsonValue.Create(allowed.Value))];
                }
                break;
            case ParameterType.Number:
                // The endpoint rejects zero and below.
                schema.Type = JsonSchemaType.Number;
                schema.ExclusiveMinimum = "0";
                break;
        }

        if (parameter.Default is not null)
        {
            schema.Default = JsonValue.Create(parameter.Default);
        }

        return schema;
    }

    // JSON Schema can't describe individual enum values, so they're listed
    // in the property's description, as Markdown.
    private static string BuildDescription(ParameterDescription parameter)
    {
        var description = new StringBuilder(parameter.Description);

        if (parameter.DefaultDescription is not null)
        {
            description.Append($"\n\nDefault: {parameter.DefaultDescription}");
        }

        if (parameter.AllowedValues is not null)
        {
            description.Append('\n');
            foreach (var allowed in parameter.AllowedValues)
            {
                description.Append($"\n- `{allowed.Value}`: {allowed.Description}");
            }
        }

        return description.ToString();
    }
}
