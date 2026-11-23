using Dvc.Desktop.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dvc.Desktop.Tests;

public class FfmpegCheckTests
{
    // Trimmed from real "ffmpeg -hide_banner -encoders" output.
    private const string EncodersHeader =
        """
        Encoders:
         V..... = Video
         A..... = Audio
         ------
        """;

    private const string EncodersWithLibx264 =
        EncodersHeader + "\n" +
        """
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D libx264rgb           libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 RGB (codec h264)
         A....D aac                  AAC (Advanced Audio Coding)
        """;

    private const string EncodersWithoutLibx264 =
        EncodersHeader + "\n" +
        """
         V....D libx264rgb           libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 RGB (codec h264)
         V....D h264_mf              H264 via MediaFoundation (codec h264)
         A....D aac                  AAC (Advanced Audio Coding)
        """;

    [Fact]
    public void HasEncoder_FindsListedEncoder()
    {
        Assert.True(FfmpegCheck.HasEncoder(EncodersWithLibx264, "libx264"));
    }

    [Fact]
    public void HasEncoder_DoesNotMatchEncoderWithLongerName()
    {
        Assert.False(FfmpegCheck.HasEncoder(EncodersWithoutLibx264, "libx264"));
    }

    [Fact]
    public void HasEncoder_HandlesWindowsLineEndings()
    {
        Assert.True(FfmpegCheck.HasEncoder(EncodersWithLibx264.ReplaceLineEndings("\r\n"), "libx264"));
    }

    [Fact]
    public async Task CheckAsync_ReportsBothExecutablesMissing()
    {
        var missing = $"dvc-missing-{Guid.NewGuid():N}";
        var check = new FfmpegCheck(NullLogger<FfmpegCheck>.Instance, missing, missing);

        var result = await check.CheckAsync(CancellationToken.None);

        Assert.False(result.IsUsable);
        Assert.Equal(["ffmpeg wasn't found on PATH.", "ffprobe wasn't found on PATH."], result.Problems);
    }
}
