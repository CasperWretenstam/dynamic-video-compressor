namespace Dvc.Api.Models.DTOs.Responses;

/// <summary>
/// Describes every <c>POST /v1/jobs</c> parameter, so clients can build
/// their UI from the running service instead of hardcoding values.
/// </summary>
public sealed record JobOptionsResponse(IReadOnlyList<ParameterDescription> Parameters);

/// <summary>
/// Standard parameters cover the common "compress away excess data" case
/// and are safe to leave unset; advanced ones give direct control and are
/// used exactly as given.
/// </summary>
public enum ParameterLevel
{
    Standard,
    Advanced
}

public enum ParameterType
{
    File,
    String,
    Number
}

/// <param name="Default">
/// The value used when the parameter is omitted, or null when there's no
/// fixed value (e.g. it follows the source).
/// </param>
/// <param name="DefaultDescription">
/// The default in words when there's no fixed value
/// (<see cref="Default"/>); otherwise null.
/// </param>
/// <param name="AllowedValues">The accepted values, or null for free-form input.</param>
public sealed record ParameterDescription(
    string Name,
    ParameterLevel Level,
    ParameterType Type,
    bool Required,
    string Description,
    string? Default,
    string? DefaultDescription,
    IReadOnlyList<AllowedValue>? AllowedValues);

public sealed record AllowedValue(string Value, string Description);
