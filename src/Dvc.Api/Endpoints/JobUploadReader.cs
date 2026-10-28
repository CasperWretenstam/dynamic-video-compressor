using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Dvc.Api.Models.Definitions;
using Dvc.Api.Models.DTOs.Requests;
using Dvc.Core.Definitions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using static Dvc.Api.Endpoints.ProblemResults;

namespace Dvc.Api.Endpoints;

// Reads a job submission's multipart body: streams the video into the input
// directory and validates each form field, so the job endpoint only has to
// hand the result on.
public static class JobUploadReader
{
    // The form fields accepted besides the file itself.
    private static readonly string[] FieldNames =
    [
        nameof(SubmitJobRequest.QualityPreference),
        nameof(SubmitJobRequest.Resolution),
        nameof(SubmitJobRequest.Fps),
        nameof(SubmitJobRequest.TargetSizeBytes),
        nameof(SubmitJobRequest.EncoderPreset),
    ];

    // Parses the body with MultipartReader rather than binding
    // [FromForm] IFormFile, which buffers the whole upload to a temp file
    // and then needs a second copy. Sections are checked in the order they
    // arrive, so a client that sends its fields before the file never
    // uploads a video that a bad field would reject. On a rejection, the
    // upload written so far is deleted; on success, the caller owns the file
    // at the returned request's input path.
    public static async Task<JobUploadResult> ReadAsync(
        HttpRequest httpRequest,
        string inputDirectory,
        CancellationToken cancellationToken)
    {
        // Checked before reading any of the body, so a client waiting on
        // "Expect: 100-continue" never starts uploading.
        if (httpRequest.ContentLength > UploadDefinitions.MaxUploadBytes)
        {
            return UploadTooLarge();
        }

        if (!httpRequest.HasFormContentType ||
            !MediaTypeHeaderValue.TryParse(httpRequest.ContentType, out var mediaType))
        {
            return BadRequest("Expected multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary))
        {
            return BadRequest("Missing multipart boundary.");
        }

        string? inputPath = null;
        var succeeded = false;

        try
        {
            string? originalFileName = null;
            long fileSizeBytes = 0;
            long? targetSizeBytes = null;
            string? resolution = null;
            double? fps = null;
            string? qualityPreference = null;
            string? encoderPreset = null;
            var receivedFields = new HashSet<string>();

            var reader = new MultipartReader(boundary, httpRequest.Body);
            for (var section = await reader.ReadNextSectionAsync(cancellationToken);
                 section is not null;
                 section = await reader.ReadNextSectionAsync(cancellationToken))
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var contentDisposition))
                {
                    continue;
                }

                if (HasFileContentDisposition(contentDisposition))
                {
                    if (inputPath is not null)
                    {
                        return BadRequest("Only one video file may be uploaded per job.");
                    }

                    originalFileName = contentDisposition.FileName.Value ?? contentDisposition.FileNameStar.Value;

                    if (string.IsNullOrEmpty(originalFileName))
                    {
                        return BadRequest("A video file is required.");
                    }

                    var extension = Path.GetExtension(originalFileName);
                    if (string.IsNullOrEmpty(extension) || !VideoFileDefinitions.SupportedExtensions.Contains(extension))
                    {
                        return BadRequest(
                            $"Unsupported file extension '{extension}'. Allowed: {string.Join(", ", VideoFileDefinitions.SupportedExtensions)}");
                    }

                    Directory.CreateDirectory(inputDirectory);
                    inputPath = Path.Combine(inputDirectory, $"{Guid.NewGuid()}{extension}");

                    await using (var fileStream = File.Create(inputPath))
                    {
                        await section.Body.CopyToAsync(fileStream, cancellationToken);
                        fileSizeBytes = fileStream.Length;
                    }
                }
                else if (HasFormDataContentDisposition(contentDisposition))
                {
                    // Matched ignoring case. Unknown and repeated fields are
                    // rejected rather than ignored or overwritten, so a
                    // malformed request can't silently change the job. Checked
                    // before the value, so a blank field is caught too.
                    var sentName = contentDisposition.Name.Value;
                    var fieldName = FieldNames.FirstOrDefault(name =>
                        string.Equals(name, sentName, StringComparison.OrdinalIgnoreCase));
                    if (fieldName is null)
                    {
                        return BadRequest(
                            $"Unknown field '{sentName}'. Accepted: {string.Join(", ", FieldNames)}");
                    }

                    if (!receivedFields.Add(fieldName))
                    {
                        return BadRequest($"Field '{fieldName}' was sent more than once.");
                    }

                    using var streamReader = new StreamReader(section.Body);
                    var value = (await streamReader.ReadToEndAsync(cancellationToken)).Trim();

                    // Forms send a blank input as an empty value: not set.
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    // Rejects only values that could never encode; the rest
                    // pass through as given. Table lookups ignore case but
                    // store the table's spelling, since values go to ffmpeg
                    // and are echoed back in job status.
                    switch (fieldName)
                    {
                        case nameof(SubmitJobRequest.TargetSizeBytes):
                            if (!TryParseWholeNumber(value, out var parsedTargetSizeBytes))
                                return ExpectedWholeNumber(nameof(SubmitJobRequest.TargetSizeBytes));
                            if (parsedTargetSizeBytes <= 0)
                                return BadRequest("TargetSizeBytes must be greater than zero.");
                            targetSizeBytes = parsedTargetSizeBytes;
                            break;
                        case nameof(SubmitJobRequest.Fps):
                            if (!TryParseNumber(value, out var parsedFps))
                                return ExpectedNumber(nameof(SubmitJobRequest.Fps));
                            if (parsedFps <= 0)
                                return BadRequest("Fps must be greater than zero.");
                            fps = parsedFps;
                            break;
                        case nameof(SubmitJobRequest.Resolution):
                            resolution = ResolutionDefinitions.ShortSides.CanonicalKey(value);
                            if (resolution is null)
                                return Unsupported("resolution", value, ResolutionDefinitions.ShortSides.Keys);
                            break;
                        case nameof(SubmitJobRequest.QualityPreference):
                            qualityPreference = QualityPreferenceDefinitions.Tiers.CanonicalKey(value);
                            if (qualityPreference is null)
                                return Unsupported("quality preference", value, QualityPreferenceDefinitions.Tiers.Keys);
                            break;
                        case nameof(SubmitJobRequest.EncoderPreset):
                            encoderPreset = EncoderPresetDefinitions.Presets.CanonicalKey(value);
                            if (encoderPreset is null)
                                return Unsupported("encoder preset", value, EncoderPresetDefinitions.Presets.Keys);
                            break;
                    }
                }
            }

            if (inputPath is null || fileSizeBytes == 0)
            {
                return BadRequest("A video file is required.");
            }

            succeeded = true;
            return new SubmitJobRequest
            {
                InputPath = inputPath,
                OriginalFileName = originalFileName!,
                FileSizeBytes = fileSizeBytes,
                TargetSizeBytes = targetSizeBytes,
                Resolution = resolution,
                Fps = fps,
                QualityPreference = qualityPreference,
                EncoderPreset = encoderPreset
            };
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // A chunked body only hits Kestrel's limit mid-stream.
            return UploadTooLarge();
        }
        finally
        {
            // A rejected or aborted upload will never become a job.
            if (!succeeded && inputPath is not null)
            {
                File.Delete(inputPath);
            }
        }
    }

    private static ProblemHttpResult UploadTooLarge() =>
        Problem(
            StatusCodes.Status413PayloadTooLarge,
            $"Upload exceeds the {UploadDefinitions.MaxUploadBytes / (1024 * 1024)}MB limit.");

    private static ProblemHttpResult ExpectedNumber(string field) =>
        BadRequest($"Expected a number for: {field}.");

    private static ProblemHttpResult ExpectedWholeNumber(string field) =>
        BadRequest($"Expected a whole number for: {field}.");

    private static ProblemHttpResult Unsupported(string parameter, string value, IEnumerable<string> allowed) =>
        BadRequest($"Unsupported {parameter} '{value}'. Allowed: {string.Join(", ", allowed)}");

    // Invariant culture, so "29.97" parses regardless of server locale.
    // Rejects NaN/infinity, which ffmpeg can't act on.
    private static bool TryParseNumber(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);

    // Digits with an optional sign: no decimal point, exponent or thousands
    // separator. The sign is allowed so a negative value gets the "greater
    // than zero" message rather than a parse error.
    private static bool TryParseWholeNumber(string value, out long result) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);

    private static bool HasFileContentDisposition(ContentDispositionHeaderValue contentDisposition) =>
        contentDisposition.DispositionType.Equals("form-data") &&
        (!StringSegment.IsNullOrEmpty(contentDisposition.FileName) ||
         !StringSegment.IsNullOrEmpty(contentDisposition.FileNameStar));

    private static bool HasFormDataContentDisposition(ContentDispositionHeaderValue contentDisposition) =>
        contentDisposition.DispositionType.Equals("form-data") &&
        StringSegment.IsNullOrEmpty(contentDisposition.FileName) &&
        StringSegment.IsNullOrEmpty(contentDisposition.FileNameStar);
}

// What JobUploadReader produced: either a validated request, with its video
// already written to disk, or the problem response to return instead.
public sealed class JobUploadResult
{
    private JobUploadResult(SubmitJobRequest? request, ProblemHttpResult? problem)
    {
        Request = request;
        Problem = problem;
    }

    public SubmitJobRequest? Request { get; }

    public ProblemHttpResult? Problem { get; }

    [MemberNotNullWhen(true, nameof(Request))]
    [MemberNotNullWhen(false, nameof(Problem))]
    public bool Succeeded => Request is not null;

    public static implicit operator JobUploadResult(SubmitJobRequest request) => new(request, null);

    public static implicit operator JobUploadResult(ProblemHttpResult problem) => new(null, problem);
}
