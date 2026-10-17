using System.Net.Http.Headers;
using System.Text;
using Dvc.Api.Endpoints;
using Dvc.Api.Models.Definitions;
using Microsoft.AspNetCore.Http;

namespace Dvc.Api.Tests;

public class JobUploadReaderTests : IDisposable
{
    private readonly string _inputDirectory = Path.Combine(Path.GetTempPath(), $"dvc-tests-{Guid.NewGuid()}");

    public void Dispose()
    {
        if (Directory.Exists(_inputDirectory))
        {
            Directory.Delete(_inputDirectory, recursive: true);
        }
    }

    private static ByteArrayContent Video(byte[]? bytes = null) =>
        new(bytes ?? [1, 2, 3, 4]) { Headers = { ContentType = new MediaTypeHeaderValue("video/mp4") } };

    private static HttpRequest NewRequest(Stream body, string contentType, long? contentLength)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = body;
        context.Request.ContentType = contentType;
        context.Request.ContentLength = contentLength;
        return context.Request;
    }

    private static async Task<HttpRequest> NewRequest(MultipartFormDataContent content)
    {
        var body = new MemoryStream();
        await content.CopyToAsync(body);
        body.Position = 0;
        return NewRequest(body, content.Headers.ContentType!.ToString(), body.Length);
    }

    private Task<JobUploadResult> Read(HttpRequest request) =>
        JobUploadReader.ReadAsync(request, _inputDirectory, CancellationToken.None);

    private async Task<JobUploadResult> Read(MultipartFormDataContent content) =>
        await Read(await NewRequest(content));

    private string[] StoredFiles() =>
        Directory.Exists(_inputDirectory) ? Directory.GetFiles(_inputDirectory) : [];

    private void AssertRejected(JobUploadResult result, int statusCode, string detailFragment)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(statusCode, result.Problem.StatusCode);
        Assert.Contains(detailFragment, result.Problem.ProblemDetails.Detail);
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task ReadAsync_ReturnsRequest_WithCanonicalValuesAndStoredFile()
    {
        var result = await Read(new MultipartFormDataContent
        {
            { new StringContent("higherquality"), "qualityPreference" },
            { new StringContent("720P"), "Resolution" },
            { new StringContent(" 29.97 "), "Fps" },
            { new StringContent("10.5"), "TargetSizeMb" },
            { new StringContent("VeryFast"), "EncoderPreset" },
            { Video([9, 8, 7]), "file", "clip.mp4" },
        });

        Assert.True(result.Succeeded);
        var request = result.Request;
        Assert.Equal("HigherQuality", request.QualityPreference);
        Assert.Equal("720p", request.Resolution);
        Assert.Equal(29.97, request.Fps);
        Assert.Equal(10.5, request.TargetSizeMb);
        Assert.Equal("veryfast", request.EncoderPreset);
        Assert.Equal("clip.mp4", request.OriginalFileName);
        Assert.Equal(3, request.FileSizeBytes);
        Assert.Equal(_inputDirectory, Path.GetDirectoryName(request.InputPath));
        Assert.Equal(".mp4", Path.GetExtension(request.InputPath));
        Assert.Equal([9, 8, 7], await File.ReadAllBytesAsync(request.InputPath));
    }

    [Fact]
    public async Task ReadAsync_TreatsBlankFieldsAsNotSet()
    {
        var result = await Read(new MultipartFormDataContent
        {
            { new StringContent(""), "QualityPreference" },
            { new StringContent("  "), "Fps" },
            { Video(), "file", "clip.mov" },
        });

        Assert.True(result.Succeeded);
        Assert.Null(result.Request.QualityPreference);
        Assert.Null(result.Request.Fps);
    }

    [Fact]
    public async Task ReadAsync_RejectsUnknownField_AndDeletesUploadedFile()
    {
        // The file comes first, so it's already on disk when the field is rejected.
        var result = await Read(new MultipartFormDataContent
        {
            { Video(), "file", "clip.mp4" },
            { new StringContent("1"), "Bitrate" },
        });

        AssertRejected(result, StatusCodes.Status400BadRequest, "Unknown field 'Bitrate'");
    }

    [Fact]
    public async Task ReadAsync_RejectsRepeatedField_IgnoringCase()
    {
        var result = await Read(new MultipartFormDataContent
        {
            { new StringContent("720p"), "Resolution" },
            { new StringContent(""), "resolution" },
            { Video(), "file", "clip.mp4" },
        });

        AssertRejected(result, StatusCodes.Status400BadRequest, "'Resolution' was sent more than once");
    }

    [Theory]
    [InlineData("TargetSizeMb", "ten", "Expected a number for: TargetSizeMb")]
    [InlineData("TargetSizeMb", "0", "TargetSizeMb must be greater than zero")]
    [InlineData("Fps", "NaN", "Expected a number for: Fps")]
    [InlineData("Fps", "29,97", "Expected a number for: Fps")]
    [InlineData("Fps", "-30", "Fps must be greater than zero")]
    [InlineData("Resolution", "1440", "Unsupported resolution '1440'")]
    [InlineData("QualityPreference", "Best", "Unsupported quality preference 'Best'")]
    [InlineData("EncoderPreset", "turbo", "Unsupported encoder preset 'turbo'")]
    public async Task ReadAsync_RejectsInvalidValue(string field, string value, string detailFragment)
    {
        var result = await Read(new MultipartFormDataContent
        {
            { new StringContent(value), field },
            { Video(), "file", "clip.mp4" },
        });

        AssertRejected(result, StatusCodes.Status400BadRequest, detailFragment);
    }

    [Fact]
    public async Task ReadAsync_RejectsDisallowedExtension()
    {
        var result = await Read(new MultipartFormDataContent { { Video(), "file", "notes.txt" } });

        AssertRejected(result, StatusCodes.Status400BadRequest, "Unsupported file extension '.txt'");
    }

    [Fact]
    public async Task ReadAsync_RejectsSecondFile_AndDeletesFirst()
    {
        var result = await Read(new MultipartFormDataContent
        {
            { Video(), "file", "one.mp4" },
            { Video(), "file", "two.mp4" },
        });

        AssertRejected(result, StatusCodes.Status400BadRequest, "Only one video file");
    }

    [Fact]
    public async Task ReadAsync_RejectsMissingFile()
    {
        var result = await Read(new MultipartFormDataContent { { new StringContent("Balanced"), "QualityPreference" } });

        AssertRejected(result, StatusCodes.Status400BadRequest, "A video file is required");
    }

    [Fact]
    public async Task ReadAsync_RejectsEmptyFile_AndDeletesIt()
    {
        var result = await Read(new MultipartFormDataContent { { Video([]), "file", "clip.mp4" } });

        AssertRejected(result, StatusCodes.Status400BadRequest, "A video file is required");
    }

    [Fact]
    public async Task ReadAsync_RejectsNonMultipartBody()
    {
        var request = NewRequest(new MemoryStream("{}"u8.ToArray()), "application/json", 2);

        AssertRejected(await Read(request), StatusCodes.Status400BadRequest, "Expected multipart/form-data");
    }

    [Fact]
    public async Task ReadAsync_RejectsDeclaredOversizedBody_WithoutReadingIt()
    {
        var request = NewRequest(
            new ThrowingStream([]),
            "multipart/form-data; boundary=b",
            UploadDefinitions.MaxUploadBytes + 1);

        AssertRejected(await Read(request), StatusCodes.Status413PayloadTooLarge, "Upload exceeds");
    }

    [Fact]
    public async Task ReadAsync_Returns413_WhenBodyLimitIsHitMidUpload_AndDeletesPartialFile()
    {
        // A chunked body has no Content-Length, so the server's limit only
        // trips partway through the file.
        var start = Encoding.ASCII.GetBytes(
            "--b\r\nContent-Disposition: form-data; name=file; filename=clip.mp4\r\n\r\nsome video bytes");
        var request = NewRequest(new ThrowingStream(start), "multipart/form-data; boundary=b", contentLength: null);

        AssertRejected(await Read(request), StatusCodes.Status413PayloadTooLarge, "Upload exceeds");
    }

    // Returns the given bytes, then fails the next read the way Kestrel does
    // when a request body exceeds its size limit.
    private sealed class ThrowingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            Position < Length ? base.Read(buffer, offset, count) : throw TooLarge();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position < Length ? base.ReadAsync(buffer, cancellationToken) : throw TooLarge();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Position < Length ? base.ReadAsync(buffer, offset, count, cancellationToken) : throw TooLarge();

        private static BadHttpRequestException TooLarge() =>
            new("Request body too large.", StatusCodes.Status413PayloadTooLarge);
    }
}
