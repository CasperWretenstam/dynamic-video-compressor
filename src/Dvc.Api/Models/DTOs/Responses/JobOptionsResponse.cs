namespace Dvc.Api.Models.DTOs.Responses;

/// <summary>
/// Every field <c>POST /v1/jobs</c> accepts, as configured on this
/// deployment, so a client can build its submission form from the running
/// service instead of hardcoding values.
/// </summary>
/// <param name="Parameters">One entry per field, including the video file itself.</param>
public sealed record JobOptionsResponse(IReadOnlyList<ParameterDescription> Parameters);

/// <summary>
/// How a parameter is meant to be used. <c>Standard</c> parameters cover the
/// common case of compressing away excess data, and are safe to leave unset.
/// <c>Advanced</c> ones give direct control over the encode, and are used
/// exactly as given.
/// </summary>
public enum ParameterLevel
{
    Standard,
    Advanced
}

/// <summary>
/// The kind of value a parameter takes: <c>File</c> for the uploaded video,
/// <c>String</c> for text (often limited to a set of allowed values), or
/// <c>Number</c> for a decimal number, written with a <c>.</c> as the decimal
/// separator.
/// </summary>
public enum ParameterType
{
    File,
    String,
    Number
}

/// <summary>One field <c>POST /v1/jobs</c> accepts.</summary>
/// <param name="Name">The form field name to send. Matched ignoring case.</param>
/// <param name="Level">Whether this is a standard or an advanced parameter.</param>
/// <param name="Type">The kind of value it takes.</param>
/// <param name="Required">Whether every submission must include it.</param>
/// <param name="Description">What the parameter does, suitable for showing to users.</param>
/// <param name="Default">
/// The value used when the parameter is left out. Null when there's no fixed
/// value, e.g. when it follows the video's own.
/// </param>
/// <param name="DefaultDescription">
/// The default in words when there's no fixed value (<c>default</c> is
/// null); otherwise null.
/// </param>
/// <param name="AllowedValues">The accepted values, or null when any value of the right type is accepted.</param>
public sealed record ParameterDescription(
    string Name,
    ParameterLevel Level,
    ParameterType Type,
    bool Required,
    string Description,
    string? Default,
    string? DefaultDescription,
    IReadOnlyList<AllowedValue>? AllowedValues);

/// <summary>One accepted value for a parameter.</summary>
/// <param name="Value">The value to send. Matched ignoring case.</param>
/// <param name="Description">What choosing this value does.</param>
public sealed record AllowedValue(string Value, string Description);
